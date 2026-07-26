using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Agent;

public sealed class RealTimeMarketHub : IRealtimeMarketFeed
{
    private sealed record TradePoint(DateTime Time,decimal Quantity,bool Buy);
    private sealed class SymbolState
    {
        public readonly object Gate=new();public readonly Queue<TradePoint> Trades=new();public decimal LastPrice,PreviousMinutePrice,BestBid,BestAsk,BidQuantity,AskQuantity,LastMinuteVolume;public DateTime UpdatedAt;public long Messages;
    }
    private readonly IReadOnlyList<string> _symbols;private readonly string _apiKey;private readonly AgentSqliteStore _db;private readonly ConcurrentDictionary<string,SymbolState> _states=new(StringComparer.OrdinalIgnoreCase);private readonly Channel<string> _triggers=Channel.CreateBounded<string>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest});private CancellationTokenSource? _cts;private Task? _marketTask,_userTask;private volatile bool _marketConnected,_userConnected;private string _status="STOPPED";
    public RealTimeMarketHub(ExchangeEnvironment environment,IEnumerable<string> symbols,string apiKey,AgentSqliteStore db)
    {
        if(environment!=ExchangeEnvironment.Testnet)throw new InvalidOperationException("Realtime hub is Testnet-only.");_symbols=symbols.Select(x=>x.ToUpperInvariant()).Distinct().ToArray();_apiKey=apiKey;_db=db;foreach(var symbol in _symbols)_states[symbol]=new();
    }
    public string Status=>_status;public bool Healthy=>_marketConnected&&_userConnected&&_states.Values.All(x=>DateTime.UtcNow-x.UpdatedAt<TimeSpan.FromSeconds(30));
    public async Task StartAsync(CancellationToken ct)
    {
        if(_cts is not null)return;var runCts=CancellationTokenSource.CreateLinkedTokenSource(ct);_cts=runCts;_marketTask=Task.Run(()=>MarketLoopAsync(runCts.Token),runCts.Token);_userTask=Task.Run(()=>UserLoopAsync(runCts.Token),runCts.Token);await Task.Yield();
    }
    public async Task<string?> WaitForTriggerAsync(TimeSpan timeout,CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);linked.CancelAfter(timeout);try{return await _triggers.Reader.ReadAsync(linked.Token);}catch(OperationCanceledException)when(!ct.IsCancellationRequested){return null;}
    }
    public RealtimeMarketSnapshot? GetSnapshot(string symbol)
    {
        if(!_states.TryGetValue(symbol,out var state))return null;lock(state.Gate){Prune(state);var buy=state.Trades.Where(x=>x.Buy).Sum(x=>x.Quantity);var sell=state.Trades.Where(x=>!x.Buy).Sum(x=>x.Quantity);return new(symbol,state.LastPrice,state.BestBid,state.BestAsk,state.BidQuantity,state.AskQuantity,buy,sell,state.LastMinuteVolume,state.UpdatedAt,state.Messages,_marketConnected);}
    }
    public MarketEvidence Enrich(MarketEvidence market)
    {
        var live=GetSnapshot(market.Symbol);if(live is null||!live.Fresh)return market;var flow=live.OrderFlowImbalance;var spread=live.SpreadBps;var liquidity=Math.Clamp((1-Math.Min(1,spread/20))*.45+Math.Min(1,(double)((live.BidQuantity+live.AskQuantity)*live.LastPrice/1_000_000m))*.25+Math.Min(1,(double)(live.BuyVolume5m+live.SellVolume5m)/1000)*.30,0,1);var anomalies=market.Quality.Anomalies.Where(x=>x!="book_ticker_missing"&&x!="order_book_missing").ToArray();var quality=new MarketQualityEvidence{BestBid=live.BestBid,BestAsk=live.BestAsk,SpreadBps=spread,OrderBookImbalance=flow,AtrPercent=market.Quality.AtrPercent,RealizedVolatility=market.Quality.RealizedVolatility,RelativeVolume=market.Quality.RelativeVolume,LiquidityScore=Math.Max(market.Quality.LiquidityScore,liquidity),LiquidationIntensity=market.Quality.LiquidationIntensity,ClockSkewMilliseconds=market.Quality.ClockSkewMilliseconds,SourceCount=market.Quality.SourceCount+1,QualityScore=Math.Min(100,market.Quality.QualityScore+5),Anomalies=anomalies};return market with{Price=live.LastPrice>0?live.LastPrice:market.Price,CollectedAt=live.UpdatedAt,Quality=quality};
    }
    private async Task MarketLoopAsync(CancellationToken ct)
    {
        var attempt=0;while(!ct.IsCancellationRequested)try
        {
            using var socket=new ClientWebSocket();var streams=string.Join('/',_symbols.SelectMany(s=>new[]{$"{s.ToLowerInvariant()}@bookTicker",$"{s.ToLowerInvariant()}@aggTrade",$"{s.ToLowerInvariant()}@kline_1m"}));await socket.ConnectAsync(new Uri($"wss://stream.binancefuture.com/stream?streams={streams}"),ct);_marketConnected=true;_status="MARKET_CONNECTED";attempt=0;await Audit("CONNECTION","MARKET","CONNECTED","Testnet market stream connected",string.Empty,ct);await ReceiveAsync(socket,HandleMarketAsync,ct);
        }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception ex){_marketConnected=false;_status="MARKET_RECONNECTING";await Audit("CONNECTION","MARKET","RETRY",ex.Message,string.Empty,CancellationToken.None);await Task.Delay(Backoff(++attempt),ct);}
    }
    private async Task UserLoopAsync(CancellationToken ct)
    {
        var attempt=0;while(!ct.IsCancellationRequested)try
        {
            var listenKey=await CreateListenKeyAsync(ct);using var socket=new ClientWebSocket();await socket.ConnectAsync(new Uri($"wss://stream.binancefuture.com/ws/{listenKey}"),ct);_userConnected=true;attempt=0;await Audit("CONNECTION","ACCOUNT","CONNECTED","Testnet user stream connected",string.Empty,ct);using var keepAlive=CancellationTokenSource.CreateLinkedTokenSource(ct);var keeper=Task.Run(()=>KeepAliveLoopAsync(listenKey,keepAlive.Token),keepAlive.Token);try{await ReceiveAsync(socket,HandleUserAsync,ct);}finally{keepAlive.Cancel();try{await keeper;}catch(OperationCanceledException){}}
        }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception ex){_userConnected=false;await Audit("CONNECTION","ACCOUNT","RETRY",ex.Message,string.Empty,CancellationToken.None);await Task.Delay(Backoff(++attempt),ct);}
    }
    private static async Task ReceiveAsync(ClientWebSocket socket,Func<string,CancellationToken,Task> handler,CancellationToken ct)
    {
        var buffer=new byte[64*1024];while(socket.State==WebSocketState.Open&&!ct.IsCancellationRequested){using var stream=new MemoryStream();WebSocketReceiveResult result;do{result=await socket.ReceiveAsync(buffer,ct);if(result.MessageType==WebSocketMessageType.Close)throw new WebSocketException("remote close");stream.Write(buffer,0,result.Count);}while(!result.EndOfMessage);await handler(Encoding.UTF8.GetString(stream.ToArray()),ct);}
    }
    internal async Task HandleMarketAsync(string json,CancellationToken ct)
    {
        using var document=JsonDocument.Parse(json);var data=document.RootElement.TryGetProperty("data",out var combined)?combined:document.RootElement;var eventType=S(data,"e");var symbol=S(data,"s");if(string.IsNullOrWhiteSpace(symbol)||!_states.TryGetValue(symbol,out var state))return;lock(state.Gate)
        {
            state.Messages++;state.UpdatedAt=DateTime.UtcNow;if(eventType=="bookTicker"){state.BestBid=D(data,"b");state.BestAsk=D(data,"a");state.BidQuantity=D(data,"B");state.AskQuantity=D(data,"A");}
            else if(eventType=="aggTrade"){var price=D(data,"p");var quantity=D(data,"q");var maker=data.TryGetProperty("m",out var m)&&m.GetBoolean();if(price>0)state.LastPrice=price;state.Trades.Enqueue(new(DateTime.UtcNow,quantity,!maker));Prune(state);}
            else if(eventType=="kline"&&data.TryGetProperty("k",out var k)){var close=D(k,"c");var closed=k.TryGetProperty("x",out var x)&&x.GetBoolean();state.LastMinuteVolume=D(k,"v");if(close>0)state.LastPrice=close;if(closed){var move=state.PreviousMinutePrice>0?Math.Abs((close-state.PreviousMinutePrice)/state.PreviousMinutePrice):0;state.PreviousMinutePrice=close;_triggers.Writer.TryWrite(move>=.005m?$"volatility:{symbol}":$"minute_close:{symbol}");}}
        }await Task.CompletedTask;
    }
    private async Task HandleUserAsync(string json,CancellationToken ct)
    {
        using var document=JsonDocument.Parse(json);var root=document.RootElement;var type=S(root,"e");if(type is not("ACCOUNT_UPDATE" or "ORDER_TRADE_UPDATE" or "MARGIN_CALL"))return;var symbol="ACCOUNT";var status=type;if(type=="ORDER_TRADE_UPDATE"&&root.TryGetProperty("o",out var order)){symbol=S(order,"s");status=S(order,"X");}var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..20];await Audit(type,symbol,status,$"{type} {symbol} {status}",hash,ct);_triggers.Writer.TryWrite($"account:{type}:{symbol}");
    }
    private async Task<string> CreateListenKeyAsync(CancellationToken ct){using var http=Client();using var request=new HttpRequestMessage(HttpMethod.Post,"https://testnet.binancefuture.com/fapi/v1/listenKey");using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));return json.RootElement.GetProperty("listenKey").GetString()??throw new JsonException("listenKey missing");}
    private async Task KeepAliveLoopAsync(string listenKey,CancellationToken ct){while(!ct.IsCancellationRequested){await Task.Delay(TimeSpan.FromMinutes(45),ct);using var http=Client();using var request=new HttpRequestMessage(HttpMethod.Put,$"https://testnet.binancefuture.com/fapi/v1/listenKey?listenKey={Uri.EscapeDataString(listenKey)}");using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();}}
    private HttpClient Client(){var client=new HttpClient{Timeout=TimeSpan.FromSeconds(15)};client.DefaultRequestHeaders.Add("X-MBX-APIKEY",_apiKey);client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WPE-Agent","2.0"));return client;}
    private Task Audit(string type,string symbol,string status,string summary,string hash,CancellationToken ct)=>_db.RecordRealtimeEventAsync(new(
        SensitiveDataRedactor.ForLog(type,80),SensitiveDataRedactor.ForLog(symbol,80),SensitiveDataRedactor.ForLog(status,80),SensitiveDataRedactor.ForLog(summary,240),DateTime.UtcNow,SensitiveDataRedactor.ForLog(hash,120)),ct);
    private static void Prune(SymbolState state){var cutoff=DateTime.UtcNow.AddMinutes(-5);while(state.Trades.Count>0&&state.Trades.Peek().Time<cutoff)state.Trades.Dequeue();}
    private static TimeSpan Backoff(int attempt)=>TimeSpan.FromSeconds(Math.Min(60,Math.Pow(2,Math.Min(5,attempt))));private static string S(JsonElement e,string name)=>e.TryGetProperty(name,out var p)?p.GetString()??string.Empty:string.Empty;private static decimal D(JsonElement e,string name)=>e.TryGetProperty(name,out var p)&&decimal.TryParse(p.GetString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var value)?value:0;
    public async ValueTask DisposeAsync(){var runCts=Interlocked.Exchange(ref _cts,null);if(runCts is null)return;runCts.Cancel();try{if(_marketTask is not null)await _marketTask;if(_userTask is not null)await _userTask;}catch(OperationCanceledException){}finally{runCts.Dispose();_marketConnected=false;_userConnected=false;_status="STOPPED";}}
}

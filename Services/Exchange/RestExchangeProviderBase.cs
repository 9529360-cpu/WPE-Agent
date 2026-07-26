using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange;

public abstract class RestExchangeProviderBase:IExchangeProvider,IMarketDataProvider,IBrokerProvider,IProviderEnvironmentGuard
{
    protected readonly ExchangeConnectionProfile Profile;
    protected readonly HttpClient Http;
    protected readonly ProviderCallPolicy Policy;
    public ExchangeEnvironment Environment=>Profile.IsTestnet?ExchangeEnvironment.Testnet:ExchangeEnvironment.Mainnet;
    public string ConnectionId=>Profile.Id;
    public abstract string ProviderId{get;}
    public abstract ExchangeProviderDescriptor Descriptor{get;}
    public ISymbolMapper Symbols{get;}
    public IMarketDataProvider MarketData=>this;
    public IBrokerProvider Broker=>this;

    public abstract ProviderEnvironmentValidation ValidateEnvironment(bool requireTestnet);

    protected RestExchangeProviderBase(ExchangeConnectionProfile profile)
    {
        Profile=profile;Symbols=new ConventionSymbolMapper(profile.ProviderId,profile.SymbolMappings);Policy=new ProviderCallPolicy(profile.TimeoutSeconds,2);
        var handler=new HttpClientHandler();if(profile.UseProxy&&Uri.TryCreate(profile.ProxyUrl,UriKind.Absolute,out var proxy)){handler.Proxy=new WebProxy(proxy);handler.UseProxy=true;}
        Http=new HttpClient(handler){BaseAddress=new Uri(profile.Endpoint.TrimEnd('/')+"/"),Timeout=Timeout.InfiniteTimeSpan};
    }

    public async Task<bool> PingAsync(CancellationToken ct){_ =await GetServerTimeAsync(ct);return true;}
    public abstract Task<DateTime> GetServerTimeAsync(CancellationToken ct);
    public abstract Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct);
    public async Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct)
    {
        var started=DateTime.UtcNow;var sw=System.Diagnostics.Stopwatch.StartNew();try{var server=await GetServerTimeAsync(ct);var skew=(long)Math.Abs((DateTime.UtcNow-server).TotalMilliseconds);var permission=await CheckPermissionsAsync(ct);return new(permission.CanRead,sw.ElapsedMilliseconds,skew,$"{Descriptor.DisplayName} · read={permission.CanRead} trade={permission.CanTrade}",started);}catch(Exception ex){return new(false,sw.ElapsedMilliseconds,0,Safe(ex.Message),started);}
    }
    public IRealtimeMarketFeed? CreateRealtimeFeed(IEnumerable<string> canonicalSymbols,AgentSqliteStore database)=>null;
    public abstract Task<AccountSnapshot> GetAccountAsync(CancellationToken ct);
    public async Task<MarginSnapshot> GetMarginAsync(CancellationToken ct){var a=await GetAccountAsync(ct);var used=Math.Max(0,a.Equity-a.AvailableBalance);return new(a.WalletBalance,a.AvailableBalance,used,0,a.Equity>0?used/a.Equity:0);}
    public abstract Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct);
    public abstract Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? canonicalSymbol,CancellationToken ct);
    public abstract Task<TradingRule> GetRulesAsync(string canonicalSymbol,CancellationToken ct);
    public abstract Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string canonicalSymbol,string interval,int limit,CancellationToken ct);
    public abstract Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string canonicalSymbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct);
    protected abstract Task<DerivativesSnapshot> GetDerivativesAsync(string canonicalSymbol,CancellationToken ct);
    public async Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string canonicalSymbol,CancellationToken ct)=>[await GetDerivativesAsync(canonicalSymbol,ct)];
    public async Task<MarketEvidence> GetMarketAsync(string canonicalSymbol,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(canonicalSymbol);var observed=DateTime.UtcNow;var c15=ConfirmedMarketCandlesV1.Select(await GetCandlesAsync(canonical,"15m",240,ct),"15m",observed);var c1=ConfirmedMarketCandlesV1.Select(await GetCandlesAsync(canonical,"1h",120,ct),"1h",observed);var c4=ConfirmedMarketCandlesV1.Select(await GetCandlesAsync(canonical,"4h",90,ct),"4h",observed);if(c15.Count<31||c1.Count<31||c4.Count<31)throw new InvalidOperationException($"{canonical} confirmed market history is incomplete");
        var sourceAt=c15[^1].OpenTime+ConfirmedMarketCandlesV1.Duration("15m");var price=c15[^1].Close;var deriv=await GetDerivativesAsync(canonical,ct);var rsi=Rsi(c15);var age=observed-sourceAt;var returns=c15.Skip(1).Select((x,i)=>c15[i].Close>0?Math.Log((double)(x.Close/c15[i].Close)):0).TakeLast(96).ToArray();var realized=returns.Length>1?Math.Sqrt(returns.Select(x=>(x-returns.Average())*(x-returns.Average())).Average())*Math.Sqrt(96):0;var recent=c15.TakeLast(8).Select(x=>x.QuoteVolume).DefaultIfEmpty().Average();var baseline=c15.TakeLast(96).Select(x=>x.QuoteVolume).DefaultIfEmpty().Average();var relative=baseline>0?(double)(recent/baseline):0;var anomalies=new List<string>();if(age>TimeSpan.FromMinutes(20))anomalies.Add("stale_candles");if(relative<=0)anomalies.Add("volume_unavailable");var quality=new MarketQualityEvidence{RealizedVolatility=realized,RelativeVolume=relative,LiquidityScore=Math.Clamp(relative/2,.25,1),SourceCount=3,QualityScore=anomalies.Count==0?88:62,Anomalies=anomalies};
        return new(canonical,price,c15.TakeLast(40).Min(x=>x.Low),c15.TakeLast(40).Max(x=>x.High),rsi,Trend(c15,10),Trend(c1,10),Trend(c4,10),deriv,sourceAt){Candles=c15,Quality=quality};
    }
    public abstract Task SetLeverageAsync(string canonicalSymbol,int leverage,CancellationToken ct);
    public abstract Task SetMarginModeAsync(string canonicalSymbol,bool isolated,CancellationToken ct);
    public abstract Task SetHedgeModeAsync(bool enabled,CancellationToken ct);
    public abstract Task<ExchangeOrder> PlaceMarketAsync(string canonicalSymbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct);
    public abstract Task<ExchangeOrder> PlaceLimitAsync(string canonicalSymbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct);
    public abstract Task<ExchangeOrder> PlaceProtectionAsync(string canonicalSymbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct);
    public abstract Task<ExchangeOrder?> FindOrderAsync(string canonicalSymbol,string clientOrderId,CancellationToken ct);
    public abstract Task CancelOrderAsync(string canonicalSymbol,string orderId,CancellationToken ct);
    public virtual ValueTask DisposeAsync(){Http.Dispose();return ValueTask.CompletedTask;}

    protected async Task<JsonDocument> SendAsync(Func<HttpRequestMessage> requestFactory,Func<JsonElement,string?> error,CancellationToken ct)
    {
        var environment=ValidateEnvironment(Profile.IsTestnet);
        if(!environment.CanRead)throw new InvalidOperationException(environment.Failure??"Provider endpoint is not allowed.");
        return await Policy.ExecuteAsync(async token=>
        {
            using var request=requestFactory();using var response=await Http.SendAsync(request,token);var payload=await response.Content.ReadAsStringAsync(token);if(!response.IsSuccessStatusCode)throw new HttpRequestException($"{ProviderId} HTTP {(int)response.StatusCode}: {Safe(payload)}",null,response.StatusCode);var document=JsonDocument.Parse(payload);var message=error(document.RootElement);if(message is not null){document.Dispose();throw new InvalidOperationException($"{ProviderId}: {Safe(message)}");}return document;
        },ct);
    }
    protected static HttpRequestMessage JsonRequest(HttpMethod method,string path,string? body=null){var request=new HttpRequestMessage(method,path);if(body is not null)request.Content=new StringContent(body,Encoding.UTF8,"application/json");return request;}
    protected static string Query(IEnumerable<KeyValuePair<string,string?>> values)=>string.Join("&",values.Where(x=>x.Value is not null).Select(x=>$"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
    protected static decimal Dec(JsonElement e,string name){if(!e.TryGetProperty(name,out var value))return 0;return decimal.TryParse(value.ValueKind==JsonValueKind.String?value.GetString():value.GetRawText(),NumberStyles.Any,CultureInfo.InvariantCulture,out var result)?result:0;}
    protected static string Str(JsonElement e,string name)=>e.TryGetProperty(name,out var value)?value.ValueKind==JsonValueKind.String?value.GetString()??string.Empty:value.GetRawText():string.Empty;
    protected static DateTime Millis(string value)=>long.TryParse(value,out var ms)?DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime:DateTime.UtcNow;
    protected static string F(decimal value)=>value.ToString(CultureInfo.InvariantCulture);
    protected static string Safe(string value)=>global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(value,240);
    private static double Trend(IReadOnlyList<CandleEvidence> candles,int lookback)
    {
        var referenceIndex=candles.Count-1-lookback;
        return referenceIndex>=0&&candles[referenceIndex].Close>0
            ?(double)(candles[^1].Close/candles[referenceIndex].Close-1)
            :0;
    }
    private static double Rsi(IReadOnlyList<CandleEvidence> candles){var changes=candles.TakeLast(15).Zip(candles.TakeLast(15).Skip(1),(a,b)=>(double)(b.Close-a.Close)).ToArray();var gain=changes.Select(x=>Math.Max(0,x)).DefaultIfEmpty().Average();var loss=changes.Select(x=>Math.Max(0,-x)).DefaultIfEmpty().Average();return loss==0?100:100-100/(1+gain/loss);}
}

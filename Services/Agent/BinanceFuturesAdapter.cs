using System.Globalization;
using System.Text.Json;
using System.Net.Http;
using System.Collections.Concurrent;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services.Agent;

public sealed class BinanceFuturesAdapter : IExchangeAdapter
{
    private readonly BinanceApiClient _api;
    private readonly Dictionary<string,TradingRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long,byte> _algoOrderIds = new();
    public ExchangeEnvironment Environment { get; }
    public BinanceFuturesAdapter(ExchangeEnvironment environment, string key, string secret,EnvironmentSlot? options=null)
    {
        Environment=environment;options??=new EnvironmentSlot{ApiBaseUrl=environment==ExchangeEnvironment.Testnet?"https://testnet.binancefuture.com":"https://fapi.binance.com"}; _api=new BinanceApiClient(useTestnet:environment==ExchangeEnvironment.Testnet,endpoint:options.ApiBaseUrl,timeoutSeconds:options.TimeoutSeconds,useProxy:options.UseProxy,proxyUrl:options.ProxyUrl,receiveWindow:options.ReceiveWindow); _api.SetApiCredentials(key,secret);
    }
    public async Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) { var b=(await _api.GetAccountBalancesAsync(ct)).FirstOrDefault(x=>x.Asset=="USDT"); return new(b?.WalletBalance??0,b?.AvailableBalance??0,b?.MarginBalance??0,DateTime.UtcNow); }
    public async Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => (await _api.GetPositionsAsync(ct)).Select(x=>new ManagedPosition(x.Symbol, ParseSide(x),Math.Abs(x.PositionAmt),x.EntryPrice,x.MarkPrice,x.UnrealizedProfit,x.Leverage,x.IsIsolated,x.LiquidationPrice)).ToArray();
    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)
    {
        var standard=(await _api.GetOpenOrdersAsync(symbol,ct)).Select(Map);
        var query=string.IsNullOrWhiteSpace(symbol)?null:new Dictionary<string,string?>{{"symbol",symbol}};
        using var document=JsonDocument.Parse(await _api.GetSignedRawAsync("/fapi/v1/openAlgoOrders",query,ct));
        var algos=document.RootElement.EnumerateArray().Select(MapAlgo).ToArray();
        foreach(var algo in algos)_algoOrderIds[algo.OrderId]=0;
        return standard.Concat(algos).ToArray();
    }
    public async Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)
    {
        if (_rules.TryGetValue(symbol,out var cached)) return cached;
        using var d=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/exchangeInfo",null,ct));
        var s=d.RootElement.GetProperty("symbols").EnumerateArray().First(x=>x.GetProperty("symbol").GetString()==symbol);
        decimal step=0,tick=0,minQty=0,minNotional=5;
        foreach(var f in s.GetProperty("filters").EnumerateArray()) { var t=f.GetProperty("filterType").GetString(); if(t=="LOT_SIZE"){step=D(f,"stepSize");minQty=D(f,"minQty");} else if(t=="PRICE_FILTER")tick=D(f,"tickSize"); else if(t=="MIN_NOTIONAL")minNotional=D(f,"notional"); }
        return _rules[symbol]=new(symbol,step,tick,minQty,minNotional,125);
    }
    public async Task<MarketEvidence> GetMarketAsync(string symbol,CancellationToken ct)
    {
        var s15=await Observe(symbol,"15m",ct); var s1=await Observe(symbol,"1h",ct); var s4=await Observe(symbol,"4h",ct);
        var deriv=await GetDerivatives(symbol,ct);
        var candles=await GetCandlesAsync(symbol,"15m",240,ct);
        var quality=await GetMarketQualityAsync(symbol,candles,ct);
        return new(symbol,s15.Price,s15.Support,s15.Resistance,s15.Rsi,s15.ShortTrend,s1.ShortTrend,s4.ShortTrend,deriv,DateTime.UtcNow){Candles=candles,Quality=quality};
    }
    public async Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol,CancellationToken ct)=>[await GetDerivatives(symbol,ct)];
    public async Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)
    {
        using var document=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/klines",new Dictionary<string,string?>{{"symbol",symbol},{"interval",interval},{"limit",Math.Clamp(limit,20,1000).ToString(CultureInfo.InvariantCulture)}},ct));
        return document.RootElement.EnumerateArray().Select(k=>new CandleEvidence(
            DateTimeOffset.FromUnixTimeMilliseconds(k[0].GetInt64()).UtcDateTime,
            Decimal(k[1]),Decimal(k[2]),Decimal(k[3]),Decimal(k[4]),Decimal(k[5]),Decimal(k[7]),k[8].GetInt64(),Decimal(k[9]))).ToArray();
    }
    public async Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)
    {
        var query=new Dictionary<string,string?>{{"symbol",symbol},{"interval",interval},{"limit",Math.Clamp(limit,20,1000).ToString(CultureInfo.InvariantCulture)},{"startTime",new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)},{"endTime",new DateTimeOffset(end.ToUniversalTime()).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}};
        using var document=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/klines",query,ct));return document.RootElement.EnumerateArray().Select(k=>new CandleEvidence(DateTimeOffset.FromUnixTimeMilliseconds(k[0].GetInt64()).UtcDateTime,Decimal(k[1]),Decimal(k[2]),Decimal(k[3]),Decimal(k[4]),Decimal(k[5]),Decimal(k[7]),k[8].GetInt64(),Decimal(k[9]))).ToArray();
    }
    public async Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct)=>_ = await _api.PostSignedRawAsync("/fapi/v1/leverage",new Dictionary<string,string?>{{"symbol",symbol},{"leverage",leverage.ToString()}},ct);
    public async Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct) { try { _=await _api.PostSignedRawAsync("/fapi/v1/marginType",new Dictionary<string,string?>{{"symbol",symbol},{"marginType",isolated?"ISOLATED":"CROSSED"}},ct); } catch(HttpRequestException ex) when(ex.Message.Contains("400")){} }
    public async Task SetHedgeModeAsync(bool enabled,CancellationToken ct) { try { _=await _api.PostSignedRawAsync("/fapi/v1/positionSide/dual",new Dictionary<string,string?>{{"dualSidePosition",enabled?"true":"false"}},ct); } catch(HttpRequestException ex) when(ex.Message.Contains("400")){} }
    public async Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)
    {
        // Hedge mode closes a leg by sending the opposite order side while keeping
        // positionSide on the leg being reduced. Binance rejects reduceOnly in hedge mode.
        var orderSide=side==PositionSide.Long?OrderSide.Buy:OrderSide.Sell;
        if(reduceOnly)orderSide=orderSide==OrderSide.Buy?OrderSide.Sell:OrderSide.Buy;
        var o=await _api.PlaceOrderAsync(new OrderRequest{Symbol=symbol,Side=orderSide,Type=OrderType.Market,Quantity=quantity,ClientOrderId=clientOrderId,PositionSide=side==PositionSide.Long?"LONG":"SHORT"},ct); return Map(o);
    }
    public async Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)
    {
        var orderSide=side==PositionSide.Long?OrderSide.Buy:OrderSide.Sell;if(reduceOnly)orderSide=orderSide==OrderSide.Buy?OrderSide.Sell:OrderSide.Buy;
        var o=await _api.PlaceOrderAsync(new OrderRequest{Symbol=symbol,Side=orderSide,Type=OrderType.Limit,Quantity=quantity,Price=price,TimeInForce=TimeInForce.Gtc,ClientOrderId=clientOrderId,PositionSide=side==PositionSide.Long?"LONG":"SHORT"},ct);return Map(o);
    }
    public async Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)
    {
        var closeSide=sideToClose==PositionSide.Long?OrderSide.Sell:OrderSide.Buy; var ps=sideToClose==PositionSide.Long?"LONG":"SHORT";
        var existing=await GetOpenOrdersAsync(symbol,ct);
        var sl=existing.FirstOrDefault(x=>x.PositionSide==sideToClose&&x.Type=="STOP_MARKET");
        var tp=existing.FirstOrDefault(x=>x.PositionSide==sideToClose&&x.Type=="TAKE_PROFIT_MARKET");
        var createdSl=false;
        if(sl is null){sl=await PlaceAlgoProtectionAsync(symbol,closeSide,ps,"STOP_MARKET",stopLoss,ProtectionId(groupId,"SL"),ct);createdSl=true;}
        try { if(tp is null)_=await PlaceAlgoProtectionAsync(symbol,closeSide,ps,"TAKE_PROFIT_MARKET",takeProfit,ProtectionId(groupId,"TP"),ct); }
        catch { if(createdSl)await CancelOrderAsync(symbol,sl.OrderId,ct); throw; }
        return sl with { IsProtection=true };
    }
    public async Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct) { var q=new Dictionary<string,string?>{{"symbol",symbol},{"origClientOrderId",clientOrderId}}; try { using var d=JsonDocument.Parse(await _api.GetSignedRawAsync("/fapi/v1/order",q,ct)); return MapRaw(d.RootElement); } catch(HttpRequestException){return null;} }
    public async Task CancelOrderAsync(string symbol,long id,CancellationToken ct)
    {
        if(_algoOrderIds.TryRemove(id,out _))
            _=await _api.DeleteSignedRawAsync("/fapi/v1/algoOrder",new Dictionary<string,string?>{{"algoId",id.ToString(CultureInfo.InvariantCulture)}},ct);
        else
            _=await _api.CancelOrderAsync(symbol,id,ct);
    }
    public ValueTask DisposeAsync(){_api.Dispose();return ValueTask.CompletedTask;}

    private async Task<MarketSkillSnapshot> Observe(string symbol,string interval,CancellationToken ct)=>await new MarketStructureSkill(_api).ObserveAsync(symbol,interval,ct);
    private async Task<DerivativesSnapshot> GetDerivatives(string symbol,CancellationToken ct)
    {
        async Task<decimal> Last(string path,string property){using var d=JsonDocument.Parse(await _api.GetPublicRawAsync(path,new Dictionary<string,string?>{{"symbol",symbol},{"period","15m"},{"limit","2"}},ct));var a=d.RootElement;var e=a.ValueKind==JsonValueKind.Array?a.EnumerateArray().Last():a;return e.TryGetProperty(property,out var p)&&decimal.TryParse(p.GetString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var v)?v:0;}
        var funding=(decimal)((await _api.GetFundingRatesAsync(symbol,2,ct)).FirstOrDefault()?.LastFundingRate??0d);
        decimal oi=0,basis=0; try{using var d=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/openInterest",new Dictionary<string,string?>{{"symbol",symbol}},ct));oi=D(d.RootElement,"openInterest");}catch{} try{using var d=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/premiumIndex",new Dictionary<string,string?>{{"symbol",symbol}},ct));var mark=D(d.RootElement,"markPrice");var index=D(d.RootElement,"indexPrice");basis=index>0?(mark-index)/index:0;}catch{}
        decimal ls=0,ta=0,tp=0,taker=0; try{ls=await Last("/futures/data/globalLongShortAccountRatio","longShortRatio");}catch{} try{ta=await Last("/futures/data/topLongShortAccountRatio","longShortRatio");}catch{} try{tp=await Last("/futures/data/topLongShortPositionRatio","longShortRatio");}catch{} try{taker=await Last("/futures/data/takerlongshortRatio","buySellRatio");}catch{}
        return new(funding,oi,ls,ta,tp,taker,basis);
    }
    private async Task<MarketQualityEvidence> GetMarketQualityAsync(string symbol,IReadOnlyList<CandleEvidence> candles,CancellationToken ct)
    {
        var anomalies=new List<string>();decimal bid=0,ask=0,bidDepth=0,askDepth=0;long skew=0;double liquidationIntensity=0;var sources=1;
        try{using var d=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/ticker/bookTicker",new Dictionary<string,string?>{{"symbol",symbol}},ct));bid=D(d.RootElement,"bidPrice");ask=D(d.RootElement,"askPrice");sources++;}catch{anomalies.Add("book_ticker_missing");}
        try{using var d=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/depth",new Dictionary<string,string?>{{"symbol",symbol},{"limit","20"}},ct));bidDepth=d.RootElement.GetProperty("bids").EnumerateArray().Sum(x=>Decimal(x[1]));askDepth=d.RootElement.GetProperty("asks").EnumerateArray().Sum(x=>Decimal(x[1]));sources++;}catch{anomalies.Add("order_book_missing");}
        try{using var d=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/time",null,ct));skew=d.RootElement.GetProperty("serverTime").GetInt64()-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();sources++;}catch{anomalies.Add("server_time_missing");}
        try{using var d=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/allForceOrders",new Dictionary<string,string?>{{"symbol",symbol},{"limit","50"}},ct));liquidationIntensity=d.RootElement.ValueKind==JsonValueKind.Array?Math.Min(1,d.RootElement.GetArrayLength()/50d):0;sources++;}catch{anomalies.Add("liquidation_feed_missing");}
        var spread=bid>0&&ask>=bid?(double)((ask-bid)/((ask+bid)/2)*10000):999;
        var imbalance=bidDepth+askDepth>0?(double)((bidDepth-askDepth)/(bidDepth+askDepth)):0;
        var trs=candles.Skip(1).Select((x,i)=>Math.Max(x.High-x.Low,Math.Max(Math.Abs(x.High-candles[i].Close),Math.Abs(x.Low-candles[i].Close)))).TakeLast(30).ToArray();
        var atr=trs.Length>0&&candles[^1].Close>0?(double)(trs.Average()/candles[^1].Close):0;
        var returns=candles.Skip(1).Select((x,i)=>candles[i].Close>0?Math.Log((double)(x.Close/candles[i].Close)):0).TakeLast(96).ToArray();
        var realized=returns.Length>1?Math.Sqrt(returns.Select(x=>(x-returns.Average())*(x-returns.Average())).Average())*Math.Sqrt(96):0;
        var recentVolume=candles.TakeLast(8).Select(x=>x.QuoteVolume).DefaultIfEmpty().Average();var baseVolume=candles.TakeLast(96).Select(x=>x.QuoteVolume).DefaultIfEmpty().Average();var relative=baseVolume>0?(double)(recentVolume/baseVolume):0;
        var depthNotional=(bidDepth*bid+askDepth*ask);var liquidity=Math.Clamp((double)(depthNotional/1_000_000m)*.35+Math.Max(0,1-spread/20)*.45+Math.Min(1,relative)*.20,0,1);
        if(spread>10)anomalies.Add("wide_spread");if(Math.Abs(skew)>2000)anomalies.Add("clock_skew");if(candles.Count<100)anomalies.Add("insufficient_candles");if(atr<=0)anomalies.Add("volatility_unavailable");
        var score=(int)Math.Round(Math.Clamp(sources/5d*.35+Math.Max(0,1-spread/20)*.20+liquidity*.25+(Math.Abs(skew)<=2000?1:0)*.10+(candles.Count>=100?1:0)*.10,0,1)*100);
        return new(){BestBid=bid,BestAsk=ask,SpreadBps=spread,OrderBookImbalance=imbalance,AtrPercent=atr,RealizedVolatility=realized,RelativeVolume=relative,LiquidityScore=liquidity,LiquidationIntensity=liquidationIntensity,ClockSkewMilliseconds=skew,SourceCount=sources,QualityScore=score,Anomalies=anomalies};
    }
    private async Task<ExchangeOrder> PlaceAlgoProtectionAsync(string symbol,OrderSide side,string positionSide,string type,decimal triggerPrice,string clientAlgoId,CancellationToken ct)
    {
        var query=new Dictionary<string,string?>
        {
            ["algoType"]="CONDITIONAL",
            ["symbol"]=symbol,
            ["side"]=side==OrderSide.Buy?"BUY":"SELL",
            ["type"]=type,
            ["triggerPrice"]=triggerPrice.ToString(CultureInfo.InvariantCulture),
            ["closePosition"]="true",
            ["positionSide"]=positionSide,
            ["workingType"]="MARK_PRICE",
            ["clientAlgoId"]=clientAlgoId
        };
        using var document=JsonDocument.Parse(await _api.PostSignedRawAsync("/fapi/v1/algoOrder",query,ct));
        var order=MapAlgo(document.RootElement);_algoOrderIds[order.OrderId]=0;return order;
    }
    private static decimal D(JsonElement e,string n)=>e.TryGetProperty(n,out var p)&&decimal.TryParse(p.GetString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var v)?v:0;
    private static decimal Decimal(JsonElement e)=>decimal.TryParse(e.GetString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var v)?v:0;
    private static PositionSide ParseSide(PositionSnapshot p)=>p.PositionSide.Equals("SHORT",StringComparison.OrdinalIgnoreCase)||p.PositionAmt<0?PositionSide.Short:PositionSide.Long;
    private static ExchangeOrder Map(OrderResponse o){var type=o.Type.ToUpperInvariant();return new(o.Symbol,o.OrderId,o.ClientOrderId,o.Status,o.ExecutedQuantity,o.AvgPrice,type,ParseSide(o.PositionSide),type is "STOP_MARKET" or "TAKE_PROFIT_MARKET",o.Time);}
    private static ExchangeOrder MapRaw(JsonElement e){var type=S(e,"type").ToUpperInvariant();return new(S(e,"symbol"),e.GetProperty("orderId").GetInt64(),S(e,"clientOrderId"),S(e,"status"),D(e,"executedQty"),D(e,"avgPrice"),type,ParseSide(S(e,"positionSide")),type is "STOP_MARKET" or "TAKE_PROFIT_MARKET",DateTime.UtcNow);}
    private static ExchangeOrder MapAlgo(JsonElement e){var type=S(e,"orderType").ToUpperInvariant();var updated=e.TryGetProperty("updateTime",out var u)&&u.TryGetInt64(out var ms)?DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime:DateTime.UtcNow;return new(S(e,"symbol"),e.GetProperty("algoId").GetInt64(),S(e,"clientAlgoId"),S(e,"algoStatus"),D(e,"actualQty"),D(e,"actualPrice"),type,ParseSide(S(e,"positionSide")),true,updated);}
    private static PositionSide? ParseSide(string? value)=>value?.ToUpperInvariant() switch{"LONG"=>PositionSide.Long,"SHORT"=>PositionSide.Short,_=>null};
    private static string S(JsonElement e,string n)=>e.TryGetProperty(n,out var p)?p.GetString()??"":"";
    private static string ProtectionId(string groupId,string suffix){var max=36-suffix.Length-1;var stem=groupId.Length>max?groupId[..max]:groupId;return $"{stem}-{suffix}";}
}

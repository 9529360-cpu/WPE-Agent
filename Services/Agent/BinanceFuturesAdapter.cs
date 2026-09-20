using System.Globalization;
using System.Text.Json;
using System.Net.Http;
using System.Collections.Concurrent;
using 币安量化机器人.Models;
using 币安量化机器人.Services.Exchange;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WpeAgent.RuntimeContracts;

namespace 币安量化机器人.Services.Agent;

public sealed class BinanceFuturesAdapter : IExchangeProvider,IMarketDataProvider,IBrokerProvider,IProviderMarketCatalog,IProviderEnvironmentGuard,IProtectionFillEvidenceProvider,IExchangeOrderFeeEvidenceReader,IExchangeFundingIncomeReader,ICryptoInstrumentFundamentalReader
{
    internal const long MaximumTradingClockSkewMilliseconds=1000;
    private readonly BinanceApiClient _api;
    private readonly ExchangeConnectionProfile _profile;
    private readonly string _streamApiKey;
    private readonly Dictionary<string,TradingRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,byte> _algoOrderIds = new();
    public ExchangeEnvironment Environment { get; }
    public string ConnectionId=>_profile.Id;
    public string ProviderId=>"binance-futures";
    public ISymbolMapper Symbols { get; }
    public IMarketDataProvider MarketData=>this;
    public IBrokerProvider Broker=>this;
    public ExchangeProviderDescriptor Descriptor=>BinanceProviderPlugin.ProviderDescriptor;
    public BinanceFuturesAdapter(ExchangeEnvironment environment, string key, string secret,EnvironmentSlot? options=null)
        :this(new ExchangeConnectionProfile{ProviderId="binance-futures",DisplayName=environment==ExchangeEnvironment.Testnet?"Binance Futures Testnet":"Binance Futures",IsTestnet=environment==ExchangeEnvironment.Testnet,Endpoint=options?.ApiBaseUrl??(environment==ExchangeEnvironment.Testnet?"https://testnet.binancefuture.com":"https://fapi.binance.com"),UseProxy=options?.UseProxy??false,ProxyUrl=options?.ProxyUrl??string.Empty,ReceiveWindow=options?.ReceiveWindow??5000,TimeoutSeconds=options?.TimeoutSeconds??20},key,secret)
    { }
    public BinanceFuturesAdapter(ExchangeConnectionProfile profile,string key,string secret)
    {
        _profile=profile;_streamApiKey=key;Environment=profile.IsTestnet?ExchangeEnvironment.Testnet:ExchangeEnvironment.Mainnet;Symbols=new ConventionSymbolMapper(ProviderId,profile.SymbolMappings);_api=new BinanceApiClient(useTestnet:profile.IsTestnet,endpoint:profile.Endpoint,timeoutSeconds:profile.TimeoutSeconds,useProxy:profile.UseProxy,proxyUrl:profile.ProxyUrl,receiveWindow:profile.ReceiveWindow);_api.SetApiCredentials(key,secret);
    }
    public ProviderEnvironmentValidation ValidateEnvironment(bool requireTestnet)
    {
        if(!requireTestnet||Environment!=ExchangeEnvironment.Testnet)return new(false,false,false,"Binance Mainnet execution is not enabled.");
        if(!ProviderEndpointPolicy.IsOfficialHttpsOrigin(_profile.Endpoint,"testnet.binancefuture.com"))return new(false,false,false,"Binance Testnet requires the official https://testnet.binancefuture.com endpoint.");
        return new(true,_profile.ExecutionEnabled,true,_profile.ExecutionEnabled?null:"Execution is disabled for this connection.");
    }
    public async Task<bool> PingAsync(CancellationToken ct){_ = await GetServerTimeAsync(ct);return true;}
    public async Task<DateTime> GetServerTimeAsync(CancellationToken ct){using var d=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/time",null,ct));return DateTimeOffset.FromUnixTimeMilliseconds(d.RootElement.GetProperty("serverTime").GetInt64()).UtcDateTime;}
    public async Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct){var clock=await _api.SynchronizeClockAsync(ct);if(!clock.Trusted)return new(false,false,false,"Binance",[$"Exchange clock is unavailable ({clock.Code})."]);using var d=JsonDocument.Parse(await _api.GetSignedRawAsync("/fapi/v2/account",null,ct));return ParsePermissionSnapshot(d.RootElement);}
    internal static ExchangePermissionSnapshot ParsePermissionSnapshot(JsonElement root){var trade=root.TryGetProperty("canTrade",out var t)&&t.ValueKind==JsonValueKind.True;var accountCanWithdraw=root.TryGetProperty("canWithdraw",out var w)&&w.ValueKind==JsonValueKind.True;var id=root.TryGetProperty("accountAlias",out var a)?a.GetString()??"Binance":"Binance";return new(true,trade,false,id,accountCanWithdraw?["Futures account withdrawal status is not API key permission evidence; Testnet withdrawals remain unavailable."]:Array.Empty<string>());}
    internal static ExchangePermissionSnapshot ApplyClockSkew(ExchangePermissionSnapshot permission,DateTime serverTimeUtc,DateTime observedAtUtc)
    {
        var skew=Math.Abs((observedAtUtc.ToUniversalTime()-serverTimeUtc.ToUniversalTime()).TotalMilliseconds);
        if(skew<=MaximumTradingClockSkewMilliseconds||!permission.CanTrade)return permission;
        return permission with{CanTrade=false,Warnings=permission.Warnings.Concat(["Server clock skew exceeds 1000 ms; trading is disabled."]).ToArray()};
    }
    public Task<BinanceClockMeasurement> GetClockMeasurementAsync(CancellationToken ct)=>_api.SynchronizeClockAsync(ct);
    public DateTimeOffset ExchangeAdjustedUtcNow=>_api.ExchangeAdjustedUtcNow;
    public async Task<ExchangeHealthSnapshot> HealthCheckAsync(CancellationToken ct){var sw=Stopwatch.StartNew();try{var clock=await _api.SynchronizeClockAsync(ct);if(!clock.Trusted)return new(false,sw.ElapsedMilliseconds,clock.UncertaintyMilliseconds,$"{Descriptor.DisplayName} · {clock.Code}",DateTime.UtcNow);var permission=await CheckPermissionsAsync(ct);return new(permission.CanRead&&permission.CanTrade,sw.ElapsedMilliseconds,clock.UncertaintyMilliseconds,$"{Descriptor.DisplayName} · read={permission.CanRead} trade={permission.CanTrade}",_api.ExchangeAdjustedUtcNow.UtcDateTime);}catch(Exception ex){return new(false,sw.ElapsedMilliseconds,0,global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(ex.Message,180),DateTime.UtcNow);}}
    public IRealtimeMarketFeed? CreateRealtimeFeed(IEnumerable<string> canonicalSymbols,AgentSqliteStore database)=>Environment==ExchangeEnvironment.Testnet?new RealTimeMarketHub(Environment,canonicalSymbols.Select(Symbols.ToNative),_streamApiKey,database):null;
    public async Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) { var b=(await _api.GetAccountBalancesAsync(ct)).FirstOrDefault(x=>x.Asset=="USDT"); return new(b?.WalletBalance??0,b?.AvailableBalance??0,b?.MarginBalance??0,DateTime.UtcNow); }
    public async Task<MarginSnapshot> GetMarginAsync(CancellationToken ct){var account=await GetAccountAsync(ct);var used=Math.Max(0,account.Equity-account.AvailableBalance);return new(account.WalletBalance,account.AvailableBalance,used,0,account.Equity>0?used/account.Equity:0);}
    public async Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => (await _api.GetPositionsAsync(ct)).Select(x=>new ManagedPosition(C(x.Symbol), ParseSide(x),Math.Abs(x.PositionAmt),x.EntryPrice,x.MarkPrice,x.UnrealizedProfit,x.Leverage,x.IsIsolated,x.LiquidationPrice)).ToArray();
    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)
    {
        var native=string.IsNullOrWhiteSpace(symbol)?null:N(symbol);
        var standard=(await _api.GetOpenOrdersAsync(native,ct)).Select(Map);
        var query=native is null?null:new Dictionary<string,string?>{{"symbol",native}};
        using var document=JsonDocument.Parse(await _api.GetSignedRawAsync("/fapi/v1/openAlgoOrders",query,ct));
        var algos=document.RootElement.EnumerateArray().Select(MapAlgo).ToArray();
        foreach(var algo in algos)_algoOrderIds[algo.OrderId]=0;
        return standard.Concat(algos).ToArray();
    }
    public async Task<IReadOnlyList<ExchangeOrder>> GetRecentOrdersAsync(string symbol,int limit,CancellationToken ct)
    {
        var query=new Dictionary<string,string?>
        {
            {"symbol",N(symbol)},
            {"limit",Math.Clamp(limit,1,100).ToString(CultureInfo.InvariantCulture)}
        };
        using var document=JsonDocument.Parse(await _api.GetSignedRawAsync("/fapi/v1/allOrders",query,ct));
        return DeduplicateOrderEvents(document.RootElement.EnumerateArray().Select(MapRaw));
    }
    public bool TryMatchProtectionFill(string parentClientOrderId,ExchangeOrder order,out ProtectionFillKind kind)=>
        TryMatchProtectionFillIdentity(parentClientOrderId,order,out kind);
    internal static bool TryMatchProtectionFillIdentity(string parentClientOrderId,ExchangeOrder order,out ProtectionFillKind kind)
    {
        kind=default;
        if(string.IsNullOrWhiteSpace(parentClientOrderId)||order is null||order.Status!="FILLED"||order.ExecutedQuantity<=0)return false;
        var child=order.ClientOrderId??string.Empty;
        if(!TryProtectionSuffix(child,out kind))return false;
        var stem=child[..^3];
        return stem.Length>=12&&parentClientOrderId.StartsWith(stem,StringComparison.OrdinalIgnoreCase);
    }
    public async Task<IReadOnlyList<CryptoInstrumentFundamentalV1>> GetInstrumentFundamentalsAsync(IReadOnlyList<string> canonicalSymbols,CancellationToken ct)
    {
        if(Environment!=ExchangeEnvironment.Testnet||canonicalSymbols.Count is 0 or >100)return [];
        var mapping=canonicalSymbols.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(N,x=>C(x),StringComparer.OrdinalIgnoreCase);using var document=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/exchangeInfo",null,ct));return BinanceInstrumentFundamentalParserV1.Parse(document.RootElement,mapping,DateTimeOffset.UtcNow);
    }
    public async Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)
    {
        var canonical=C(symbol);var native=N(canonical);
        if (_rules.TryGetValue(canonical,out var cached)) return cached;
        using var d=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/exchangeInfo",null,ct));
        var s=d.RootElement.GetProperty("symbols").EnumerateArray().First(x=>x.GetProperty("symbol").GetString()==native);
        decimal step=0,tick=0,minQty=0,minNotional=5;
        foreach(var f in s.GetProperty("filters").EnumerateArray()) { var t=f.GetProperty("filterType").GetString(); if(t=="LOT_SIZE"){step=D(f,"stepSize");minQty=D(f,"minQty");} else if(t=="PRICE_FILTER")tick=D(f,"tickSize"); else if(t=="MIN_NOTIONAL")minNotional=D(f,"notional"); }
        return _rules[canonical]=new(canonical,step,tick,minQty,minNotional,125);
    }
    public async Task<ProviderMarketCatalog> DiscoverMarketCatalogAsync(CancellationToken ct)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            using var document = JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/exchangeInfo", null, ct));
            var entries = BinanceMarketCatalogParser.Parse(document.RootElement, Descriptor.Id, ProviderId, Symbols.ToCanonical, Descriptor.SupportsTestnet, Environment == ExchangeEnvironment.Testnet, checkedAt);
            return new ProviderMarketCatalog(ProviderCatalogState.Available, entries, checkedAt);
        }
        catch (Exception ex)
        {
            return new ProviderMarketCatalog(ProviderCatalogState.Error,Array.Empty<ProviderMarketCatalogEntry>(),checkedAt,global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(ex.Message,180));
        }
    }
    public async Task<MarketEvidence> GetMarketAsync(string symbol,CancellationToken ct)
    {
        var canonical=C(symbol);var native=N(canonical);var observed=DateTime.UtcNow;
        var candles=ConfirmedMarketCandlesV1.Select(await GetCandlesAsync(canonical,"15m",240,ct),"15m",observed);var c1=await GetCandlesAsync(canonical,"1h",120,ct);var c4=await GetCandlesAsync(canonical,"4h",90,ct);
        var s15=ConfirmedMarketCandlesV1.Analyze(canonical,"15m",candles,observed);var s1=ConfirmedMarketCandlesV1.Analyze(canonical,"1h",c1,observed);var s4=ConfirmedMarketCandlesV1.Analyze(canonical,"4h",c4,observed);
        var deriv=await GetDerivativesNative(native,ct);
        var quality=await GetMarketQualityAsync(native,candles,ct);
        return new(canonical,s15.Price,s15.Support,s15.Resistance,s15.Rsi,s15.ShortTrend,s1.ShortTrend,s4.ShortTrend,deriv,s15.Timestamp){Candles=candles,Quality=quality};
    }
    public async Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol,CancellationToken ct)=>[await GetDerivativesNative(N(symbol),ct)];
    public async Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)
    {
        using var document=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/klines",new Dictionary<string,string?>{{"symbol",N(symbol)},{"interval",interval},{"limit",Math.Clamp(limit,20,1000).ToString(CultureInfo.InvariantCulture)}},ct));
        return document.RootElement.EnumerateArray().Select(k=>new CandleEvidence(
            DateTimeOffset.FromUnixTimeMilliseconds(k[0].GetInt64()).UtcDateTime,
            Decimal(k[1]),Decimal(k[2]),Decimal(k[3]),Decimal(k[4]),Decimal(k[5]),Decimal(k[7]),k[8].GetInt64(),Decimal(k[9]))).ToArray();
    }
    public async Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)
    {
        var query=new Dictionary<string,string?>{{"symbol",N(symbol)},{"interval",interval},{"limit",Math.Clamp(limit,20,1000).ToString(CultureInfo.InvariantCulture)},{"startTime",new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)},{"endTime",new DateTimeOffset(end.ToUniversalTime()).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}};
        using var document=JsonDocument.Parse(await _api.GetPublicRawAsync("/fapi/v1/klines",query,ct));return document.RootElement.EnumerateArray().Select(k=>new CandleEvidence(DateTimeOffset.FromUnixTimeMilliseconds(k[0].GetInt64()).UtcDateTime,Decimal(k[1]),Decimal(k[2]),Decimal(k[3]),Decimal(k[4]),Decimal(k[5]),Decimal(k[7]),k[8].GetInt64(),Decimal(k[9]))).ToArray();
    }
    public async Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct)=>_ = await _api.PostSignedRawAsync("/fapi/v1/leverage",new Dictionary<string,string?>{{"symbol",N(symbol)},{"leverage",leverage.ToString()}},ct);
    public async Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct) { try { _=await _api.PostSignedRawAsync("/fapi/v1/marginType",new Dictionary<string,string?>{{"symbol",N(symbol)},{"marginType",isolated?"ISOLATED":"CROSSED"}},ct); } catch(global::币安量化机器人.Services.BinanceHttpException ex) when(ex.ExchangeCode==-4046){} }
    public Task SetHedgeModeAsync(bool enabled,CancellationToken ct)=>SetHedgeModeIfNeededAsync(_api,enabled,ct);
    internal static async Task SetHedgeModeIfNeededAsync(BinanceApiClient api,bool enabled,CancellationToken ct)
    {
        using var current=JsonDocument.Parse(await api.GetSignedRawAsync("/fapi/v1/positionSide/dual",null,ct));
        if(current.RootElement.TryGetProperty("dualSidePosition",out var value)&&(value.ValueKind is JsonValueKind.True or JsonValueKind.False)&&value.GetBoolean()==enabled)return;
        try { _=await api.PostSignedRawAsync("/fapi/v1/positionSide/dual",new Dictionary<string,string?>{{"dualSidePosition",enabled?"true":"false"}},ct); }
        catch(global::币安量化机器人.Services.BinanceHttpException ex) when(ex.ExchangeCode==-4059){}
    }
    public async Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)
    {
        // Hedge mode closes a leg by sending the opposite order side while keeping
        // positionSide on the leg being reduced. Binance rejects reduceOnly in hedge mode.
        var orderSide=side==PositionSide.Long?OrderSide.Buy:OrderSide.Sell;
        if(reduceOnly)orderSide=orderSide==OrderSide.Buy?OrderSide.Sell:OrderSide.Buy;
        var o=await _api.PlaceOrderAsync(new OrderRequest{Symbol=N(symbol),Side=orderSide,Type=OrderType.Market,Quantity=quantity,ClientOrderId=clientOrderId,PositionSide=side==PositionSide.Long?"LONG":"SHORT"},ct); return Map(o);
    }
    public async Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)
    {
        var orderSide=side==PositionSide.Long?OrderSide.Buy:OrderSide.Sell;if(reduceOnly)orderSide=orderSide==OrderSide.Buy?OrderSide.Sell:OrderSide.Buy;
        var o=await _api.PlaceOrderAsync(new OrderRequest{Symbol=N(symbol),Side=orderSide,Type=OrderType.Limit,Quantity=quantity,Price=price,TimeInForce=TimeInForce.Gtc,ClientOrderId=clientOrderId,PositionSide=side==PositionSide.Long?"LONG":"SHORT"},ct);return Map(o);
    }
    public async Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)
    {
        var closeSide=sideToClose==PositionSide.Long?OrderSide.Sell:OrderSide.Buy; var ps=sideToClose==PositionSide.Long?"LONG":"SHORT";
        var existing=await GetOpenOrdersAsync(symbol,ct);
        var sl=existing.FirstOrDefault(x=>x.PositionSide==sideToClose&&x.Type=="STOP_MARKET");
        var tp=existing.FirstOrDefault(x=>x.PositionSide==sideToClose&&x.Type=="TAKE_PROFIT_MARKET");
        var createdSl=false;
        if(sl is null){sl=await PlaceAlgoProtectionAsync(N(symbol),closeSide,ps,"STOP_MARKET",stopLoss,ProtectionId(groupId,"SL"),ct);createdSl=true;}
        try { if(tp is null)_=await PlaceAlgoProtectionAsync(N(symbol),closeSide,ps,"TAKE_PROFIT_MARKET",takeProfit,ProtectionId(groupId,"TP"),ct); }
        catch { if(createdSl)await CancelOrderAsync(symbol,sl.OrderId,ct); throw; }
        return sl with { IsProtection=true };
    }
    public async Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct) { var q=new Dictionary<string,string?>{{"symbol",N(symbol)},{"origClientOrderId",clientOrderId}}; try { using var d=JsonDocument.Parse(await _api.GetSignedRawAsync("/fapi/v1/order",q,ct)); return MapRaw(d.RootElement); } catch(HttpRequestException ex)when(ex.Message.Contains("-2013",StringComparison.Ordinal)||ex.Message.Contains("Order does not exist",StringComparison.OrdinalIgnoreCase)){return null;} }
    public async Task<ExchangeOrderFeeEvidenceV1> ReadOrderFeeEvidenceAsync(ExchangeOrder order,CancellationToken ct)
    {
        var observed=DateTimeOffset.UtcNow;if(Environment!=ExchangeEnvironment.Testnet)return ExchangeOrderFeeEvidenceCanonicalizerV1.Create(ProviderId,Environment.ToString(),order.Symbol,order.OrderId,order.ClientOrderId,0,0,0,"",observed,ExchangeOrderFeeEvidenceStateV1.Unsupported);
        try
        {
            using var document=JsonDocument.Parse(await _api.GetSignedRawAsync("/fapi/v1/userTrades",new Dictionary<string,string?>{{"symbol",N(order.Symbol)},{"orderId",order.OrderId},{"limit","1000"}},ct));
            return BinanceOrderFeeEvidenceParserV1.Parse(document.RootElement,order,observed,N(order.Symbol));
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception){return ExchangeOrderFeeEvidenceCanonicalizerV1.Create(ProviderId,"Testnet",order.Symbol,order.OrderId,order.ClientOrderId,0,0,0,"",observed,ExchangeOrderFeeEvidenceStateV1.Error);}
    }
    public async Task<FundingObservationResultV1> ReadFundingIncomeAsync(string canonicalSymbol,DateTimeOffset startUtc,DateTimeOffset endUtc,CancellationToken ct)
    {
        startUtc=startUtc.ToUniversalTime();endUtc=endUtc.ToUniversalTime();var observed=DateTimeOffset.UtcNow;var emptyHash=Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        FundingObservationResultV1 Result(FundingObservationStateV1 state,IReadOnlyList<FundingIncomeEventV1>? events=null,string? sourceHash=null)
        {
            var values=events??Array.Empty<FundingIncomeEventV1>();return new(FundingEvidenceCanonicalizerV1.Window(ProviderId,Environment.ToString(),canonicalSymbol,startUtc,endUtc,observed,state,values,sourceHash??emptyHash),values);
        }
        if(Environment!=ExchangeEnvironment.Testnet)return Result(FundingObservationStateV1.Unsupported);
        if(string.IsNullOrWhiteSpace(canonicalSymbol)||startUtc>endUtc||endUtc>observed.AddMinutes(1)||endUtc-startUtc>TimeSpan.FromDays(90))return Result(FundingObservationStateV1.Invalid);
        try
        {
            var native=N(canonicalSymbol);var events=new List<FundingIncomeEventV1>();var responseHashes=new List<string>();var cursor=startUtc;var chunks=0;
            do
            {
                if(++chunks>14)return Result(FundingObservationStateV1.Invalid);
                var chunkEnd=cursor.AddDays(7);if(chunkEnd>endUtc)chunkEnd=endUtc;
                var raw=await _api.GetSignedRawAsync("/fapi/v1/income",new Dictionary<string,string?>{{"symbol",native},{"incomeType","FUNDING_FEE"},{"startTime",cursor.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)},{"endTime",chunkEnd.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)},{"limit","1000"}},ct);
                responseHashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant());
                using var document=JsonDocument.Parse(raw);events.AddRange(BinanceFundingIncomeParserV1.Parse(document.RootElement,canonicalSymbol,native,cursor,chunkEnd));
                if(chunkEnd==endUtc)break;cursor=chunkEnd.AddMilliseconds(1);
            }while(cursor<=endUtc);
            if(events.GroupBy(x=>x.TransactionId,StringComparer.Ordinal).Any(x=>x.Count()>1))return Result(FundingObservationStateV1.Invalid);
            var sourceHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',responseHashes)))).ToLowerInvariant();
            return Result(FundingObservationStateV1.Available,events.OrderBy(x=>x.OccurredAtUtc).ThenBy(x=>x.TransactionId,StringComparer.Ordinal).ToArray(),sourceHash);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception){return Result(FundingObservationStateV1.Error);}
    }
    public async Task CancelOrderAsync(string symbol,string id,CancellationToken ct)
    {
        if(_algoOrderIds.TryRemove(id,out _))
            _=await _api.DeleteSignedRawAsync("/fapi/v1/algoOrder",new Dictionary<string,string?>{{"algoId",id}},ct);
        else
            _=await _api.CancelOrderAsync(N(symbol),long.Parse(id,CultureInfo.InvariantCulture),ct);
    }
    public ValueTask DisposeAsync(){_api.Dispose();return ValueTask.CompletedTask;}

    private async Task<DerivativesSnapshot> GetDerivativesNative(string symbol,CancellationToken ct)
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
    private ExchangeOrder Map(OrderResponse o){var type=o.Type.ToUpperInvariant();return new(C(o.Symbol),o.OrderId.ToString(CultureInfo.InvariantCulture),o.ClientOrderId,NormalizeStandardOrderStatus(o.Status),o.ExecutedQuantity,o.AvgPrice,type,ParseSide(o.PositionSide),(type is "STOP_MARKET" or "TAKE_PROFIT_MARKET")||LooksLikeProtectionClientOrderId(o.ClientOrderId),o.Time);}
    private ExchangeOrder MapRaw(JsonElement e){var type=S(e,"type").ToUpperInvariant();var clientId=S(e,"clientOrderId");return new(C(S(e,"symbol")),e.GetProperty("orderId").GetRawText().Trim('"'),clientId,NormalizeStandardOrderStatus(S(e,"status")),D(e,"executedQty"),D(e,"avgPrice"),type,ParseSide(S(e,"positionSide")),(type is "STOP_MARKET" or "TAKE_PROFIT_MARKET")||LooksLikeProtectionClientOrderId(clientId),DateTime.UtcNow);}
    private ExchangeOrder MapAlgo(JsonElement e){var type=S(e,"orderType").ToUpperInvariant();var updated=e.TryGetProperty("updateTime",out var u)&&u.TryGetInt64(out var ms)?DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime:DateTime.UtcNow;return new(C(S(e,"symbol")),e.GetProperty("algoId").GetRawText().Trim('"'),S(e,"clientAlgoId"),S(e,"algoStatus"),D(e,"actualQty"),D(e,"actualPrice"),type,ParseSide(S(e,"positionSide")),true,updated);}
    private string N(string symbol)=>Symbols.ToNative(symbol);
    private string C(string symbol)=>Symbols.ToCanonical(symbol);
    private static PositionSide? ParseSide(string? value)=>value?.ToUpperInvariant() switch{"LONG"=>PositionSide.Long,"SHORT"=>PositionSide.Short,_=>null};
    internal static string NormalizeStandardOrderStatus(string? status)=>status?.Trim().ToUpperInvariant() switch
    {
        "NEW"=>"NEW",
        "PARTIALLY_FILLED"=>"PARTIALLY_FILLED",
        "FILLED"=>"FILLED",
        "CANCELED"=>"CANCELED",
        "REJECTED"=>"REJECTED",
        "EXPIRED" or "EXPIRED_IN_MATCH"=>"EXPIRED",
        _=>"UNKNOWN"
    };
    internal static IReadOnlyList<ExchangeOrder> DeduplicateOrderEvents(IEnumerable<ExchangeOrder> events)=>events
        .GroupBy(x=>new{x.OrderId,x.ClientOrderId,x.Status,x.ExecutedQuantity,x.AvgPrice,x.UpdatedAt})
        .Select(x=>x.First()).ToArray();
    internal static BinanceProviderFailure ClassifyFailure(Exception error,params string?[] knownSecrets)
    {
        var state=error is TimeoutException or TaskCanceledException or JsonException?"Unavailable":"UNKNOWN";
        if(error is BinanceHttpException classified)
            state=classified.Outcome==BinanceHttpOutcome.Rejected?"Rejected":"Unavailable";
        if(error is HttpRequestException http&&
            (http.StatusCode==System.Net.HttpStatusCode.TooManyRequests||http.StatusCode==(System.Net.HttpStatusCode)418))state="Unavailable";
        return new(state,global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(error.Message,180,knownSecrets));
    }
    private static string S(JsonElement e,string n)=>e.TryGetProperty(n,out var p)?p.GetString()??"":"";
    internal static bool LooksLikeProtectionClientOrderId(string? clientOrderId)=>TryProtectionSuffix(clientOrderId??string.Empty,out _);
    private static bool TryProtectionSuffix(string clientOrderId,out ProtectionFillKind kind)
    {
        if(clientOrderId.EndsWith("-SL",StringComparison.OrdinalIgnoreCase)){kind=ProtectionFillKind.StopLoss;return true;}
        if(clientOrderId.EndsWith("-TP",StringComparison.OrdinalIgnoreCase)){kind=ProtectionFillKind.TakeProfit;return true;}
        kind=default;return false;
    }
    private static string ProtectionId(string groupId,string suffix){var max=36-suffix.Length-1;var stem=groupId.Length>max?groupId[..max]:groupId;return $"{stem}-{suffix}";}
}

internal sealed record BinanceProviderFailure(string State,string SafeMessage);

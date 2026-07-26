using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange;

public sealed class BitgetProviderPlugin:IExchangeProviderPlugin
{
    public static ExchangeProviderDescriptor ProviderDescriptor{get;}=new("bitget","Bitget",ExchangeAssetClass.CryptoCex,true,false,[new("apiKey","API Key",ExchangeCredentialKind.ApiKey),new("secret","API Secret",ExchangeCredentialKind.Secret),new("passphrase","Passphrase",ExchangeCredentialKind.Passphrase)],new HashSet<string>(["account","balance","positions","margin","candles","funding","open-interest","index-price","mark-price","place-order","cancel-order","query-order","protection-orders","health"]));
    public ExchangeProviderDescriptor Descriptor=>ProviderDescriptor;
    public IExchangeProvider Create(ExchangeConnectionProfile profile,IReadOnlyDictionary<string,string> credentials)=>new BitgetExchangeProvider(profile,Required(credentials,"apiKey"),Required(credentials,"secret"),Required(credentials,"passphrase"));
    private static string Required(IReadOnlyDictionary<string,string> values,string key)=>values.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:throw new InvalidOperationException($"Bitget credential '{key}' is incomplete.");
}

public sealed class BitgetExchangeProvider:RestExchangeProviderBase,WpeAgent.RuntimeContracts.IProviderMarketCatalog
{
    private const string Product="USDT-FUTURES";
    private readonly string _key,_passphrase;private readonly byte[] _secret;private readonly Dictionary<string,bool> _isolated=new(StringComparer.OrdinalIgnoreCase);
    public override string ProviderId=>"bitget";public override ExchangeProviderDescriptor Descriptor=>BitgetProviderPlugin.ProviderDescriptor;
    public BitgetExchangeProvider(ExchangeConnectionProfile profile,string key,string secret,string passphrase):base(profile){_key=key;_secret=Encoding.UTF8.GetBytes(secret);_passphrase=passphrase;}
    public override ProviderEnvironmentValidation ValidateEnvironment(bool requireTestnet)
    {
        if(!requireTestnet||Environment!=ExchangeEnvironment.Testnet)return new(false,false,false,"Bitget Mainnet execution is not enabled.");
        if(!IsOfficialRestEndpoint(Profile.Endpoint))return new(false,false,false,"Bitget Demo Trading requires the official https://api.bitget.com endpoint.");
        return new(true,Profile.ExecutionEnabled,true,Profile.ExecutionEnabled?null:"Execution is disabled for this connection.");
    }
    public override async Task<DateTime> GetServerTimeAsync(CancellationToken ct){using var d=await Public(HttpMethod.Get,"/api/v2/public/time",null,ct);return Millis(Str(d.RootElement.GetProperty("data"),"serverTime"));}
    public async Task<WpeAgent.RuntimeContracts.ProviderMarketCatalog> DiscoverMarketCatalogAsync(CancellationToken ct)
    {
        var checkedAt=DateTimeOffset.UtcNow;
        if(!IsOfficialRestEndpoint(Profile.Endpoint))
            return new(WpeAgent.RuntimeContracts.ProviderCatalogState.Unsupported,Array.Empty<WpeAgent.RuntimeContracts.ProviderMarketCatalogEntry>(),checkedAt,"Bitget catalog requires the official https://api.bitget.com endpoint");
        try
        {
            using var document=await MarketCatalog(ct);
            var entries=WpeAgent.RuntimeContracts.BitgetMarketCatalogParser.Parse(document.RootElement,Descriptor.Id,ProviderId,Symbols.ToCanonical,Descriptor.SupportsTestnet,Environment==ExchangeEnvironment.Testnet,checkedAt);
            return new(WpeAgent.RuntimeContracts.ProviderCatalogState.Available,entries,checkedAt);
        }
        catch(Exception ex)when(ex is not OperationCanceledException)
        {
            return new(WpeAgent.RuntimeContracts.ProviderCatalogState.Error,Array.Empty<WpeAgent.RuntimeContracts.ProviderMarketCatalogEntry>(),checkedAt,global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(ex.Message,180));
        }
    }
    public override async Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct){_ =await GetAccountAsync(ct);return ConservativePermissionSnapshot();}
    internal static ExchangePermissionSnapshot ConservativePermissionSnapshot()=>new(true,false,false,"Bitget",["Bitget does not expose complete key trade permissions; trading remains disabled until a credentialed Testnet lifecycle is verified."]);
    public override async Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)
    {
        using var d=await Private(HttpMethod.Get,"/api/v2/mix/account/accounts",new Dictionary<string,string?>{{"productType",Product}},null,ct);var data=d.RootElement.GetProperty("data");var a=data.EnumerateArray().FirstOrDefault(x=>Str(x,"marginCoin")=="USDT");if(a.ValueKind==JsonValueKind.Undefined)throw new InvalidOperationException("Bitget USDT futures account was not found");var equity=Dec(a,"accountEquity");return new(equity,Dec(a,"available"),equity,DateTime.UtcNow);
    }
    public override async Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)
    {
        using var d=await Private(HttpMethod.Get,"/api/v2/mix/position/all-position",new Dictionary<string,string?>{{"productType",Product},{"marginCoin","USDT"}},null,ct);return d.RootElement.GetProperty("data").EnumerateArray().Where(x=>Dec(x,"total")>0).Select(x=>new ManagedPosition(Symbols.ToCanonical(Str(x,"symbol")),Str(x,"holdSide")=="short"?PositionSide.Short:PositionSide.Long,Dec(x,"total"),Dec(x,"openPriceAvg"),Dec(x,"markPrice"),Dec(x,"unrealizedPL"),Dec(x,"leverage"),Str(x,"marginMode")=="isolated",Dec(x,"liquidationPrice"))).ToArray();
    }
    public override async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)
    {
        var q=new Dictionary<string,string?>{{"productType",Product},{"symbol",string.IsNullOrWhiteSpace(symbol)?null:Symbols.ToNative(symbol)},{"limit","100"}};using var d=await Private(HttpMethod.Get,"/api/v2/mix/order/orders-pending",q,null,ct);var data=d.RootElement.GetProperty("data");var regular=data.TryGetProperty("entrustedList",out var orders)?orders.EnumerateArray().Select(x=>MapOrder(x,false)).ToArray():[];
        var pq=new Dictionary<string,string?>{{"productType",Product},{"planType","profit_loss"},{"symbol",string.IsNullOrWhiteSpace(symbol)?null:Symbols.ToNative(symbol)},{"limit","100"}};using var plans=await Private(HttpMethod.Get,"/api/v2/mix/order/orders-plan-pending",pq,null,ct);var pd=plans.RootElement.GetProperty("data");var protection=pd.TryGetProperty("entrustedList",out var pending)?pending.EnumerateArray().Select(x=>MapOrder(x,true)).ToArray():[];
        return MergeOrderPages([regular,protection]);
    }
    public override async Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(symbol);using var d=await Public(HttpMethod.Get,"/api/v2/mix/market/contracts",new Dictionary<string,string?>{{"productType",Product},{"symbol",Symbols.ToNative(canonical)}},ct);var x=d.RootElement.GetProperty("data")[0];var pricePlace=(int)Dec(x,"pricePlace");var priceEnd=Math.Max(1,Dec(x,"priceEndStep"));var tick=priceEnd*(decimal)Math.Pow(10,-pricePlace);return new(canonical,Dec(x,"sizeMultiplier"),tick,Dec(x,"minTradeNum"),Dec(x,"minTradeUSDT"),(int)Math.Max(1,Dec(x,"maxLever")));
    }
    public override Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)=>Candles(symbol,interval,limit,null,null,ct);
    public override Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>Candles(symbol,interval,limit,new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeMilliseconds(),new DateTimeOffset(end.ToUniversalTime()).ToUnixTimeMilliseconds(),ct);
    protected override async Task<DerivativesSnapshot> GetDerivativesAsync(string symbol,CancellationToken ct)
    {
        using var d=await Public(HttpMethod.Get,"/api/v2/mix/market/ticker",new Dictionary<string,string?>{{"productType",Product},{"symbol",Symbols.ToNative(symbol)}},ct);var x=d.RootElement.GetProperty("data")[0];var index=Dec(x,"indexPrice");var mark=Dec(x,"markPrice");return new(Dec(x,"fundingRate"),Dec(x,"holdingAmount"),0,0,0,0,index>0?(mark-index)/index:0);
    }
    public override async Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct){var body=new Dictionary<string,object?>{{"symbol",Symbols.ToNative(symbol)},{"productType",Product},{"marginCoin","USDT"},{"leverage",leverage.ToString()}};using var _=await Private(HttpMethod.Post,"/api/v2/mix/account/set-leverage",null,body,ct);}
    public override async Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct){_isolated[Symbols.ToCanonical(symbol)]=isolated;var body=new Dictionary<string,object?>{{"symbol",Symbols.ToNative(symbol)},{"productType",Product},{"marginCoin","USDT"},{"marginMode",isolated?"isolated":"crossed"}};using var _=await Private(HttpMethod.Post,"/api/v2/mix/account/set-margin-mode",null,body,ct);}
    public override async Task SetHedgeModeAsync(bool enabled,CancellationToken ct){var body=new Dictionary<string,object?>{{"productType",Product},{"posMode",enabled?"hedge_mode":"one_way_mode"}};try{using var _=await Private(HttpMethod.Post,"/api/v2/mix/account/set-position-mode",null,body,ct);}catch(InvalidOperationException ex)when(ex.Message.Contains("not",StringComparison.OrdinalIgnoreCase)&&ex.Message.Contains("change",StringComparison.OrdinalIgnoreCase)){} }
    public override Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)=>Place(symbol,side,quantity,0,"market",clientOrderId,reduceOnly,ct);
    public override Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>Place(symbol,side,quantity,price,"limit",clientOrderId,reduceOnly,ct);
    public override async Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)
    {
        var body=new Dictionary<string,object?>{{"marginCoin","USDT"},{"productType",Product},{"symbol",Symbols.ToNative(symbol)},{"stopSurplusTriggerPrice",F(takeProfit)},{"stopSurplusTriggerType","mark_price"},{"stopSurplusExecutePrice","0"},{"stopLossTriggerPrice",F(stopLoss)},{"stopLossTriggerType","mark_price"},{"stopLossExecutePrice","0"},{"holdSide",sideToClose==PositionSide.Long?"long":"short"},{"stopSurplusClientOid",groupId+"-tp"},{"stopLossClientOid",groupId+"-sl"}};using var d=await Private(HttpMethod.Post,"/api/v2/mix/order/place-pos-tpsl",null,body,ct);var data=d.RootElement.GetProperty("data");var id=data.ValueKind==JsonValueKind.Array&&data.GetArrayLength()>0?Str(data[0],"orderId"):groupId;return new(Symbols.ToCanonical(symbol),id,groupId,"NEW",0,0,"POSITION_TPSL",sideToClose,true,DateTime.UtcNow);
    }
    public override async Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct){var q=new Dictionary<string,string?>{{"symbol",Symbols.ToNative(symbol)},{"productType",Product},{"clientOid",clientOrderId}};using var d=await Private(HttpMethod.Get,"/api/v2/mix/order/detail",q,null,ct);var data=d.RootElement.GetProperty("data");return data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined?null:MapOrder(data,false);}
    public override async Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct)
    {
        var protection=(await GetOpenOrdersAsync(symbol,ct)).FirstOrDefault(x=>x.OrderId==orderId)?.IsProtection==true;var body=new Dictionary<string,object?>{{"symbol",Symbols.ToNative(symbol)},{"productType",Product},{"marginCoin","USDT"},{"orderId",orderId},{"planType",protection?"profit_loss":null}};using var _=await Private(HttpMethod.Post,protection?"/api/v2/mix/order/cancel-plan-order":"/api/v2/mix/order/cancel-order",null,body,ct);
    }

    private async Task<IReadOnlyList<CandleEvidence>> Candles(string symbol,string interval,int limit,long? start,long? end,CancellationToken ct){var granularity=interval switch{"1h"=>"1H","4h"=>"4H","1d"=>"1D",_=>interval};var q=new Dictionary<string,string?>{{"symbol",Symbols.ToNative(symbol)},{"productType",Product},{"granularity",granularity},{"limit",Math.Clamp(limit,1,1000).ToString()},{"startTime",start?.ToString()},{"endTime",end?.ToString()}};using var d=await Public(HttpMethod.Get,"/api/v2/mix/market/candles",q,ct);return d.RootElement.GetProperty("data").EnumerateArray().Select(x=>new CandleEvidence(Millis(x[0].GetString()??""),P(x[1]),P(x[2]),P(x[3]),P(x[4]),P(x[5]),P(x[6]),0,0)).OrderBy(x=>x.OpenTime).ToArray();}
    private async Task<ExchangeOrder> Place(string symbol,PositionSide side,decimal quantity,decimal price,string type,string clientOrderId,bool reduceOnly,CancellationToken ct){var orderSide=side==PositionSide.Long?"buy":"sell";if(reduceOnly)orderSide=orderSide=="buy"?"sell":"buy";var body=new Dictionary<string,object?>{{"symbol",Symbols.ToNative(symbol)},{"productType",Product},{"marginMode",_isolated.GetValueOrDefault(Symbols.ToCanonical(symbol),true)?"isolated":"crossed"},{"marginCoin","USDT"},{"size",F(quantity)},{"price",type=="limit"?F(price):null},{"side",orderSide},{"tradeSide",reduceOnly?"close":"open"},{"orderType",type},{"force",type=="limit"?"gtc":null},{"clientOid",clientOrderId}};using var d=await Private(HttpMethod.Post,"/api/v2/mix/order/place-order",null,body,ct);var x=d.RootElement.GetProperty("data");return new(Symbols.ToCanonical(symbol),Str(x,"orderId"),clientOrderId,"NEW",0,0,type.ToUpperInvariant(),side,false,DateTime.UtcNow);}
    private ExchangeOrder MapOrder(JsonElement x,bool protection){var side=Str(x,"holdSide")=="short"||Str(x,"side")=="sell"?PositionSide.Short:PositionSide.Long;var client=Str(x,"clientOid");return new(Symbols.ToCanonical(Str(x,"symbol")),Str(x,"orderId"),client,Status(Str(x,"status") is {Length:>0}s?s:Str(x,"state")),Dec(x,"baseVolume")>0?Dec(x,"baseVolume"):Dec(x,"filledQty"),Dec(x,"priceAvg"),protection?ProtectionType(Str(x,"planType"),client):Str(x,"orderType").ToUpperInvariant(),side,protection,Millis(Str(x,"uTime") is {Length:>0}t?t:Str(x,"cTime")));}
    internal static string ProtectionType(string planType,string clientOrderId){var value=(planType??string.Empty).ToLowerInvariant();if(value.Contains("loss",StringComparison.Ordinal)||clientOrderId.EndsWith("-sl",StringComparison.OrdinalIgnoreCase))return "STOP_TRIGGER";if(value.Contains("profit",StringComparison.Ordinal)||clientOrderId.EndsWith("-tp",StringComparison.OrdinalIgnoreCase))return "TAKE_PROFIT_TRIGGER";return "TPSL_UNCLASSIFIED";}
    private async Task<JsonDocument> Public(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,CancellationToken ct){var q=query is null?"":CanonicalQuery(query);var target=q.Length==0?path:$"{path}?{q}";return await SendAsync(()=>JsonRequest(method,target),Error,ct);}
    private Task<JsonDocument> MarketCatalog(CancellationToken ct)=>SendAsync(()=>CreateMarketCatalogRequest(Profile.IsTestnet),Error,ct);
    private async Task<JsonDocument> Private(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,object? body,CancellationToken ct){var q=query is null?"":CanonicalQuery(query);var target=q.Length==0?path:$"{path}?{q}";var json=body is null?null:JsonSerializer.Serialize(body);return await SendAsync(()=>{var timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);var sign=ComputeSignature(_secret,timestamp,method.Method,target,json??"");var request=JsonRequest(method,target,json);AddUniqueHeader(request,"ACCESS-KEY",_key);AddUniqueHeader(request,"ACCESS-SIGN",sign);AddUniqueHeader(request,"ACCESS-TIMESTAMP",timestamp);AddUniqueHeader(request,"ACCESS-PASSPHRASE",_passphrase);AddUniqueHeader(request,"locale","en-US");if(Profile.IsTestnet)AddUniqueHeader(request,"paptrading","1");return request;},Error,ct);}
    private static string? Error(JsonElement root)=>Str(root,"code") is "" or "00000"?null:$"{Str(root,"code")} {Str(root,"msg")}";
    private static decimal P(JsonElement value)=>decimal.TryParse(value.GetString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var result)?result:0;
    private static string Status(string value)=>NormalizeOrderStatus(value);
    // Bitget Demo Trading shares the production REST host and is selected by this request header.
    internal static HttpRequestMessage CreateMarketCatalogRequest(bool testnet)
    {
        var request=JsonRequest(HttpMethod.Get,$"/api/v2/mix/market/contracts?productType={Uri.EscapeDataString(Product)}");
        if(testnet)request.Headers.Add("paptrading","1");
        return request;
    }
    internal static bool IsOfficialRestEndpoint(string endpoint)=>ProviderEndpointPolicy.IsOfficialHttpsOrigin(endpoint,"api.bitget.com");
    internal static string NormalizeOrderStatus(string status)=>status.ToLowerInvariant() switch{"live" or "new"=>"NEW","partially_filled" or "partial-fill"=>"PARTIALLY_FILLED","filled" or "full-fill"=>"FILLED","cancelled" or "canceled"=>"CANCELED","expired"=>"EXPIRED","rejected"=>"REJECTED",_=>"UNKNOWN"};
    internal static bool IsServerTimeWithinReceiveWindow(DateTime serverUtc,DateTime localUtc)=>serverUtc!=default&&Math.Abs((localUtc-serverUtc).TotalSeconds)<=5;
    internal static IReadOnlyList<ExchangeOrder> MergeOrderPages(IEnumerable<IEnumerable<ExchangeOrder>> pages)=>MergeLatest(pages);
    private static IReadOnlyList<ExchangeOrder> MergeLatest(IEnumerable<IEnumerable<ExchangeOrder>> pages){var identified=new Dictionary<string,ExchangeOrder>(StringComparer.OrdinalIgnoreCase);var anonymous=new List<ExchangeOrder>();foreach(var order in pages.SelectMany(x=>x)){var key=!string.IsNullOrWhiteSpace(order.OrderId)?"id:"+order.OrderId:!string.IsNullOrWhiteSpace(order.ClientOrderId)?"client:"+order.ClientOrderId:null;if(key is null){anonymous.Add(order);continue;}if(!identified.TryGetValue(key,out var current)||order.UpdatedAt>=current.UpdatedAt)identified[key]=order;}return identified.Values.Concat(anonymous).OrderBy(x=>x.UpdatedAt).ToArray();}
    internal static string ComputeSignature(byte[] secret,string timestamp,string method,string target,string body){ValidateSigningInput(secret,timestamp,method,target);return Convert.ToBase64String(HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(timestamp+method.ToUpperInvariant()+target+body)));}
    internal static string CanonicalQuery(IEnumerable<KeyValuePair<string,string?>> values){var materialized=values.Where(x=>x.Value is not null).ToArray();if(materialized.Any(x=>string.IsNullOrWhiteSpace(x.Key))||materialized.GroupBy(x=>x.Key,StringComparer.Ordinal).Any(x=>x.Count()>1))throw new InvalidOperationException("Bitget query contains an empty or duplicate parameter.");return string.Join("&",materialized.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>$"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));}
    internal static bool IsTimestampWithinReceiveWindow(string timestamp,DateTimeOffset now){return long.TryParse(timestamp,NumberStyles.None,CultureInfo.InvariantCulture,out var milliseconds)&&Math.Abs((now-DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)).TotalSeconds)<=5;}
    internal static void AddUniqueHeader(HttpRequestMessage request,string name,string value){if(request.Headers.Contains(name))throw new InvalidOperationException($"Duplicate Bitget header '{name}' is not allowed.");request.Headers.Add(name,value);}
    private static void ValidateSigningInput(byte[] secret,string timestamp,string method,string target){var normalized=method.ToUpperInvariant();if(secret.Length==0||!long.TryParse(timestamp,NumberStyles.None,CultureInfo.InvariantCulture,out _)||normalized is not ("GET" or "POST" or "DELETE")||!target.StartsWith("/api/",StringComparison.Ordinal)||target.Any(char.IsWhiteSpace)||target.Count(x=>x=='?')>1)throw new InvalidOperationException("Bitget signing input is invalid.");}
}

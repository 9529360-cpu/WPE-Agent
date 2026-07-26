using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange;

public sealed class BybitProviderPlugin:IExchangeProviderPlugin
{
    public static ExchangeProviderDescriptor ProviderDescriptor{get;}=new("bybit","Bybit",ExchangeAssetClass.CryptoCex,true,false,[new("apiKey","API Key",ExchangeCredentialKind.ApiKey),new("secret","API Secret",ExchangeCredentialKind.Secret)],new HashSet<string>(["account","balance","positions","margin","candles","funding","open-interest","mark-price","place-order","cancel-order","query-order","protection-orders","health"]));
    public ExchangeProviderDescriptor Descriptor=>ProviderDescriptor;
    public IExchangeProvider Create(ExchangeConnectionProfile profile,IReadOnlyDictionary<string,string> credentials)=>new BybitExchangeProvider(profile,Required(credentials,"apiKey"),Required(credentials,"secret"));
    private static string Required(IReadOnlyDictionary<string,string> values,string key)=>values.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:throw new InvalidOperationException($"Bybit credential '{key}' is incomplete.");
}

public sealed class BybitExchangeProvider:RestExchangeProviderBase,IProviderMarketCatalog
{
    private readonly string _key;private readonly byte[] _secret;private readonly Dictionary<string,int> _leverage=new(StringComparer.OrdinalIgnoreCase);
    public override string ProviderId=>"bybit";public override ExchangeProviderDescriptor Descriptor=>BybitProviderPlugin.ProviderDescriptor;
    public BybitExchangeProvider(ExchangeConnectionProfile profile,string key,string secret):base(profile){_key=key;_secret=Encoding.UTF8.GetBytes(secret);}
    public override ProviderEnvironmentValidation ValidateEnvironment(bool requireTestnet)
    {
        if(!requireTestnet||Environment!=ExchangeEnvironment.Testnet)return new(false,false,false,"Bybit Mainnet execution is not enabled.");
        if(!ProviderEndpointPolicy.IsOfficialHttpsOrigin(Profile.Endpoint,"api-testnet.bybit.com"))return new(false,false,false,"Bybit Testnet requires the official https://api-testnet.bybit.com endpoint.");
        return new(true,Profile.ExecutionEnabled,true,Profile.ExecutionEnabled?null:"Execution is disabled for this connection.");
    }
    public override async Task<DateTime> GetServerTimeAsync(CancellationToken ct){using var d=await Public(HttpMethod.Get,"/v5/market/time",null,ct);return ParseServerTime(Str(d.RootElement,"time"));}
    public override async Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct)
    {
        using var d=await Private(HttpMethod.Get,"/v5/user/query-api",null,null,ct);return ParsePermissionSnapshot(d.RootElement.GetProperty("result"));
    }
    internal static ExchangePermissionSnapshot ParsePermissionSnapshot(JsonElement result){var readOnly=Str(result,"readOnly");var canTrade=readOnly is "0" or "false" or "False";var uid=Str(result,"userID");return new(true,canTrade,false,string.IsNullOrWhiteSpace(uid)?"Bybit":uid,canTrade?[]:["Trade permission was not positively verified."]);}
    public override async Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)
    {
        using var d=await Private(HttpMethod.Get,"/v5/account/wallet-balance",new Dictionary<string,string?>{{"accountType","UNIFIED"},{"coin","USDT"}},null,ct);var list=d.RootElement.GetProperty("result").GetProperty("list");if(list.GetArrayLength()==0)throw new InvalidOperationException("Bybit wallet response is empty");var a=list[0];var available=Dec(a,"totalAvailableBalance");if(available<=0&&a.TryGetProperty("coin",out var coins)&&coins.GetArrayLength()>0){var usdt=coins.EnumerateArray().FirstOrDefault(x=>Str(x,"coin")=="USDT");if(usdt.ValueKind!=JsonValueKind.Undefined)available=Math.Max(0,Dec(usdt,"walletBalance")-Dec(usdt,"totalPositionIM")-Dec(usdt,"totalOrderIM")-Dec(usdt,"locked")-Dec(usdt,"bonus"));}return new(Dec(a,"totalWalletBalance"),available,Dec(a,"totalEquity"),DateTime.UtcNow);
    }
    public override async Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)
    {
        using var account=await Private(HttpMethod.Get,"/v5/account/info",null,null,ct);var isolated=Str(account.RootElement.GetProperty("result"),"marginMode")=="ISOLATED_MARGIN";using var d=await Private(HttpMethod.Get,"/v5/position/list",new Dictionary<string,string?>{{"category","linear"},{"settleCoin","USDT"}},null,ct);return d.RootElement.GetProperty("result").GetProperty("list").EnumerateArray().Where(x=>Dec(x,"size")>0).Select(x=>new ManagedPosition(Symbols.ToCanonical(Str(x,"symbol")),Str(x,"side")=="Sell"?PositionSide.Short:PositionSide.Long,Dec(x,"size"),Dec(x,"avgPrice"),Dec(x,"markPrice"),Dec(x,"unrealisedPnl"),Dec(x,"leverage"),isolated,Dec(x,"liqPrice"))).ToArray();
    }
    public override async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)
    {
        var query=new Dictionary<string,string?>{{"category","linear"},{"settleCoin","USDT"},{"symbol",string.IsNullOrWhiteSpace(symbol)?null:Symbols.ToNative(symbol)}};using var d=await Private(HttpMethod.Get,"/v5/order/realtime",query,null,ct);return NormalizeOrderEvents(d.RootElement.GetProperty("result").GetProperty("list").EnumerateArray().Select(MapOrder));
    }
    public override async Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(symbol);using var d=await Public(HttpMethod.Get,"/v5/market/instruments-info",new Dictionary<string,string?>{{"category","linear"},{"symbol",Symbols.ToNative(canonical)}},ct);var x=d.RootElement.GetProperty("result").GetProperty("list")[0];var lot=x.GetProperty("lotSizeFilter");var price=x.GetProperty("priceFilter");var leverage=x.GetProperty("leverageFilter");return new(canonical,Dec(lot,"qtyStep"),Dec(price,"tickSize"),Dec(lot,"minOrderQty"),Dec(lot,"minNotionalValue"),(int)Math.Max(1,Dec(leverage,"maxLeverage")));
    }
    public async Task<ProviderMarketCatalog> DiscoverMarketCatalogAsync(CancellationToken ct)
    {
        var checkedAt=DateTimeOffset.UtcNow;
        try
        {
            var markets=new Dictionary<string,ProviderMarketCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            var seenCursors=new HashSet<string>(StringComparer.Ordinal);
            string? cursor=null;
            do
            {
                var query=new Dictionary<string,string?>{{"category","linear"},{"limit","1000"},{"cursor",cursor}};
                using var document=await Public(HttpMethod.Get,"/v5/market/instruments-info",query,ct);
                foreach(var market in BybitMarketCatalogParser.Parse(document.RootElement,Descriptor.Id,ProviderId,Symbols.ToCanonical,Descriptor.SupportsTestnet,Environment==ExchangeEnvironment.Testnet,checkedAt))
                    markets[market.Instrument.NativeSymbol]=market;

                var result=document.RootElement.GetProperty("result");
                cursor=result.TryGetProperty("nextPageCursor",out var next)&&next.ValueKind==JsonValueKind.String?next.GetString():null;
                if(!string.IsNullOrWhiteSpace(cursor)&&!seenCursors.Add(cursor))throw new InvalidOperationException("Bybit instruments pagination cursor repeated");
            } while(!string.IsNullOrWhiteSpace(cursor));

            if(markets.Count==0)throw new InvalidOperationException("Bybit market catalog payload contained no supported instruments.");
            return new ProviderMarketCatalog(ProviderCatalogState.Available,markets.Values.OrderBy(x=>x.Instrument.CanonicalSymbol,StringComparer.OrdinalIgnoreCase).ToArray(),checkedAt);
        }
        catch(Exception ex)
        {
            return new ProviderMarketCatalog(ProviderCatalogState.Error,Array.Empty<ProviderMarketCatalogEntry>(),checkedAt,Failure(ex));
        }
    }
    public override async Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)=>await Candles(symbol,interval,limit,null,null,ct);
    public override async Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>await Candles(symbol,interval,limit,new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeMilliseconds(),new DateTimeOffset(end.ToUniversalTime()).ToUnixTimeMilliseconds(),ct);
    protected override async Task<DerivativesSnapshot> GetDerivativesAsync(string symbol,CancellationToken ct)
    {
        using var d=await Public(HttpMethod.Get,"/v5/market/tickers",new Dictionary<string,string?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)}},ct);var list=d.RootElement.GetProperty("result").GetProperty("list");if(list.GetArrayLength()==0)return new(0,0,0,0,0,0,0);var x=list[0];var index=Dec(x,"indexPrice");var mark=Dec(x,"markPrice");return new(Dec(x,"fundingRate"),Dec(x,"openInterest"),0,0,0,0,index>0?(mark-index)/index:0);
    }
    public override async Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct){_leverage[Symbols.ToCanonical(symbol)]=leverage;var body=new Dictionary<string,object?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"buyLeverage",leverage.ToString(CultureInfo.InvariantCulture)},{"sellLeverage",leverage.ToString(CultureInfo.InvariantCulture)}};using var _=await Private(HttpMethod.Post,"/v5/position/set-leverage",null,body,ct);}
    public override async Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct){var body=new Dictionary<string,object?>{{"setMarginMode",isolated?"ISOLATED_MARGIN":"REGULAR_MARGIN"}};try{using var _=await Private(HttpMethod.Post,"/v5/account/set-margin-mode",null,body,ct);}catch(InvalidOperationException ex)when(ex.Message.Contains("not modified",StringComparison.OrdinalIgnoreCase)||ex.Message.Contains("110026")){} }
    public override async Task SetHedgeModeAsync(bool enabled,CancellationToken ct){var body=new Dictionary<string,object?>{{"category","linear"},{"coin","USDT"},{"mode",enabled?3:0}};try{using var _=await Private(HttpMethod.Post,"/v5/position/switch-mode",null,body,ct);}catch(InvalidOperationException ex)when(ex.Message.Contains("not modified",StringComparison.OrdinalIgnoreCase)||ex.Message.Contains("110025")){} }
    public override Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)=>Place(symbol,side,quantity,0,"Market",clientOrderId,reduceOnly,ct);
    public override Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>Place(symbol,side,quantity,price,"Limit",clientOrderId,reduceOnly,ct);
    public override async Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)
    {
        var body=new Dictionary<string,object?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"tpslMode","Full"},{"positionIdx",sideToClose==PositionSide.Long?1:2},{"stopLoss",F(stopLoss)},{"takeProfit",F(takeProfit)},{"slTriggerBy","MarkPrice"},{"tpTriggerBy","MarkPrice"}};using var _=await Private(HttpMethod.Post,"/v5/position/trading-stop",null,body,ct);return new(Symbols.ToCanonical(symbol),$"position-tpsl:{groupId}",groupId,"NEW",0,0,"POSITION_TPSL",sideToClose,true,DateTime.UtcNow);
    }
    public override async Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct){var query=new Dictionary<string,string?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"orderLinkId",clientOrderId}};using var d=await Private(HttpMethod.Get,"/v5/order/realtime",query,null,ct);var list=d.RootElement.GetProperty("result").GetProperty("list");return list.GetArrayLength()==0?null:MapOrder(list[0]);}
    public override async Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct){if(orderId.StartsWith("position-tpsl:",StringComparison.Ordinal))throw new NotSupportedException("Bybit position TP/SL cancellation requires an explicit position protection update.");var body=new Dictionary<string,object?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"orderId",orderId}};using var _=await Private(HttpMethod.Post,"/v5/order/cancel",null,body,ct);}

    private async Task<IReadOnlyList<CandleEvidence>> Candles(string symbol,string interval,int limit,long? start,long? end,CancellationToken ct)
    {
        var map=interval switch{"1m"=>"1","5m"=>"5","15m"=>"15","1h"=>"60","4h"=>"240","1d"=>"D",_=>interval};var q=new Dictionary<string,string?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"interval",map},{"limit",Math.Clamp(limit,1,1000).ToString(CultureInfo.InvariantCulture)},{"start",start?.ToString(CultureInfo.InvariantCulture)},{"end",end?.ToString(CultureInfo.InvariantCulture)}};using var d=await Public(HttpMethod.Get,"/v5/market/kline",q,ct);var rows=d.RootElement.GetProperty("result").GetProperty("list").EnumerateArray().Select(x=>new CandleEvidence(Millis(x[0].GetString()??""),P(x[1]),P(x[2]),P(x[3]),P(x[4]),P(x[5]),P(x[6]),0,0)).OrderBy(x=>x.OpenTime).ToArray();return rows;
    }
    private async Task<ExchangeOrder> Place(string symbol,PositionSide side,decimal quantity,decimal price,string type,string clientOrderId,bool reduceOnly,CancellationToken ct)
    {
        var orderSide=side==PositionSide.Long?"Buy":"Sell";if(reduceOnly)orderSide=orderSide=="Buy"?"Sell":"Buy";var body=new Dictionary<string,object?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"side",orderSide},{"positionIdx",side==PositionSide.Long?1:2},{"orderType",type},{"qty",F(quantity)},{"price",type=="Limit"?F(price):null},{"timeInForce",type=="Limit"?"GTC":"IOC"},{"orderLinkId",clientOrderId},{"reduceOnly",reduceOnly}};using var d=await Private(HttpMethod.Post,"/v5/order/create",null,body,ct);var r=d.RootElement.GetProperty("result");return new(Symbols.ToCanonical(symbol),Str(r,"orderId"),clientOrderId,"NEW",0,0,type.ToUpperInvariant(),side,false,DateTime.UtcNow);
    }
    private ExchangeOrder MapOrder(JsonElement x){var type=Str(x,"orderType").ToUpperInvariant();var status=Status(Str(x,"orderStatus"));var link=Str(x,"orderLinkId");var side=Str(x,"positionIdx") switch{"1"=>PositionSide.Long,"2"=>PositionSide.Short,_=>Str(x,"side")=="Sell"?PositionSide.Short:PositionSide.Long};var protection=Str(x,"stopOrderType") is not("" or "UNKNOWN")||Str(x,"triggerPrice").Length>0;return new(Symbols.ToCanonical(Str(x,"symbol")),Str(x,"orderId"),link,status,Dec(x,"cumExecQty"),Dec(x,"avgPrice"),type,side,protection,OrderTimestamp(Str(x,"updatedTime")));}
    private async Task<JsonDocument> Public(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,CancellationToken ct){var target=CanonicalTarget(method.Method,path,query);return await SendAsync(()=>JsonRequest(method,target),Error,ct);}
    private async Task<JsonDocument> Private(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,IReadOnlyDictionary<string,object?>? body,CancellationToken ct)
    {
        var target=CanonicalTarget(method.Method,path,query);var q=target.Contains('?')?target[(target.IndexOf('?')+1)..]:string.Empty;var json=CanonicalBody(body);return await SendAsync(()=>{var timestamp=FormatTimestamp(DateTimeOffset.UtcNow);var payload=method==HttpMethod.Get?q:json;var sign=ComputeSignature(_secret,timestamp,_key,Profile.ReceiveWindow,payload);var request=JsonRequest(method,target,json.Length==0?null:json);AddHeader(request,"X-BAPI-API-KEY",_key);AddHeader(request,"X-BAPI-SIGN",sign);AddHeader(request,"X-BAPI-TIMESTAMP",timestamp);AddHeader(request,"X-BAPI-RECV-WINDOW",Profile.ReceiveWindow.ToString(CultureInfo.InvariantCulture));return request;},Error,ct);
    }
    internal static string? Error(JsonElement root)
    {
        var code=Str(root,"retCode");if(code!="0")return Redact($"{(code.Length==0?"UNKNOWN":code)} {Str(root,"retMsg")}");
        if(root.TryGetProperty("result",out var result)&&result.TryGetProperty("list",out var list)&&list.ValueKind==JsonValueKind.Array)
            foreach(var item in list.EnumerateArray()){var itemCode=Str(item,"retCode");if(itemCode.Length==0)itemCode=Str(item,"code");if(itemCode.Length>0&&itemCode!="0")return Redact($"{itemCode} {Str(item,"retMsg")} {Str(item,"msg")}");}
        return null;
    }
    private static decimal P(JsonElement value)=>decimal.TryParse(value.GetString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var result)?result:0;
    internal static string Status(string value)=>value switch{"Created" or "New" or "Untriggered"=>"NEW","PartiallyFilled"=>"PARTIALLY_FILLED","Filled"=>"FILLED","Cancelled" or "Deactivated"=>"CANCELED","Rejected"=>"REJECTED",_=>"UNKNOWN"};
    internal static DateTime ParseServerTime(string value)=>long.TryParse(value,out var milliseconds)?DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime:throw new InvalidOperationException("Bybit server time is invalid.");
    internal static DateTime OrderTimestamp(string value)=>long.TryParse(value,out var milliseconds)?DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime:DateTime.UnixEpoch;
    internal static IReadOnlyList<ExchangeOrder> NormalizeOrderEvents(IEnumerable<ExchangeOrder> orders)=>orders.GroupBy(OrderKey,StringComparer.Ordinal).Select(group=>group.OrderByDescending(OrderRank).ThenByDescending(order=>order.UpdatedAt).First()).OrderBy(order=>order.OrderId,StringComparer.Ordinal).ToArray();
    internal static string Failure(Exception exception)=>$"{(exception is HttpRequestException or TimeoutException or TaskCanceledException?"UNAVAILABLE":"UNKNOWN")}: {Redact(exception.Message)}";
    private static string OrderKey(ExchangeOrder order)=>string.IsNullOrWhiteSpace(order.ClientOrderId)?$"order:{order.OrderId}":$"client:{order.ClientOrderId}";
    private static int OrderRank(ExchangeOrder order)=>order.Status switch{"FILLED" or "CANCELED" or "REJECTED"=>3,"UNKNOWN"=>2,"PARTIALLY_FILLED"=>1,_=>0};
    private static string Redact(string value)=>global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(value,180);
    internal static string ComputeSignature(byte[] secret,string timestamp,string key,int receiveWindow,string payload)
    {
        if(secret.Length==0||string.IsNullOrWhiteSpace(key))throw new InvalidOperationException("Bybit signing credentials are incomplete.");if(!long.TryParse(timestamp,NumberStyles.None,CultureInfo.InvariantCulture,out var milliseconds)||milliseconds<=0)throw new InvalidOperationException("Bybit signing timestamp is invalid.");if(receiveWindow is <1000 or >60000)throw new InvalidOperationException("Bybit receive window is invalid.");return Convert.ToHexString(HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(timestamp+key+receiveWindow.ToString(CultureInfo.InvariantCulture)+payload))).ToLowerInvariant();
    }
    internal static string CanonicalTarget(string method,string path,IEnumerable<KeyValuePair<string,string?>>? query){ValidateMethod(method);ValidatePath(path);var canonical=CanonicalQuery(query);return canonical.Length==0?path:$"{path}?{canonical}";}
    internal static string CanonicalQuery(IEnumerable<KeyValuePair<string,string?>>? query)
    {
        if(query is null)return string.Empty;var values=query.Where(item=>item.Value is not null).ToArray();RejectDuplicateKeys(values.Select(item=>item.Key),"Bybit query");return string.Join("&",values.OrderBy(item=>item.Key,StringComparer.Ordinal).Select(item=>$"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}"));
    }
    internal static string CanonicalBody(IEnumerable<KeyValuePair<string,object?>>? body)
    {
        if(body is null)return string.Empty;var values=body.Where(item=>item.Value is not null).ToArray();RejectDuplicateKeys(values.Select(item=>item.Key),"Bybit body");return JsonSerializer.Serialize(values.OrderBy(item=>item.Key,StringComparer.Ordinal).ToDictionary(item=>item.Key,item=>item.Value,StringComparer.Ordinal));
    }
    internal static string FormatTimestamp(DateTimeOffset value)=>value.ToUniversalTime().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    internal static string Sha256Hex(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static void AddHeader(HttpRequestMessage request,string name,string value){if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(value)||request.Headers.Contains(name)||!request.Headers.TryAddWithoutValidation(name,value))throw new InvalidOperationException("Bybit request header is missing, invalid, or duplicated.");}
    private static void ValidateMethod(string method){if(method is not("GET" or "POST"))throw new InvalidOperationException("Bybit request method is unsupported.");}
    private static void ValidatePath(string value){if(string.IsNullOrWhiteSpace(value)||!value.StartsWith("/",StringComparison.Ordinal)||value.Contains('?')||value.Contains('#')||value.Any(char.IsControl)||Uri.TryCreate(value,UriKind.Absolute,out _))throw new InvalidOperationException("Bybit request path is invalid.");}
    private static void RejectDuplicateKeys(IEnumerable<string> keys,string subject){var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var key in keys){if(string.IsNullOrWhiteSpace(key)||!set.Add(key))throw new InvalidOperationException($"{subject} contains an invalid or duplicate key.");}}
}

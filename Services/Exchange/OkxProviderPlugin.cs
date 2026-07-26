using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange;

public sealed class OkxProviderPlugin:IExchangeProviderPlugin
{
    public static ExchangeProviderDescriptor ProviderDescriptor{get;}=new("okx","OKX",ExchangeAssetClass.CryptoCex,true,false,[new("apiKey","API Key",ExchangeCredentialKind.ApiKey),new("secret","API Secret",ExchangeCredentialKind.Secret),new("passphrase","Passphrase",ExchangeCredentialKind.Passphrase)],new HashSet<string>(["account","balance","positions","margin","candles","funding","open-interest","index-price","mark-price","place-order","cancel-order","query-order","protection-orders","health"]));
    public ExchangeProviderDescriptor Descriptor=>ProviderDescriptor;
    public IExchangeProvider Create(ExchangeConnectionProfile profile,IReadOnlyDictionary<string,string> credentials)=>new OkxExchangeProvider(profile,Required(credentials,"apiKey"),Required(credentials,"secret"),Required(credentials,"passphrase"));
    private static string Required(IReadOnlyDictionary<string,string> values,string key)=>values.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:throw new InvalidOperationException($"OKX credential '{key}' is incomplete.");
}

public sealed class OkxExchangeProvider:RestExchangeProviderBase,WpeAgent.RuntimeContracts.IProviderMarketCatalog
{
    private readonly string _key,_passphrase;private readonly byte[] _secret;private readonly Dictionary<string,int> _leverage=new(StringComparer.OrdinalIgnoreCase);private readonly Dictionary<string,bool> _isolated=new(StringComparer.OrdinalIgnoreCase);private readonly ConcurrentDictionary<string,byte> _algoIds=new();private readonly ConcurrentDictionary<string,decimal> _contractValues=new(StringComparer.OrdinalIgnoreCase);
    public override string ProviderId=>"okx";public override ExchangeProviderDescriptor Descriptor=>OkxProviderPlugin.ProviderDescriptor;
    public OkxExchangeProvider(ExchangeConnectionProfile profile,string key,string secret,string passphrase):base(profile){_key=key;_secret=Encoding.UTF8.GetBytes(secret);_passphrase=passphrase;}
    public override ProviderEnvironmentValidation ValidateEnvironment(bool requireTestnet)
    {
        if(!requireTestnet||Environment!=ExchangeEnvironment.Testnet)return new(false,false,false,"OKX Mainnet execution is not enabled.");
        if(!ProviderEndpointPolicy.IsOfficialHttpsOrigin(Profile.Endpoint,"www.okx.com"))return new(false,false,false,"OKX Demo Trading requires the official https://www.okx.com endpoint.");
        return new(true,Profile.ExecutionEnabled,true,Profile.ExecutionEnabled?null:"Execution is disabled for this connection.");
    }
    public override async Task<DateTime> GetServerTimeAsync(CancellationToken ct){using var d=await Public(HttpMethod.Get,"/api/v5/public/time",null,ct);return ParseServerTime(Str(d.RootElement.GetProperty("data")[0],"ts"));}
    public override async Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct){using var d=await Private(HttpMethod.Get,"/api/v5/account/config",null,null,ct);return ParsePermissionSnapshot(d.RootElement.GetProperty("data")[0]);}
    public override async Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)
    {
        using var d=await Private(HttpMethod.Get,"/api/v5/account/balance",new Dictionary<string,string?>{{"ccy","USDT"}},null,ct);var data=d.RootElement.GetProperty("data");if(data.GetArrayLength()==0)throw new InvalidOperationException("OKX balance response is empty");var a=data[0];var details=a.GetProperty("details");var usdt=details.EnumerateArray().FirstOrDefault(x=>Str(x,"ccy")=="USDT");var wallet=usdt.ValueKind==JsonValueKind.Undefined?Dec(a,"totalEq"):Dec(usdt,"cashBal");var available=usdt.ValueKind==JsonValueKind.Undefined?Dec(a,"adjEq"):Dec(usdt,"availBal");var equity=usdt.ValueKind==JsonValueKind.Undefined?Dec(a,"totalEq"):Dec(usdt,"eq");return new(wallet,available,equity,DateTime.UtcNow);
    }
    public override async Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)
    {
        using var d=await Private(HttpMethod.Get,"/api/v5/account/positions",new Dictionary<string,string?>{{"instType","SWAP"}},null,ct);var result=new List<ManagedPosition>();foreach(var x in d.RootElement.GetProperty("data").EnumerateArray()){var contracts=Math.Abs(Dec(x,"pos"));if(contracts<=0)continue;var native=Str(x,"instId");var value=await ContractValue(native,ct);result.Add(new(Symbols.ToCanonical(native),Str(x,"posSide")=="short"||Dec(x,"pos")<0?PositionSide.Short:PositionSide.Long,contracts*value,Dec(x,"avgPx"),Dec(x,"markPx"),Dec(x,"upl"),Dec(x,"lever"),Str(x,"mgnMode")=="isolated",Dec(x,"liqPx")));}return result;
    }
    public override async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol,CancellationToken ct)
    {
        var native=string.IsNullOrWhiteSpace(symbol)?null:Symbols.ToNative(symbol);using var regular=await Private(HttpMethod.Get,"/api/v5/trade/orders-pending",new Dictionary<string,string?>{{"instType","SWAP"},{"instId",native}},null,ct);var result=new List<ExchangeOrder>();foreach(var x in regular.RootElement.GetProperty("data").EnumerateArray())result.Add(await MapOrder(x,false,ct));foreach(var type in new[]{"conditional","oco"}){using var algo=await Private(HttpMethod.Get,"/api/v5/trade/orders-algo-pending",new Dictionary<string,string?>{{"ordType",type},{"instType","SWAP"},{"instId",native}},null,ct);foreach(var x in algo.RootElement.GetProperty("data").EnumerateArray())result.Add(await MapOrder(x,true,ct));}return NormalizeOrderEvents(result);
    }
    public override async Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(symbol);using var d=await Public(HttpMethod.Get,"/api/v5/public/instruments",new Dictionary<string,string?>{{"instType","SWAP"},{"instId",Symbols.ToNative(canonical)}},ct);var x=d.RootElement.GetProperty("data")[0];var value=Math.Max(Dec(x,"ctVal"),0.00000001m)*Math.Max(Dec(x,"ctMult"),1);_contractValues[Symbols.ToNative(canonical)]=value;return new(canonical,Dec(x,"lotSz")*value,Dec(x,"tickSz"),Dec(x,"minSz")*value,5,125);
    }
    public async Task<WpeAgent.RuntimeContracts.ProviderMarketCatalog> DiscoverMarketCatalogAsync(CancellationToken ct)
    {
        var checkedAt=DateTimeOffset.UtcNow;
        try
        {
            using var document=await Public(HttpMethod.Get,"/api/v5/public/instruments",new Dictionary<string,string?>{{"instType","SWAP"}},ct);
            var entries=WpeAgent.RuntimeContracts.OkxMarketCatalogParser.Parse(document.RootElement,Descriptor.Id,ProviderId,Symbols.ToCanonical,Descriptor.SupportsTestnet,Environment==ExchangeEnvironment.Testnet,checkedAt);
            if(entries.Count==0)throw new InvalidOperationException("OKX market catalog payload contained no supported instruments.");
            return new WpeAgent.RuntimeContracts.ProviderMarketCatalog(WpeAgent.RuntimeContracts.ProviderCatalogState.Available,entries,checkedAt);
        }
        catch(Exception ex)
        {
            return new WpeAgent.RuntimeContracts.ProviderMarketCatalog(WpeAgent.RuntimeContracts.ProviderCatalogState.Error,Array.Empty<WpeAgent.RuntimeContracts.ProviderMarketCatalogEntry>(),checkedAt,Failure(ex));
        }
    }
    public override Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)=>Candles(symbol,interval,limit,null,null,ct);
    public override Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>Candles(symbol,interval,limit,new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeMilliseconds(),new DateTimeOffset(end.ToUniversalTime()).ToUnixTimeMilliseconds(),ct);
    protected override async Task<DerivativesSnapshot> GetDerivativesAsync(string symbol,CancellationToken ct)
    {
        var native=Symbols.ToNative(symbol);decimal funding=0,oi=0,mark=0,index=0;try{using var d=await Public(HttpMethod.Get,"/api/v5/public/funding-rate",new Dictionary<string,string?>{{"instId",native}},ct);funding=Dec(d.RootElement.GetProperty("data")[0],"fundingRate");}catch{}try{using var d=await Public(HttpMethod.Get,"/api/v5/public/open-interest",new Dictionary<string,string?>{{"instType","SWAP"},{"instId",native}},ct);oi=Dec(d.RootElement.GetProperty("data")[0],"oi");}catch{}try{using var d=await Public(HttpMethod.Get,"/api/v5/public/mark-price",new Dictionary<string,string?>{{"instType","SWAP"},{"instId",native}},ct);mark=Dec(d.RootElement.GetProperty("data")[0],"markPx");}catch{}try{var indexId=native.Replace("-SWAP","",StringComparison.OrdinalIgnoreCase);using var d=await Public(HttpMethod.Get,"/api/v5/market/index-tickers",new Dictionary<string,string?>{{"instId",indexId}},ct);index=Dec(d.RootElement.GetProperty("data")[0],"idxPx");}catch{}return new(funding,oi,0,0,0,0,index>0?(mark-index)/index:0);
    }
    public override async Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct){var canonical=Symbols.ToCanonical(symbol);_leverage[canonical]=leverage;var mode=_isolated.GetValueOrDefault(canonical,true)?"isolated":"cross";foreach(var side in new[]{"long","short"}){var body=new Dictionary<string,object?>{{"instId",Symbols.ToNative(canonical)},{"lever",leverage.ToString(CultureInfo.InvariantCulture)},{"mgnMode",mode},{"posSide",side}};using var _=await Private(HttpMethod.Post,"/api/v5/account/set-leverage",null,body,ct);}}
    public override async Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct){var canonical=Symbols.ToCanonical(symbol);_isolated[canonical]=isolated;if(_leverage.TryGetValue(canonical,out var leverage))await SetLeverageAsync(canonical,leverage,ct);}
    public override async Task SetHedgeModeAsync(bool enabled,CancellationToken ct){var body=new Dictionary<string,object?>{{"posMode",enabled?"long_short_mode":"net_mode"}};try{using var _=await Private(HttpMethod.Post,"/api/v5/account/set-position-mode",null,body,ct);}catch(InvalidOperationException ex)when(ex.Message.Contains("already",StringComparison.OrdinalIgnoreCase)||ex.Message.Contains("51000")){} }
    public override Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)=>Place(symbol,side,quantity,0,"market",clientOrderId,reduceOnly,ct);
    public override Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>Place(symbol,side,quantity,price,"limit",clientOrderId,reduceOnly,ct);
    public override async Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(symbol);var position=(await GetPositionsAsync(ct)).FirstOrDefault(x=>x.Symbol==canonical&&x.Side==sideToClose);if(position is null||position.Quantity<=0)throw new InvalidOperationException($"OKX {canonical} position was not found for protection");var contracts=position.Quantity/await ContractValue(Symbols.ToNative(canonical),ct);var body=new Dictionary<string,object?>{{"instId",Symbols.ToNative(canonical)},{"tdMode",position.Isolated?"isolated":"cross"},{"side",sideToClose==PositionSide.Long?"sell":"buy"},{"posSide",sideToClose==PositionSide.Long?"long":"short"},{"ordType","oco"},{"sz",F(contracts)},{"tpTriggerPx",F(takeProfit)},{"tpOrdPx","-1"},{"tpTriggerPxType","mark"},{"slTriggerPx",F(stopLoss)},{"slOrdPx","-1"},{"slTriggerPxType","mark"},{"algoClOrdId",ClientId(groupId)},{"reduceOnly",true}};using var d=await Private(HttpMethod.Post,"/api/v5/trade/order-algo",null,body,ct);var id=Str(d.RootElement.GetProperty("data")[0],"algoId");_algoIds[id]=0;return new(canonical,id,groupId,"NEW",0,0,"OCO",sideToClose,true,DateTime.UtcNow);
    }
    public override async Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct){using var d=await Private(HttpMethod.Get,"/api/v5/trade/order",new Dictionary<string,string?>{{"instId",Symbols.ToNative(symbol)},{"clOrdId",ClientId(clientOrderId)}},null,ct);var data=d.RootElement.GetProperty("data");if(data.GetArrayLength()==0)return null;var mapped=await MapOrder(data[0],false,ct);return mapped with{ClientOrderId=clientOrderId};}
    public override async Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct){var native=Symbols.ToNative(symbol);if(_algoIds.TryRemove(orderId,out _)){var body=new[]{new Dictionary<string,object?>{{"instId",native},{"algoId",orderId}}};using var _=await Private(HttpMethod.Post,"/api/v5/trade/cancel-algos",null,body,ct);}else{var body=new Dictionary<string,object?>{{"instId",native},{"ordId",orderId}};using var _=await Private(HttpMethod.Post,"/api/v5/trade/cancel-order",null,body,ct);}}

    private async Task<IReadOnlyList<CandleEvidence>> Candles(string symbol,string interval,int limit,long? after,long? before,CancellationToken ct){var bar=interval switch{"1m"=>"1m","5m"=>"5m","15m"=>"15m","1h"=>"1H","4h"=>"4H","1d"=>"1D",_=>interval};var q=new Dictionary<string,string?>{{"instId",Symbols.ToNative(symbol)},{"bar",bar},{"limit",Math.Clamp(limit,1,300).ToString(CultureInfo.InvariantCulture)},{"after",after?.ToString(CultureInfo.InvariantCulture)},{"before",before?.ToString(CultureInfo.InvariantCulture)}};using var d=await Public(HttpMethod.Get,"/api/v5/market/candles",q,ct);return d.RootElement.GetProperty("data").EnumerateArray().Select(x=>new CandleEvidence(Millis(x[0].GetString()??""),P(x[1]),P(x[2]),P(x[3]),P(x[4]),P(x[5]),x.GetArrayLength()>7?P(x[7]):0,0,0)).OrderBy(x=>x.OpenTime).ToArray();}
    private async Task<ExchangeOrder> Place(string symbol,PositionSide side,decimal quantity,decimal price,string type,string clientOrderId,bool reduceOnly,CancellationToken ct){var canonical=Symbols.ToCanonical(symbol);var native=Symbols.ToNative(canonical);var contracts=quantity/await ContractValue(native,ct);var orderSide=side==PositionSide.Long?"buy":"sell";if(reduceOnly)orderSide=orderSide=="buy"?"sell":"buy";var mode=_isolated.GetValueOrDefault(canonical,true)?"isolated":"cross";var body=new Dictionary<string,object?>{{"instId",native},{"tdMode",mode},{"clOrdId",ClientId(clientOrderId)},{"side",orderSide},{"posSide",side==PositionSide.Long?"long":"short"},{"ordType",type},{"sz",F(contracts)},{"px",type=="limit"?F(price):null},{"reduceOnly",reduceOnly}};using var d=await Private(HttpMethod.Post,"/api/v5/trade/order",null,body,ct);var x=d.RootElement.GetProperty("data")[0];return new(canonical,Str(x,"ordId"),clientOrderId,"NEW",0,0,type.ToUpperInvariant(),side,false,DateTime.UtcNow);}
    private async Task<ExchangeOrder> MapOrder(JsonElement x,bool algo,CancellationToken ct){var id=algo?Str(x,"algoId"):Str(x,"ordId");if(algo)_algoIds[id]=0;var side=Str(x,"posSide")=="short"?PositionSide.Short:PositionSide.Long;var type=Str(x,"ordType").ToUpperInvariant();var updated=OrderTimestamp(Str(x,"uTime") is {Length:>0} t?t:Str(x,"cTime"));var value=await ContractValue(Str(x,"instId"),ct);return new(Symbols.ToCanonical(Str(x,"instId")),id,algo?Str(x,"algoClOrdId"):Str(x,"clOrdId"),Status(Str(x,"state")),Dec(x,"accFillSz")*value,Dec(x,"avgPx"),type,side,algo,updated);}
    private async Task<decimal> ContractValue(string native,CancellationToken ct){if(_contractValues.TryGetValue(native,out var value)&&value>0)return value;using var d=await Public(HttpMethod.Get,"/api/v5/public/instruments",new Dictionary<string,string?>{{"instType","SWAP"},{"instId",native}},ct);var x=d.RootElement.GetProperty("data")[0];value=Math.Max(Dec(x,"ctVal"),0.00000001m)*Math.Max(Dec(x,"ctMult"),1);_contractValues[native]=value;return value;}
    private async Task<JsonDocument> Public(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,CancellationToken ct){var target=CanonicalTarget(method.Method,path,query);return await SendAsync(()=>{var request=JsonRequest(method,target);if(Profile.IsTestnet)AddHeader(request,"x-simulated-trading","1");return request;},Error,ct);}
    private async Task<JsonDocument> Private(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,object? body,CancellationToken ct)
    {
        var target=CanonicalTarget(method.Method,path,query);var json=CanonicalBody(body);return await SendAsync(()=>{var timestamp=FormatTimestamp(DateTimeOffset.UtcNow);var signature=ComputeSignature(_secret,timestamp,method.Method,target,json);var request=JsonRequest(method,target,json.Length==0?null:json);AddHeader(request,"OK-ACCESS-KEY",_key);AddHeader(request,"OK-ACCESS-SIGN",signature);AddHeader(request,"OK-ACCESS-TIMESTAMP",timestamp);AddHeader(request,"OK-ACCESS-PASSPHRASE",_passphrase);if(Profile.IsTestnet)AddHeader(request,"x-simulated-trading","1");return request;},Error,ct);
    }
    internal static string? Error(JsonElement root)
    {
        var code=Str(root,"code");if(code!="0")return Redact($"{(code.Length==0?"UNKNOWN":code)} {Str(root,"msg")}");
        if(root.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.Array)
            foreach(var item in data.EnumerateArray()){var itemCode=Str(item,"sCode");if(itemCode.Length>0&&itemCode!="0")return Redact($"{itemCode} {Str(item,"sMsg")}");}
        return null;
    }
    private static decimal P(JsonElement value)=>decimal.TryParse(value.GetString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var result)?result:0;
    internal static string Status(string value)=>value switch{"live"=>"NEW","partially_filled"=>"PARTIALLY_FILLED","filled"=>"FILLED","canceled" or "mmp_canceled"=>"CANCELED","order_failed"=>"REJECTED",_=>"UNKNOWN"};
    internal static DateTime ParseServerTime(string value)=>long.TryParse(value,out var milliseconds)?DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime:throw new InvalidOperationException("OKX server time is invalid.");
    internal static ExchangePermissionSnapshot ParsePermissionSnapshot(JsonElement value)
    {
        var permissions=Str(value,"perm").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);var canTrade=permissions.Contains("trade");var canWithdraw=permissions.Contains("withdraw");var warnings=new List<string>();if(!canTrade)warnings.Add("Trade permission was not positively verified.");if(canWithdraw)warnings.Add("Withdrawal permission should be disabled.");return new(true,canTrade,canWithdraw,Str(value,"uid"),warnings);
    }
    internal static DateTime OrderTimestamp(string value)=>long.TryParse(value,out var milliseconds)?DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime:DateTime.UnixEpoch;
    internal static IReadOnlyList<ExchangeOrder> NormalizeOrderEvents(IEnumerable<ExchangeOrder> orders)=>orders.GroupBy(OrderKey,StringComparer.Ordinal).Select(group=>group.OrderByDescending(OrderRank).ThenByDescending(order=>order.UpdatedAt).First()).OrderBy(order=>order.OrderId,StringComparer.Ordinal).ToArray();
    internal static string Failure(Exception exception)=>$"{(exception is HttpRequestException or TimeoutException or TaskCanceledException?"UNAVAILABLE":"UNKNOWN")}: {Redact(exception.Message)}";
    private static string OrderKey(ExchangeOrder order)=>string.IsNullOrWhiteSpace(order.ClientOrderId)?$"order:{order.OrderId}":$"client:{order.ClientOrderId}";
    private static int OrderRank(ExchangeOrder order)=>order.Status switch{"FILLED" or "CANCELED" or "REJECTED"=>3,"UNKNOWN"=>2,"PARTIALLY_FILLED"=>1,_=>0};
    private static string Redact(string value)=>global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(value,180);
    internal static string ComputeSignature(byte[] secret,string timestamp,string method,string target,string body)
    {
        if(secret.Length==0)throw new InvalidOperationException("OKX signing secret is missing.");ValidateTimestamp(timestamp);ValidateMethod(method);ValidatePathAndTarget(target);return Convert.ToBase64String(HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(timestamp+method.ToUpperInvariant()+target+body)));
    }
    internal static string CanonicalTarget(string method,string path,IEnumerable<KeyValuePair<string,string?>>? query){ValidateMethod(method);ValidatePathAndTarget(path);if(path.Contains('?'))throw new InvalidOperationException("OKX signing path is not canonical.");var canonical=CanonicalQuery(query);return canonical.Length==0?path:$"{path}?{canonical}";}
    internal static string CanonicalQuery(IEnumerable<KeyValuePair<string,string?>>? query)
    {
        if(query is null)return string.Empty;var values=query.Where(item=>item.Value is not null).ToArray();RejectDuplicateKeys(values.Select(item=>item.Key),"OKX query");return string.Join("&",values.OrderBy(item=>item.Key,StringComparer.Ordinal).Select(item=>$"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}"));
    }
    internal static string CanonicalBody(object? body)
    {
        if(body is null)return string.Empty;if(body is IEnumerable<KeyValuePair<string,object?>> fields){var values=fields.Where(item=>item.Value is not null).ToArray();RejectDuplicateKeys(values.Select(item=>item.Key),"OKX body");return CanonicalJson(values.ToDictionary(item=>item.Key,item=>item.Value,StringComparer.Ordinal));}return CanonicalJson(body);
    }
    internal static string FormatTimestamp(DateTimeOffset value)=>value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture);
    internal static string Sha256Hex(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static void AddHeader(HttpRequestMessage request,string name,string value){if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(value)||request.Headers.Contains(name)||!request.Headers.TryAddWithoutValidation(name,value))throw new InvalidOperationException("OKX request header is missing, invalid, or duplicated.");}
    private static void ValidateTimestamp(string value){if(!DateTimeOffset.TryParseExact(value,"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out _))throw new InvalidOperationException("OKX signing timestamp is invalid.");}
    private static void ValidateMethod(string method){if(method is not("GET" or "POST"))throw new InvalidOperationException("OKX signing method is unsupported.");}
    private static void ValidatePathAndTarget(string value){if(string.IsNullOrWhiteSpace(value)||!value.StartsWith("/",StringComparison.Ordinal)||value.Contains('#')||value.Any(char.IsControl)||Uri.TryCreate(value,UriKind.Absolute,out _))throw new InvalidOperationException("OKX signing target is invalid.");}
    private static void RejectDuplicateKeys(IEnumerable<string> keys,string subject){var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var key in keys){if(string.IsNullOrWhiteSpace(key)||!set.Add(key))throw new InvalidOperationException($"{subject} contains an invalid or duplicate key.");}}
    private static string CanonicalJson(object value){var element=JsonSerializer.SerializeToElement(value);using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream)){WriteCanonicalJson(writer,element);}return Encoding.UTF8.GetString(stream.ToArray());}
    private static void WriteCanonicalJson(Utf8JsonWriter writer,JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object){var properties=value.EnumerateObject().ToArray();RejectDuplicateKeys(properties.Select(property=>property.Name),"OKX JSON body");writer.WriteStartObject();foreach(var property in properties.OrderBy(property=>property.Name,StringComparer.Ordinal)){writer.WritePropertyName(property.Name);WriteCanonicalJson(writer,property.Value);}writer.WriteEndObject();return;}if(value.ValueKind==JsonValueKind.Array){writer.WriteStartArray();foreach(var item in value.EnumerateArray())WriteCanonicalJson(writer,item);writer.WriteEndArray();return;}value.WriteTo(writer);
    }
    internal static string ClientId(string value)=>"wpe"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..29];
}

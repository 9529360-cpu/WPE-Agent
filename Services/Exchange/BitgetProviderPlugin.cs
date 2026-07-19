using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange;

public sealed class BitgetProviderPlugin:IExchangeProviderPlugin
{
    public static ExchangeProviderDescriptor ProviderDescriptor{get;}=new("bitget","Bitget",ExchangeAssetClass.CryptoCex,true,true,[new("apiKey","API Key",ExchangeCredentialKind.ApiKey),new("secret","API Secret",ExchangeCredentialKind.Secret),new("passphrase","Passphrase",ExchangeCredentialKind.Passphrase)],new HashSet<string>(["account","balance","positions","margin","candles","funding","open-interest","index-price","mark-price","place-order","cancel-order","query-order","protection-orders","health"]));
    public ExchangeProviderDescriptor Descriptor=>ProviderDescriptor;
    public IExchangeProvider Create(ExchangeConnectionProfile profile,IReadOnlyDictionary<string,string> credentials)=>new BitgetExchangeProvider(profile,Required(credentials,"apiKey"),Required(credentials,"secret"),Required(credentials,"passphrase"));
    private static string Required(IReadOnlyDictionary<string,string> values,string key)=>values.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:throw new InvalidOperationException($"Bitget credential '{key}' is incomplete.");
}

public sealed class BitgetExchangeProvider:RestExchangeProviderBase
{
    private const string Product="USDT-FUTURES";
    private readonly string _key,_passphrase;private readonly byte[] _secret;private readonly Dictionary<string,bool> _isolated=new(StringComparer.OrdinalIgnoreCase);
    public override string ProviderId=>"bitget";public override ExchangeProviderDescriptor Descriptor=>BitgetProviderPlugin.ProviderDescriptor;
    public BitgetExchangeProvider(ExchangeConnectionProfile profile,string key,string secret,string passphrase):base(profile){_key=key;_secret=Encoding.UTF8.GetBytes(secret);_passphrase=passphrase;}
    public override async Task<DateTime> GetServerTimeAsync(CancellationToken ct){using var d=await Public(HttpMethod.Get,"/api/v2/public/time",null,ct);return Millis(Str(d.RootElement.GetProperty("data"),"serverTime"));}
    public override async Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct){_ =await GetAccountAsync(ct);return new(true,true,false,"Bitget",["Bitget does not expose complete key permission introspection; trading permission is verified by Testnet order lifecycle."]);}
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
        var q=new Dictionary<string,string?>{{"productType",Product},{"symbol",string.IsNullOrWhiteSpace(symbol)?null:Symbols.ToNative(symbol)}};using var d=await Private(HttpMethod.Get,"/api/v2/mix/order/orders-pending",q,null,ct);var data=d.RootElement.GetProperty("data");var list=data.TryGetProperty("entrustedList",out var regular)?regular.EnumerateArray().Select(x=>MapOrder(x,false)).ToList():[];
        try{var pq=new Dictionary<string,string?>{{"productType",Product},{"planType","profit_loss"},{"symbol",string.IsNullOrWhiteSpace(symbol)?null:Symbols.ToNative(symbol)}};using var plans=await Private(HttpMethod.Get,"/api/v2/mix/order/orders-plan-pending",pq,null,ct);var pd=plans.RootElement.GetProperty("data");if(pd.TryGetProperty("entrustedList",out var pending))list.AddRange(pending.EnumerateArray().Select(x=>MapOrder(x,true)));}catch{/* Protection discovery is best effort; regular orders remain available. */}
        return list;
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
    private ExchangeOrder MapOrder(JsonElement x,bool protection){var side=Str(x,"holdSide")=="short"||Str(x,"side")=="sell"?PositionSide.Short:PositionSide.Long;return new(Symbols.ToCanonical(Str(x,"symbol")),Str(x,"orderId"),Str(x,"clientOid"),Status(Str(x,"status") is {Length:>0}s?s:Str(x,"state")),Dec(x,"baseVolume")>0?Dec(x,"baseVolume"):Dec(x,"filledQty"),Dec(x,"priceAvg"),protection?"TPSL":Str(x,"orderType").ToUpperInvariant(),side,protection,Millis(Str(x,"uTime") is {Length:>0}t?t:Str(x,"cTime")));}
    private async Task<JsonDocument> Public(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,CancellationToken ct){var q=query is null?"":Query(query);var target=q.Length==0?path:$"{path}?{q}";return await SendAsync(()=>JsonRequest(method,target),Error,ct);}
    private async Task<JsonDocument> Private(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,object? body,CancellationToken ct){var q=query is null?"":Query(query);var target=q.Length==0?path:$"{path}?{q}";var json=body is null?null:JsonSerializer.Serialize(body);return await SendAsync(()=>{var timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();var sign=ComputeSignature(_secret,timestamp,method.Method,target,json??"");var request=JsonRequest(method,target,json);request.Headers.Add("ACCESS-KEY",_key);request.Headers.Add("ACCESS-SIGN",sign);request.Headers.Add("ACCESS-TIMESTAMP",timestamp);request.Headers.Add("ACCESS-PASSPHRASE",_passphrase);request.Headers.Add("locale","en-US");if(Profile.IsTestnet)request.Headers.Add("paptrading","1");return request;},Error,ct);}
    private static string? Error(JsonElement root)=>Str(root,"code") is "" or "00000"?null:$"{Str(root,"code")} {Str(root,"msg")}";
    private static decimal P(JsonElement value)=>decimal.TryParse(value.GetString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var result)?result:0;
    private static string Status(string value)=>value.ToLowerInvariant() switch{"live" or "new"=>"NEW","partially_filled" or "partial-fill"=>"PARTIALLY_FILLED","filled" or "full-fill"=>"FILLED","cancelled" or "canceled"=>"CANCELED","rejected"=>"REJECTED",_=>value.ToUpperInvariant()};
    internal static string ComputeSignature(byte[] secret,string timestamp,string method,string target,string body)=>Convert.ToBase64String(HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(timestamp+method.ToUpperInvariant()+target+body)));
}

using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange;

public sealed class BybitProviderPlugin:IExchangeProviderPlugin
{
    public static ExchangeProviderDescriptor ProviderDescriptor{get;}=new("bybit","Bybit",ExchangeAssetClass.CryptoCex,true,true,[new("apiKey","API Key",ExchangeCredentialKind.ApiKey),new("secret","API Secret",ExchangeCredentialKind.Secret)],new HashSet<string>(["account","balance","positions","margin","candles","funding","open-interest","mark-price","place-order","cancel-order","query-order","protection-orders","health"]));
    public ExchangeProviderDescriptor Descriptor=>ProviderDescriptor;
    public IExchangeProvider Create(ExchangeConnectionProfile profile,IReadOnlyDictionary<string,string> credentials)=>new BybitExchangeProvider(profile,Required(credentials,"apiKey"),Required(credentials,"secret"));
    private static string Required(IReadOnlyDictionary<string,string> values,string key)=>values.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:throw new InvalidOperationException($"Bybit credential '{key}' is incomplete.");
}

public sealed class BybitExchangeProvider:RestExchangeProviderBase
{
    private readonly string _key;private readonly byte[] _secret;private readonly Dictionary<string,int> _leverage=new(StringComparer.OrdinalIgnoreCase);
    public override string ProviderId=>"bybit";public override ExchangeProviderDescriptor Descriptor=>BybitProviderPlugin.ProviderDescriptor;
    public BybitExchangeProvider(ExchangeConnectionProfile profile,string key,string secret):base(profile){_key=key;_secret=Encoding.UTF8.GetBytes(secret);}
    public override async Task<DateTime> GetServerTimeAsync(CancellationToken ct){using var d=await Public(HttpMethod.Get,"/v5/market/time",null,ct);var root=d.RootElement;return Millis(Str(root,"time"));}
    public override async Task<ExchangePermissionSnapshot> CheckPermissionsAsync(CancellationToken ct)
    {
        using var d=await Private(HttpMethod.Get,"/v5/user/query-api",null,null,ct);var r=d.RootElement.GetProperty("result");var readOnly=Str(r,"readOnly");var canTrade=readOnly is not("1" or "true" or "True");var uid=Str(r,"userID");return new(true,canTrade,false,string.IsNullOrWhiteSpace(uid)?"Bybit":uid,[]);
    }
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
        var query=new Dictionary<string,string?>{{"category","linear"},{"settleCoin","USDT"},{"symbol",string.IsNullOrWhiteSpace(symbol)?null:Symbols.ToNative(symbol)}};using var d=await Private(HttpMethod.Get,"/v5/order/realtime",query,null,ct);return d.RootElement.GetProperty("result").GetProperty("list").EnumerateArray().Select(MapOrder).ToArray();
    }
    public override async Task<TradingRule> GetRulesAsync(string symbol,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(symbol);using var d=await Public(HttpMethod.Get,"/v5/market/instruments-info",new Dictionary<string,string?>{{"category","linear"},{"symbol",Symbols.ToNative(canonical)}},ct);var x=d.RootElement.GetProperty("result").GetProperty("list")[0];var lot=x.GetProperty("lotSizeFilter");var price=x.GetProperty("priceFilter");var leverage=x.GetProperty("leverageFilter");return new(canonical,Dec(lot,"qtyStep"),Dec(price,"tickSize"),Dec(lot,"minOrderQty"),Dec(lot,"minNotionalValue"),(int)Math.Max(1,Dec(leverage,"maxLeverage")));
    }
    public override async Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)=>await Candles(symbol,interval,limit,null,null,ct);
    public override async Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct)=>await Candles(symbol,interval,limit,new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeMilliseconds(),new DateTimeOffset(end.ToUniversalTime()).ToUnixTimeMilliseconds(),ct);
    protected override async Task<DerivativesSnapshot> GetDerivativesAsync(string symbol,CancellationToken ct)
    {
        using var d=await Public(HttpMethod.Get,"/v5/market/tickers",new Dictionary<string,string?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)}},ct);var list=d.RootElement.GetProperty("result").GetProperty("list");if(list.GetArrayLength()==0)return new(0,0,0,0,0,0,0);var x=list[0];var index=Dec(x,"indexPrice");var mark=Dec(x,"markPrice");return new(Dec(x,"fundingRate"),Dec(x,"openInterest"),0,0,0,0,index>0?(mark-index)/index:0);
    }
    public override async Task SetLeverageAsync(string symbol,int leverage,CancellationToken ct){_leverage[Symbols.ToCanonical(symbol)]=leverage;var body=new Dictionary<string,object?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"buyLeverage",leverage.ToString()},{"sellLeverage",leverage.ToString()}};using var _=await Private(HttpMethod.Post,"/v5/position/set-leverage",null,body,ct);}
    public override async Task SetMarginModeAsync(string symbol,bool isolated,CancellationToken ct){var body=new Dictionary<string,object?>{{"setMarginMode",isolated?"ISOLATED_MARGIN":"REGULAR_MARGIN"}};try{using var _=await Private(HttpMethod.Post,"/v5/account/set-margin-mode",null,body,ct);}catch(InvalidOperationException ex)when(ex.Message.Contains("not modified",StringComparison.OrdinalIgnoreCase)||ex.Message.Contains("110026")){} }
    public override async Task SetHedgeModeAsync(bool enabled,CancellationToken ct){var body=new Dictionary<string,object?>{{"category","linear"},{"coin","USDT"},{"mode",enabled?3:0}};try{using var _=await Private(HttpMethod.Post,"/v5/position/switch-mode",null,body,ct);}catch(InvalidOperationException ex)when(ex.Message.Contains("not modified",StringComparison.OrdinalIgnoreCase)||ex.Message.Contains("110025")){} }
    public override Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)=>Place(symbol,side,quantity,0,"Market",clientOrderId,reduceOnly,ct);
    public override Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct)=>Place(symbol,side,quantity,price,"Limit",clientOrderId,reduceOnly,ct);
    public override async Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)
    {
        var body=new Dictionary<string,object?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"tpslMode","Full"},{"positionIdx",sideToClose==PositionSide.Long?1:2},{"stopLoss",F(stopLoss)},{"takeProfit",F(takeProfit)},{"slTriggerBy","MarkPrice"},{"tpTriggerBy","MarkPrice"}};using var _=await Private(HttpMethod.Post,"/v5/position/trading-stop",null,body,ct);return new(Symbols.ToCanonical(symbol),$"position-tpsl:{groupId}",groupId,"NEW",0,0,"POSITION_TPSL",sideToClose,true,DateTime.UtcNow);
    }
    public override async Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct){var query=new Dictionary<string,string?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"orderLinkId",clientOrderId}};using var d=await Private(HttpMethod.Get,"/v5/order/realtime",query,null,ct);var list=d.RootElement.GetProperty("result").GetProperty("list");return list.GetArrayLength()==0?null:MapOrder(list[0]);}
    public override async Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct){if(orderId.StartsWith("position-tpsl:",StringComparison.Ordinal))return;var body=new Dictionary<string,object?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"orderId",orderId}};using var _=await Private(HttpMethod.Post,"/v5/order/cancel",null,body,ct);}

    private async Task<IReadOnlyList<CandleEvidence>> Candles(string symbol,string interval,int limit,long? start,long? end,CancellationToken ct)
    {
        var map=interval switch{"1m"=>"1","5m"=>"5","15m"=>"15","1h"=>"60","4h"=>"240","1d"=>"D",_=>interval};var q=new Dictionary<string,string?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"interval",map},{"limit",Math.Clamp(limit,1,1000).ToString()},{"start",start?.ToString()},{"end",end?.ToString()}};using var d=await Public(HttpMethod.Get,"/v5/market/kline",q,ct);var rows=d.RootElement.GetProperty("result").GetProperty("list").EnumerateArray().Select(x=>new CandleEvidence(Millis(x[0].GetString()??""),P(x[1]),P(x[2]),P(x[3]),P(x[4]),P(x[5]),P(x[6]),0,0)).OrderBy(x=>x.OpenTime).ToArray();return rows;
    }
    private async Task<ExchangeOrder> Place(string symbol,PositionSide side,decimal quantity,decimal price,string type,string clientOrderId,bool reduceOnly,CancellationToken ct)
    {
        var orderSide=side==PositionSide.Long?"Buy":"Sell";if(reduceOnly)orderSide=orderSide=="Buy"?"Sell":"Buy";var body=new Dictionary<string,object?>{{"category","linear"},{"symbol",Symbols.ToNative(symbol)},{"side",orderSide},{"positionIdx",side==PositionSide.Long?1:2},{"orderType",type},{"qty",F(quantity)},{"price",type=="Limit"?F(price):null},{"timeInForce",type=="Limit"?"GTC":"IOC"},{"orderLinkId",clientOrderId},{"reduceOnly",reduceOnly}};using var d=await Private(HttpMethod.Post,"/v5/order/create",null,body,ct);var r=d.RootElement.GetProperty("result");return new(Symbols.ToCanonical(symbol),Str(r,"orderId"),clientOrderId,"NEW",0,0,type.ToUpperInvariant(),side,false,DateTime.UtcNow);
    }
    private ExchangeOrder MapOrder(JsonElement x){var type=Str(x,"orderType").ToUpperInvariant();var status=Status(Str(x,"orderStatus"));var link=Str(x,"orderLinkId");var side=Str(x,"positionIdx") switch{"1"=>PositionSide.Long,"2"=>PositionSide.Short,_=>Str(x,"side")=="Sell"?PositionSide.Short:PositionSide.Long};var protection=Str(x,"stopOrderType") is not("" or "UNKNOWN")||Str(x,"triggerPrice").Length>0;return new(Symbols.ToCanonical(Str(x,"symbol")),Str(x,"orderId"),link,status,Dec(x,"cumExecQty"),Dec(x,"avgPrice"),type,side,protection,Millis(Str(x,"updatedTime")));}
    private async Task<JsonDocument> Public(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,CancellationToken ct){var suffix=query is null?"":Query(query);var target=suffix.Length==0?path:$"{path}?{suffix}";return await SendAsync(()=>JsonRequest(method,target),Error,ct);}
    private async Task<JsonDocument> Private(HttpMethod method,string path,IReadOnlyDictionary<string,string?>? query,IReadOnlyDictionary<string,object?>? body,CancellationToken ct)
    {
        var q=query is null?"":Query(query);var target=q.Length==0?path:$"{path}?{q}";var json=body is null?null:JsonSerializer.Serialize(body.Where(x=>x.Value is not null).ToDictionary(x=>x.Key,x=>x.Value));return await SendAsync(()=>{var timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();var payload=method==HttpMethod.Get?q:json??"";var sign=ComputeSignature(_secret,timestamp,_key,Profile.ReceiveWindow,payload);var request=JsonRequest(method,target,json);request.Headers.Add("X-BAPI-API-KEY",_key);request.Headers.Add("X-BAPI-SIGN",sign);request.Headers.Add("X-BAPI-TIMESTAMP",timestamp);request.Headers.Add("X-BAPI-RECV-WINDOW",Profile.ReceiveWindow.ToString());return request;},Error,ct);
    }
    private static string? Error(JsonElement root)=>Str(root,"retCode") is "" or "0"?null:$"{Str(root,"retCode")} {Str(root,"retMsg")}";
    private static decimal P(JsonElement value)=>decimal.TryParse(value.GetString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var result)?result:0;
    private static string Status(string value)=>value switch{"New" or "Untriggered"=>"NEW","PartiallyFilled"=>"PARTIALLY_FILLED","Filled"=>"FILLED","Cancelled" or "Deactivated"=>"CANCELED","Rejected"=>"REJECTED",_=>value.ToUpperInvariant()};
    internal static string ComputeSignature(byte[] secret,string timestamp,string key,int receiveWindow,string payload)=>Convert.ToHexString(HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(timestamp+key+receiveWindow+payload))).ToLowerInvariant();
}

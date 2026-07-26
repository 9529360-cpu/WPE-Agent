using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum ExchangeOrderFeeEvidenceStateV1 { Confirmed,Missing,Unsupported,Invalid,Error }

public sealed record ExchangeOrderFeeEvidenceV1(
    string Schema,string ProviderId,string Environment,string Symbol,string OrderId,string ClientOrderId,
    int FillCount,decimal ExecutedQuantity,decimal FeeAmount,string FeeAsset,DateTimeOffset ObservedAtUtc,
    ExchangeOrderFeeEvidenceStateV1 State,string CanonicalSha256,byte[] CanonicalBytes);

public interface IExchangeOrderFeeEvidenceReader
{
    Task<ExchangeOrderFeeEvidenceV1> ReadOrderFeeEvidenceAsync(ExchangeOrder order,CancellationToken ct);
}

public static class ExchangeOrderFeeEvidenceCanonicalizerV1
{
    public const string Schema="wpe.exchange-order-fee-evidence/1.0";
    public static ExchangeOrderFeeEvidenceV1 Create(string providerId,string environment,string symbol,string orderId,string clientOrderId,int fillCount,decimal executedQuantity,decimal feeAmount,string feeAsset,DateTimeOffset observedAtUtc,ExchangeOrderFeeEvidenceStateV1 state)
    {
        var bytes=Bytes(providerId,environment,symbol,orderId,clientOrderId,fillCount,executedQuantity,feeAmount,feeAsset,observedAtUtc,state);
        return new(Schema,providerId,environment,symbol,orderId,clientOrderId,fillCount,executedQuantity,feeAmount,feeAsset,observedAtUtc.ToUniversalTime(),state,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),bytes);
    }
    public static bool IsCanonical(ExchangeOrderFeeEvidenceV1 value)
    {
        if(value.Schema!=Schema||value.CanonicalBytes is null||value.CanonicalSha256.Length!=64)return false;
        if(!Bounded(value.ProviderId,64)||!Bounded(value.Environment,16)||!Bounded(value.Symbol,64)||!Bounded(value.OrderId,128)||!Bounded(value.ClientOrderId,128)||value.FillCount is <0 or >1000||value.ExecutedQuantity<0||value.FeeAmount<0||value.FeeAsset.Length>32||value.ObservedAtUtc.Offset!=TimeSpan.Zero)return false;
        if(value.State==ExchangeOrderFeeEvidenceStateV1.Confirmed&&(value.FillCount==0||value.ExecutedQuantity<=0||string.IsNullOrWhiteSpace(value.FeeAsset)))return false;
        if(value.State!=ExchangeOrderFeeEvidenceStateV1.Confirmed&&(value.FillCount!=0||value.ExecutedQuantity!=0||value.FeeAmount!=0||value.FeeAsset.Length!=0))return false;
        var expected=Create(value.ProviderId,value.Environment,value.Symbol,value.OrderId,value.ClientOrderId,value.FillCount,value.ExecutedQuantity,value.FeeAmount,value.FeeAsset,value.ObservedAtUtc,value.State);
        return CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes,value.CanonicalBytes)&&string.Equals(expected.CanonicalSha256,value.CanonicalSha256,StringComparison.Ordinal);
    }
    private static bool Bounded(string value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max;
    private static byte[] Bytes(string providerId,string environment,string symbol,string orderId,string clientOrderId,int fillCount,decimal executedQuantity,decimal feeAmount,string feeAsset,DateTimeOffset observedAtUtc,ExchangeOrderFeeEvidenceStateV1 state)
    {
        static string E(string value)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var text=string.Join('|',Schema,E(providerId),E(environment),E(symbol),E(orderId),E(clientOrderId),fillCount.ToString(CultureInfo.InvariantCulture),executedQuantity.ToString(CultureInfo.InvariantCulture),feeAmount.ToString(CultureInfo.InvariantCulture),E(feeAsset),observedAtUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),state.ToString());
        return Encoding.UTF8.GetBytes(text);
    }
}

internal static class BinanceOrderFeeEvidenceParserV1
{
    internal static ExchangeOrderFeeEvidenceV1 Parse(JsonElement root,ExchangeOrder order,DateTimeOffset observedAtUtc,string? expectedProviderSymbol=null)
    {
        if(root.ValueKind!=JsonValueKind.Array||root.GetArrayLength()>1000)return Invalid(order,observedAtUtc);
        var seen=new HashSet<string>(StringComparer.Ordinal);decimal quantity=0,fee=0;string? asset=null;var count=0;
        foreach(var trade in root.EnumerateArray())
        {
            if(trade.ValueKind!=JsonValueKind.Object||!TryText(trade,"id",out var id)||!seen.Add(id)||!TryText(trade,"orderId",out var orderId)||orderId!=order.OrderId||!TryText(trade,"symbol",out var symbol)||!string.Equals(symbol,expectedProviderSymbol??order.Symbol,StringComparison.OrdinalIgnoreCase)||!TryDecimal(trade,"qty",out var qty)||qty<=0||!TryDecimal(trade,"price",out var price)||price<=0||!TryDecimal(trade,"commission",out var commission)||commission<0||!TryText(trade,"commissionAsset",out var commissionAsset))return Invalid(order,observedAtUtc);
            if(asset is not null&&!string.Equals(asset,commissionAsset,StringComparison.OrdinalIgnoreCase))return Invalid(order,observedAtUtc);
            asset=commissionAsset.ToUpperInvariant();quantity+=qty;fee+=commission;count++;
        }
        if(count==0)return ExchangeOrderFeeEvidenceCanonicalizerV1.Create("binance-futures","Testnet",order.Symbol,order.OrderId,order.ClientOrderId,0,0,0,"",observedAtUtc,ExchangeOrderFeeEvidenceStateV1.Missing);
        if(quantity!=order.ExecutedQuantity)return Invalid(order,observedAtUtc);
        return ExchangeOrderFeeEvidenceCanonicalizerV1.Create("binance-futures","Testnet",order.Symbol,order.OrderId,order.ClientOrderId,count,quantity,fee,asset??"",observedAtUtc,ExchangeOrderFeeEvidenceStateV1.Confirmed);
    }
    private static ExchangeOrderFeeEvidenceV1 Invalid(ExchangeOrder order,DateTimeOffset observedAtUtc)=>ExchangeOrderFeeEvidenceCanonicalizerV1.Create("binance-futures","Testnet",order.Symbol,order.OrderId,order.ClientOrderId,0,0,0,"",observedAtUtc,ExchangeOrderFeeEvidenceStateV1.Invalid);
    private static bool TryText(JsonElement value,string name,out string result){result="";if(!value.TryGetProperty(name,out var p)||(p.ValueKind!=JsonValueKind.String&&p.ValueKind!=JsonValueKind.Number))return false;result=p.ValueKind==JsonValueKind.String?p.GetString()??"":p.GetRawText();return result.Length is >0 and <=128;}
    private static bool TryDecimal(JsonElement value,string name,out decimal result){result=0;return value.TryGetProperty(name,out var p)&&decimal.TryParse(p.ValueKind==JsonValueKind.String?p.GetString():p.GetRawText(),NumberStyles.Number,CultureInfo.InvariantCulture,out result);}
}

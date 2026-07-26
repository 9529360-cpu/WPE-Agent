using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum FundingObservationStateV1 { Available,Unsupported,Invalid,Error }
public sealed record FundingIncomeEventV1(string ProviderId,string Environment,string Symbol,string TransactionId,DateTimeOffset OccurredAtUtc,decimal Amount,string Asset,string CanonicalSha256,byte[] CanonicalBytes);
public sealed record FundingObservationWindowV1(string Schema,string ProviderId,string Environment,string Symbol,DateTimeOffset StartUtc,DateTimeOffset EndUtc,DateTimeOffset ObservedAtUtc,FundingObservationStateV1 State,int EventCount,string SourceArtifactSha256,string CanonicalSha256,byte[] CanonicalBytes);
public sealed record FundingObservationResultV1(FundingObservationWindowV1 Window,IReadOnlyList<FundingIncomeEventV1> Events);
public interface IExchangeFundingIncomeReader { Task<FundingObservationResultV1> ReadFundingIncomeAsync(string canonicalSymbol,DateTimeOffset startUtc,DateTimeOffset endUtc,CancellationToken ct); }

public static class FundingEvidenceCanonicalizerV1
{
    public const string WindowSchema="wpe.funding-observation-window/1.0";
    public static FundingIncomeEventV1 Event(string provider,string environment,string symbol,string transactionId,DateTimeOffset occurredAtUtc,decimal amount,string asset)
    {
        var bytes=Encoding.UTF8.GetBytes(string.Join('|',"wpe.funding-income-event/1.0",provider,environment,symbol,transactionId,occurredAtUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),amount.ToString(CultureInfo.InvariantCulture),asset));return new(provider,environment,symbol,transactionId,occurredAtUtc.ToUniversalTime(),amount,asset,Hash(bytes),bytes);
    }
    public static FundingObservationWindowV1 Window(string provider,string environment,string symbol,DateTimeOffset startUtc,DateTimeOffset endUtc,DateTimeOffset observedAtUtc,FundingObservationStateV1 state,IReadOnlyList<FundingIncomeEventV1> events,string sourceArtifactSha256)
    {
        var eventHashes=string.Join(',',events.OrderBy(x=>x.OccurredAtUtc).ThenBy(x=>x.TransactionId,StringComparer.Ordinal).Select(x=>x.CanonicalSha256));var bytes=Encoding.UTF8.GetBytes(string.Join('|',WindowSchema,provider,environment,symbol,startUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),endUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),observedAtUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),state,events.Count,sourceArtifactSha256,eventHashes));return new(WindowSchema,provider,environment,symbol,startUtc.ToUniversalTime(),endUtc.ToUniversalTime(),observedAtUtc.ToUniversalTime(),state,events.Count,sourceArtifactSha256,Hash(bytes),bytes);
    }
    public static bool IsCanonical(FundingIncomeEventV1 value)=>Safe(value.ProviderId,64)&&Safe(value.Environment,16)&&Safe(value.Symbol,64)&&Safe(value.TransactionId,128)&&value.OccurredAtUtc.Offset==TimeSpan.Zero&&Safe(value.Asset,32)&&Sha(value.CanonicalSha256)&&Equivalent(Event(value.ProviderId,value.Environment,value.Symbol,value.TransactionId,value.OccurredAtUtc,value.Amount,value.Asset),value);
    public static bool IsCanonical(FundingObservationResultV1 value,DateTimeOffset now)
    {
        var w=value.Window;if(w.Schema!=WindowSchema||!Enum.IsDefined(w.State)||!Safe(w.ProviderId,64)||!Safe(w.Environment,16)||!Safe(w.Symbol,64)||w.StartUtc.Offset!=TimeSpan.Zero||w.EndUtc.Offset!=TimeSpan.Zero||w.ObservedAtUtc.Offset!=TimeSpan.Zero||w.StartUtc>w.EndUtc||w.EndUtc>w.ObservedAtUtc||w.ObservedAtUtc>now.AddMinutes(1)||now-w.ObservedAtUtc>TimeSpan.FromMinutes(5)||w.EventCount!=value.Events.Count||!Sha(w.SourceArtifactSha256)||!Sha(w.CanonicalSha256)||value.Events.Any(x=>!IsCanonical(x)||x.ProviderId!=w.ProviderId||x.Environment!=w.Environment||x.Symbol!=w.Symbol||x.OccurredAtUtc<w.StartUtc||x.OccurredAtUtc>w.EndUtc)||value.Events.GroupBy(x=>x.TransactionId,StringComparer.Ordinal).Any(x=>x.Count()>1))return false;
        if(w.State!=FundingObservationStateV1.Available&&value.Events.Count!=0)return false;return Equivalent(Window(w.ProviderId,w.Environment,w.Symbol,w.StartUtc,w.EndUtc,w.ObservedAtUtc,w.State,value.Events,w.SourceArtifactSha256),w);
    }
    private static bool Equivalent(FundingIncomeEventV1 a,FundingIncomeEventV1 b)=>a.CanonicalSha256==b.CanonicalSha256&&CryptographicOperations.FixedTimeEquals(a.CanonicalBytes,b.CanonicalBytes);
    private static bool Equivalent(FundingObservationWindowV1 a,FundingObservationWindowV1 b)=>a.CanonicalSha256==b.CanonicalSha256&&CryptographicOperations.FixedTimeEquals(a.CanonicalBytes,b.CanonicalBytes);
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();private static bool Sha(string value)=>value.Length==64&&value.All(Uri.IsHexDigit);private static bool Safe(string value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
}

internal static class BinanceFundingIncomeParserV1
{
    internal static IReadOnlyList<FundingIncomeEventV1> Parse(JsonElement root,string canonicalSymbol,string nativeSymbol,DateTimeOffset startUtc,DateTimeOffset endUtc)
    {
        if(root.ValueKind!=JsonValueKind.Array||root.GetArrayLength()>1000)throw new InvalidOperationException("Binance funding response is invalid.");var result=new List<FundingIncomeEventV1>();var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var item in root.EnumerateArray())
        {
            if(!Text(item,"incomeType",out var type)||type!="FUNDING_FEE"||!Text(item,"symbol",out var symbol)||!string.Equals(symbol,nativeSymbol,StringComparison.OrdinalIgnoreCase)||!Text(item,"tranId",out var id)||!ids.Add(id)||!Text(item,"asset",out var asset)||!Decimal(item,"income",out var amount)||!item.TryGetProperty("time",out var time)||!time.TryGetInt64(out var ms))throw new InvalidOperationException("Binance funding event is invalid.");var occurred=DateTimeOffset.FromUnixTimeMilliseconds(ms);if(occurred<startUtc||occurred>endUtc)throw new InvalidOperationException("Binance funding event is outside the requested window.");result.Add(FundingEvidenceCanonicalizerV1.Event("binance-futures","Testnet",canonicalSymbol,id,occurred,amount,asset.ToUpperInvariant()));
        }
        return result;
    }
    private static bool Text(JsonElement item,string name,out string value){value="";if(!item.TryGetProperty(name,out var p)||p.ValueKind is not(JsonValueKind.String or JsonValueKind.Number))return false;value=p.ValueKind==JsonValueKind.String?p.GetString()??"":p.GetRawText();return value.Length is >0 and <=128;}
    private static bool Decimal(JsonElement item,string name,out decimal value){value=0;return item.TryGetProperty(name,out var p)&&decimal.TryParse(p.ValueKind==JsonValueKind.String?p.GetString():p.GetRawText(),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value);}
}

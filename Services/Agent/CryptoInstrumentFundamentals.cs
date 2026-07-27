using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record CryptoInstrumentFundamentalV1(
    string Schema,string ProviderId,string Environment,string Symbol,string NativeSymbol,string BaseAsset,
    string QuoteAsset,string MarginAsset,string ContractType,string TradingStatus,DateTimeOffset OnboardAtUtc,
    DateTimeOffset ObservedAtUtc,string SourceArtifactSha256,string CanonicalSha256,byte[] CanonicalBytes);

public interface ICryptoInstrumentFundamentalReader
{
    Task<IReadOnlyList<CryptoInstrumentFundamentalV1>> GetInstrumentFundamentalsAsync(IReadOnlyList<string> canonicalSymbols,CancellationToken ct);
}

public static class CryptoInstrumentFundamentalCanonicalizerV1
{
    public const string Schema="wpe.crypto-instrument-fundamental/1.0";
    public static CryptoInstrumentFundamentalV1 Create(string providerId,string environment,string symbol,string nativeSymbol,string baseAsset,string quoteAsset,string marginAsset,string contractType,string tradingStatus,DateTimeOffset onboardAtUtc,DateTimeOffset observedAtUtc,string sourceArtifactSha256)
    {
        var bytes=Encoding.UTF8.GetBytes(string.Join('|',Schema,providerId,environment,symbol,nativeSymbol,baseAsset,quoteAsset,marginAsset,contractType,tradingStatus,onboardAtUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),observedAtUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),sourceArtifactSha256));
        return new(Schema,providerId,environment,symbol,nativeSymbol,baseAsset,quoteAsset,marginAsset,contractType,tradingStatus,onboardAtUtc.ToUniversalTime(),observedAtUtc.ToUniversalTime(),sourceArtifactSha256,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),bytes);
    }
    public static bool IsCanonical(CryptoInstrumentFundamentalV1 value,DateTimeOffset now)
    {
        if(value.Schema!=Schema||value.CanonicalBytes is null||value.OnboardAtUtc.Offset!=TimeSpan.Zero||value.ObservedAtUtc.Offset!=TimeSpan.Zero||value.OnboardAtUtc>value.ObservedAtUtc||value.ObservedAtUtc>now||now-value.ObservedAtUtc>TimeSpan.FromHours(24))return false;
        if(value.ProviderId!="binance-futures"||value.Environment!="Testnet"||!Canonical(value.Symbol,5,30)||!Canonical(value.NativeSymbol,5,30)||!Canonical(value.BaseAsset,2,16)||!Canonical(value.QuoteAsset,2,16)||!Canonical(value.MarginAsset,2,16)||value.ContractType!="PERPETUAL"||value.TradingStatus!="TRADING"||!Sha(value.SourceArtifactSha256)||!Sha(value.CanonicalSha256))return false;
        if(value.Symbol!=value.BaseAsset+value.QuoteAsset||value.NativeSymbol!=value.Symbol||value.QuoteAsset!=value.MarginAsset)return false;
        var expected=Create(value.ProviderId,value.Environment,value.Symbol,value.NativeSymbol,value.BaseAsset,value.QuoteAsset,value.MarginAsset,value.ContractType,value.TradingStatus,value.OnboardAtUtc,value.ObservedAtUtc,value.SourceArtifactSha256);
        return string.Equals(expected.CanonicalSha256,value.CanonicalSha256,StringComparison.Ordinal)&&CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes,value.CanonicalBytes);
    }
    private static bool Canonical(string value,int min,int max)=>value.Length>=min&&value.Length<=max&&value.All(c=>c is>='A' and<='Z' or>='0' and<='9');
    private static bool Sha(string value)=>value.Length==64&&value.All(Uri.IsHexDigit);
}

internal static class BinanceInstrumentFundamentalParserV1
{
    internal static IReadOnlyList<CryptoInstrumentFundamentalV1> Parse(JsonElement root,IReadOnlyDictionary<string,string> nativeToCanonical,DateTimeOffset observedAtUtc)
    {
        if(observedAtUtc==default||observedAtUtc.Offset!=TimeSpan.Zero)throw new InvalidOperationException("Binance exchangeInfo observation time must be UTC.");
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("symbols",out var symbols)||symbols.ValueKind!=JsonValueKind.Array||symbols.GetArrayLength()>5000)throw new InvalidOperationException("Binance exchangeInfo symbols are invalid.");
        var requested=new HashSet<string>(nativeToCanonical.Keys,StringComparer.OrdinalIgnoreCase);var result=new List<CryptoInstrumentFundamentalV1>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in symbols.EnumerateArray())
        {
            if(!Text(item,"symbol",out var native)||!requested.Contains(native))continue;if(!seen.Add(native))throw new InvalidOperationException("Binance exchangeInfo contains duplicate requested symbols.");
            if(!Text(item,"status",out var status)||!Text(item,"contractType",out var contract)||!Text(item,"baseAsset",out var baseAsset)||!Text(item,"quoteAsset",out var quoteAsset)||!Text(item,"marginAsset",out var marginAsset)||!item.TryGetProperty("onboardDate",out var onboard)||!onboard.TryGetInt64(out var onboardMs)||onboardMs<=0)continue;
            if(status!="TRADING"||contract!="PERPETUAL")continue;var sourceHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.GetRawText()))).ToLowerInvariant();
            var fact=CryptoInstrumentFundamentalCanonicalizerV1.Create("binance-futures","Testnet",nativeToCanonical[native].ToUpperInvariant(),native.ToUpperInvariant(),baseAsset.ToUpperInvariant(),quoteAsset.ToUpperInvariant(),marginAsset.ToUpperInvariant(),contract,status,DateTimeOffset.FromUnixTimeMilliseconds(onboardMs),observedAtUtc,sourceHash);
            if(!CryptoInstrumentFundamentalCanonicalizerV1.IsCanonical(fact,observedAtUtc))throw new InvalidOperationException("Binance exchangeInfo requested symbol mapping is inconsistent.");
            result.Add(fact);
        }
        return result;
    }
    private static bool Text(JsonElement item,string name,out string value){value="";if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty(name,out var field)||field.ValueKind!=JsonValueKind.String)return false;value=field.GetString()??"";return value.Length is >0 and <=64;}
}

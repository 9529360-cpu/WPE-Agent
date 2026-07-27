using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record TeacherCryptoMarketFactV2(string Schema,string SourceId,string Symbol,DateTimeOffset ObservedAtUtc,DateTimeOffset RetrievedAtUtc,decimal MarkPrice,decimal IndexPrice,decimal FundingRate,DateTimeOffset NextFundingAtUtc,decimal OpenInterest,decimal PriceChangePercent24h,decimal QuoteVolume24h,IReadOnlyList<string> RawEvidenceHashes,string CanonicalSha256,string Status,string DiagnosticCode);

public static class TeacherCryptoMarketFactCanonicalizerV2
{
    public static TeacherCryptoMarketFactV2 Create(string sourceId,string symbol,DateTimeOffset observedAtUtc,DateTimeOffset retrievedAtUtc,decimal markPrice,decimal indexPrice,decimal fundingRate,DateTimeOffset nextFundingAtUtc,decimal openInterest,decimal change24h,decimal quoteVolume24h,IReadOnlyList<string> rawHashes)
    {
        symbol=symbol.ToUpperInvariant();var draft=new TeacherCryptoMarketFactV2("wpe.teacher-crypto-market-fact/2.0",sourceId,symbol,observedAtUtc.ToUniversalTime(),retrievedAtUtc.ToUniversalTime(),markPrice,indexPrice,fundingRate,nextFundingAtUtc.ToUniversalTime(),openInterest,change24h,quoteVolume24h,rawHashes.Order().ToArray(),string.Empty,"available","teacher.crypto.available");Validate(draft,false);return draft with{CanonicalSha256=Hash(Bytes(draft))};
    }
    public static bool IsCanonical(TeacherCryptoMarketFactV2 value)
    {
        try{Validate(value,true);return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value.CanonicalSha256),Encoding.ASCII.GetBytes(Hash(Bytes(value with{CanonicalSha256=string.Empty}))));}catch{return false;}
    }
    private static byte[] Bytes(TeacherCryptoMarketFactV2 x)=>Encoding.UTF8.GetBytes(string.Join('|',x.Schema,x.SourceId,x.Symbol,x.ObservedAtUtc.ToString("O",CultureInfo.InvariantCulture),x.RetrievedAtUtc.ToString("O",CultureInfo.InvariantCulture),x.MarkPrice.ToString(CultureInfo.InvariantCulture),x.IndexPrice.ToString(CultureInfo.InvariantCulture),x.FundingRate.ToString(CultureInfo.InvariantCulture),x.NextFundingAtUtc.ToString("O",CultureInfo.InvariantCulture),x.OpenInterest.ToString(CultureInfo.InvariantCulture),x.PriceChangePercent24h.ToString(CultureInfo.InvariantCulture),x.QuoteVolume24h.ToString(CultureInfo.InvariantCulture),string.Join(',',x.RawEvidenceHashes.Order()),x.Status,x.DiagnosticCode));
    private static void Validate(TeacherCryptoMarketFactV2 x,bool requireHash)
    {
        if(x.Schema!="wpe.teacher-crypto-market-fact/2.0"||x.SourceId!="binance-futures-public"||x.Symbol.Length is <6 or >20||!x.Symbol.All(char.IsAsciiLetterOrDigit)||x.ObservedAtUtc.Offset!=TimeSpan.Zero||x.RetrievedAtUtc.Offset!=TimeSpan.Zero||x.NextFundingAtUtc.Offset!=TimeSpan.Zero||x.ObservedAtUtc>x.RetrievedAtUtc.AddMinutes(1)||x.RetrievedAtUtc-x.ObservedAtUtc>TimeSpan.FromMinutes(5)||x.MarkPrice<=0||x.IndexPrice<=0||x.OpenInterest<0||x.QuoteVolume24h<0||Math.Abs(x.FundingRate)>1||Math.Abs(x.PriceChangePercent24h)>1000||x.RawEvidenceHashes.Count!=3||x.RawEvidenceHashes.Distinct(StringComparer.Ordinal).Count()!=3||x.RawEvidenceHashes.Any(h=>h.Length!=64||!h.All(Uri.IsHexDigit))||x.Status!="available"||x.DiagnosticCode!="teacher.crypto.available"||(requireHash&&(x.CanonicalSha256.Length!=64||!x.CanonicalSha256.All(Uri.IsHexDigit))))throw new InvalidOperationException("Teacher crypto evidence is invalid.");
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class TeacherBinancePublicEvidenceAdapterV2(TeacherPublicEvidenceGatewayV2 gateway)
{
    public static TeacherPublicSourceV2 Source { get; }=new("binance-futures-public","official","fapi.binance.com",["/fapi/v1/"],["application/json"],256_000,TimeSpan.FromSeconds(8),true,"none","Binance Futures public market data","unknown","review-required","https://developers.binance.com/docs/derivatives",new DateOnly(2026,7,27));
    public async Task<TeacherCryptoMarketFactV2?> FetchAsync(string symbol,TeacherNetworkBudgetStateV2 budget,CancellationToken ct)
    {
        symbol=symbol.ToUpperInvariant();if(symbol.Length is <6 or >20||!symbol.All(char.IsAsciiLetterOrDigit))return null;var encoded=Uri.EscapeDataString(symbol);
        var premium=await gateway.GetAsync(Source.SourceId,new($"https://{Source.Host}/fapi/v1/premiumIndex?symbol={encoded}"),budget,ct);var interest=await gateway.GetAsync(Source.SourceId,new($"https://{Source.Host}/fapi/v1/openInterest?symbol={encoded}"),budget,ct);var ticker=await gateway.GetAsync(Source.SourceId,new($"https://{Source.Host}/fapi/v1/ticker/24hr?symbol={encoded}"),budget,ct);if(new[]{premium,interest,ticker}.Any(x=>x.Status!="available"))return null;
        try
        {
            using var p=JsonDocument.Parse(premium.Bytes);using var o=JsonDocument.Parse(interest.Bytes);using var t=JsonDocument.Parse(ticker.Bytes);var pr=p.RootElement;var oi=o.RootElement;var tr=t.RootElement;
            if(Text(pr,"symbol")!=symbol||Text(oi,"symbol")!=symbol||Text(tr,"symbol")!=symbol)return null;var retrieved=new[]{premium.RetrievedAtUtc,interest.RetrievedAtUtc,ticker.RetrievedAtUtc}.Max();var marketTime=DateTimeOffset.FromUnixTimeMilliseconds(Long(pr,"time"));var interestTime=DateTimeOffset.FromUnixTimeMilliseconds(Long(oi,"time"));var closeTime=DateTimeOffset.FromUnixTimeMilliseconds(Long(tr,"closeTime"));var observed=new[]{marketTime,interestTime,closeTime}.Min();if(retrieved-observed>TimeSpan.FromMinutes(5))return null;
            return TeacherCryptoMarketFactCanonicalizerV2.Create(Source.SourceId,symbol,observed,retrieved,Decimal(pr,"markPrice"),Decimal(pr,"indexPrice"),Decimal(pr,"lastFundingRate"),DateTimeOffset.FromUnixTimeMilliseconds(Long(pr,"nextFundingTime")),Decimal(oi,"openInterest"),Decimal(tr,"priceChangePercent"),Decimal(tr,"quoteVolume"),[premium.Sha256,interest.Sha256,ticker.Sha256]);
        }
        catch(Exception ex) when(ex is JsonException or FormatException or InvalidOperationException or OverflowException){return null;}
    }
    private static string Text(JsonElement value,string name)=>value.TryGetProperty(name,out var item)&&item.ValueKind==JsonValueKind.String?item.GetString()!:throw new InvalidOperationException();
    private static decimal Decimal(JsonElement value,string name)=>decimal.Parse(Text(value,name),NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture);
    private static long Long(JsonElement value,string name)=>value.TryGetProperty(name,out var item)&&item.TryGetInt64(out var result)?result:throw new InvalidOperationException();
}

public sealed record TeacherCryptoRefreshResultV2(string Symbol,string Status,string DiagnosticCode,string? EvidenceHash);
public sealed class TeacherCryptoEvidenceCollectorV2(TeacherBinancePublicEvidenceAdapterV2 adapter,AgentSqliteStore store,Func<DateTimeOffset>? utcNow=null)
{
    private readonly Func<DateTimeOffset> _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    public async Task<TeacherCryptoRefreshResultV2> RefreshAsync(string symbol,TeacherNetworkBudgetStateV2 budget,CancellationToken ct)
    {
        var checkedAt=_utcNow().ToUniversalTime();var fact=await adapter.FetchAsync(symbol,budget,ct);if(fact is null){await store.RecordTeacherSourceHealthAsync(TeacherBinancePublicEvidenceAdapterV2.Source.SourceId,checkedAt,"unavailable","teacher.crypto.fetch-unavailable",null,ct);return new(symbol.ToUpperInvariant(),"unavailable","teacher.crypto.fetch-unavailable",null);}var saved=await store.SaveTeacherCryptoEvidenceAsync(fact,ct);if(!saved.Succeeded){await store.RecordTeacherSourceHealthAsync(fact.SourceId,checkedAt,"invalid",saved.Code,null,ct);return new(fact.Symbol,"invalid",saved.Code,null);}return new(fact.Symbol,"available",saved.Code,fact.CanonicalSha256);
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using 币安量化机器人.Services.Exchange;

namespace WpeAgent.ModelOff;

public sealed record MarketAcceptanceSampleV1(
    DateTimeOffset ObservedAtUtc,DateTimeOffset SourceAtUtc,decimal Price,int QualityScore,int CandleCount,string MarketEvidenceSha256);

public sealed record MarketAggregateEvidenceV1(
    string Schema,string RequirementId,string ProviderId,string Environment,string Symbol,
    DateTimeOffset StartedAtUtc,DateTimeOffset CompletedAtUtc,IReadOnlyList<MarketAcceptanceSampleV1> Samples,
    string CanonicalSha256,byte[] CanonicalBytes);

public static class MarketAggregateEvidenceCanonicalizerV1
{
    public const string Schema="wpe.market-aggregate-evidence/1.0";
    public const string LiveReadRequirement="market.live-provider-read";
    public const string SustainedFreshnessRequirement="market.sustained-freshness";
    public static readonly TimeSpan MinimumSustainedWindow=TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumSampleGap=TimeSpan.FromMinutes(6);
    public static readonly TimeSpan MaximumSourceAge=TimeSpan.FromMinutes(20);

    public static MarketAggregateEvidenceV1 Create(string requirementId,string providerId,string environment,string symbol,IReadOnlyList<MarketAcceptanceSampleV1> samples)
    {
        var ordered=samples.OrderBy(x=>x.ObservedAtUtc).ToArray();var started=ordered.FirstOrDefault()?.ObservedAtUtc??default;var completed=ordered.LastOrDefault()?.ObservedAtUtc??default;
        var bytes=Serialize(requirementId,providerId,environment,symbol,started,completed,ordered);var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Schema,requirementId,providerId,environment,symbol,started,completed,ordered,hash,bytes);
    }

    public static bool IsCanonical(MarketAggregateEvidenceV1? value,DateTimeOffset evaluatedAtUtc)
    {
        if(value is null||value.Schema!=Schema||evaluatedAtUtc.Offset!=TimeSpan.Zero||value.ProviderId!="binance-futures"||value.Environment!="Testnet"||!Symbol(value.Symbol)||value.StartedAtUtc.Offset!=TimeSpan.Zero||value.CompletedAtUtc.Offset!=TimeSpan.Zero||value.CompletedAtUtc>evaluatedAtUtc.AddMinutes(1)||evaluatedAtUtc-value.CompletedAtUtc>TimeSpan.FromHours(24))return false;
        var required=value.RequirementId==LiveReadRequirement?1:value.RequirementId==SustainedFreshnessRequirement?4:0;if(value.Samples.Count<required)return false;
        var ordered=value.Samples.OrderBy(x=>x.ObservedAtUtc).ToArray();if(!ordered.SequenceEqual(value.Samples)||ordered[0].ObservedAtUtc!=value.StartedAtUtc||ordered[^1].ObservedAtUtc!=value.CompletedAtUtc)return false;
        if(value.RequirementId==SustainedFreshnessRequirement&&(value.CompletedAtUtc-value.StartedAtUtc<MinimumSustainedWindow||ordered.Zip(ordered.Skip(1),(a,b)=>b.ObservedAtUtc-a.ObservedAtUtc).Any(x=>x<=TimeSpan.Zero||x>MaximumSampleGap)))return false;
        if(ordered.Any(x=>x.ObservedAtUtc.Offset!=TimeSpan.Zero||x.SourceAtUtc.Offset!=TimeSpan.Zero||x.SourceAtUtc>x.ObservedAtUtc.AddMinutes(1)||x.ObservedAtUtc-x.SourceAtUtc>MaximumSourceAge||x.Price<=0||x.QualityScore<65||x.CandleCount<31||!Sha(x.MarketEvidenceSha256)))return false;
        if(value.RequirementId==SustainedFreshnessRequirement&&ordered.Select(x=>x.MarketEvidenceSha256).Distinct(StringComparer.Ordinal).Count()<2)return false;
        var expected=Create(value.RequirementId,value.ProviderId,value.Environment,value.Symbol,value.Samples);return expected.CanonicalSha256==value.CanonicalSha256&&value.CanonicalBytes.Length>0&&CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes,value.CanonicalBytes);
    }

    public static AggregateAcceptanceEvidenceV1 ToAcceptanceEvidence(MarketAggregateEvidenceV1 value,DateTimeOffset evaluatedAtUtc)
    {
        if(!IsCanonical(value,evaluatedAtUtc))throw new InvalidOperationException("Market aggregate evidence is not canonical or eligible.");
        return new(value.RequirementId,ModelOffAggregateAgentV1.Market,AcceptanceEvidenceEnvironmentV1.Target,value.CompletedAtUtc,value.CompletedAtUtc.AddHours(24),true,value.CanonicalSha256,value.CanonicalBytes,value.ProviderId);
    }

    private static byte[] Serialize(string requirementId,string providerId,string environment,string symbol,DateTimeOffset started,DateTimeOffset completed,IReadOnlyList<MarketAcceptanceSampleV1> samples)
    {
        using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream)){writer.WriteStartObject();writer.WriteString("completed_at_utc",completed);writer.WriteString("environment",environment);writer.WriteString("provider_id",providerId);writer.WriteString("requirement_id",requirementId);writer.WriteString("schema",Schema);writer.WriteString("started_at_utc",started);writer.WriteString("symbol",symbol);writer.WritePropertyName("samples");writer.WriteStartArray();foreach(var x in samples){writer.WriteStartObject();writer.WriteNumber("candle_count",x.CandleCount);writer.WriteNumber("price",x.Price);writer.WriteNumber("quality_score",x.QualityScore);writer.WriteString("market_evidence_sha256",x.MarketEvidenceSha256);writer.WriteString("observed_at_utc",x.ObservedAtUtc);writer.WriteString("source_at_utc",x.SourceAtUtc);writer.WriteEndObject();}writer.WriteEndArray();writer.WriteEndObject();}return stream.ToArray();
    }
    private static bool Sha(string value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
    private static bool Symbol(string value)=>value is{Length:>=5 and<=30}&&value.All(x=>x is>='A'and<='Z'or>='0'and<='9');
}

public interface IMarketAcceptanceSourceV1:IAsyncDisposable
{
    Task<币安量化机器人.Services.Agent.MarketEvidence> ReadAsync(string symbol,CancellationToken ct);
}

public sealed class BinanceTestnetMarketAcceptanceSourceV1:IMarketAcceptanceSourceV1
{
    private readonly 币安量化机器人.Services.Agent.BinanceFuturesAdapter _provider=new(new ExchangeConnectionProfile{Id="market-acceptance",ProviderId="binance-futures",DisplayName="Binance Futures Testnet read-only",IsTestnet=true,ExecutionEnabled=false,Endpoint="https://testnet.binancefuture.com",TimeoutSeconds=20},string.Empty,string.Empty);
    public async Task<币安量化机器人.Services.Agent.MarketEvidence> ReadAsync(string symbol,CancellationToken ct)
    {
        var market=await _provider.GetMarketAsync(symbol,ct);var provenance=币安量化机器人.Services.Agent.MarketEvidenceProvenanceCanonicalizerV1.Create(market,"binance-futures","Testnet");return market with{Provenance=provenance};
    }
    public ValueTask DisposeAsync()=>_provider.DisposeAsync();
}

public static class MarketAggregateAcceptanceCollectorV1
{
    public static async Task<MarketAggregateEvidenceV1> CollectAsync(IMarketAcceptanceSourceV1 source,string requirementId,string symbol,int samples,TimeSpan interval,Func<DateTimeOffset>? utcNow=null,Func<TimeSpan,CancellationToken,Task>? delay=null,CancellationToken ct=default)
    {
        if(samples is<1 or>24)throw new ArgumentOutOfRangeException(nameof(samples));if(interval<TimeSpan.Zero||interval>TimeSpan.FromMinutes(30))throw new ArgumentOutOfRangeException(nameof(interval));utcNow??=()=>DateTimeOffset.UtcNow;delay??=Task.Delay;var result=new List<MarketAcceptanceSampleV1>();
        for(var i=0;i<samples;i++)
        {
            if(i>0)await delay(interval,ct);var market=await source.ReadAsync(symbol,ct);if(!币安量化机器人.Services.Agent.MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market))throw new InvalidOperationException("Market source returned noncanonical evidence.");var observed=utcNow().ToUniversalTime();
            result.Add(new(observed,new DateTimeOffset(market.CollectedAt.ToUniversalTime()),market.Price,market.Quality.QualityScore,market.Candles.Count,market.Provenance!.CanonicalSha256));
        }
        return MarketAggregateEvidenceCanonicalizerV1.Create(requirementId,"binance-futures","Testnet",symbol,result);
    }
}

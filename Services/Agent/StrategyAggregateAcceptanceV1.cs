using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record StrategyShadowSampleV1(DateTimeOffset OpenedAtUtc,DateTimeOffset ClosedAtUtc,decimal Price,int Direction,double Confidence,string MarketEvidenceSha256);

public sealed record StrategyAggregateEvidenceV1(
    string Schema,string RequirementId,string ProviderId,string Environment,string Symbol,
    string CandidateVersion,string CandidateSha256,string StrategyId,string StrategyVersion,
    DateTimeOffset ObservedAtUtc,string SourceArtifactSha256,IReadOnlyList<StrategyShadowSampleV1> Samples,
    int RecordedObservations,string FinalLifecycle,bool MutationAttempted,string CanonicalSha256,byte[] CanonicalBytes);

public static class StrategyAggregateEvidenceCanonicalizerV1
{
    public const string Schema="wpe.strategy-aggregate-evidence/1.0";
    public const string ShadowRequirement="strategy.shadow-observation";
    public const int RequiredObservations=24;

    public static StrategyAggregateEvidenceV1 Create(string symbol,MarketAcceptanceCandidateV1 candidate,string strategyId,string strategyVersion,DateTimeOffset observedAtUtc,string sourceArtifactSha256,IReadOnlyList<StrategyShadowSampleV1> samples,int recordedObservations,string finalLifecycle,bool mutationAttempted)
    {
        var ordered=samples.OrderBy(x=>x.OpenedAtUtc).ToArray();var bytes=Serialize(symbol,candidate,strategyId,strategyVersion,observedAtUtc,sourceArtifactSha256,ordered,recordedObservations,finalLifecycle,mutationAttempted);return new(Schema,ShadowRequirement,"binance-futures","Testnet",symbol,candidate.Version,candidate.Sha256,strategyId,strategyVersion,observedAtUtc,sourceArtifactSha256,ordered,recordedObservations,finalLifecycle,mutationAttempted,Hash(bytes),bytes);
    }

    public static bool IsCanonical(StrategyAggregateEvidenceV1? value,DateTimeOffset evaluatedAtUtc)
    {
        if(value is null||value.Schema!=Schema||value.RequirementId!=ShadowRequirement||value.ProviderId!="binance-futures"||value.Environment!="Testnet"||value.Symbol!="BTCUSDT"||!Token(value.StrategyId)||!Token(value.StrategyVersion)||!Version(value.CandidateVersion)||!Sha(value.CandidateSha256)||!Sha(value.SourceArtifactSha256)||value.ObservedAtUtc.Offset!=TimeSpan.Zero||evaluatedAtUtc.Offset!=TimeSpan.Zero||value.ObservedAtUtc>evaluatedAtUtc.AddMinutes(1)||evaluatedAtUtc-value.ObservedAtUtc>TimeSpan.FromDays(7)||value.MutationAttempted||value.FinalLifecycle!="Shadow"||value.RecordedObservations!=RequiredObservations||value.Samples.Count!=RequiredObservations)return false;
        var ordered=value.Samples.OrderBy(x=>x.OpenedAtUtc).ToArray();if(!ordered.SequenceEqual(value.Samples)||ordered.Select(x=>x.OpenedAtUtc).Distinct().Count()!=RequiredObservations||ordered.Zip(ordered.Skip(1),(a,b)=>b.OpenedAtUtc-a.OpenedAtUtc).Any(x=>x!=TimeSpan.FromMinutes(1)))return false;
        if(ordered.Any(x=>x.OpenedAtUtc.Offset!=TimeSpan.Zero||x.ClosedAtUtc.Offset!=TimeSpan.Zero||x.ClosedAtUtc-x.OpenedAtUtc!=TimeSpan.FromMinutes(1)||x.ClosedAtUtc>value.ObservedAtUtc.AddMinutes(1)||x.Price<=0||x.Direction is<-1 or>1||!double.IsFinite(x.Confidence)||x.Confidence is<0 or>1||!Sha(x.MarketEvidenceSha256))||value.ObservedAtUtc-ordered[^1].ClosedAtUtc>TimeSpan.FromMinutes(20)||ordered.Select(x=>x.MarketEvidenceSha256).Distinct(StringComparer.Ordinal).Count()!=RequiredObservations)return false;
        var expected=Create(value.Symbol,new(value.CandidateVersion,value.CandidateSha256),value.StrategyId,value.StrategyVersion,value.ObservedAtUtc,value.SourceArtifactSha256,value.Samples,value.RecordedObservations,value.FinalLifecycle,value.MutationAttempted);return value.CanonicalSha256==expected.CanonicalSha256&&value.CanonicalBytes.Length>0&&CryptographicOperations.FixedTimeEquals(value.CanonicalBytes,expected.CanonicalBytes);
    }

    public static AggregateAcceptanceEvidenceV1 ToAcceptanceEvidence(StrategyAggregateEvidenceV1 value,DateTimeOffset evaluatedAtUtc)
    {
        if(!IsCanonical(value,evaluatedAtUtc))throw new InvalidOperationException("Strategy aggregate evidence is not canonical or eligible.");return new(ShadowRequirement,ModelOffAggregateAgentV1.Strategy,AcceptanceEvidenceEnvironmentV1.Testnet,value.ObservedAtUtc,value.ObservedAtUtc.AddDays(7),true,value.CanonicalSha256,value.CanonicalBytes,value.ProviderId);
    }

    public static bool TryParseCanonical(byte[]? bytes,out StrategyAggregateEvidenceV1? value)
    {
        value=null;if(bytes is not{Length:>0 and<=1_048_576})return false;try{using var document=JsonDocument.Parse(bytes);var root=document.RootElement;if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count()!=15)return false;var samples=root.GetProperty("samples").EnumerateArray().Select(x=>new StrategyShadowSampleV1(x.GetProperty("opened_at_utc").GetDateTimeOffset(),x.GetProperty("closed_at_utc").GetDateTimeOffset(),x.GetProperty("price").GetDecimal(),x.GetProperty("direction").GetInt32(),x.GetProperty("confidence").GetDouble(),x.GetProperty("market_evidence_sha256").GetString()??"")).ToArray();var parsed=Create(root.GetProperty("symbol").GetString()??"",new(root.GetProperty("candidate_version").GetString()??"",root.GetProperty("candidate_sha256").GetString()??""),root.GetProperty("strategy_id").GetString()??"",root.GetProperty("strategy_version").GetString()??"",root.GetProperty("observed_at_utc").GetDateTimeOffset(),root.GetProperty("source_artifact_sha256").GetString()??"",samples,root.GetProperty("recorded_observations").GetInt32(),root.GetProperty("final_lifecycle").GetString()??"",root.GetProperty("mutation_attempted").GetBoolean());if(root.GetProperty("schema").GetString()!=Schema||root.GetProperty("requirement_id").GetString()!=ShadowRequirement||root.GetProperty("provider_id").GetString()!="binance-futures"||root.GetProperty("environment").GetString()!="Testnet"||!CryptographicOperations.FixedTimeEquals(parsed.CanonicalBytes,bytes))return false;value=parsed;return true;}catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or OverflowException){return false;}
    }

    private static byte[] Serialize(string symbol,MarketAcceptanceCandidateV1 candidate,string strategyId,string strategyVersion,DateTimeOffset observed,string sourceHash,IReadOnlyList<StrategyShadowSampleV1> samples,int recorded,string lifecycle,bool mutation)
    {
        using var stream=new MemoryStream();using(var w=new Utf8JsonWriter(stream)){w.WriteStartObject();w.WriteString("candidate_sha256",candidate.Sha256);w.WriteString("candidate_version",candidate.Version);w.WriteString("environment","Testnet");w.WriteString("final_lifecycle",lifecycle);w.WriteBoolean("mutation_attempted",mutation);w.WriteString("observed_at_utc",observed);w.WriteString("provider_id","binance-futures");w.WriteNumber("recorded_observations",recorded);w.WriteString("requirement_id",ShadowRequirement);w.WritePropertyName("samples");w.WriteStartArray();foreach(var x in samples){w.WriteStartObject();w.WriteString("closed_at_utc",x.ClosedAtUtc);w.WriteNumber("confidence",x.Confidence);w.WriteNumber("direction",x.Direction);w.WriteString("market_evidence_sha256",x.MarketEvidenceSha256);w.WriteString("opened_at_utc",x.OpenedAtUtc);w.WriteNumber("price",x.Price);w.WriteEndObject();}w.WriteEndArray();w.WriteString("schema",Schema);w.WriteString("source_artifact_sha256",sourceHash);w.WriteString("strategy_id",strategyId);w.WriteString("strategy_version",strategyVersion);w.WriteString("symbol",symbol);w.WriteEndObject();}return stream.ToArray();
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Sha(string value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
    private static bool Token(string value)=>value is{Length:>0 and<=96}&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '-' or '_' or '.');
    private static bool Version(string value)=>value is{Length:>0 and<=32}&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '.' or '-' or '+');
}

using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.ModelOff;

public sealed record ResearchAcceptanceSourceV1(string Category,string SourceId,string ProviderId,DateTimeOffset AsOfUtc,string ArtifactSha256);
public sealed record ResearchAcceptanceOutputV1(string Capability,string CanonicalSha256,byte[] CanonicalBytes);
public sealed record ResearchAggregateEvidenceV1(string Schema,string RequirementId,string ProviderId,string Environment,string Symbol,string CandidateVersion,string CandidateSha256,DateTimeOffset ObservedAtUtc,IReadOnlyList<ResearchAcceptanceSourceV1> Sources,IReadOnlyList<ResearchAcceptanceOutputV1> Outputs,string CanonicalSha256,byte[] CanonicalBytes);

public static class ResearchAggregateEvidenceCanonicalizerV1
{
    public const string Schema="wpe.research-aggregate-evidence/1.0";
    public const string LiveSourceRequirement="research.live-source-availability";
    public const string ModelOffCycleRequirement="research.model-off-cycle";
    private static readonly string[] Capabilities=["Backtest","Fundamental","Macro","News","Technical"];

    public static ResearchAggregateEvidenceV1 Create(string requirementId,string symbol,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observedAtUtc,IReadOnlyList<ResearchAcceptanceSourceV1> sources,IReadOnlyList<ResearchAcceptanceOutputV1>? outputs=null)
    {
        if(requirementId is not (LiveSourceRequirement or ModelOffCycleRequirement))throw new ArgumentException("Unsupported Research acceptance requirement.",nameof(requirementId));var orderedSources=sources.OrderBy(x=>x.Category,StringComparer.Ordinal).ThenBy(x=>x.SourceId,StringComparer.Ordinal).ToArray();var orderedOutputs=(outputs??[]).OrderBy(x=>x.Capability,StringComparer.Ordinal).ToArray();var bytes=Serialize(requirementId,symbol,candidate,observedAtUtc,orderedSources,orderedOutputs);return new(Schema,requirementId,"multi-source-research","Target",symbol,candidate.Version,candidate.Sha256,observedAtUtc,orderedSources,orderedOutputs,Hash(bytes),bytes);
    }

    public static bool IsCanonical(ResearchAggregateEvidenceV1? value,DateTimeOffset evaluatedAtUtc)
    {
        if(value is null||value.Schema!=Schema||value.ProviderId!="multi-source-research"||value.Environment!="Target"||value.Symbol!="BTCUSDT"||value.ObservedAtUtc.Offset!=TimeSpan.Zero||evaluatedAtUtc.Offset!=TimeSpan.Zero||value.ObservedAtUtc>evaluatedAtUtc.AddMinutes(1)||evaluatedAtUtc-value.ObservedAtUtc>TimeSpan.FromHours(24)||!Sha(value.CandidateSha256))return false;
        if(value.Sources.Count<5||!value.Sources.SequenceEqual(value.Sources.OrderBy(x=>x.Category,StringComparer.Ordinal).ThenBy(x=>x.SourceId,StringComparer.Ordinal))||value.Sources.GroupBy(x=>(x.Category,x.SourceId)).Any(x=>x.Count()!=1)||value.Sources.Any(x=>!Token(x.Category)||!Token(x.SourceId)||!Token(x.ProviderId)||x.AsOfUtc.Offset!=TimeSpan.Zero||x.AsOfUtc>value.ObservedAtUtc.AddMinutes(1)||value.ObservedAtUtc-x.AsOfUtc>TimeSpan.FromDays(62)||!Sha(x.ArtifactSha256)))return false;
        if(!value.Sources.Any(x=>x.Category=="Market"&&x.ProviderId=="binance-futures")||!value.Sources.Any(x=>x.Category=="Fundamental"&&x.ProviderId=="binance-futures")||value.Sources.Count(x=>x.Category=="Macro"&&x.ProviderId=="bls-public-api-v2")<2||!value.Sources.Any(x=>x.Category=="News"))return false;
        if(value.RequirementId==LiveSourceRequirement&&value.Outputs.Count!=0)return false;
        if(value.RequirementId==ModelOffCycleRequirement)
        {
            if(!value.Outputs.Select(x=>x.Capability).SequenceEqual(Capabilities,StringComparer.Ordinal)||value.Outputs.Any(x=>!Sha(x.CanonicalSha256)||x.CanonicalBytes is not{Length:>0 and<=1_048_576}||x.CanonicalSha256!=Hash(x.CanonicalBytes)))return false;
        }
        else if(value.RequirementId!=LiveSourceRequirement)return false;
        var recreated=Serialize(value.RequirementId,value.Symbol,new(value.CandidateVersion,value.CandidateSha256),value.ObservedAtUtc,value.Sources,value.Outputs);return value.CanonicalSha256==Hash(recreated)&&value.CanonicalBytes.Length>0&&CryptographicOperations.FixedTimeEquals(recreated,value.CanonicalBytes);
    }

    public static AggregateAcceptanceEvidenceV1 ToAcceptanceEvidence(ResearchAggregateEvidenceV1 value,DateTimeOffset evaluatedAtUtc)
    {
        if(!IsCanonical(value,evaluatedAtUtc))throw new InvalidOperationException("Research aggregate evidence is not canonical or eligible.");return new(value.RequirementId,ModelOffAggregateAgentV1.Research,AcceptanceEvidenceEnvironmentV1.Target,value.ObservedAtUtc,value.ObservedAtUtc.AddHours(24),true,value.CanonicalSha256,value.CanonicalBytes,value.ProviderId);
    }

    public static bool TryParseCanonical(byte[]? bytes,out ResearchAggregateEvidenceV1? value)
    {
        value=null;if(bytes is not{Length:>0 and<=1_048_576})return false;try{using var d=JsonDocument.Parse(bytes);var r=d.RootElement;if(r.ValueKind!=JsonValueKind.Object||r.EnumerateObject().Count()!=10)return false;var sources=r.GetProperty("sources").EnumerateArray().Select(x=>new ResearchAcceptanceSourceV1(x.GetProperty("category").GetString()??"",x.GetProperty("source_id").GetString()??"",x.GetProperty("provider_id").GetString()??"",x.GetProperty("as_of_utc").GetDateTimeOffset(),x.GetProperty("artifact_sha256").GetString()??"")).ToArray();var outputs=r.GetProperty("outputs").EnumerateArray().Select(x=>new ResearchAcceptanceOutputV1(x.GetProperty("capability").GetString()??"",x.GetProperty("canonical_sha256").GetString()??"",x.GetProperty("canonical_bytes").GetBytesFromBase64())).ToArray();var parsed=Create(r.GetProperty("requirement_id").GetString()??"",r.GetProperty("symbol").GetString()??"",new(r.GetProperty("candidate_version").GetString()??"",r.GetProperty("candidate_sha256").GetString()??""),r.GetProperty("observed_at_utc").GetDateTimeOffset(),sources,outputs);if(r.GetProperty("schema").GetString()!=Schema||r.GetProperty("provider_id").GetString()!="multi-source-research"||r.GetProperty("environment").GetString()!="Target"||!CryptographicOperations.FixedTimeEquals(parsed.CanonicalBytes,bytes))return false;value=parsed;return true;}catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or OverflowException){return false;}
    }

    private static byte[] Serialize(string requirementId,string symbol,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observed,IReadOnlyList<ResearchAcceptanceSourceV1> sources,IReadOnlyList<ResearchAcceptanceOutputV1> outputs)
    {
        using var stream=new MemoryStream();using(var w=new Utf8JsonWriter(stream)){w.WriteStartObject();w.WriteString("candidate_sha256",candidate.Sha256);w.WriteString("candidate_version",candidate.Version);w.WriteString("environment","Target");w.WriteString("observed_at_utc",observed);w.WritePropertyName("outputs");w.WriteStartArray();foreach(var x in outputs){w.WriteStartObject();w.WriteBase64String("canonical_bytes",x.CanonicalBytes);w.WriteString("canonical_sha256",x.CanonicalSha256);w.WriteString("capability",x.Capability);w.WriteEndObject();}w.WriteEndArray();w.WriteString("provider_id","multi-source-research");w.WriteString("requirement_id",requirementId);w.WriteString("schema",Schema);w.WritePropertyName("sources");w.WriteStartArray();foreach(var x in sources){w.WriteStartObject();w.WriteString("artifact_sha256",x.ArtifactSha256);w.WriteString("as_of_utc",x.AsOfUtc);w.WriteString("category",x.Category);w.WriteString("provider_id",x.ProviderId);w.WriteString("source_id",x.SourceId);w.WriteEndObject();}w.WriteEndArray();w.WriteString("symbol",symbol);w.WriteEndObject();}return stream.ToArray();
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Sha(string value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
    private static bool Token(string value)=>value is{Length:>0 and<=96}&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '-' or '_' or '.' or ':');
}

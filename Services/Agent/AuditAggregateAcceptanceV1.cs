using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record AuditAggregateEvidenceV1(
    string Schema,string RequirementId,string ProviderId,string Environment,string CandidateVersion,string CandidateSha256,
    DateTimeOffset ObservedAtUtc,string LiveSourceSha256,IReadOnlyList<string> OutputSha256,IReadOnlyList<string> HandoffSha256,
    int AuditSourceCount,bool RestartDurable,bool IdempotentReplay,string CanonicalSha256,byte[] CanonicalBytes);

public static class AuditAggregateEvidenceCanonicalizerV1
{
    public const string Schema="wpe.audit-aggregate-evidence/1.0";
    public const string LiveCycleRequirement="audit.live-cycle-completeness";
    public const string RestartRequirement="audit.restart-durability";

    public static AuditAggregateEvidenceV1 Create(string requirementId,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observedAtUtc,string liveSourceSha256,IReadOnlyList<string> outputs,IReadOnlyList<string> handoffs,int auditSourceCount,bool restartDurable,bool idempotentReplay)
    {
        var outputValues=outputs.ToArray();var handoffValues=handoffs.ToArray();var bytes=Serialize(requirementId,candidate,observedAtUtc,liveSourceSha256,outputValues,handoffValues,auditSourceCount,restartDurable,idempotentReplay);return new(Schema,requirementId,"binance-futures","Testnet",candidate.Version,candidate.Sha256,observedAtUtc,liveSourceSha256,outputValues,handoffValues,auditSourceCount,restartDurable,idempotentReplay,Hash(bytes),bytes);
    }

    public static bool IsCanonical(AuditAggregateEvidenceV1? value,DateTimeOffset now)
    {
        if(value is null||value.Schema!=Schema||value.RequirementId is not (LiveCycleRequirement or RestartRequirement)||value.ProviderId!="binance-futures"||value.Environment!="Testnet"||now.Offset!=TimeSpan.Zero||value.ObservedAtUtc.Offset!=TimeSpan.Zero||value.ObservedAtUtc>now.AddMinutes(1)||now-value.ObservedAtUtc>TimeSpan.FromHours(24)||!Version(value.CandidateVersion)||!Sha(value.CandidateSha256)||!Sha(value.LiveSourceSha256)||value.OutputSha256.Count!=7||value.HandoffSha256.Count!=6||value.OutputSha256.Any(x=>!Sha(x))||value.HandoffSha256.Any(x=>!Sha(x))||value.OutputSha256.Distinct(StringComparer.Ordinal).Count()!=7||value.HandoffSha256.Distinct(StringComparer.Ordinal).Count()!=6||value.AuditSourceCount!=6)return false;
        if(value.RequirementId==RestartRequirement&&(!value.RestartDurable||!value.IdempotentReplay))return false;
        var expected=Create(value.RequirementId,new(value.CandidateVersion,value.CandidateSha256),value.ObservedAtUtc,value.LiveSourceSha256,value.OutputSha256,value.HandoffSha256,value.AuditSourceCount,value.RestartDurable,value.IdempotentReplay);return expected.CanonicalSha256==value.CanonicalSha256&&CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes,value.CanonicalBytes);
    }

    public static AggregateAcceptanceEvidenceV1 ToAcceptanceEvidence(AuditAggregateEvidenceV1 value,DateTimeOffset now)
    {
        if(!IsCanonical(value,now))throw new InvalidOperationException("Audit aggregate evidence is not canonical or eligible.");return new(value.RequirementId,ModelOffAggregateAgentV1.Audit,AcceptanceEvidenceEnvironmentV1.Testnet,value.ObservedAtUtc,value.ObservedAtUtc.AddHours(24),true,value.CanonicalSha256,value.CanonicalBytes,value.ProviderId);
    }

    public static bool TryParseCanonical(byte[]? bytes,out AuditAggregateEvidenceV1? value)
    {
        value=null;if(bytes is not{Length:>0 and<=1_048_576})return false;try{using var document=JsonDocument.Parse(bytes);var root=document.RootElement;if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count()!=14)return false;var parsed=Create(root.GetProperty("requirement_id").GetString()??"",new(root.GetProperty("candidate_version").GetString()??"",root.GetProperty("candidate_sha256").GetString()??""),root.GetProperty("observed_at_utc").GetDateTimeOffset(),root.GetProperty("live_source_sha256").GetString()??"",root.GetProperty("output_sha256").EnumerateArray().Select(x=>x.GetString()??"").ToArray(),root.GetProperty("handoff_sha256").EnumerateArray().Select(x=>x.GetString()??"").ToArray(),root.GetProperty("audit_source_count").GetInt32(),root.GetProperty("restart_durable").GetBoolean(),root.GetProperty("idempotent_replay").GetBoolean());if(!CryptographicOperations.FixedTimeEquals(parsed.CanonicalBytes,bytes))return false;value=parsed;return true;}catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or OverflowException){return false;}
    }

    private static byte[] Serialize(string requirementId,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observedAtUtc,string liveSourceSha256,IReadOnlyList<string> outputs,IReadOnlyList<string> handoffs,int auditSourceCount,bool restartDurable,bool idempotentReplay)
    {
        using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream)){writer.WriteStartObject();writer.WriteNumber("audit_source_count",auditSourceCount);writer.WriteString("candidate_sha256",candidate.Sha256);writer.WriteString("candidate_version",candidate.Version);writer.WriteString("environment","Testnet");writer.WritePropertyName("handoff_sha256");writer.WriteStartArray();foreach(var hash in handoffs)writer.WriteStringValue(hash);writer.WriteEndArray();writer.WriteBoolean("idempotent_replay",idempotentReplay);writer.WriteString("live_source_sha256",liveSourceSha256);writer.WriteString("observed_at_utc",observedAtUtc);writer.WritePropertyName("output_sha256");writer.WriteStartArray();foreach(var hash in outputs)writer.WriteStringValue(hash);writer.WriteEndArray();writer.WriteString("provider_id","binance-futures");writer.WriteBoolean("restart_durable",restartDurable);writer.WriteString("requirement_id",requirementId);writer.WriteString("schema",Schema);writer.WriteString("scope","audit-only-live-correlation-no-mutation");writer.WriteEndObject();}return stream.ToArray();
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();private static bool Sha(string value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');private static bool Version(string value)=>value is{Length:>=1 and<=32}&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '.' or '-' or '+');
}

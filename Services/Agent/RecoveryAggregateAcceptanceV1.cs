using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record RecoveryAggregateEvidenceV1(
    string Schema,string RequirementId,string ProviderId,string Environment,string Symbol,
    string CandidateVersion,string CandidateSha256,DateTimeOffset ObservedAtUtc,
    string SubmissionSha256,string TerminalOrderSha256,string FirstReconciliationSha256,string SecondReconciliationSha256,
    string FirstReconciliationCode,string SecondReconciliationCode,bool TerminalWithoutFill,bool QueryOnlyAfterRestart,
    bool ResubmitAttempted,bool FinalPositionFlat,bool FinalOpenOrderAbsent,string CanonicalSha256,byte[] CanonicalBytes);

public static class RecoveryAggregateEvidenceCanonicalizerV1
{
    public const string Schema="wpe.recovery-aggregate-evidence/1.0";
    public const string TimeoutRequirement="recovery.live-timeout-reconcile";
    public const string NoResubmitRequirement="recovery.no-resubmit-proof";

    public static RecoveryAggregateEvidenceV1 Create(string requirementId,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observedAtUtc,string submissionSha256,string terminalOrderSha256,string firstReconciliationSha256,string secondReconciliationSha256,string firstReconciliationCode,string secondReconciliationCode,bool terminalWithoutFill,bool queryOnlyAfterRestart,bool resubmitAttempted,bool finalPositionFlat,bool finalOpenOrderAbsent)
    {
        var bytes=Serialize(requirementId,candidate,observedAtUtc,submissionSha256,terminalOrderSha256,firstReconciliationSha256,secondReconciliationSha256,firstReconciliationCode,secondReconciliationCode,terminalWithoutFill,queryOnlyAfterRestart,resubmitAttempted,finalPositionFlat,finalOpenOrderAbsent);
        return new(Schema,requirementId,"binance-futures","Testnet","BTCUSDT",candidate.Version,candidate.Sha256,observedAtUtc,submissionSha256,terminalOrderSha256,firstReconciliationSha256,secondReconciliationSha256,firstReconciliationCode,secondReconciliationCode,terminalWithoutFill,queryOnlyAfterRestart,resubmitAttempted,finalPositionFlat,finalOpenOrderAbsent,Hash(bytes),bytes);
    }

    public static bool IsCanonical(RecoveryAggregateEvidenceV1? value,DateTimeOffset evaluatedAtUtc)
    {
        if(value is null||value.Schema!=Schema||value.RequirementId is not (TimeoutRequirement or NoResubmitRequirement)||value.ProviderId!="binance-futures"||value.Environment!="Testnet"||value.Symbol!="BTCUSDT"||!System.Version.TryParse(value.CandidateVersion,out var version)||version.Major<1||!Hashes(value.CandidateSha256,value.SubmissionSha256,value.TerminalOrderSha256,value.FirstReconciliationSha256,value.SecondReconciliationSha256)||value.ObservedAtUtc.Offset!=TimeSpan.Zero||evaluatedAtUtc.Offset!=TimeSpan.Zero||value.ObservedAtUtc>evaluatedAtUtc.AddMinutes(1)||evaluatedAtUtc-value.ObservedAtUtc>TimeSpan.FromHours(24)||value.FirstReconciliationCode!="review.reconcile-order-terminal"||value.SecondReconciliationCode!="review.reconcile-order-terminal"||!value.TerminalWithoutFill||!value.QueryOnlyAfterRestart||value.ResubmitAttempted||!value.FinalPositionFlat||!value.FinalOpenOrderAbsent)return false;
        var expected=Create(value.RequirementId,new(value.CandidateVersion,value.CandidateSha256),value.ObservedAtUtc,value.SubmissionSha256,value.TerminalOrderSha256,value.FirstReconciliationSha256,value.SecondReconciliationSha256,value.FirstReconciliationCode,value.SecondReconciliationCode,value.TerminalWithoutFill,value.QueryOnlyAfterRestart,value.ResubmitAttempted,value.FinalPositionFlat,value.FinalOpenOrderAbsent);
        return value.CanonicalSha256==expected.CanonicalSha256&&value.CanonicalBytes.Length>0&&CryptographicOperations.FixedTimeEquals(value.CanonicalBytes,expected.CanonicalBytes);
    }

    public static AggregateAcceptanceEvidenceV1 ToAcceptanceEvidence(RecoveryAggregateEvidenceV1 value,DateTimeOffset evaluatedAtUtc)
    {
        if(!IsCanonical(value,evaluatedAtUtc))throw new InvalidOperationException("Recovery aggregate evidence is not canonical or eligible.");return new(value.RequirementId,ModelOffAggregateAgentV1.Recovery,AcceptanceEvidenceEnvironmentV1.Testnet,value.ObservedAtUtc,value.ObservedAtUtc.AddHours(24),true,value.CanonicalSha256,value.CanonicalBytes,value.ProviderId);
    }

    public static bool TryParseCanonical(byte[]? bytes,out RecoveryAggregateEvidenceV1? value)
    {
        value=null;if(bytes is not{Length:>0 and<=1_048_576})return false;try{using var d=JsonDocument.Parse(bytes);var r=d.RootElement;if(r.ValueKind!=JsonValueKind.Object||r.EnumerateObject().Count()!=19)return false;var parsed=Create(r.GetProperty("requirement_id").GetString()??"",new(r.GetProperty("candidate_version").GetString()??"",r.GetProperty("candidate_sha256").GetString()??""),r.GetProperty("observed_at_utc").GetDateTimeOffset(),r.GetProperty("submission_sha256").GetString()??"",r.GetProperty("terminal_order_sha256").GetString()??"",r.GetProperty("first_reconciliation_sha256").GetString()??"",r.GetProperty("second_reconciliation_sha256").GetString()??"",r.GetProperty("first_reconciliation_code").GetString()??"",r.GetProperty("second_reconciliation_code").GetString()??"",r.GetProperty("terminal_without_fill").GetBoolean(),r.GetProperty("query_only_after_restart").GetBoolean(),r.GetProperty("resubmit_attempted").GetBoolean(),r.GetProperty("final_position_flat").GetBoolean(),r.GetProperty("final_open_order_absent").GetBoolean());if(r.GetProperty("schema").GetString()!=Schema||r.GetProperty("provider_id").GetString()!="binance-futures"||r.GetProperty("environment").GetString()!="Testnet"||r.GetProperty("symbol").GetString()!="BTCUSDT"||!CryptographicOperations.FixedTimeEquals(parsed.CanonicalBytes,bytes))return false;value=parsed;return true;}catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or OverflowException){return false;}
    }

    private static byte[] Serialize(string requirementId,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observed,string submission,string terminalOrder,string firstHash,string secondHash,string firstCode,string secondCode,bool terminal,bool queryOnly,bool resubmit,bool flat,bool noOpen)
    {
        using var stream=new MemoryStream();using(var w=new Utf8JsonWriter(stream)){w.WriteStartObject();w.WriteString("candidate_sha256",candidate.Sha256);w.WriteString("candidate_version",candidate.Version);w.WriteBoolean("final_open_order_absent",noOpen);w.WriteBoolean("final_position_flat",flat);w.WriteString("first_reconciliation_code",firstCode);w.WriteString("first_reconciliation_sha256",firstHash);w.WriteString("observed_at_utc",observed);w.WriteString("provider_id","binance-futures");w.WriteBoolean("query_only_after_restart",queryOnly);w.WriteString("environment","Testnet");w.WriteString("requirement_id",requirementId);w.WriteBoolean("resubmit_attempted",resubmit);w.WriteString("schema",Schema);w.WriteString("second_reconciliation_code",secondCode);w.WriteString("second_reconciliation_sha256",secondHash);w.WriteString("submission_sha256",submission);w.WriteString("symbol","BTCUSDT");w.WriteString("terminal_order_sha256",terminalOrder);w.WriteBoolean("terminal_without_fill",terminal);w.WriteEndObject();}return stream.ToArray();
    }
    private static bool Sha(string value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');private static bool Hashes(params string[] values)=>values.All(Sha);private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

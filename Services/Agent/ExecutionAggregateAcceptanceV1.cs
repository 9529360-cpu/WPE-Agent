using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record ExecutionAggregateEvidenceV1(
    string Schema,string RequirementId,string ProviderId,string Environment,string Symbol,
    string CandidateVersion,string CandidateSha256,DateTimeOffset ObservedAtUtc,
    string CorrelationSha256,string OpeningIntentSha256,string OpeningOrderSha256,
    string ClosingIntentSha256,string ClosingOrderSha256,string LifecycleCode,
    bool ExactExchangeCorrelation,bool FinalPositionFlat,bool FinalProtectionOrdersAbsent,
    string CanonicalSha256,byte[] CanonicalBytes);

public static class ExecutionAggregateEvidenceCanonicalizerV1
{
    public const string Schema="wpe.execution-aggregate-evidence/1.0";
    public const string LifecycleRequirement="execution.live-order-lifecycle";
    public const string CorrelationRequirement="execution.exchange-correlation";

    public static ExecutionAggregateEvidenceV1 Create(string requirementId,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observedAtUtc,string correlationSha256,string openingIntentSha256,string openingOrderSha256,string closingIntentSha256,string closingOrderSha256,string lifecycleCode,bool exactExchangeCorrelation,bool finalPositionFlat,bool finalProtectionOrdersAbsent)
    {
        var bytes=Serialize(requirementId,candidate,observedAtUtc,correlationSha256,openingIntentSha256,openingOrderSha256,closingIntentSha256,closingOrderSha256,lifecycleCode,exactExchangeCorrelation,finalPositionFlat,finalProtectionOrdersAbsent);
        return new(Schema,requirementId,"binance-futures","Testnet","BTCUSDT",candidate.Version,candidate.Sha256,observedAtUtc,correlationSha256,openingIntentSha256,openingOrderSha256,closingIntentSha256,closingOrderSha256,lifecycleCode,exactExchangeCorrelation,finalPositionFlat,finalProtectionOrdersAbsent,Hash(bytes),bytes);
    }

    public static bool IsCanonical(ExecutionAggregateEvidenceV1? value,DateTimeOffset evaluatedAtUtc)
    {
        if(value is null||value.Schema!=Schema||value.RequirementId is not (LifecycleRequirement or CorrelationRequirement)||value.ProviderId!="binance-futures"||value.Environment!="Testnet"||value.Symbol!="BTCUSDT"||!Version(value.CandidateVersion)||!Sha(value.CandidateSha256)||value.ObservedAtUtc.Offset!=TimeSpan.Zero||evaluatedAtUtc.Offset!=TimeSpan.Zero||value.ObservedAtUtc>evaluatedAtUtc.AddMinutes(1)||evaluatedAtUtc-value.ObservedAtUtc>TimeSpan.FromHours(24)||!Hashes(value.CorrelationSha256,value.OpeningIntentSha256,value.OpeningOrderSha256,value.ClosingIntentSha256,value.ClosingOrderSha256)||value.LifecycleCode!="execution.testnet-open-protect-close-confirmed"||!value.ExactExchangeCorrelation||!value.FinalPositionFlat||!value.FinalProtectionOrdersAbsent)return false;
        var expected=Create(value.RequirementId,new(value.CandidateVersion,value.CandidateSha256),value.ObservedAtUtc,value.CorrelationSha256,value.OpeningIntentSha256,value.OpeningOrderSha256,value.ClosingIntentSha256,value.ClosingOrderSha256,value.LifecycleCode,value.ExactExchangeCorrelation,value.FinalPositionFlat,value.FinalProtectionOrdersAbsent);
        return value.CanonicalSha256==expected.CanonicalSha256&&value.CanonicalBytes.Length>0&&CryptographicOperations.FixedTimeEquals(value.CanonicalBytes,expected.CanonicalBytes);
    }

    public static AggregateAcceptanceEvidenceV1 ToAcceptanceEvidence(ExecutionAggregateEvidenceV1 value,DateTimeOffset evaluatedAtUtc)
    {
        if(!IsCanonical(value,evaluatedAtUtc))throw new InvalidOperationException("Execution aggregate evidence is not canonical or eligible.");
        return new(value.RequirementId,ModelOffAggregateAgentV1.Execution,AcceptanceEvidenceEnvironmentV1.Testnet,value.ObservedAtUtc,value.ObservedAtUtc.AddHours(24),true,value.CanonicalSha256,value.CanonicalBytes,value.ProviderId);
    }

    public static bool TryParseCanonical(byte[]? bytes,out ExecutionAggregateEvidenceV1? value)
    {
        value=null;if(bytes is not{Length:>0 and<=1_048_576})return false;
        try
        {
            using var d=JsonDocument.Parse(bytes);var r=d.RootElement;if(r.ValueKind!=JsonValueKind.Object||r.EnumerateObject().Count()!=17)return false;
            var parsed=Create(r.GetProperty("requirement_id").GetString()??"",new(r.GetProperty("candidate_version").GetString()??"",r.GetProperty("candidate_sha256").GetString()??""),r.GetProperty("observed_at_utc").GetDateTimeOffset(),r.GetProperty("correlation_sha256").GetString()??"",r.GetProperty("opening_intent_sha256").GetString()??"",r.GetProperty("opening_order_sha256").GetString()??"",r.GetProperty("closing_intent_sha256").GetString()??"",r.GetProperty("closing_order_sha256").GetString()??"",r.GetProperty("lifecycle_code").GetString()??"",r.GetProperty("exact_exchange_correlation").GetBoolean(),r.GetProperty("final_position_flat").GetBoolean(),r.GetProperty("final_protection_orders_absent").GetBoolean());
            if(r.GetProperty("schema").GetString()!=Schema||r.GetProperty("provider_id").GetString()!="binance-futures"||r.GetProperty("environment").GetString()!="Testnet"||r.GetProperty("symbol").GetString()!="BTCUSDT"||!CryptographicOperations.FixedTimeEquals(parsed.CanonicalBytes,bytes))return false;value=parsed;return true;
        }
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or OverflowException){return false;}
    }

    private static byte[] Serialize(string requirementId,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observed,string correlation,string openingIntent,string openingOrder,string closingIntent,string closingOrder,string lifecycle,bool exact,bool flat,bool noProtection)
    {
        using var stream=new MemoryStream();using(var w=new Utf8JsonWriter(stream)){w.WriteStartObject();w.WriteString("candidate_sha256",candidate.Sha256);w.WriteString("candidate_version",candidate.Version);w.WriteString("closing_intent_sha256",closingIntent);w.WriteString("closing_order_sha256",closingOrder);w.WriteString("correlation_sha256",correlation);w.WriteBoolean("exact_exchange_correlation",exact);w.WriteBoolean("final_position_flat",flat);w.WriteBoolean("final_protection_orders_absent",noProtection);w.WriteString("lifecycle_code",lifecycle);w.WriteString("observed_at_utc",observed);w.WriteString("opening_intent_sha256",openingIntent);w.WriteString("opening_order_sha256",openingOrder);w.WriteString("provider_id","binance-futures");w.WriteString("environment","Testnet");w.WriteString("requirement_id",requirementId);w.WriteString("schema",Schema);w.WriteString("symbol","BTCUSDT");w.WriteEndObject();}return stream.ToArray();
    }
    private static bool Version(string value)=>System.Version.TryParse(value,out var parsed)&&parsed.Major>=1;
    private static bool Sha(string value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
    private static bool Hashes(params string[] values)=>values.All(Sha);
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

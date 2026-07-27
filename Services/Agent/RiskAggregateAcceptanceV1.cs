using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record RiskAggregateEvidenceV1(
    string Schema,string RequirementId,string ProviderId,string Environment,string Symbol,
    string CandidateVersion,string CandidateSha256,DateTimeOffset ObservedAtUtc,
    string PermissionSha256,string AccountSha256,string PositionsSha256,string MarketSha256,string RuleSha256,
    string RiskIntentSha256,string RiskLedgerSha256,string PlannedIntentSha256,bool Approved,
    DateTimeOffset? RevalidatedAtUtc,string? RevalidatedAccountSha256,string? RevalidatedPositionsSha256,string? RevalidatedMarketSha256,
    string? AuthorityCode,string? StaleAuthorityCode,string? ConflictingAuthorityCode,bool MutationAttempted,
    string CanonicalSha256,byte[] CanonicalBytes);

public static class RiskAggregateEvidenceCanonicalizerV1
{
    public const string Schema="wpe.risk-aggregate-evidence/1.0";
    public const string PreauthorizationRequirement="risk.live-preauthorization";
    public const string ToctouRequirement="risk.toctou-revalidation";

    public static RiskAggregateEvidenceV1 Create(string requirementId,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observedAtUtc,string permissionSha256,string accountSha256,string positionsSha256,string marketSha256,string ruleSha256,string riskIntentSha256,string riskLedgerSha256,string plannedIntentSha256,bool approved,DateTimeOffset? revalidatedAtUtc=null,string? revalidatedAccountSha256=null,string? revalidatedPositionsSha256=null,string? revalidatedMarketSha256=null,string? authorityCode=null,string? staleAuthorityCode=null,string? conflictingAuthorityCode=null,bool mutationAttempted=false)
    {
        var bytes=Serialize(requirementId,candidate,observedAtUtc,permissionSha256,accountSha256,positionsSha256,marketSha256,ruleSha256,riskIntentSha256,riskLedgerSha256,plannedIntentSha256,approved,revalidatedAtUtc,revalidatedAccountSha256,revalidatedPositionsSha256,revalidatedMarketSha256,authorityCode,staleAuthorityCode,conflictingAuthorityCode,mutationAttempted);return new(Schema,requirementId,"binance-futures","Testnet","BTCUSDT",candidate.Version,candidate.Sha256,observedAtUtc,permissionSha256,accountSha256,positionsSha256,marketSha256,ruleSha256,riskIntentSha256,riskLedgerSha256,plannedIntentSha256,approved,revalidatedAtUtc,revalidatedAccountSha256,revalidatedPositionsSha256,revalidatedMarketSha256,authorityCode,staleAuthorityCode,conflictingAuthorityCode,mutationAttempted,Hash(bytes),bytes);
    }

    public static bool IsCanonical(RiskAggregateEvidenceV1? value,DateTimeOffset evaluatedAtUtc)
    {
        if(value is null||value.Schema!=Schema||value.RequirementId is not (PreauthorizationRequirement or ToctouRequirement)||value.ProviderId!="binance-futures"||value.Environment!="Testnet"||value.Symbol!="BTCUSDT"||!Version(value.CandidateVersion)||!Sha(value.CandidateSha256)||value.ObservedAtUtc.Offset!=TimeSpan.Zero||evaluatedAtUtc.Offset!=TimeSpan.Zero||value.ObservedAtUtc>evaluatedAtUtc.AddMinutes(1)||evaluatedAtUtc-value.ObservedAtUtc>TimeSpan.FromHours(24)||!value.Approved||value.MutationAttempted||!Hashes(value.PermissionSha256,value.AccountSha256,value.PositionsSha256,value.MarketSha256,value.RuleSha256,value.RiskIntentSha256,value.RiskLedgerSha256,value.PlannedIntentSha256))return false;
        if(value.RequirementId==PreauthorizationRequirement)
        {
            if(value.RevalidatedAtUtc is not null||value.RevalidatedAccountSha256 is not null||value.RevalidatedPositionsSha256 is not null||value.RevalidatedMarketSha256 is not null||value.AuthorityCode is not null||value.StaleAuthorityCode is not null||value.ConflictingAuthorityCode is not null)return false;
        }
        else
        {
            if(value.RevalidatedAtUtc is null||value.RevalidatedAtUtc.Value.Offset!=TimeSpan.Zero||value.RevalidatedAtUtc<value.ObservedAtUtc||value.RevalidatedAtUtc-value.ObservedAtUtc>TimeSpan.FromMinutes(2)||!Hashes(value.RevalidatedAccountSha256,value.RevalidatedPositionsSha256,value.RevalidatedMarketSha256)||value.AuthorityCode!="automatic.authority-allowed"||value.StaleAuthorityCode!="automatic.authority-stale"||value.ConflictingAuthorityCode!="automatic.authority-conflicting")return false;
        }
        var expected=Create(value.RequirementId,new(value.CandidateVersion,value.CandidateSha256),value.ObservedAtUtc,value.PermissionSha256,value.AccountSha256,value.PositionsSha256,value.MarketSha256,value.RuleSha256,value.RiskIntentSha256,value.RiskLedgerSha256,value.PlannedIntentSha256,value.Approved,value.RevalidatedAtUtc,value.RevalidatedAccountSha256,value.RevalidatedPositionsSha256,value.RevalidatedMarketSha256,value.AuthorityCode,value.StaleAuthorityCode,value.ConflictingAuthorityCode,value.MutationAttempted);return value.CanonicalSha256==expected.CanonicalSha256&&value.CanonicalBytes.Length>0&&CryptographicOperations.FixedTimeEquals(value.CanonicalBytes,expected.CanonicalBytes);
    }

    public static AggregateAcceptanceEvidenceV1 ToAcceptanceEvidence(RiskAggregateEvidenceV1 value,DateTimeOffset evaluatedAtUtc)
    {
        if(!IsCanonical(value,evaluatedAtUtc))throw new InvalidOperationException("Risk aggregate evidence is not canonical or eligible.");return new(value.RequirementId,ModelOffAggregateAgentV1.Risk,AcceptanceEvidenceEnvironmentV1.Testnet,value.RevalidatedAtUtc??value.ObservedAtUtc,(value.RevalidatedAtUtc??value.ObservedAtUtc).AddHours(24),true,value.CanonicalSha256,value.CanonicalBytes,value.ProviderId);
    }

    public static bool TryParseCanonical(byte[]? bytes,out RiskAggregateEvidenceV1? value)
    {
        value=null;if(bytes is not{Length:>0 and<=1_048_576})return false;try{using var d=JsonDocument.Parse(bytes);var r=d.RootElement;if(r.ValueKind!=JsonValueKind.Object||r.EnumerateObject().Count()!=25)return false;DateTimeOffset? revalidated=r.GetProperty("revalidated_at_utc").ValueKind==JsonValueKind.Null?null:r.GetProperty("revalidated_at_utc").GetDateTimeOffset();string? S(string name)=>r.GetProperty(name).ValueKind==JsonValueKind.Null?null:r.GetProperty(name).GetString();var parsed=Create(r.GetProperty("requirement_id").GetString()??"",new(r.GetProperty("candidate_version").GetString()??"",r.GetProperty("candidate_sha256").GetString()??""),r.GetProperty("observed_at_utc").GetDateTimeOffset(),r.GetProperty("permission_sha256").GetString()??"",r.GetProperty("account_sha256").GetString()??"",r.GetProperty("positions_sha256").GetString()??"",r.GetProperty("market_sha256").GetString()??"",r.GetProperty("rule_sha256").GetString()??"",r.GetProperty("risk_intent_sha256").GetString()??"",r.GetProperty("risk_ledger_sha256").GetString()??"",r.GetProperty("planned_intent_sha256").GetString()??"",r.GetProperty("approved").GetBoolean(),revalidated,S("revalidated_account_sha256"),S("revalidated_positions_sha256"),S("revalidated_market_sha256"),S("authority_code"),S("stale_authority_code"),S("conflicting_authority_code"),r.GetProperty("mutation_attempted").GetBoolean());if(r.GetProperty("schema").GetString()!=Schema||r.GetProperty("provider_id").GetString()!="binance-futures"||r.GetProperty("environment").GetString()!="Testnet"||r.GetProperty("symbol").GetString()!="BTCUSDT"||!CryptographicOperations.FixedTimeEquals(parsed.CanonicalBytes,bytes))return false;value=parsed;return true;}catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or OverflowException){return false;}
    }

    private static byte[] Serialize(string requirementId,MarketAcceptanceCandidateV1 candidate,DateTimeOffset observed,string permission,string account,string positions,string market,string rule,string riskIntent,string riskLedger,string plannedIntent,bool approved,DateTimeOffset? revalidated,string? reAccount,string? rePositions,string? reMarket,string? authority,string? stale,string? conflicting,bool mutation)
    {
        using var stream=new MemoryStream();using(var w=new Utf8JsonWriter(stream)){w.WriteStartObject();w.WriteString("account_sha256",account);w.WriteBoolean("approved",approved);WriteNullable(w,"authority_code",authority);w.WriteString("candidate_sha256",candidate.Sha256);w.WriteString("candidate_version",candidate.Version);WriteNullable(w,"conflicting_authority_code",conflicting);w.WriteString("environment","Testnet");w.WriteString("market_sha256",market);w.WriteBoolean("mutation_attempted",mutation);w.WriteString("observed_at_utc",observed);w.WriteString("permission_sha256",permission);w.WriteString("planned_intent_sha256",plannedIntent);w.WriteString("positions_sha256",positions);w.WriteString("provider_id","binance-futures");if(revalidated is null)w.WriteNull("revalidated_at_utc");else w.WriteString("revalidated_at_utc",revalidated.Value);WriteNullable(w,"revalidated_account_sha256",reAccount);WriteNullable(w,"revalidated_market_sha256",reMarket);WriteNullable(w,"revalidated_positions_sha256",rePositions);w.WriteString("requirement_id",requirementId);w.WriteString("risk_intent_sha256",riskIntent);w.WriteString("risk_ledger_sha256",riskLedger);w.WriteString("rule_sha256",rule);w.WriteString("schema",Schema);WriteNullable(w,"stale_authority_code",stale);w.WriteString("symbol","BTCUSDT");w.WriteEndObject();}return stream.ToArray();
    }
    private static void WriteNullable(Utf8JsonWriter w,string name,string? value){if(value is null)w.WriteNull(name);else w.WriteString(name,value);}
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Hashes(params string?[] values)=>values.All(x=>x is not null&&Sha(x));
    private static bool Sha(string value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
    private static bool Version(string value)=>value is{Length:>0 and<=32}&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '.' or '-' or '+');
}

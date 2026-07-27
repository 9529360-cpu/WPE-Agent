using System.Security.Cryptography;
using System.Text;

namespace WpeAgent.ModelOff;

public enum ModelOffAggregateAgentV1 { Market,Research,Strategy,Risk,Execution,Recovery,Audit }
public enum AcceptanceEvidenceEnvironmentV1 { Local,Target,Testnet }

public sealed record AggregateAcceptanceRequirementV1(
    string Id,ModelOffAggregateAgentV1 Agent,AcceptanceEvidenceEnvironmentV1 Environment,TimeSpan MaximumAge,bool LiveEvidence);

public sealed record AggregateAcceptanceEvidenceV1(
    string RequirementId,ModelOffAggregateAgentV1 Agent,AcceptanceEvidenceEnvironmentV1 Environment,
    DateTimeOffset ObservedAtUtc,DateTimeOffset ValidUntilUtc,bool Passed,string ArtifactSha256,byte[] CanonicalArtifact,string? ProviderId);

public sealed record AggregateAcceptanceAssessmentV1(
    string Schema,ModelOffAggregateAgentV1 Agent,bool Accepted,IReadOnlyList<string> Errors,
    IReadOnlyList<string> SatisfiedRequirements,string EvidenceSetSha256);

public static class ModelOffAggregateAcceptanceGateV1
{
    public const string Schema="wpe.model-off-aggregate-acceptance/1.0";
    private static readonly TimeSpan LocalAge=TimeSpan.FromDays(180);
    private static readonly TimeSpan LiveAge=TimeSpan.FromHours(24);
    private static readonly AggregateAcceptanceRequirementV1[] Matrix=
    [
        R("market.canonical-contract",ModelOffAggregateAgentV1.Market,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("market.adversarial-fail-closed",ModelOffAggregateAgentV1.Market,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("market.live-provider-read",ModelOffAggregateAgentV1.Market,AcceptanceEvidenceEnvironmentV1.Target,LiveAge,true),
        R("market.sustained-freshness",ModelOffAggregateAgentV1.Market,AcceptanceEvidenceEnvironmentV1.Target,LiveAge,true),
        R("research.canonical-contract",ModelOffAggregateAgentV1.Research,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("research.point-in-time-replay",ModelOffAggregateAgentV1.Research,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("research.live-source-availability",ModelOffAggregateAgentV1.Research,AcceptanceEvidenceEnvironmentV1.Target,LiveAge,true),
        R("research.model-off-cycle",ModelOffAggregateAgentV1.Research,AcceptanceEvidenceEnvironmentV1.Target,LiveAge,true),
        R("strategy.lifecycle-contract",ModelOffAggregateAgentV1.Strategy,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("strategy.anti-overfit",ModelOffAggregateAgentV1.Strategy,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("strategy.shadow-observation",ModelOffAggregateAgentV1.Strategy,AcceptanceEvidenceEnvironmentV1.Testnet,TimeSpan.FromDays(7),true),
        R("strategy.no-risk-bypass",ModelOffAggregateAgentV1.Strategy,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("risk.deterministic-gate",ModelOffAggregateAgentV1.Risk,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("risk.adversarial-bypass",ModelOffAggregateAgentV1.Risk,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("risk.live-preauthorization",ModelOffAggregateAgentV1.Risk,AcceptanceEvidenceEnvironmentV1.Testnet,LiveAge,true),
        R("risk.toctou-revalidation",ModelOffAggregateAgentV1.Risk,AcceptanceEvidenceEnvironmentV1.Testnet,LiveAge,true),
        R("execution.mutation-route",ModelOffAggregateAgentV1.Execution,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("execution.idempotency",ModelOffAggregateAgentV1.Execution,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("execution.live-order-lifecycle",ModelOffAggregateAgentV1.Execution,AcceptanceEvidenceEnvironmentV1.Testnet,LiveAge,true),
        R("execution.exchange-correlation",ModelOffAggregateAgentV1.Execution,AcceptanceEvidenceEnvironmentV1.Testnet,LiveAge,true),
        R("recovery.restart-reconciliation",ModelOffAggregateAgentV1.Recovery,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("recovery.unknown-quarantine",ModelOffAggregateAgentV1.Recovery,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("recovery.live-timeout-reconcile",ModelOffAggregateAgentV1.Recovery,AcceptanceEvidenceEnvironmentV1.Testnet,LiveAge,true),
        R("recovery.no-resubmit-proof",ModelOffAggregateAgentV1.Recovery,AcceptanceEvidenceEnvironmentV1.Testnet,LiveAge,true),
        R("audit.append-only",ModelOffAggregateAgentV1.Audit,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("audit.seven-role-correlation",ModelOffAggregateAgentV1.Audit,AcceptanceEvidenceEnvironmentV1.Local,LocalAge),
        R("audit.live-cycle-completeness",ModelOffAggregateAgentV1.Audit,AcceptanceEvidenceEnvironmentV1.Testnet,LiveAge,true),
        R("audit.restart-durability",ModelOffAggregateAgentV1.Audit,AcceptanceEvidenceEnvironmentV1.Testnet,LiveAge,true)
    ];

    public static IReadOnlyList<AggregateAcceptanceRequirementV1> Requirements(ModelOffAggregateAgentV1 agent)
        =>Matrix.Where(x=>x.Agent==agent).OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray();

    public static AggregateAcceptanceAssessmentV1 Evaluate(ModelOffAggregateAgentV1 agent,IReadOnlyList<AggregateAcceptanceEvidenceV1>? evidence,DateTimeOffset nowUtc)
    {
        var errors=new List<string>();var satisfied=new List<string>();var requirements=Requirements(agent);var rows=evidence??[];
        if(nowUtc.Offset!=TimeSpan.Zero)errors.Add("acceptance.now-not-utc");
        if(rows.Any(x=>x.Agent!=agent))errors.Add("acceptance.cross-agent-evidence");
        if(rows.GroupBy(x=>x.RequirementId,StringComparer.Ordinal).Any(x=>x.Count()!=1))errors.Add("acceptance.duplicate-evidence");
        if(rows.Any(x=>!requirements.Any(r=>r.Id==x.RequirementId)))errors.Add("acceptance.unexpected-evidence");
        foreach(var requirement in requirements)
        {
            var matches=rows.Where(x=>x.RequirementId==requirement.Id).ToArray();if(matches.Length==0){errors.Add($"acceptance.missing.{requirement.Id}");continue;}if(matches.Length>1)continue;var row=matches[0];
            if(row.Environment!=requirement.Environment){errors.Add($"acceptance.environment.{requirement.Id}");continue;}
            if(row.ObservedAtUtc.Offset!=TimeSpan.Zero||row.ValidUntilUtc.Offset!=TimeSpan.Zero||row.ObservedAtUtc>nowUtc.AddMinutes(1)||row.ValidUntilUtc<=nowUtc||row.ValidUntilUtc<=row.ObservedAtUtc||row.ValidUntilUtc-row.ObservedAtUtc>requirement.MaximumAge||nowUtc-row.ObservedAtUtc>requirement.MaximumAge){errors.Add($"acceptance.stale.{requirement.Id}");continue;}
            if(!row.Passed){errors.Add($"acceptance.failed.{requirement.Id}");continue;}
            if(!Artifact(row.ArtifactSha256,row.CanonicalArtifact)){errors.Add($"acceptance.hash.{requirement.Id}");continue;}
            if(requirement.LiveEvidence&&!Token(row.ProviderId)){errors.Add($"acceptance.provider.{requirement.Id}");continue;}
            satisfied.Add(requirement.Id);
        }
        var hash=Hash(rows.Where(x=>x.Agent==agent).OrderBy(x=>x.RequirementId,StringComparer.Ordinal));
        return new(Schema,agent,errors.Count==0&&satisfied.Count==requirements.Count,errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),satisfied.Order(StringComparer.Ordinal).ToArray(),hash);
    }

    private static AggregateAcceptanceRequirementV1 R(string id,ModelOffAggregateAgentV1 agent,AcceptanceEvidenceEnvironmentV1 environment,TimeSpan age,bool live=false)=>new(id,agent,environment,age,live);
    private static bool Sha(string value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
    private static bool Artifact(string hash,byte[]? bytes)=>Sha(hash)&&bytes is{Length:>0 and<=1_048_576}&&string.Equals(hash,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),StringComparison.Ordinal);
    private static bool Token(string? value)=>value is{Length:>0 and<=64}&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '-' or '_' or '.');
    private static string Hash(IEnumerable<AggregateAcceptanceEvidenceV1> rows)
    {
        var canonical=string.Join('\n',rows.Select(x=>$"{x.RequirementId}|{x.Agent}|{x.Environment}|{x.ObservedAtUtc:O}|{x.ValidUntilUtc:O}|{x.Passed}|{x.ArtifactSha256}|{x.ProviderId}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

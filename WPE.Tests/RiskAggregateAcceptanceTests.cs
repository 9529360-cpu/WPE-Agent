using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class RiskAggregateAcceptanceTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,7,0,0,TimeSpan.Zero);private static readonly MarketAcceptanceCandidateV1 Candidate=new("3.6.0",Hash("candidate"));
    [Fact]
    public void ExactPreauthorizationAndToctouArtifactsSatisfyOnlyRiskTestnetRequirements()
    {
        var pre=Create(RiskAggregateEvidenceCanonicalizerV1.PreauthorizationRequirement);Assert.True(RiskAggregateEvidenceCanonicalizerV1.IsCanonical(pre,Now));var toctou=Create(RiskAggregateEvidenceCanonicalizerV1.ToctouRequirement,true);Assert.True(RiskAggregateEvidenceCanonicalizerV1.IsCanonical(toctou,Now));Assert.True(RiskAggregateEvidenceCanonicalizerV1.TryParseCanonical(toctou.CanonicalBytes,out var parsed));Assert.Equal(toctou.CanonicalSha256,parsed!.CanonicalSha256);var evidence=RiskAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(toctou,Now);Assert.Equal(ModelOffAggregateAgentV1.Risk,evidence.Agent);Assert.Equal(AcceptanceEvidenceEnvironmentV1.Testnet,evidence.Environment);
    }
    [Fact]
    public void SyntheticMissingStaleMutatingAndIncorrectRevalidationFailClosed()
    {
        var pre=Create(RiskAggregateEvidenceCanonicalizerV1.PreauthorizationRequirement);var toctou=Create(RiskAggregateEvidenceCanonicalizerV1.ToctouRequirement,true);Assert.False(RiskAggregateEvidenceCanonicalizerV1.IsCanonical(pre with{Approved=false},Now));Assert.False(RiskAggregateEvidenceCanonicalizerV1.IsCanonical(pre with{AccountSha256="bad"},Now));Assert.False(RiskAggregateEvidenceCanonicalizerV1.IsCanonical(pre with{MutationAttempted=true},Now));Assert.False(RiskAggregateEvidenceCanonicalizerV1.IsCanonical(toctou with{AuthorityCode="automatic.authority-unknown"},Now));Assert.False(RiskAggregateEvidenceCanonicalizerV1.IsCanonical(toctou with{RevalidatedAtUtc=Now.AddMinutes(3)},Now.AddMinutes(3)));Assert.False(RiskAggregateEvidenceCanonicalizerV1.IsCanonical(toctou with{CanonicalBytes=Encoding.UTF8.GetBytes("tampered")},Now));Assert.False(RiskAggregateEvidenceCanonicalizerV1.TryParseCanonical(Encoding.UTF8.GetBytes("{}"),out _));
    }
    [Fact]
    public void LiveAcceptanceRunnerUsesEncryptedSettingsThroughAnExecutionDisabledReadOnlyPath()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","Agent","RiskAggregateAcceptanceRunnerV1.cs"));Assert.Contains("GetExchangeCredentials",source,StringComparison.Ordinal);Assert.Contains("ExecutionEnabled=false",source,StringComparison.Ordinal);Assert.Contains("CheckPermissionsAsync",source,StringComparison.Ordinal);Assert.Contains("GetAccountAsync",source,StringComparison.Ordinal);Assert.Contains("GetPositionsAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceMarketAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceLimitAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("CancelOrderAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("SaveEnvironmentExchange",source,StringComparison.Ordinal);
    }
    private static RiskAggregateEvidenceV1 Create(string requirement,bool revalidated=false)=>RiskAggregateEvidenceCanonicalizerV1.Create(requirement,Candidate,Now,Hash("permission"),Hash("account"),Hash("positions"),Hash("market"),Hash("rule"),Hash("risk-intent"),Hash("risk-ledger"),Hash("plan"),true,revalidated?Now:null,revalidated?Hash("account-2"):null,revalidated?Hash("positions-2"):null,revalidated?Hash("market-2"):null,revalidated?"automatic.authority-allowed":null,revalidated?"automatic.authority-stale":null,revalidated?"automatic.authority-conflicting":null,false);
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

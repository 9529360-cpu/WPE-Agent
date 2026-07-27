using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class StrategyAggregateAcceptanceTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,6,0,0,TimeSpan.Zero);
    private static readonly MarketAcceptanceCandidateV1 Candidate=new("3.6.0",Hash("candidate"));

    [Fact]
    public void ExactClosedTestnetShadowSequenceProducesCandidateBoundEvidence()
    {
        var artifact=Create(Samples());Assert.True(StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(artifact,Now));Assert.True(StrategyAggregateEvidenceCanonicalizerV1.TryParseCanonical(artifact.CanonicalBytes,out var parsed));Assert.Equal(artifact.CanonicalSha256,parsed!.CanonicalSha256);var evidence=StrategyAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(artifact,Now);Assert.Equal(ModelOffAggregateAgentV1.Strategy,evidence.Agent);Assert.Equal(AcceptanceEvidenceEnvironmentV1.Testnet,evidence.Environment);
    }

    [Fact]
    public void RepeatedOpenStaleIncompleteMutatingPromotedAndTamperedEvidenceFailClosed()
    {
        var samples=Samples();var valid=Create(samples);Assert.False(StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(Create(samples[..^1],23),Now));Assert.False(StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(Create([samples[0],..samples[1..^1],samples[0]]),Now));Assert.False(StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(Create(samples.Select((x,i)=>i==23?x with{ClosedAtUtc=Now.AddMinutes(-21),OpenedAtUtc=Now.AddMinutes(-22)}:x).OrderBy(x=>x.OpenedAtUtc).ToArray()),Now));Assert.False(StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(valid with{MutationAttempted=true},Now));Assert.False(StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(valid with{FinalLifecycle="Active"},Now));Assert.False(StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(valid with{CanonicalBytes=Encoding.UTF8.GetBytes("tampered")},Now));Assert.False(StrategyAggregateEvidenceCanonicalizerV1.TryParseCanonical(Encoding.UTF8.GetBytes("{}"),out _));
    }

    [Fact]
    public void TestnetAcceptanceRunnerHasNoCredentialOrMutationPath()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","Agent","StrategyAggregateAcceptanceRunnerV1.cs"));Assert.Contains("ExecutionEnabled=false",source,StringComparison.Ordinal);Assert.Contains("string.Empty,string.Empty",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceOrderAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("CancelOrderAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("Mainnet",source,StringComparison.Ordinal);
    }

    private static StrategyAggregateEvidenceV1 Create(IReadOnlyList<StrategyShadowSampleV1> samples,int recorded=24)=>StrategyAggregateEvidenceCanonicalizerV1.Create("BTCUSDT",Candidate,"acceptance-shadow","trendbreakout-1",Now,Hash("source"),samples,recorded,"Shadow",false);
    private static StrategyShadowSampleV1[] Samples()=>Enumerable.Range(0,24).Select(i=>{var open=Now.AddMinutes(-24+i);return new StrategyShadowSampleV1(open,open.AddMinutes(1),100+i,i%3-1,.5,Hash("market-"+i));}).ToArray();
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

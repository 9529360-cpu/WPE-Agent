using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class ExecutionAggregateAcceptanceTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,6,30,0,TimeSpan.Zero);

    [Theory]
    [InlineData(ExecutionAggregateEvidenceCanonicalizerV1.LifecycleRequirement)]
    [InlineData(ExecutionAggregateEvidenceCanonicalizerV1.CorrelationRequirement)]
    public void CompleteCandidateBoundTestnetLifecycleProducesExecutionEvidence(string requirement)
    {
        var value=Valid(requirement);Assert.True(ExecutionAggregateEvidenceCanonicalizerV1.IsCanonical(value,Now));Assert.True(ExecutionAggregateEvidenceCanonicalizerV1.TryParseCanonical(value.CanonicalBytes,out var parsed));Assert.Equal(value.CanonicalSha256,parsed!.CanonicalSha256);
        var evidence=ExecutionAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(value,Now);Assert.Equal(ModelOffAggregateAgentV1.Execution,evidence.Agent);Assert.Equal(AcceptanceEvidenceEnvironmentV1.Testnet,evidence.Environment);Assert.Equal("binance-futures",evidence.ProviderId);
    }

    [Fact]
    public void TamperingStalenessResidueAndIncompleteCorrelationFailClosed()
    {
        var value=Valid(ExecutionAggregateEvidenceCanonicalizerV1.LifecycleRequirement);
        Assert.False(ExecutionAggregateEvidenceCanonicalizerV1.IsCanonical(value with{CanonicalBytes=Encoding.UTF8.GetBytes("tampered")},Now));
        Assert.False(ExecutionAggregateEvidenceCanonicalizerV1.IsCanonical(value with{ObservedAtUtc=Now.AddHours(-25)},Now));
        Assert.False(ExecutionAggregateEvidenceCanonicalizerV1.IsCanonical(value with{ExactExchangeCorrelation=false},Now));
        Assert.False(ExecutionAggregateEvidenceCanonicalizerV1.IsCanonical(value with{FinalPositionFlat=false},Now));
        Assert.False(ExecutionAggregateEvidenceCanonicalizerV1.IsCanonical(value with{FinalProtectionOrdersAbsent=false},Now));
        Assert.False(ExecutionAggregateEvidenceCanonicalizerV1.TryParseCanonical(Encoding.UTF8.GetBytes("{}"),out _));
    }

    [Fact]
    public void LiveRunnerIsPinnedToOfficialTestnetAndSharedGateway()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","Agent","ExecutionAggregateAcceptanceRunnerV1.cs"));
        Assert.Contains("ProviderEndpointPolicy.IsOfficialHttpsOrigin",source,StringComparison.Ordinal);Assert.Contains("ExecutionEnabled=true",source,StringComparison.Ordinal);Assert.Contains("TradingExecutionGateway",source,StringComparison.Ordinal);Assert.Contains("ExecuteTestnetSmokeAsync",source,StringComparison.Ordinal);Assert.Contains("GetExchangeCredentials",source,StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceMarketAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceLimitAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("CancelOrderAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("Mainnet",source,StringComparison.Ordinal);
    }

    private static ExecutionAggregateEvidenceV1 Valid(string requirement)=>ExecutionAggregateEvidenceCanonicalizerV1.Create(requirement,new("3.6.0.0",Hash("candidate")),Now.AddMinutes(-1),Hash("correlation"),Hash("opening-intent"),Hash("opening-order"),Hash("closing-intent"),Hash("closing-order"),"execution.testnet-open-protect-close-confirmed",true,true,true);
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

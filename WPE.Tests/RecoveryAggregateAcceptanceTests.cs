using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class RecoveryAggregateAcceptanceTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,7,0,0,TimeSpan.Zero);

    [Theory]
    [InlineData(RecoveryAggregateEvidenceCanonicalizerV1.TimeoutRequirement)]
    [InlineData(RecoveryAggregateEvidenceCanonicalizerV1.NoResubmitRequirement)]
    public void TerminalTimeoutAndRepeatedReadOnlyReconciliationProduceRecoveryEvidence(string requirement)
    {
        var value=Valid(requirement);Assert.True(RecoveryAggregateEvidenceCanonicalizerV1.IsCanonical(value,Now));Assert.True(RecoveryAggregateEvidenceCanonicalizerV1.TryParseCanonical(value.CanonicalBytes,out var parsed));Assert.Equal(value.CanonicalSha256,parsed!.CanonicalSha256);var evidence=RecoveryAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(value,Now);Assert.Equal(ModelOffAggregateAgentV1.Recovery,evidence.Agent);Assert.Equal(AcceptanceEvidenceEnvironmentV1.Testnet,evidence.Environment);
    }

    [Fact]
    public void TamperingStalenessResubmitAndResidueFailClosed()
    {
        var value=Valid(RecoveryAggregateEvidenceCanonicalizerV1.TimeoutRequirement);Assert.False(RecoveryAggregateEvidenceCanonicalizerV1.IsCanonical(value with{CanonicalBytes=Encoding.UTF8.GetBytes("tampered")},Now));Assert.False(RecoveryAggregateEvidenceCanonicalizerV1.IsCanonical(value with{ObservedAtUtc=Now.AddHours(-25)},Now));Assert.False(RecoveryAggregateEvidenceCanonicalizerV1.IsCanonical(value with{ResubmitAttempted=true},Now));Assert.False(RecoveryAggregateEvidenceCanonicalizerV1.IsCanonical(value with{QueryOnlyAfterRestart=false},Now));Assert.False(RecoveryAggregateEvidenceCanonicalizerV1.IsCanonical(value with{FinalPositionFlat=false},Now));Assert.False(RecoveryAggregateEvidenceCanonicalizerV1.IsCanonical(value with{FinalOpenOrderAbsent=false},Now));Assert.False(RecoveryAggregateEvidenceCanonicalizerV1.TryParseCanonical(Encoding.UTF8.GetBytes("{}"),out _));
    }

    [Fact]
    public void LiveRunnerPinsOfficialTestnetAndRestartPathHasNoDirectMutation()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","Agent","RecoveryAggregateAcceptanceRunnerV1.cs"));Assert.Contains("ProviderEndpointPolicy.IsOfficialHttpsOrigin",source,StringComparison.Ordinal);Assert.Contains("TradingExecutionGateway",source,StringComparison.Ordinal);Assert.Contains("ReconcileAsync",source,StringComparison.Ordinal);Assert.Contains("RecoverPendingAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceMarketAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceLimitAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("CancelOrderAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("Mainnet",source,StringComparison.Ordinal);
    }

    private static RecoveryAggregateEvidenceV1 Valid(string requirement)=>RecoveryAggregateEvidenceCanonicalizerV1.Create(requirement,new("3.6.0.0",Hash("candidate")),Now.AddMinutes(-1),Hash("submission"),Hash("terminal"),Hash("first"),Hash("second"),"review.reconcile-order-terminal","review.reconcile-order-terminal",true,true,false,true,true);
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

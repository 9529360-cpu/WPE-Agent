using System.Security.Cryptography;
using System.Text;
using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class AuditAggregateAcceptanceTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,8,0,0,TimeSpan.Zero);
    [Theory]
    [InlineData(AuditAggregateEvidenceCanonicalizerV1.LiveCycleRequirement)]
    [InlineData(AuditAggregateEvidenceCanonicalizerV1.RestartRequirement)]
    public void CanonicalEvidenceBindsCompleteSevenRoleChain(string requirement)
    {
        var value=Valid(requirement);Assert.True(AuditAggregateEvidenceCanonicalizerV1.IsCanonical(value,Now));Assert.True(AuditAggregateEvidenceCanonicalizerV1.TryParseCanonical(value.CanonicalBytes,out var parsed));Assert.Equal(value.CanonicalSha256,parsed!.CanonicalSha256);var evidence=AuditAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(value,Now);Assert.Equal(ModelOffAggregateAgentV1.Audit,evidence.Agent);Assert.Equal(AcceptanceEvidenceEnvironmentV1.Testnet,evidence.Environment);
    }
    [Fact]
    public void MissingCorrelationOrRestartProofFailsClosed()
    {
        var value=Valid(AuditAggregateEvidenceCanonicalizerV1.RestartRequirement);Assert.False(AuditAggregateEvidenceCanonicalizerV1.IsCanonical(value with{OutputSha256=value.OutputSha256.Take(6).ToArray()},Now));Assert.False(AuditAggregateEvidenceCanonicalizerV1.IsCanonical(value with{HandoffSha256=value.HandoffSha256.Take(5).ToArray()},Now));Assert.False(AuditAggregateEvidenceCanonicalizerV1.IsCanonical(value with{AuditSourceCount=5},Now));Assert.False(AuditAggregateEvidenceCanonicalizerV1.IsCanonical(value with{RestartDurable=false},Now));Assert.False(AuditAggregateEvidenceCanonicalizerV1.IsCanonical(value with{IdempotentReplay=false},Now));Assert.False(AuditAggregateEvidenceCanonicalizerV1.TryParseCanonical(Encoding.UTF8.GetBytes("{}"),out _));
    }
    [Fact]
    public void RunnerIsOfficialReadOnlyTestnetAndUsesProductionPersistence()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","Agent","AuditAggregateAcceptanceRunnerV1.cs"));Assert.Contains("BinanceTestnetMarketAcceptanceSourceV1",source,StringComparison.Ordinal);Assert.Contains("ModelOffProductionCycleOrchestratorV1",source,StringComparison.Ordinal);Assert.Contains("GetModelOffCanonicalAuditsAsync",source,StringComparison.Ordinal);Assert.Contains("GetModelOffCanonicalHandoffsAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceMarketAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("PlaceLimitAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("CancelOrderAsync",source,StringComparison.Ordinal);Assert.DoesNotContain("Mainnet",source,StringComparison.Ordinal);
    }
    [Fact]
    public void LocalAuditRequirementMapsToAuditAggregate()
    {
        var candidate=Encoding.UTF8.GetBytes("candidate");var tests=Encoding.UTF8.GetBytes("tests");var trx=LocalTrx("audit.append-only",["WPE.Tests.ModelOffProductionCycleOrchestratorTests.HandoffIdentityConflictRollsBackTheWholeProductionCycle","WPE.Tests.ModelOffProductionCycleOrchestratorTests.PersistedProductionEvidenceIsDatabaseAppendOnly"]);var artifact=LocalTestAcceptanceCanonicalizerV1.Create("audit.append-only","3.6.0",candidate,tests,trx);var evidence=LocalTestAcceptanceCanonicalizerV1.ToAcceptanceEvidence(artifact,artifact.ObservedAtUtc,HashBytes(candidate));Assert.Equal(ModelOffAggregateAgentV1.Audit,evidence.Agent);
    }
    private static byte[] LocalTrx(string requirement,IReadOnlyList<string> tests){var finish=Now.ToString("O");var definitions=string.Join("",tests.Select(x=>$"<UnitTest name=\"{x}\" />"));var results=string.Join("",tests.Select(x=>$"<UnitTestResult testName=\"{x}\" outcome=\"Passed\" />"));return Encoding.UTF8.GetBytes($"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Times finish=\"{finish}\"/><TestDefinitions>{definitions}</TestDefinitions><Results>{results}</Results><ResultSummary><Counters total=\"{tests.Count}\" passed=\"{tests.Count}\" failed=\"0\" error=\"0\"/></ResultSummary></TestRun>");}
    private static AuditAggregateEvidenceV1 Valid(string requirement)=>AuditAggregateEvidenceCanonicalizerV1.Create(requirement,new("3.6.0.0",Hash("candidate")),Now.AddMinutes(-1),Hash("market"),Enumerable.Range(0,7).Select(x=>Hash("output"+x)).ToArray(),Enumerable.Range(0,6).Select(x=>Hash("handoff"+x)).ToArray(),6,true,true);private static string Hash(string value)=>HashBytes(Encoding.UTF8.GetBytes(value));private static string HashBytes(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

using System.Diagnostics;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffSmokeLaneTests
{
    [Fact]
    public void CriticalSmokeSourceDoesNotConstructOrAwaitModelProvider()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Services", "Agent", "SmokeTestRunner.cs"));
        var run = source[..source.IndexOf("private static async Task Step", StringComparison.Ordinal)];
        Assert.DoesNotContain("CreateBrain(", run, StringComparison.Ordinal);
        Assert.DoesNotContain("brain.HealthCheckAsync", run, StringComparison.Ordinal);
        Assert.DoesNotContain("brain.DecideAsync", run, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, true, "smoke.model-off.ready")]
    [InlineData(false, true, false, "smoke.model-off.critical-readiness-failed")]
    [InlineData(true, false, false, "smoke.model-off.canonical-input-invalid")]
    public void SafetyVerdictUsesOnlyCriticalDeterministicInputs(bool readiness, bool canonical, bool allowed, string code)
    {
        var verdict = ModelOffSmokeSafetyVerdict.Evaluate(readiness, canonical);
        Assert.Equal(allowed, verdict.Allowed);
        Assert.Equal(code, verdict.Code);
    }

    [Fact]
    public void CanonicalInputValidityAcceptsCanonicalHoldDecision()
    {
        var now=DateTime.UtcNow;
        var raw=new MarketEvidence("BTCUSDT",50_000m,49_000m,51_000m,50,0.01,0.02,0.03,new(0,1,1,1,1,1,0),now);
        var market=raw with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(raw,"binance-futures","Testnet")};
        var evidence=new EvidencePack{CollectedAt=now,Markets=new Dictionary<string,MarketEvidence>{{"BTCUSDT",market}},Completeness=100};
        var decision=new DecisionPlan{Action=DecisionAction.Hold,Instrument="BTCUSDT",DecisionContextKind="market-observation"};

        Assert.True(SmokeTestRunner.CanonicalSmokeInputsValid(evidence,decision));
    }

    [Fact]
    public void CanonicalInputValidityRejectsTamperedMarketEvidence()
    {
        var now=DateTime.UtcNow;
        var raw=new MarketEvidence("BTCUSDT",50_000m,49_000m,51_000m,50,0.01,0.02,0.03,new(0,1,1,1,1,1,0),now);
        var canonical=raw with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(raw,"binance-futures","Testnet")};
        var tampered=canonical with{Price=50_001m};
        var evidence=new EvidencePack{CollectedAt=now,Markets=new Dictionary<string,MarketEvidence>{{"BTCUSDT",tampered}},Completeness=100};
        var decision=new DecisionPlan{Action=DecisionAction.Hold,Instrument="BTCUSDT",DecisionContextKind="market-observation"};

        Assert.False(SmokeTestRunner.CanonicalSmokeInputsValid(evidence,decision));
    }

    [Theory]
    [InlineData("hung")]
    [InlineData("late")]
    [InlineData("cancelled")]
    [InlineData("malformed")]
    public async Task OptionalDiagnosticCannotChangeCompletedSafetyVerdict(string fixture)
    {
        var before = ModelOffSmokeSafetyVerdict.Evaluate(true, true);
        using var cts = new CancellationTokenSource();
        if (fixture == "cancelled") cts.Cancel();
        Func<CancellationToken, Task<string?>> probe = fixture switch
        {
            "hung" => _ => new TaskCompletionSource<string?>().Task,
            "late" => async _ => { await Task.Delay(200); return "late"; },
            "cancelled" => _ => Task.FromResult<string?>("unused"),
            "malformed" => _ => Task.FromResult<string?>(null),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };
        var sw = Stopwatch.StartNew();
        _ = await OptionalModelDiagnosticRunnerV1.RunAsync(probe, TimeSpan.FromMilliseconds(20), cts.Token);
        var after = ModelOffSmokeSafetyVerdict.Evaluate(true, true);
        Assert.Equal(before, after);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(120), $"elapsed={sw.Elapsed}");
    }

    [Fact]
    public void OptionalDiagnosticSchemaIsSeparateFromCanonicalAgentOutput()
    {
        Assert.NotEqual(WpeAgent.ModelOff.ModelOffAgentOutputV1.Schema, OptionalModelDiagnosticV1.Schema);
        Assert.StartsWith("wpe.optional-model-diagnostic/", OptionalModelDiagnosticV1.Schema, StringComparison.Ordinal);
    }
}

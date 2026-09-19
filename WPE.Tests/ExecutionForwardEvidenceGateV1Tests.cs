using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionForwardEvidenceGateV1Tests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SufficientFreshExecutionEvidenceIsReadyAndCanonical()
    {
        var facts = new[]
        {
            Filled("a", Now.AddHours(-4)),
            Filled("b", Now.AddHours(-3)),
            Filled("c", Now.AddHours(-2)),
            Filled("d", Now.AddMinutes(-5))
        };

        var decision = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", facts, Policy(), Now);

        Assert.Equal(ExecutionForwardEvidenceStateV1.Ready, decision.State);
        Assert.True(decision.EvidenceReady);
        Assert.Empty(decision.ReasonCodes);
        Assert.Equal(4, decision.ObservationCount);
        Assert.Equal(4, decision.PriceComparableCount);
        Assert.Equal(4, decision.TotalComparableCount);
        Assert.True(decision.ObservationWindowSeconds >= TimeSpan.FromHours(3).TotalSeconds);
        Assert.NotNull(decision.CalibrationSha256);
        Assert.Equal(64, decision.EvidenceSetSha256.Length);
        Assert.True(ExecutionForwardEvidenceGateV1.IsCanonical(decision));
    }

    [Fact]
    public void TinyOrShortWindowCannotBecomeReady()
    {
        var facts = new[]
        {
            Filled("a", Now.AddMinutes(-20)),
            Filled("b", Now.AddMinutes(-10))
        };

        var decision = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", facts, Policy(), Now);

        Assert.Equal(ExecutionForwardEvidenceStateV1.Insufficient, decision.State);
        Assert.False(decision.EvidenceReady);
        Assert.Contains("sample.observations", decision.ReasonCodes);
        Assert.Contains("sample.price-comparable", decision.ReasonCodes);
        Assert.Contains("sample.total-comparable", decision.ReasonCodes);
        Assert.Contains("sample.window", decision.ReasonCodes);
    }

    [Fact]
    public void StaleEvidenceIsInsufficientEvenWhenSampleSizePasses()
    {
        var facts = new[]
        {
            Filled("a", Now.AddHours(-8)),
            Filled("b", Now.AddHours(-7)),
            Filled("c", Now.AddHours(-6)),
            Filled("d", Now.AddHours(-5))
        };

        var decision = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", facts, Policy() with { MaximumEvidenceAge = TimeSpan.FromHours(1) }, Now);

        Assert.Equal(ExecutionForwardEvidenceStateV1.Insufficient, decision.State);
        Assert.Contains("freshness.stale", decision.ReasonCodes);
    }

    [Fact]
    public void RealisticCostRegressionIsExplicitlyRejectedByPolicy()
    {
        var facts = new[]
        {
            Filled("a", Now.AddHours(-4)),
            Filled("b", Now.AddHours(-3)),
            Filled("c", Now.AddHours(-2), actualPrice:101m),
            Filled("d", Now.AddMinutes(-5))
        };

        var decision = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", facts,
            Policy() with { MaximumP95AdverseSlippageBps = 25m, MaximumP95TotalExecutionDriftBps = 25m },
            Now);

        Assert.Equal(ExecutionForwardEvidenceStateV1.Regressed, decision.State);
        Assert.False(decision.EvidenceReady);
        Assert.Contains("quality.adverse-slippage", decision.ReasonCodes);
        Assert.Contains("quality.total-drift", decision.ReasonCodes);
    }

    [Fact]
    public void MissingFeeComparabilityCannotSatisfyTotalCostEvidenceMinimum()
    {
        var facts = new[]
        {
            Filled("a", Now.AddHours(-4), feeAvailable:false),
            Filled("b", Now.AddHours(-3), feeAvailable:false),
            Filled("c", Now.AddHours(-2), feeAvailable:false),
            Filled("d", Now.AddMinutes(-5), feeAvailable:false)
        };

        var decision = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", facts, Policy(), Now);

        Assert.Equal(ExecutionForwardEvidenceStateV1.Insufficient, decision.State);
        Assert.Equal(4, decision.PriceComparableCount);
        Assert.Equal(0, decision.TotalComparableCount);
        Assert.Contains("sample.total-comparable", decision.ReasonCodes);
    }

    [Fact]
    public void ExcessUnknownOrTerminalNoFillRateIsRegressionNotSampleSuccess()
    {
        var facts = new[]
        {
            Filled("a", Now.AddHours(-4)),
            Filled("b", Now.AddHours(-3)),
            Unknown("c", Now.AddHours(-2)),
            Rejected("d", Now.AddMinutes(-5))
        };
        var policy = Policy() with
        {
            MinimumPriceComparableObservations = 2,
            MinimumTotalComparableObservations = 2,
            MaximumUnknownFraction = .20m,
            MaximumTerminalNoFillFraction = .20m
        };

        var decision = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", facts, policy, Now);

        Assert.Equal(ExecutionForwardEvidenceStateV1.Regressed, decision.State);
        Assert.Contains("quality.unknown-rate", decision.ReasonCodes);
        Assert.Contains("quality.terminal-no-fill-rate", decision.ReasonCodes);
    }

    [Fact]
    public void CrossIdentityFutureOrTamperedEvidenceIsInvalid()
    {
        var valid = Filled("a", Now.AddHours(-4));
        var crossCost = Make("strategy-a", "v7", "research-cost-v2", "b", "FILLED", 1m, 100m, true, Now.AddHours(-3));
        var future = Filled("c", Now.AddMinutes(1));
        var tampered = Filled("d", Now.AddHours(-2)) with { AveragePrice = 999m };

        var decision = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1",
            new[] { valid, crossCost, future, tampered },
            Policy() with { MinimumObservations = 1, MinimumPriceComparableObservations = 1, MinimumTotalComparableObservations = 1 },
            Now);

        Assert.Equal(ExecutionForwardEvidenceStateV1.Invalid, decision.State);
        Assert.False(decision.EvidenceReady);
        Assert.Contains("evidence.cross-cost-model", decision.ReasonCodes);
        Assert.Contains("evidence.future", decision.ReasonCodes);
        Assert.Contains("evidence.noncanonical", decision.ReasonCodes);
    }

    [Fact]
    public void DuplicateCanonicalObservationIsIntegrityFailureAndCannotInflateSample()
    {
        var duplicate = Filled("a", Now.AddHours(-4));
        var facts = new[]
        {
            duplicate,
            duplicate,
            Filled("b", Now.AddHours(-3)),
            Filled("c", Now.AddHours(-2)),
            Filled("d", Now.AddMinutes(-5))
        };

        var decision = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", facts, Policy(), Now);

        Assert.Equal(ExecutionForwardEvidenceStateV1.Invalid, decision.State);
        Assert.False(decision.EvidenceReady);
        Assert.Equal(4, decision.ObservationCount);
        Assert.Contains("evidence.duplicate", decision.ReasonCodes);
    }

    [Fact]
    public void EvidenceSetHashDistinguishesDifferentFactsWithIdenticalAggregateMetrics()
    {
        var times = new[]
        {
            Now.AddHours(-4),
            Now.AddHours(-3),
            Now.AddHours(-2),
            Now.AddMinutes(-5)
        };
        var firstFacts = times.Select((time, index) => Filled("a" + index, time)).ToArray();
        var secondFacts = times.Select((time, index) => Filled("b" + index, time)).ToArray();

        var first = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", firstFacts, Policy(), Now);
        var second = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", secondFacts, Policy(), Now);

        Assert.Equal(ExecutionForwardEvidenceStateV1.Ready, first.State);
        Assert.Equal(ExecutionForwardEvidenceStateV1.Ready, second.State);
        Assert.Equal(first.CalibrationSha256, second.CalibrationSha256);
        Assert.NotEqual(first.EvidenceSetSha256, second.EvidenceSetSha256);
        Assert.NotEqual(first.CanonicalSha256, second.CanonicalSha256);
    }

    [Fact]
    public void PolicyIsCryptographicallyBoundToDecision()
    {
        var facts = new[]
        {
            Filled("a", Now.AddHours(-4)),
            Filled("b", Now.AddHours(-3)),
            Filled("c", Now.AddHours(-2)),
            Filled("d", Now.AddMinutes(-5))
        };
        var first = ExecutionForwardEvidenceGateV1.Evaluate("strategy-a", "v7", "research-cost-v1", facts, Policy(), Now);
        var second = ExecutionForwardEvidenceGateV1.Evaluate(
            "strategy-a", "v7", "research-cost-v1", facts,
            Policy() with { MaximumP95ObservationLatencyMs = 2500 },
            Now);

        Assert.NotEqual(first.PolicySha256, second.PolicySha256);
        Assert.NotEqual(first.CanonicalSha256, second.CanonicalSha256);
        Assert.False(ExecutionForwardEvidenceGateV1.IsCanonical(first with { EvidenceReady = false }));
        Assert.False(ExecutionForwardEvidenceGateV1.IsCanonical(first with { EvidenceSetSha256 = "bad" }));
    }

    [Fact]
    public void InvalidPolicyIsRejectedBeforeEvaluation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExecutionForwardEvidenceGateV1.Evaluate(
                "strategy-a", "v7", "research-cost-v1",
                new[] { Filled("a", Now.AddHours(-1)) },
                Policy() with { MaximumUnknownFraction = 1.1m },
                Now));
    }

    private static ExecutionForwardEvidencePolicyV1 Policy() => new(
        Version:"execution-forward-policy-v1",
        MinimumObservations:4,
        MinimumPriceComparableObservations:3,
        MinimumTotalComparableObservations:3,
        MinimumObservationWindow:TimeSpan.FromHours(2),
        MaximumEvidenceAge:TimeSpan.FromHours(1),
        MinimumAverageFillRatio:.80m,
        MaximumP95AdverseSlippageBps:25m,
        MaximumP95TotalExecutionDriftBps:25m,
        MaximumP95ObservationLatencyMs:2000,
        MaximumUnknownFraction:.25m,
        MaximumTerminalNoFillFraction:.25m);

    private static ExecutionRealityDriftFactV1 Filled(
        string id,
        DateTimeOffset observedAt,
        decimal actualPrice = 100m,
        bool feeAvailable = true) =>
        Make("strategy-a", "v7", "research-cost-v1", id, "FILLED", 1m, actualPrice, feeAvailable, observedAt);

    private static ExecutionRealityDriftFactV1 Unknown(string id, DateTimeOffset observedAt) =>
        Make("strategy-a", "v7", "research-cost-v1", id, "VENUE_UNKNOWN", 0m, 0m, false, observedAt);

    private static ExecutionRealityDriftFactV1 Rejected(string id, DateTimeOffset observedAt) =>
        Make("strategy-a", "v7", "research-cost-v1", id, "REJECTED", 0m, 0m, false, observedAt);

    private static ExecutionRealityDriftFactV1 Make(
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string id,
        string status,
        decimal executed,
        decimal actualPrice,
        bool feeAvailable,
        DateTimeOffset observedAt)
    {
        const decimal expectedPrice = 100m;
        var expectation = new ExecutionRealityExpectationV1(
            "cycle-" + id,
            "order-" + id,
            strategyId,
            strategyVersion,
            costModelVersion,
            "BTCUSDT",
            PositionSide.Long,
            false,
            ExecutionOrderType.Market,
            1m,
            expectedPrice,
            .0004m,
            .001m,
            observedAt.AddMilliseconds(-500));
        var fee = feeAvailable && executed > 0 ? actualPrice * executed * .0004m : 0m;
        var observation = new ExecutionRealityObservationV1(
            "order-" + id,
            status,
            executed,
            executed > 0 ? actualPrice : 0m,
            fee,
            feeAvailable && executed > 0 ? ExecutionRealityDriftV1.ExchangeReportedFeeBasis : ExecutionRealityDriftV1.UnavailableFeeBasis,
            observedAt.AddMilliseconds(-100),
            observedAt);
        return ExecutionRealityDriftV1.Analyze(expectation, observation);
    }
}

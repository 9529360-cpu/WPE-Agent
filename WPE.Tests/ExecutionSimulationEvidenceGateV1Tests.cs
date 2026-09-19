using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionSimulationEvidenceGateV1Tests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SufficientFreshSimulationEvidenceIsReadyAndCanonical()
    {
        var comparisons = ReadySet("a");

        var decision = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a",
            "v7",
            "research-cost-v1",
            "execution-sim-v1",
            "binance-testnet-rules-v1",
            "BTCUSDT",
            comparisons,
            Policy(),
            Now);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Ready, decision.State);
        Assert.True(decision.EvidenceReady);
        Assert.Empty(decision.ReasonCodes);
        Assert.Equal(4, decision.ComparisonCount);
        Assert.Equal(4, decision.StateMatchCount);
        Assert.Equal(4, decision.PriceComparableCount);
        Assert.Equal(4, decision.FeeComparableCount);
        Assert.Equal(4, decision.LatencyComparableCount);
        Assert.Equal(4, decision.TotalComparableCount);
        Assert.Equal(0m, decision.StateMismatchFraction);
        Assert.Equal(0m, decision.UnsupportedFraction);
        Assert.Equal(0m, decision.P95AbsoluteFillRatioDelta);
        Assert.Equal(0m, decision.P95AdversePriceDriftBps);
        Assert.Equal(0m, decision.P95AdverseFeeDriftBps);
        Assert.Equal(0, decision.P95PositiveLatencyDriftMs);
        Assert.Equal(0m, decision.P95TotalExecutionDriftBps);
        Assert.Equal(64, decision.EvidenceSetSha256.Length);
        Assert.True(ExecutionSimulationEvidenceGateV1.IsCanonical(decision));
    }

    [Fact]
    public void TinyShortOrStaleSampleCannotBecomeReady()
    {
        var comparisons = new[]
        {
            Compare("a", Now.AddHours(-4)),
            Compare("b", Now.AddHours(-3))
        };

        var decision = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            comparisons, Policy(), Now);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Insufficient, decision.State);
        Assert.False(decision.EvidenceReady);
        Assert.Contains("sample.comparisons", decision.ReasonCodes);
        Assert.Contains("sample.price-comparable", decision.ReasonCodes);
        Assert.Contains("sample.fee-comparable", decision.ReasonCodes);
        Assert.Contains("sample.latency-comparable", decision.ReasonCodes);
        Assert.Contains("sample.total-comparable", decision.ReasonCodes);
        Assert.Contains("sample.window", decision.ReasonCodes);
        Assert.Contains("freshness.stale", decision.ReasonCodes);
    }

    [Fact]
    public void MissingFeeAndLatencyComparabilityCannotMasqueradeAsZeroDrift()
    {
        var comparisons = new[]
        {
            Compare("a", Now.AddHours(-4), simulatedFeeRole:ExecutionSimulationFeeRoleV1.Unavailable, latencyModeled:false),
            Compare("b", Now.AddHours(-3), simulatedFeeRole:ExecutionSimulationFeeRoleV1.Unavailable, latencyModeled:false),
            Compare("c", Now.AddHours(-2), simulatedFeeRole:ExecutionSimulationFeeRoleV1.Unavailable, latencyModeled:false),
            Compare("d", Now.AddMinutes(-5), simulatedFeeRole:ExecutionSimulationFeeRoleV1.Unavailable, latencyModeled:false)
        };

        var decision = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            comparisons, Policy(), Now);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Insufficient, decision.State);
        Assert.Equal(4, decision.PriceComparableCount);
        Assert.Equal(0, decision.FeeComparableCount);
        Assert.Equal(0, decision.LatencyComparableCount);
        Assert.Equal(0, decision.TotalComparableCount);
        Assert.Contains("sample.fee-comparable", decision.ReasonCodes);
        Assert.Contains("sample.latency-comparable", decision.ReasonCodes);
        Assert.Contains("sample.total-comparable", decision.ReasonCodes);
        Assert.DoesNotContain("quality.fee-drift", decision.ReasonCodes);
        Assert.DoesNotContain("quality.latency-drift", decision.ReasonCodes);
        Assert.DoesNotContain("quality.total-drift", decision.ReasonCodes);
    }

    [Fact]
    public void FillStateAndFillRatioRegressionIsExplicit()
    {
        var comparisons = new[]
        {
            Compare("a", Now.AddHours(-4)),
            Compare("b", Now.AddHours(-3)),
            Compare(
                "c",
                Now.AddHours(-2),
                simulatedState:ExecutionSimulationFillStateV1.Partial,
                simulatedExecuted:.5m,
                observedStatus:"FILLED",
                observedExecuted:1m),
            Compare("d", Now.AddMinutes(-5))
        };

        var decision = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            comparisons,
            Policy() with { MaximumStateMismatchFraction = .20m },
            Now);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Regressed, decision.State);
        Assert.Equal(.25m, decision.StateMismatchFraction);
        Assert.Equal(.5m, decision.P95AbsoluteFillRatioDelta);
        Assert.Contains("quality.state-mismatch", decision.ReasonCodes);
        Assert.Contains("quality.fill-ratio-delta", decision.ReasonCodes);
    }

    [Fact]
    public void UnsupportedSimulationRateIsRegressionNotMissingEvidenceSuccess()
    {
        var comparisons = new[]
        {
            Compare("a", Now.AddHours(-4)),
            Compare("b", Now.AddHours(-3)),
            Compare(
                "c",
                Now.AddHours(-2),
                simulatedState:ExecutionSimulationFillStateV1.Unsupported,
                simulatedExecuted:0m,
                simulatedPrice:0m,
                simulatedFeeRole:ExecutionSimulationFeeRoleV1.Unavailable,
                latencyModeled:false,
                observedStatus:"REJECTED",
                observedExecuted:0m,
                observedPrice:0m,
                observedFeeAvailable:false),
            Compare("d", Now.AddMinutes(-5))
        };

        var decision = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            comparisons,
            Policy() with
            {
                MaximumUnsupportedFraction = .20m,
                MaximumStateMismatchFraction = 1m
            },
            Now);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Regressed, decision.State);
        Assert.Equal(.25m, decision.UnsupportedFraction);
        Assert.Equal(3, decision.PriceComparableCount);
        Assert.Equal(3, decision.TotalComparableCount);
        Assert.Contains("quality.unsupported", decision.ReasonCodes);
    }

    [Fact]
    public void PriceLatencyAndTotalDriftRegressionAreMeasuredInAdverseDirection()
    {
        var comparisons = new[]
        {
            Compare("a", Now.AddHours(-4)),
            Compare("b", Now.AddHours(-3)),
            Compare("c", Now.AddHours(-2), observedPrice:101m, observedLatencyMs:1500),
            Compare("d", Now.AddMinutes(-5))
        };

        var decision = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            comparisons, Policy(), Now);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Regressed, decision.State);
        Assert.Equal(100m, decision.P95AdversePriceDriftBps);
        Assert.Equal(1000, decision.P95PositiveLatencyDriftMs);
        Assert.Equal(100m, decision.P95TotalExecutionDriftBps);
        Assert.Contains("quality.price-drift", decision.ReasonCodes);
        Assert.Contains("quality.latency-drift", decision.ReasonCodes);
        Assert.Contains("quality.total-drift", decision.ReasonCodes);
    }

    [Fact]
    public void FeeDriftRegressionIsIndependentOfPriceDrift()
    {
        var comparisons = new[]
        {
            Compare("a", Now.AddHours(-4)),
            Compare("b", Now.AddHours(-3)),
            Compare("c", Now.AddHours(-2), observedFeeRateBps:10m),
            Compare("d", Now.AddMinutes(-5))
        };

        var decision = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            comparisons, Policy(), Now);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Regressed, decision.State);
        Assert.Equal(0m, decision.P95AdversePriceDriftBps);
        Assert.Equal(6m, decision.P95AdverseFeeDriftBps);
        Assert.Equal(6m, decision.P95TotalExecutionDriftBps);
        Assert.Contains("quality.fee-drift", decision.ReasonCodes);
        Assert.DoesNotContain("quality.price-drift", decision.ReasonCodes);
        Assert.DoesNotContain("quality.total-drift", decision.ReasonCodes);
    }

    [Fact]
    public void CrossIdentityFutureOrTamperedComparisonIsInvalid()
    {
        var valid = Compare("a", Now.AddHours(-4));
        var crossModel = Compare("b", Now.AddHours(-3), simulationModelVersion:"execution-sim-v2");
        var future = Compare("c", Now.AddMinutes(1));
        var tampered = Compare("d", Now.AddHours(-2)) with { FillRatioDelta = .5m };

        var decision = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            new[] { valid, crossModel, future, tampered },
            Policy() with
            {
                MinimumComparisons = 1,
                MinimumPriceComparableComparisons = 1,
                MinimumFeeComparableComparisons = 1,
                MinimumLatencyComparableComparisons = 1,
                MinimumTotalComparableComparisons = 1
            },
            Now);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Invalid, decision.State);
        Assert.False(decision.EvidenceReady);
        Assert.Contains("evidence.cross-simulation-model", decision.ReasonCodes);
        Assert.Contains("evidence.future", decision.ReasonCodes);
        Assert.Contains("evidence.noncanonical", decision.ReasonCodes);
    }

    [Fact]
    public void DuplicateCanonicalComparisonCannotInflateSample()
    {
        var duplicate = Compare("a", Now.AddHours(-4));
        var comparisons = new[]
        {
            duplicate,
            duplicate,
            Compare("b", Now.AddHours(-3)),
            Compare("c", Now.AddHours(-2)),
            Compare("d", Now.AddMinutes(-5))
        };

        var decision = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            comparisons, Policy(), Now);

        Assert.Equal(ExecutionSimulationEvidenceStateV1.Invalid, decision.State);
        Assert.Equal(4, decision.ComparisonCount);
        Assert.Contains("evidence.duplicate", decision.ReasonCodes);
    }

    [Fact]
    public void EvidenceSetHashBindsDecisionToExactComparisons()
    {
        var first = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            ReadySet("a"), Policy(), Now);
        var second = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            ReadySet("b"), Policy(), Now);

        Assert.Equal(first.ComparisonCount, second.ComparisonCount);
        Assert.Equal(first.StateMismatchFraction, second.StateMismatchFraction);
        Assert.Equal(first.P95AbsoluteFillRatioDelta, second.P95AbsoluteFillRatioDelta);
        Assert.Equal(first.P95AdversePriceDriftBps, second.P95AdversePriceDriftBps);
        Assert.Equal(first.P95AdverseFeeDriftBps, second.P95AdverseFeeDriftBps);
        Assert.Equal(first.P95PositiveLatencyDriftMs, second.P95PositiveLatencyDriftMs);
        Assert.Equal(first.P95TotalExecutionDriftBps, second.P95TotalExecutionDriftBps);
        Assert.NotEqual(first.EvidenceSetSha256, second.EvidenceSetSha256);
        Assert.NotEqual(first.CanonicalSha256, second.CanonicalSha256);
    }

    [Fact]
    public void PolicyAndDecisionProvenanceAreCryptographicallyBound()
    {
        var comparisons = ReadySet("a");
        var first = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            comparisons, Policy(), Now);
        var second = ExecutionSimulationEvidenceGateV1.Evaluate(
            "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
            comparisons, Policy() with { MaximumP95PositiveLatencyDriftMs = 750 }, Now);

        Assert.NotEqual(first.PolicySha256, second.PolicySha256);
        Assert.NotEqual(first.CanonicalSha256, second.CanonicalSha256);
        Assert.False(ExecutionSimulationEvidenceGateV1.IsCanonical(first with { EvidenceReady = false }));
        Assert.False(ExecutionSimulationEvidenceGateV1.IsCanonical(first with { EvidenceSetSha256 = "bad" }));
        Assert.False(ExecutionSimulationEvidenceGateV1.IsCanonical(first with { PolicySha256 = "bad" }));
    }

    [Fact]
    public void InvalidPolicyOrNonUtcEvaluationIsRejectedBeforeQualification()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExecutionSimulationEvidenceGateV1.Evaluate(
                "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
                ReadySet("a"), Policy() with { MaximumUnsupportedFraction = 1.1m }, Now));

        Assert.Throws<ArgumentException>(() =>
            ExecutionSimulationEvidenceGateV1.Evaluate(
                "strategy-a","v7","research-cost-v1","execution-sim-v1","binance-testnet-rules-v1","BTCUSDT",
                ReadySet("a"), Policy(), Now.ToOffset(TimeSpan.FromHours(2))));
    }

    private static ExecutionSimulationEvidencePolicyV1 Policy() => new(
        Version:"simulation-evidence-policy-v1",
        MinimumComparisons:4,
        MinimumPriceComparableComparisons:3,
        MinimumFeeComparableComparisons:3,
        MinimumLatencyComparableComparisons:3,
        MinimumTotalComparableComparisons:3,
        MinimumObservationWindow:TimeSpan.FromHours(2),
        MaximumEvidenceAge:TimeSpan.FromHours(1),
        MaximumStateMismatchFraction:.25m,
        MaximumUnsupportedFraction:.25m,
        MaximumP95AbsoluteFillRatioDelta:.10m,
        MaximumP95AdversePriceDriftBps:25m,
        MaximumP95AdverseFeeDriftBps:3m,
        MaximumP95PositiveLatencyDriftMs:500,
        MaximumP95TotalExecutionDriftBps:30m);

    private static ExecutionSimulationComparisonV1[] ReadySet(string prefix) => new[]
    {
        Compare(prefix + "1", Now.AddHours(-4)),
        Compare(prefix + "2", Now.AddHours(-3)),
        Compare(prefix + "3", Now.AddHours(-2)),
        Compare(prefix + "4", Now.AddMinutes(-5))
    };

    private static ExecutionSimulationComparisonV1 Compare(
        string id,
        DateTimeOffset comparedAt,
        string strategyId = "strategy-a",
        string strategyVersion = "v7",
        string costModelVersion = "research-cost-v1",
        string simulationModelVersion = "execution-sim-v1",
        string venueRuleVersion = "binance-testnet-rules-v1",
        string symbol = "BTCUSDT",
        ExecutionSimulationFillStateV1 simulatedState = ExecutionSimulationFillStateV1.Filled,
        decimal simulatedExecuted = 1m,
        decimal simulatedPrice = 100m,
        ExecutionSimulationFeeRoleV1 simulatedFeeRole = ExecutionSimulationFeeRoleV1.Taker,
        decimal simulatedFeeRateBps = 4m,
        bool latencyModeled = true,
        long simulatedLatencyMs = 500,
        string observedStatus = "FILLED",
        decimal observedExecuted = 1m,
        decimal observedPrice = 100m,
        bool observedFeeAvailable = true,
        decimal observedFeeRateBps = 4m,
        long observedLatencyMs = 500)
    {
        var simulatedAt = comparedAt.AddSeconds(-10);
        var marketAt = simulatedAt.AddSeconds(-1);
        var simulatedFee = simulatedFeeRole == ExecutionSimulationFeeRoleV1.Unavailable || simulatedExecuted <= 0
            ? 0m
            : simulatedPrice * simulatedExecuted * simulatedFeeRateBps / 10000m;

        var simulated = ExecutionSimulationFillCanonicalizerV1.Create(
            correlationId:"cycle-" + id,
            clientOrderId:"order-" + id,
            strategyId:strategyId,
            strategyVersion:strategyVersion,
            costModelVersion:costModelVersion,
            simulationModelVersion:simulationModelVersion,
            venueRuleVersion:venueRuleVersion,
            symbol:symbol,
            side:PositionSide.Long,
            reduceOnly:false,
            orderType:ExecutionOrderType.Market,
            intendedQuantity:1m,
            state:simulatedState,
            executedQuantity:simulatedExecuted,
            averagePrice:simulatedPrice,
            feeAmount:simulatedFee,
            feeRole:simulatedFeeRole,
            latencyModeled:latencyModeled,
            simulatedLatencyMs:latencyModeled ? simulatedLatencyMs : 0,
            marketAsOfUtc:marketAt,
            simulatedAtUtc:simulatedAt,
            reasonCode:simulatedState == ExecutionSimulationFillStateV1.Unsupported ? "venue-rule-unsupported" : "modeled");

        var expectation = new ExecutionRealityExpectationV1(
            "cycle-" + id,
            "order-" + id,
            strategyId,
            strategyVersion,
            costModelVersion,
            symbol,
            PositionSide.Long,
            false,
            ExecutionOrderType.Market,
            1m,
            100m,
            .0004m,
            .001m,
            simulatedAt);
        var observedAt = simulatedAt.AddMilliseconds(observedLatencyMs);
        var observedFee = observedFeeAvailable && observedExecuted > 0
            ? observedPrice * observedExecuted * observedFeeRateBps / 10000m
            : 0m;
        var observation = new ExecutionRealityObservationV1(
            "order-" + id,
            observedStatus,
            observedExecuted,
            observedExecuted > 0 ? observedPrice : 0m,
            observedFee,
            observedFeeAvailable && observedExecuted > 0
                ? ExecutionRealityDriftV1.ExchangeReportedFeeBasis
                : ExecutionRealityDriftV1.UnavailableFeeBasis,
            observedAt.AddMilliseconds(-50),
            observedAt);
        var observed = ExecutionRealityDriftV1.Analyze(expectation, observation);

        return ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, comparedAt);
    }
}

using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionRealityDriftV1Tests
{
    private static readonly DateTimeOffset IntendedAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FilledLongOpen_ComputesAdversePriceFeeAndLatencyDrift()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = Observation(expected, "FILLED", 2m, 101m, .0808m, ExecutionRealityDriftV1.ExchangeReportedFeeBasis, IntendedAt.AddSeconds(1));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(ExecutionRealityStateV1.Filled, fact.State);
        Assert.True(fact.Terminal);
        Assert.True(fact.Comparable);
        Assert.True(fact.FeeComparable);
        Assert.True(fact.TotalComparable);
        Assert.Equal(1m, fact.FillRatio);
        Assert.Equal(100m, fact.AdverseSlippageBps);
        Assert.Equal(30m, fact.ExpectedSlippageBps);
        Assert.Equal(70m, fact.SlippageDriftBps);
        Assert.Equal(4m, fact.ObservedFeeRateBps);
        Assert.Equal(4m, fact.ExpectedCommissionBps);
        Assert.Equal(0m, fact.FeeDriftBps);
        Assert.Equal(70m, fact.TotalExecutionDriftBps);
        Assert.Equal(1000, fact.ObservationLatencyMs);
        Assert.True(ExecutionRealityDriftV1.IsCanonical(fact));
    }

    [Fact]
    public void FilledWithoutFeeEvidenceKeepsPriceDriftButWithholdsTotalCostComparison()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = Observation(expected, "FILLED", 2m, 101m, 0m, ExecutionRealityDriftV1.UnavailableFeeBasis, IntendedAt.AddSeconds(1));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.True(fact.Comparable);
        Assert.False(fact.FeeComparable);
        Assert.False(fact.TotalComparable);
        Assert.Equal(70m, fact.SlippageDriftBps);
        Assert.Equal(0m, fact.FeeDriftBps);
        Assert.Equal(0m, fact.TotalExecutionDriftBps);
        Assert.Equal("filled-fee-unavailable", fact.ReasonCode);
    }

    [Fact]
    public void FilledShortOpen_FavorableHigherSellPriceIsNegativeSlippage()
    {
        var expected = Expectation(PositionSide.Short, reduceOnly:false, expectedPrice:100m);
        var observed = Observation(expected, "FILLED", 2m, 101m, .0808m, ExecutionRealityDriftV1.ExchangeReportedFeeBasis, IntendedAt.AddMilliseconds(500));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(-100m, fact.AdverseSlippageBps);
        Assert.Equal(-130m, fact.SlippageDriftBps);
        Assert.Equal(-130m, fact.TotalExecutionDriftBps);
    }

    [Fact]
    public void ReduceLong_UsesSellSideSlippageDirection()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:true, expectedPrice:100m);
        var observed = Observation(expected, "FILLED", 2m, 99m, .0792m, ExecutionRealityDriftV1.ExchangeReportedFeeBasis, IntendedAt.AddMilliseconds(500));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(100m, fact.AdverseSlippageBps);
    }

    [Fact]
    public void PartialFill_IsComparableButNotTerminalWhileExchangeIsOpen()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = Observation(expected, "PARTIALLY_FILLED", .5m, 100.2m, .02004m, ExecutionRealityDriftV1.ExchangeReportedFeeBasis, IntendedAt.AddMilliseconds(250));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(ExecutionRealityStateV1.Partial, fact.State);
        Assert.False(fact.Terminal);
        Assert.True(fact.Comparable);
        Assert.True(fact.FeeComparable);
        Assert.Equal(.25m, fact.FillRatio);
        Assert.Equal("partial-fill", fact.ReasonCode);
    }

    [Fact]
    public void RejectedOrder_IsNotComparableAndDoesNotInventZeroCostAsPerformance()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = Observation(expected, "REJECTED", 0m, 0m, 0m, ExecutionRealityDriftV1.UnavailableFeeBasis, IntendedAt.AddMilliseconds(120));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(ExecutionRealityStateV1.NotFilled, fact.State);
        Assert.True(fact.Terminal);
        Assert.False(fact.Comparable);
        Assert.False(fact.FeeComparable);
        Assert.False(fact.TotalComparable);
        Assert.Equal("terminal-no-fill", fact.ReasonCode);
        Assert.Equal(0m, fact.TotalExecutionDriftBps);
    }

    [Fact]
    public void UnknownStatus_FailsClosedAsUnknown()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = Observation(expected, "VENUE_PENDING_RECONCILIATION", 0m, 0m, 0m, ExecutionRealityDriftV1.UnavailableFeeBasis, IntendedAt.AddMilliseconds(120));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(ExecutionRealityStateV1.Unknown, fact.State);
        Assert.False(fact.Comparable);
        Assert.False(fact.Terminal);
        Assert.Equal("unknown-status", fact.ReasonCode);
    }

    [Fact]
    public void InvalidFilledQuantityFailsClosed()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = Observation(expected, "FILLED", 1m, 100m, .04m, ExecutionRealityDriftV1.ExchangeReportedFeeBasis, IntendedAt.AddMilliseconds(120));

        Assert.Throws<InvalidOperationException>(() => ExecutionRealityDriftV1.Analyze(expected, observed));
    }

    [Fact]
    public void ClientOrderIdentityMismatchFailsClosed()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = new ExecutionRealityObservationV1(
            "different-order", "FILLED", 2m, 100m, .08m, ExecutionRealityDriftV1.ExchangeReportedFeeBasis,
            IntendedAt.AddMilliseconds(100), IntendedAt.AddMilliseconds(120));

        Assert.Throws<InvalidOperationException>(() => ExecutionRealityDriftV1.Analyze(expected, observed));
    }

    [Fact]
    public void UnavailableFeeEvidenceCannotSmuggleObservedFee()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = Observation(expected, "FILLED", 2m, 100m, .08m, ExecutionRealityDriftV1.UnavailableFeeBasis, IntendedAt.AddMilliseconds(120));

        Assert.Throws<InvalidOperationException>(() => ExecutionRealityDriftV1.Analyze(expected, observed));
    }

    [Fact]
    public void CanonicalHashDetectsTampering()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = Observation(expected, "FILLED", 2m, 100m, .08m, ExecutionRealityDriftV1.ExchangeReportedFeeBasis, IntendedAt.AddMilliseconds(120));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);
        Assert.True(ExecutionRealityDriftV1.IsCanonical(fact));
        Assert.False(ExecutionRealityDriftV1.IsCanonical(fact with { AveragePrice = 101m }));
        Assert.False(ExecutionRealityDriftV1.IsCanonical(fact with { FeeBasis = ExecutionRealityDriftV1.UnavailableFeeBasis }));
    }

    private static ExecutionRealityObservationV1 Observation(
        ExecutionRealityExpectationV1 expected,
        string status,
        decimal executed,
        decimal price,
        decimal fee,
        string feeBasis,
        DateTimeOffset observedAt) => new(
            expected.ClientOrderId,
            status,
            executed,
            price,
            fee,
            feeBasis,
            observedAt.AddMilliseconds(-100),
            observedAt);

    private static ExecutionRealityExpectationV1 Expectation(PositionSide side, bool reduceOnly, decimal expectedPrice) => new(
        CorrelationId:"cycle-1",
        ClientOrderId:"order-1",
        StrategyId:"trend-btc",
        StrategyVersion:"v1",
        CostModelVersion:"research-cost-v1",
        Symbol:"BTCUSDT",
        Side:side,
        ReduceOnly:reduceOnly,
        OrderType:ExecutionOrderType.Market,
        Quantity:2m,
        ExpectedPrice:expectedPrice,
        ExpectedCommissionRate:.0004m,
        ExpectedSlippageRate:.003m,
        IntendedAtUtc:IntendedAt);
}

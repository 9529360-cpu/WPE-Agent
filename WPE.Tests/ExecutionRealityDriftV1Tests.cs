using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionRealityDriftV1Tests
{
    private static readonly DateTimeOffset IntendedAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FilledLongOpen_ComputesAdversePriceFeeAndLatencyDrift()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = new ExecutionRealityObservationV1(
            expected.ClientOrderId, "FILLED", 2m, 101m, .0808m,
            IntendedAt.AddMilliseconds(750), IntendedAt.AddSeconds(1));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(ExecutionRealityStateV1.Filled, fact.State);
        Assert.True(fact.Terminal);
        Assert.True(fact.Comparable);
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
    public void FilledShortOpen_FavorablePriceIsNegativeSlippage()
    {
        var expected = Expectation(PositionSide.Short, reduceOnly:false, expectedPrice:100m);
        var observed = new ExecutionRealityObservationV1(
            expected.ClientOrderId, "FILLED", 2m, 99m, .0792m,
            IntendedAt.AddMilliseconds(300), IntendedAt.AddMilliseconds(500));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(-100m, fact.AdverseSlippageBps);
        Assert.Equal(-130m, fact.SlippageDriftBps);
        Assert.Equal(-130m, fact.TotalExecutionDriftBps);
    }

    [Fact]
    public void ReduceLong_UsesSellSideSlippageDirection()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:true, expectedPrice:100m);
        var observed = new ExecutionRealityObservationV1(
            expected.ClientOrderId, "FILLED", 2m, 99m, .0792m,
            IntendedAt.AddMilliseconds(300), IntendedAt.AddMilliseconds(500));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(100m, fact.AdverseSlippageBps);
    }

    [Fact]
    public void PartialFill_IsComparableButNotTerminalWhileExchangeIsOpen()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = new ExecutionRealityObservationV1(
            expected.ClientOrderId, "PARTIALLY_FILLED", .5m, 100.2m, .02004m,
            IntendedAt.AddMilliseconds(200), IntendedAt.AddMilliseconds(250));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(ExecutionRealityStateV1.Partial, fact.State);
        Assert.False(fact.Terminal);
        Assert.True(fact.Comparable);
        Assert.Equal(.25m, fact.FillRatio);
        Assert.Equal("partial-fill", fact.ReasonCode);
    }

    [Fact]
    public void RejectedOrder_IsNotComparableAndDoesNotInventZeroCostAsPerformance()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = new ExecutionRealityObservationV1(
            expected.ClientOrderId, "REJECTED", 0m, 0m, 0m,
            IntendedAt.AddMilliseconds(100), IntendedAt.AddMilliseconds(120));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);

        Assert.Equal(ExecutionRealityStateV1.NotFilled, fact.State);
        Assert.True(fact.Terminal);
        Assert.False(fact.Comparable);
        Assert.Equal("terminal-no-fill", fact.ReasonCode);
        Assert.Equal(0m, fact.TotalExecutionDriftBps);
    }

    [Fact]
    public void UnknownStatus_FailsClosedAsUnknown()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = new ExecutionRealityObservationV1(
            expected.ClientOrderId, "VENUE_PENDING_RECONCILIATION", 0m, 0m, 0m,
            IntendedAt.AddMilliseconds(100), IntendedAt.AddMilliseconds(120));

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
        var observed = new ExecutionRealityObservationV1(
            expected.ClientOrderId, "FILLED", 1m, 100m, .04m,
            IntendedAt.AddMilliseconds(100), IntendedAt.AddMilliseconds(120));

        Assert.Throws<InvalidOperationException>(() => ExecutionRealityDriftV1.Analyze(expected, observed));
    }

    [Fact]
    public void ClientOrderIdentityMismatchFailsClosed()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = new ExecutionRealityObservationV1(
            "different-order", "FILLED", 2m, 100m, .08m,
            IntendedAt.AddMilliseconds(100), IntendedAt.AddMilliseconds(120));

        Assert.Throws<InvalidOperationException>(() => ExecutionRealityDriftV1.Analyze(expected, observed));
    }

    [Fact]
    public void CanonicalHashDetectsTampering()
    {
        var expected = Expectation(PositionSide.Long, reduceOnly:false, expectedPrice:100m);
        var observed = new ExecutionRealityObservationV1(
            expected.ClientOrderId, "FILLED", 2m, 100m, .08m,
            IntendedAt.AddMilliseconds(100), IntendedAt.AddMilliseconds(120));

        var fact = ExecutionRealityDriftV1.Analyze(expected, observed);
        Assert.True(ExecutionRealityDriftV1.IsCanonical(fact));
        Assert.False(ExecutionRealityDriftV1.IsCanonical(fact with { AveragePrice = 101m }));
    }

    private static ExecutionRealityExpectationV1 Expectation(PositionSide side, bool reduceOnly, decimal expectedPrice) => new(
        CorrelationId:"cycle-1",
        ClientOrderId:"order-1",
        StrategyId:"trend-btc",
        StrategyVersion:"v1",
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

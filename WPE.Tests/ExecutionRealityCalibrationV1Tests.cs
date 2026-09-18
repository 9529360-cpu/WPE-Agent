using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionRealityCalibrationV1Tests
{
    private static readonly DateTimeOffset IntendedAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MixedObservationsProduceDeterministicDescriptiveCalibration()
    {
        var facts = new[]
        {
            Fact("a", "FILLED", 1m, 100.2m, .04008m, true, 100),
            Fact("b", "FILLED", 1m, 100.4m, .0502m, true, 200),
            Fact("c", "PARTIALLY_FILLED", .5m, 99.9m, 0m, false, 300),
            Fact("d", "REJECTED", 0m, 0m, 0m, false, 400)
        };

        var snapshot = ExecutionRealityCalibrationV1.Create("strategy-a", "v1", facts);

        Assert.Equal(4, snapshot.ObservationCount);
        Assert.Equal(3, snapshot.ComparableCount);
        Assert.Equal(2, snapshot.FeeComparableCount);
        Assert.Equal(2, snapshot.TotalComparableCount);
        Assert.Equal(2, snapshot.FilledCount);
        Assert.Equal(1, snapshot.PartialCount);
        Assert.Equal(1, snapshot.TerminalNoFillCount);
        Assert.Equal(0, snapshot.UnknownCount);
        Assert.Equal((1m + 1m + .5m) / 3m, snapshot.AverageFillRatio);
        Assert.Equal(20m, snapshot.MedianAdverseSlippageBps);
        Assert.Equal(40m, snapshot.P95AdverseSlippageBps);
        Assert.Equal(10m, snapshot.MedianSlippageDriftBps);
        Assert.Equal(4.5m, snapshot.MedianObservedFeeRateBps);
        Assert.Equal(.5m, snapshot.MedianFeeDriftBps);
        Assert.Equal(20.5m, snapshot.MedianTotalExecutionDriftBps);
        Assert.Equal(31m, snapshot.P95TotalExecutionDriftBps);
        Assert.Equal(250, snapshot.MedianObservationLatencyMs);
        Assert.Equal(400, snapshot.P95ObservationLatencyMs);
        Assert.Equal(IntendedAt.AddMilliseconds(400), snapshot.LatestObservedAtUtc);
        Assert.True(ExecutionRealityCalibrationV1.IsCanonical(snapshot));
    }

    [Fact]
    public void CrossStrategyAndTamperedFactsAreRejected()
    {
        var valid = Fact("a", "FILLED", 1m, 100m, .04m, true, 100);
        var other = MakeFact("strategy-b", "v1", "b", "FILLED", 1m, 100m, .04m, true, 200);

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionRealityCalibrationV1.Create("strategy-a", "v1", new[] { valid, other }));

        var tampered = valid with { AveragePrice = 101m };
        Assert.Throws<InvalidOperationException>(() =>
            ExecutionRealityCalibrationV1.Create("strategy-a", "v1", new[] { tampered }));
    }

    [Fact]
    public void CalibrationHashDetectsProjectionTampering()
    {
        var snapshot = ExecutionRealityCalibrationV1.Create(
            "strategy-a", "v1", new[] { Fact("a", "FILLED", 1m, 100m, .04m, true, 100) });

        Assert.True(ExecutionRealityCalibrationV1.IsCanonical(snapshot));
        Assert.False(ExecutionRealityCalibrationV1.IsCanonical(snapshot with { FilledCount = 99 }));
    }

    private static ExecutionRealityDriftFactV1 Fact(
        string orderId,
        string status,
        decimal executed,
        decimal price,
        decimal fee,
        bool feeAvailable,
        long latencyMs) =>
        MakeFact("strategy-a", "v1", orderId, status, executed, price, fee, feeAvailable, latencyMs);

    private static ExecutionRealityDriftFactV1 MakeFact(
        string strategyId,
        string strategyVersion,
        string orderId,
        string status,
        decimal executed,
        decimal price,
        decimal fee,
        bool feeAvailable,
        long latencyMs)
    {
        var expected = new ExecutionRealityExpectationV1(
            "cycle-" + orderId,
            orderId,
            strategyId,
            strategyVersion,
            "BTCUSDT",
            PositionSide.Long,
            false,
            ExecutionOrderType.Market,
            1m,
            100m,
            .0004m,
            .001m,
            IntendedAt);
        var observedAt = IntendedAt.AddMilliseconds(latencyMs);
        var observation = new ExecutionRealityObservationV1(
            orderId,
            status,
            executed,
            price,
            fee,
            feeAvailable ? ExecutionRealityDriftV1.ExchangeReportedFeeBasis : ExecutionRealityDriftV1.UnavailableFeeBasis,
            observedAt.AddMilliseconds(-50),
            observedAt);
        return ExecutionRealityDriftV1.Analyze(expected, observation);
    }
}

using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionSimulationComparisonV1Tests
{
    private static readonly DateTimeOffset MarketAt = new(2026, 9, 18, 11, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset SimulatedAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 18, 12, 0, 1, TimeSpan.Zero);

    [Fact]
    public void MatchingModeledFillProducesZeroDriftAndCanonicalComparison()
    {
        var simulated = Simulated(
            ExecutionSimulationFillStateV1.Filled,
            executed:1m,
            price:100m,
            fee:0.04m,
            feeRole:ExecutionSimulationFeeRoleV1.Taker,
            latencyModeled:true,
            latencyMs:500);
        var observed = Observed("FILLED", 1m, 100m, 0.04m, feeAvailable:true, latencyMs:500);

        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));

        Assert.True(comparison.StateMatch);
        Assert.Equal(0m, comparison.FillRatioDelta);
        Assert.True(comparison.PriceComparable);
        Assert.Equal(0m, comparison.PriceDriftBps);
        Assert.True(comparison.FeeComparable);
        Assert.Equal(0m, comparison.FeeDriftBps);
        Assert.True(comparison.LatencyComparable);
        Assert.Equal(0, comparison.LatencyDriftMs);
        Assert.True(comparison.TotalComparable);
        Assert.Equal(0m, comparison.TotalExecutionDriftBps);
        Assert.Equal("comparable", comparison.ReasonCode);
        Assert.True(ExecutionSimulationComparisonCanonicalizerV1.IsCanonical(comparison));
    }

    [Fact]
    public void ObservedWorseLongBuyFillIsPositiveAdverseDrift()
    {
        var simulated = Simulated(
            ExecutionSimulationFillStateV1.Filled,
            executed:1m,
            price:100m,
            fee:0.04m,
            feeRole:ExecutionSimulationFeeRoleV1.Taker,
            latencyModeled:true,
            latencyMs:300);
        var observed = Observed("FILLED", 1m, 101m, 0.0404m, feeAvailable:true, latencyMs:800);

        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));

        Assert.Equal(100m, comparison.PriceDriftBps);
        Assert.Equal(0m, comparison.FeeDriftBps);
        Assert.Equal(500, comparison.LatencyDriftMs);
        Assert.Equal(100m, comparison.TotalExecutionDriftBps);
    }

    [Fact]
    public void PartialSimulationVersusFullObservedFillPreservesFillStateDrift()
    {
        var simulated = Simulated(
            ExecutionSimulationFillStateV1.Partial,
            executed:0.5m,
            price:100m,
            fee:0.02m,
            feeRole:ExecutionSimulationFeeRoleV1.Taker,
            latencyModeled:false,
            latencyMs:0);
        var observed = Observed("FILLED", 1m, 100m, 0.04m, feeAvailable:true, latencyMs:900);

        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));

        Assert.False(comparison.StateMatch);
        Assert.Equal(0.5m, comparison.SimulatedFillRatio);
        Assert.Equal(1m, comparison.ObservedFillRatio);
        Assert.Equal(0.5m, comparison.FillRatioDelta);
        Assert.False(comparison.LatencyComparable);
        Assert.Null(comparison.LatencyDriftMs);
        Assert.Equal("fill-state-drift", comparison.ReasonCode);
    }

    [Fact]
    public void MissingSimulatedFeeKeepsPriceComparisonButWithholdsTotalCost()
    {
        var simulated = Simulated(
            ExecutionSimulationFillStateV1.Filled,
            executed:1m,
            price:100m,
            fee:0m,
            feeRole:ExecutionSimulationFeeRoleV1.Unavailable,
            latencyModeled:true,
            latencyMs:500);
        var observed = Observed("FILLED", 1m, 100.5m, 0.0402m, feeAvailable:true, latencyMs:500);

        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));

        Assert.True(comparison.PriceComparable);
        Assert.Equal(50m, comparison.PriceDriftBps);
        Assert.False(comparison.FeeComparable);
        Assert.Null(comparison.FeeDriftBps);
        Assert.False(comparison.TotalComparable);
        Assert.Null(comparison.TotalExecutionDriftBps);
        Assert.Equal("fee-not-comparable", comparison.ReasonCode);
    }

    [Fact]
    public void UnsupportedSimulationCannotInventOptimisticZeroExecution()
    {
        var simulated = Simulated(
            ExecutionSimulationFillStateV1.Unsupported,
            executed:0m,
            price:0m,
            fee:0m,
            feeRole:ExecutionSimulationFeeRoleV1.Unavailable,
            latencyModeled:false,
            latencyMs:0,
            reason:"venue-rule-unsupported");
        var observed = Observed("FILLED", 1m, 100m, 0.04m, feeAvailable:true, latencyMs:500);

        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));

        Assert.False(comparison.StateMatch);
        Assert.False(comparison.PriceComparable);
        Assert.Null(comparison.PriceDriftBps);
        Assert.False(comparison.FeeComparable);
        Assert.Null(comparison.FeeDriftBps);
        Assert.False(comparison.TotalComparable);
        Assert.Equal("simulation-unsupported", comparison.ReasonCode);
    }

    [Fact]
    public void NotFilledSimulationCanCompareStateWithoutInventingPriceEconomics()
    {
        var simulated = Simulated(
            ExecutionSimulationFillStateV1.NotFilled,
            executed:0m,
            price:0m,
            fee:0m,
            feeRole:ExecutionSimulationFeeRoleV1.Unavailable,
            latencyModeled:true,
            latencyMs:1000,
            reason:"limit-not-crossed");
        var observed = Observed("REJECTED", 0m, 0m, 0m, feeAvailable:false, latencyMs:1200);

        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1));

        Assert.True(comparison.StateMatch);
        Assert.False(comparison.PriceComparable);
        Assert.Null(comparison.PriceDriftBps);
        Assert.True(comparison.LatencyComparable);
        Assert.Equal(200, comparison.LatencyDriftMs);
        Assert.Equal("price-not-comparable", comparison.ReasonCode);
    }

    [Fact]
    public void IdentityMismatchFailsClosed()
    {
        var simulated = Simulated(
            ExecutionSimulationFillStateV1.Filled,
            executed:1m,
            price:100m,
            fee:0.04m,
            feeRole:ExecutionSimulationFeeRoleV1.Taker,
            latencyModeled:true,
            latencyMs:500);
        var observed = Observed(
            "FILLED", 1m, 100m, 0.04m, feeAvailable:true, latencyMs:500,
            strategyVersion:"different-version");

        Assert.True(ExecutionRealityDriftV1.IsCanonical(observed));
        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationComparisonCanonicalizerV1.Create(simulated, observed, ObservedAt.AddSeconds(1)));
    }

    [Fact]
    public void FutureMarketEvidenceAndUnsupportedEconomicsFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExecutionSimulationFillCanonicalizerV1.Create(
                "cycle-1","order-1","strategy-a","v7","research-cost-v1","sim-v1","binance-testnet-rules-v1",
                "BTCUSDT",PositionSide.Long,false,ExecutionOrderType.Market,1m,
                ExecutionSimulationFillStateV1.Filled,1m,100m,0.04m,ExecutionSimulationFeeRoleV1.Taker,
                true,500,SimulatedAt.AddSeconds(1),SimulatedAt,"future-market"));

        Assert.Throws<InvalidOperationException>(() =>
            Simulated(
                ExecutionSimulationFillStateV1.Unsupported,
                executed:0m,
                price:100m,
                fee:0m,
                feeRole:ExecutionSimulationFeeRoleV1.Unavailable,
                latencyModeled:false,
                latencyMs:0,
                reason:"unsupported"));
    }

    [Fact]
    public void CanonicalHashesDetectTampering()
    {
        var simulated = Simulated(
            ExecutionSimulationFillStateV1.Filled,
            executed:1m,
            price:100m,
            fee:0.04m,
            feeRole:ExecutionSimulationFeeRoleV1.Taker,
            latencyModeled:true,
            latencyMs:500);
        Assert.True(ExecutionSimulationFillCanonicalizerV1.IsCanonical(simulated));
        Assert.False(ExecutionSimulationFillCanonicalizerV1.IsCanonical(simulated with { AveragePrice = 999m }));

        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(
            simulated,
            Observed("FILLED",1m,100m,0.04m,true,500),
            ObservedAt.AddSeconds(1));
        Assert.True(ExecutionSimulationComparisonCanonicalizerV1.IsCanonical(comparison));
        Assert.False(ExecutionSimulationComparisonCanonicalizerV1.IsCanonical(comparison with { FillRatioDelta = 0.5m }));
    }

    private static ExecutionSimulationFillV1 Simulated(
        ExecutionSimulationFillStateV1 state,
        decimal executed,
        decimal price,
        decimal fee,
        ExecutionSimulationFeeRoleV1 feeRole,
        bool latencyModeled,
        long latencyMs,
        string reason = "modeled") =>
        ExecutionSimulationFillCanonicalizerV1.Create(
            correlationId:"cycle-1",
            clientOrderId:"order-1",
            strategyId:"strategy-a",
            strategyVersion:"v7",
            costModelVersion:"research-cost-v1",
            simulationModelVersion:"execution-sim-v1",
            venueRuleVersion:"binance-testnet-rules-v1",
            symbol:"BTCUSDT",
            side:PositionSide.Long,
            reduceOnly:false,
            orderType:ExecutionOrderType.Market,
            intendedQuantity:1m,
            state:state,
            executedQuantity:executed,
            averagePrice:price,
            feeAmount:fee,
            feeRole:feeRole,
            latencyModeled:latencyModeled,
            simulatedLatencyMs:latencyMs,
            marketAsOfUtc:MarketAt,
            simulatedAtUtc:SimulatedAt,
            reasonCode:reason);

    private static ExecutionRealityDriftFactV1 Observed(
        string status,
        decimal executed,
        decimal price,
        decimal fee,
        bool feeAvailable,
        long latencyMs,
        string strategyVersion = "v7")
    {
        var expectation = new ExecutionRealityExpectationV1(
            "cycle-1","order-1","strategy-a",strategyVersion,"research-cost-v1","BTCUSDT",
            PositionSide.Long,false,ExecutionOrderType.Market,1m,100m,0.0004m,0.001m,SimulatedAt);
        var observedAt = SimulatedAt.AddMilliseconds(latencyMs);
        var observation = new ExecutionRealityObservationV1(
            "order-1",
            status,
            executed,
            price,
            feeAvailable ? fee : 0m,
            feeAvailable ? ExecutionRealityDriftV1.ExchangeReportedFeeBasis : ExecutionRealityDriftV1.UnavailableFeeBasis,
            observedAt.AddMilliseconds(-100),
            observedAt);
        return ExecutionRealityDriftV1.Analyze(expectation, observation);
    }
}

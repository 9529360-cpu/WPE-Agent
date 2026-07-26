using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class DegradedPlanningTests
{
    [Fact]
    public void Planner_RejectsIncreaseButAllowsReductionWhenRecoveryIsUnsafe()
    {
        var planner = new RiskAndPositionPlanner();
        var evidence = Evidence();
        var rule = new TradingRule("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m, 125);
        var limits = new RiskLimits();

        var increase = planner.Plan(Decision(DecisionAction.AddLong), evidence, rule, limits, 1_000, safeToIncreaseRisk: false, "unknown order");
        var reduction = planner.Plan(Decision(DecisionAction.ReduceLong), evidence, rule, limits, 1_000, safeToIncreaseRisk: false, "unknown order");

        Assert.Empty(increase.Intents);
        Assert.Contains("unknown order", increase.Result, StringComparison.Ordinal);
        var reduceIntent = Assert.Single(reduction.Intents);
        Assert.True(reduceIntent.ReduceOnly);
        Assert.Equal(0.005m, reduceIntent.Quantity);
    }

    private static EvidencePack Evidence() => new()
    {
        Completeness = 100,
        Account = new AccountSnapshot(1_000, 1_000, 1_000, DateTime.UtcNow),
        Positions = [new ManagedPosition("BTCUSDT", PositionSide.Long, 0.01m, 49_000, 50_000, 10, 10, true, 40_000)],
        Markets = new Dictionary<string, MarketEvidence>
        {
            ["BTCUSDT"] = new("BTCUSDT", 50_000, 49_000, 51_000, 50, 0, 0, 0, new(0, 0, 0, 0, 0, 0, 0), DateTime.UtcNow)
        }
    };

    private static DecisionPlan Decision(DecisionAction action) => new()
    {
        Action = action,
        Instrument = "BTCUSDT",
        TargetTier = 1,
        StopLossPrice = 49_000,
        TakeProfitPrice = 51_000
    };
}

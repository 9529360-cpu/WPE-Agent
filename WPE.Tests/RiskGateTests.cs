using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class RiskGateTests
{
    [Fact]
    public void Approve_RejectsWhenRiskSnapshotIsStale()
    {
        var manager = new RiskManager();

        var approved = manager.Approve(new TradeAction(TradeActionType.EnterLong, 0.01, "test"));

        Assert.False(approved);
    }

    [Fact]
    public async Task Approve_RejectsQuantityAboveConfiguredCap()
    {
        var manager = new RiskManager();
        manager.Configure(new RiskConfiguration(0.1, 0.15, 0.05, 1.5, 3, TimeSpan.FromMinutes(1)));
        await manager.UpdateAsync(Snapshot());

        var approved = manager.Approve(new TradeAction(TradeActionType.EnterLong, 0.11, "test"));

        Assert.False(approved);
    }

    [Fact]
    public void Planner_RejectsRiskIncreaseWhenEvidenceIsIncomplete()
    {
        var planner = new RiskAndPositionPlanner();
        var evidence = new EvidencePack
        {
            Completeness = 69,
            Account = new AccountSnapshot(1_000, 1_000, 1_000, DateTime.UtcNow),
            Markets = new Dictionary<string, MarketEvidence>
            {
                ["BTCUSDT"] = new("BTCUSDT", 50_000, 49_000, 51_000, 50, 0, 0, 0, new(0, 0, 0, 0, 0, 0, 0), DateTime.UtcNow)
            }
        };
        var decision = new DecisionPlan
        {
            Action = DecisionAction.OpenLong,
            Instrument = "BTCUSDT",
            TargetTier = 1,
            StopLossPrice = 49_000,
            TakeProfitPrice = 51_000
        };

        var result = planner.Plan(decision, evidence, new TradingRule("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m, 125), new RiskLimits(), 1_000);

        Assert.Empty(result.Intents);
        Assert.Contains("Risk.Completeness", result.Result, StringComparison.Ordinal);
    }

    [Fact]
    public void PerformanceThrottle_ReducesRiskInsteadOfBlockingAfterLossStreak()
    {
        var throttle = PerformanceRiskThrottle.Evaluate(
            new RiskHistorySnapshot(-120m, 3, 0, false),
            equity: 1_000m,
            dayHigh: 1_100m,
            new RiskLimits { MaxDailyLoss = .05m, DailyDrawdownLimit = .08m });

        Assert.Equal(.25, throttle.Multiplier, 8);
        Assert.Equal("MINIMUM", throttle.Mode);
        Assert.Contains(throttle.Reasons, reason => reason.StartsWith("loss-streak:", StringComparison.Ordinal));
        Assert.Contains(throttle.Reasons, reason => reason.StartsWith("daily-loss:", StringComparison.Ordinal));
        Assert.Contains(throttle.Reasons, reason => reason.StartsWith("intraday-drawdown:", StringComparison.Ordinal));
    }

    [Fact]
    public void Planner_DoesNotHardBlockWhenDailyDrawdownThresholdIsExceeded()
    {
        var planner = new RiskAndPositionPlanner();
        var evidence = new EvidencePack
        {
            Completeness = 100,
            Account = new AccountSnapshot(900, 900, 900, DateTime.UtcNow),
            Markets = new Dictionary<string, MarketEvidence>
            {
                ["BTCUSDT"] = new("BTCUSDT", 50_000, 49_000, 51_000, 50, 0, 0, 0, new(0, 0, 0, 0, 0, 0, 0), DateTime.UtcNow)
            }
        };
        var decision = new DecisionPlan
        {
            Action = DecisionAction.OpenLong,
            Instrument = "BTCUSDT",
            TargetTier = 1,
            EntryPrice = 50_000,
            StopLossPrice = 49_000,
            TakeProfitPrice = 52_000,
            RiskRewardRatio = 2
        };

        var result = planner.Plan(
            decision,
            evidence,
            new TradingRule("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m, 125),
            new RiskLimits { DailyDrawdownLimit = .08m },
            dayHigh: 1_000m,
            riskBudgetMultiplier: .25);

        Assert.Single(result.Intents);
        Assert.DoesNotContain("Drawdown", result.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyRiskManager_DoesNotBlacklistAfterConsecutiveLosses()
    {
        var manager = new RiskManager();
        manager.Configure(new RiskConfiguration(0.1, 0.15, 0.05, 1.5, 3, TimeSpan.FromMinutes(1)));
        await manager.UpdateAsync(new PositionSnapshot(
            "BTCUSDT", 0.01, 50_000, 50_000, 0, 0, 1_000, 0, 0, 10));

        Assert.True(manager.Approve(new TradeAction(TradeActionType.EnterLong, 0.01, "adaptive risk")));
        Assert.Empty(manager.CurrentProfile.BlacklistedSymbols);
    }

    private static PositionSnapshot Snapshot() => new(
        "BTCUSDT", 0.01, 50_000, 50_000, 0, 0, 1_000, 0, 0, 0);
}

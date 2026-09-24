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

        var result = planner.Plan(decision, evidence, new TradingRule("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m, 125), new RiskLimits());

        Assert.Empty(result.Intents);
        Assert.Contains("Risk.Completeness", result.Result, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionRiskGate_BlocksAtConfiguredDailyLossWithoutLossStreakState()
    {
        var gate = SessionRiskGate.Evaluate(
            new RiskHistorySnapshot(-60m, 0, false),
            equity: 1_000m,
            dayHigh: 1_000m,
            new RiskLimits { MaxDailyLoss = .05m, DailyDrawdownLimit = .08m });

        Assert.False(gate.AllowsRiskIncrease);
        Assert.Contains(gate.Reasons, reason => reason.StartsWith("daily-loss-limit:", StringComparison.Ordinal));
        Assert.DoesNotContain(gate.Reasons, reason => reason.Contains("streak", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SessionRiskGate_BlocksAtConfiguredIntradayDrawdown()
    {
        var gate = SessionRiskGate.Evaluate(
            new RiskHistorySnapshot(0m, 0, false),
            equity: 900m,
            dayHigh: 1_000m,
            new RiskLimits { MaxDailyLoss = .05m, DailyDrawdownLimit = .08m });

        Assert.False(gate.AllowsRiskIncrease);
        Assert.Contains(gate.Reasons, reason => reason.StartsWith("intraday-drawdown-limit:", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionRiskPathDoesNotAdaptSizingFromLossStreaks()
    {
        var planner = File.ReadAllText(SourcePath("Services","Agent","RiskAndPositionPlanner.cs"));
        var agent = File.ReadAllText(SourcePath("Services","AutoTradingAgent.cs"));
        var store = File.ReadAllText(SourcePath("Services","Agent","AgentSqliteStore.cs"));

        Assert.DoesNotContain("PerformanceRiskThrottle", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("riskBudgetMultiplier", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("ConsecutiveLosses", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("ConsecutiveLosses", store, StringComparison.Ordinal);
        Assert.DoesNotContain("loss-streak:", agent, StringComparison.Ordinal);
        Assert.Contains("SessionRiskGate.Evaluate", agent, StringComparison.Ordinal);
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

    private static string SourcePath(params string[] path)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        return Path.Combine([root,..path]);
    }

    private static PositionSnapshot Snapshot() => new(
        "BTCUSDT", 0.01, 50_000, 50_000, 0, 0, 1_000, 0, 0, 0);
}

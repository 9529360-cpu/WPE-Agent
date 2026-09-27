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
    public void PlannerCapsInitialMarginPerEntryEvenWhenRequestedLeverageIsVeryHigh()
    {
        var planner=new RiskAndPositionPlanner();
        var evidence=new EvidencePack
        {
            Completeness=100,
            Account=new AccountSnapshot(1_000m,1_000m,1_000m,DateTime.UtcNow),
            Markets=new Dictionary<string,MarketEvidence>
            {
                ["BTCUSDT"]=new("BTCUSDT",100m,95m,110m,50,0,0,0,new(0,0,0,0,0,0,0),DateTime.UtcNow)
            }
        };
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            TargetTier=1,
            EntryPrice=100m,
            StopLossPrice=99.5m,
            TakeProfitPrice=101m
        };
        var limits=new RiskLimits
        {
            Leverage=150,
            MaxInitialMarginPerTrade=.05m,
            MarginTiers=[.35m],
            MaxMargin=.50m,
            MaxRiskPerTrade=1m,
            MaxSymbolExposure=10m,
            MaxAccountExposure=10m,
            MinimumRiskReward=1.8
        };

        var result=planner.Plan(
            decision,
            evidence,
            new TradingRule("BTCUSDT",.1m,.1m,.1m,5m,125),
            limits);

        var intent=Assert.Single(result.Intents);
        var effectiveLeverage=(decimal)RiskAndPositionPlanner.SelectEffectiveLeverage(
            decision,
            new TradingRule("BTCUSDT",.1m,.1m,.1m,5m,125),
            limits);
        var initialMargin=intent.Quantity*intent.ExpectedPrice/effectiveLeverage;
        Assert.True(initialMargin>0);
        Assert.True(initialMargin<=evidence.Account.Equity*.05m);
        Assert.True(intent.Quantity*Math.Abs(intent.ExpectedPrice-intent.StopLoss)<=evidence.Account.Equity*limits.MaxRiskPerTrade);
    }

    [Fact]
    public void PlannerBlocksSixthDistinctPositionButAllowsFifthWithinAccountBasedSizing()
    {
        static ManagedPosition Position(string symbol)=>new(symbol,PositionSide.Long,1m,100m,100m,0m,5m,true,80m);
        var planner=new RiskAndPositionPlanner();
        var market=new MarketEvidence("NEWUSDT",100m,95m,110m,50,0,0,0,new(0,0,0,0,0,0,0),DateTime.UtcNow)
        {
            Quality=new MarketQualityEvidence{AtrPercent=.01,LiquidityScore=1,QualityScore=100,BestBid=99.9m,BestAsk=100m}
        };
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="NEWUSDT",
            TargetTier=1,
            EntryPrice=100m,
            StopLossPrice=98m,
            TakeProfitPrice=104m,
            RiskRewardRatio=2
        };
        var limits=new RiskLimits
        {
            Leverage=20,
            MaxConcurrentPositions=5,
            MaxInitialMarginPerTrade=.05m,
            MarginTiers=[.10m],
            MaxMargin=.50m,
            MaxRiskPerTrade=.01m,
            MaxSymbolExposure=.25m,
            MaxAccountExposure=.50m,
            MinimumRiskReward=1.8
        };
        EvidencePack Evidence(params string[] symbols)=>new()
        {
            Completeness=100,
            Account=new AccountSnapshot(10_000m,10_000m,10_000m,DateTime.UtcNow),
            Markets=new Dictionary<string,MarketEvidence>{{"NEWUSDT",market}},
            Positions=symbols.Select(Position).ToArray()
        };
        var rule=new TradingRule("NEWUSDT",.1m,.1m,.1m,5m,125);

        var fifth=planner.Plan(decision,Evidence("AUSDT","BUSDT","CUSDT","DUSDT"),rule,limits);
        var fifthIntent=Assert.Single(fifth.Intents);
        var leverage=(decimal)RiskAndPositionPlanner.SelectEffectiveLeverage(decision,rule,limits);
        var fifthMargin=fifthIntent.Quantity*fifthIntent.ExpectedPrice/leverage;
        Assert.True(fifthMargin<=10_000m*limits.MaxInitialMarginPerTrade);
        Assert.True(fifthIntent.Quantity*Math.Abs(fifthIntent.ExpectedPrice-fifthIntent.StopLoss)<=10_000m*limits.MaxRiskPerTrade);

        var sixth=planner.Plan(decision,Evidence("AUSDT","BUSDT","CUSDT","DUSDT","EUSDT"),rule,limits);
        Assert.Empty(sixth.Intents);
        Assert.Equal("risk.max-concurrent-positions:5",sixth.Result);
    }

    [Fact]
    public void DeterministicPlanRepairsInvalidLongTakeProfitBeforeRiskPlanning()
    {
        var market=new MarketEvidence(
            "BTCUSDT",100m,95m,100.1m,50,0,0,0,
            new DerivativesSnapshot(0,0,0,0,0,0,0),
            DateTime.UtcNow)
        {
            Quality=new MarketQualityEvidence{AtrPercent=.004}
        };
        var source=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            TargetTier=1,
            EntryPrice=100m,
            StopLossPrice=99.5m,
            TakeProfitPrice=100m
        };
        var limits=new RiskLimits
        {
            Leverage=150,
            MaxInitialMarginPerTrade=.05m,
            MarginTiers=[.35m],
            MaxMargin=.50m,
            MaxRiskPerTrade=1m,
            MaxSymbolExposure=10m,
            MaxAccountExposure=10m,
            MinimumRiskReward=1.8
        };

        var completed=new DeterministicPlanSkill().Complete(source,market,limits);

        Assert.True(completed.TakeProfitPrice>completed.EntryPrice);
        Assert.True(completed.RiskRewardRatio>=limits.MinimumRiskReward);

        var evidence=new EvidencePack
        {
            Completeness=100,
            Account=new AccountSnapshot(1_000m,1_000m,1_000m,DateTime.UtcNow),
            Markets=new Dictionary<string,MarketEvidence>{{"BTCUSDT",market}}
        };
        var planned=new RiskAndPositionPlanner().Plan(
            completed,
            evidence,
            new TradingRule("BTCUSDT",.1m,.1m,.1m,5m,125),
            limits);

        Assert.Single(planned.Intents);
        Assert.DoesNotContain("保护价格方向错误",planned.Result,StringComparison.Ordinal);
    }

    [Fact]
    public void DeterministicPlanRepairsInvalidShortTakeProfitBeforeRiskPlanning()
    {
        var market=new MarketEvidence(
            "BTCUSDT",100m,99.9m,105m,50,0,0,0,
            new DerivativesSnapshot(0,0,0,0,0,0,0),
            DateTime.UtcNow)
        {
            Quality=new MarketQualityEvidence{AtrPercent=.004}
        };
        var source=new DecisionPlan
        {
            Action=DecisionAction.OpenShort,
            Instrument="BTCUSDT",
            TargetTier=1,
            EntryPrice=100m,
            StopLossPrice=100.5m,
            TakeProfitPrice=100m
        };
        var limits=new RiskLimits
        {
            Leverage=150,
            MaxInitialMarginPerTrade=.05m,
            MarginTiers=[.35m],
            MaxMargin=.50m,
            MaxRiskPerTrade=1m,
            MaxSymbolExposure=10m,
            MaxAccountExposure=10m,
            MinimumRiskReward=1.8
        };

        var completed=new DeterministicPlanSkill().Complete(source,market,limits);

        Assert.True(completed.TakeProfitPrice<completed.EntryPrice);
        Assert.True(completed.RiskRewardRatio>=limits.MinimumRiskReward);

        var evidence=new EvidencePack
        {
            Completeness=100,
            Account=new AccountSnapshot(1_000m,1_000m,1_000m,DateTime.UtcNow),
            Markets=new Dictionary<string,MarketEvidence>{{"BTCUSDT",market}}
        };
        var planned=new RiskAndPositionPlanner().Plan(
            completed,
            evidence,
            new TradingRule("BTCUSDT",.1m,.1m,.1m,5m,125),
            limits);

        Assert.Single(planned.Intents);
        Assert.DoesNotContain("保护价格方向错误",planned.Result,StringComparison.Ordinal);
    }

    [Fact]
    public void DeterministicMinimumRiskRewardFallbackRemainsExactlyAtConfiguredFloor()
    {
        var market=new MarketEvidence(
            "SOONUSDT",.3069m,.2865317857142857m,.3069m,50,0,0,0,
            new DerivativesSnapshot(0,0,0,0,0,0,0),DateTime.UtcNow)
        {
            Quality=new MarketQualityEvidence{AtrPercent=.01}
        };
        var source=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="SOONUSDT",
            TargetTier=1,
            EntryPrice=.3069m,
            StopLossPrice=.2865317857142857m,
            TakeProfitPrice=0m
        };
        var limits=new RiskLimits{MinimumRiskReward=1.8};

        var completed=new DeterministicPlanSkill().Complete(source,market,limits);

        Assert.Equal(limits.MinimumRiskReward,completed.RiskRewardRatio);
        Assert.True(completed.RiskRewardRatio>=limits.MinimumRiskReward);
    }

    [Fact]
    public void PlannerDoesNotRejectDeterministicMinimumRrFallbackForSubNanoscopicDecimalError()
    {
        var evidence=new EvidencePack
        {
            Completeness=100,
            Account=new AccountSnapshot(1_000m,1_000m,1_000m,DateTime.UtcNow),
            Markets=new Dictionary<string,MarketEvidence>
            {
                ["SOONUSDT"]=new("SOONUSDT",.3057m,.2865789285714286m,.35m,50,0,0,0,new(0,0,0,0,0,0,0),DateTime.UtcNow)
            }
        };
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="SOONUSDT",
            TargetTier=1,
            EntryPrice=.3057m,
            StopLossPrice=.2865789285714286m,
            TakeProfitPrice=.3401179285714286m,
            RiskRewardRatio=1.8
        };
        var limits=new RiskLimits
        {
            Leverage=20,
            MaxInitialMarginPerTrade=.10m,
            MarginTiers=[.50m],
            MaxMargin=.50m,
            MaxRiskPerTrade=1m,
            MaxSymbolExposure=10m,
            MaxAccountExposure=10m,
            MinimumRiskReward=1.8
        };

        var result=new RiskAndPositionPlanner().Plan(
            decision,evidence,new TradingRule("SOONUSDT",1m,.0001m,1m,5m,150),limits);

        Assert.Single(result.Intents);
        Assert.DoesNotContain("风险收益比",result.Result,StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerSizesAgainstFinalRoundedStopSoRiskCannotIncreaseAfterTickAlignment()
    {
        var evidence=new EvidencePack
        {
            Completeness=100,
            Account=new AccountSnapshot(1_000m,1_000m,1_000m,DateTime.UtcNow),
            Markets=new Dictionary<string,MarketEvidence>
            {
                ["BTCUSDT"]=new("BTCUSDT",100m,95m,110m,50,0,0,0,new(0,0,0,0,0,0,0),DateTime.UtcNow)
            }
        };
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            TargetTier=1,
            EntryPrice=100m,
            StopLossPrice=98.04m,
            TakeProfitPrice=103.528m,
            RiskRewardRatio=1.8
        };
        var limits=new RiskLimits
        {
            Leverage=125,
            MaxInitialMarginPerTrade=.10m,
            MarginTiers=[.50m],
            MaxMargin=.50m,
            MaxRiskPerTrade=.01m,
            MaxSymbolExposure=10m,
            MaxAccountExposure=10m,
            MinimumRiskReward=1.8
        };

        var result=new RiskAndPositionPlanner().Plan(
            decision,evidence,new TradingRule("BTCUSDT",.1m,.1m,.1m,5m,125),limits);

        var intent=Assert.Single(result.Intents);
        var actualRisk=intent.Quantity*Math.Abs(intent.ExpectedPrice-intent.StopLoss);
        Assert.Equal(98.0m,intent.StopLoss);
        Assert.True(actualRisk<=evidence.Account.Equity*limits.MaxRiskPerTrade);
    }

    [Fact]
    public void EffectiveLeverageFallsWhenStructuralStopNeedsMoreLiquidationRoom()
    {
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            EntryPrice=100m,
            StopLossPrice=98.1m,
            TakeProfitPrice=104m
        };
        var rule=new TradingRule("BTCUSDT",.1m,.1m,.1m,5m,125);
        var limits=new RiskLimits{Leverage=150};

        var effective=RiskAndPositionPlanner.SelectEffectiveLeverage(decision,rule,limits);

        Assert.Equal(30,effective);
        Assert.True(effective<limits.Leverage);
        Assert.True(effective<=rule.MaxLeverage);
    }

    [Fact]
    public void EffectiveLeverageKeepsRecentBtcStopInsideConservativeLiquidationReserve()
    {
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            EntryPrice=84945m,
            StopLossPrice=84428.2275m,
            TakeProfitPrice=85875.1905m
        };
        var rule=new TradingRule("BTCUSDT",.0001m,.1m,.0001m,5m,125);
        var limits=new RiskLimits{Leverage=150};

        var effective=RiskAndPositionPlanner.SelectEffectiveLeverage(decision,rule,limits);
        var stopFraction=(decision.EntryPrice-decision.StopLossPrice)/decision.EntryPrice;
        const decimal maintenanceAllowance=.006m;
        const decimal usableLiquidationSpan=.70m;
        var conservativeSafeStopFraction=(1m/effective-maintenanceAllowance)*usableLiquidationSpan;

        Assert.Equal(68,effective);
        Assert.True(stopFraction<=conservativeSafeStopFraction);
        Assert.True(effective<115);
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

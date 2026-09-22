using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class AdaptiveStrategyPortfolioAllocatorTests
{
    [Fact]
    public void StrongProvenOpportunityEarnsMoreCapitalAndHigherTier()
    {
        var strong=Selection("BTCUSDT","btc-strong",StrategyFamily.TrendBreakout,.95,.90,.90,MarketRegime.Trending,
            StrategyGovernor.MinimumRegimeCalibrationObservations,.03,.90,StrategyGovernor.MinimumExecutionFeedbackTrades,.75);
        var weaker=Selection("ETHUSDT","eth-weaker",StrategyFamily.MeanReversion,.70,.60,.65,MarketRegime.Ranging,
            StrategyGovernor.MinimumRegimeCalibrationObservations,.005,.65,StrategyGovernor.MinimumExecutionFeedbackTrades,.55);
        var evidence=Evidence(Market("BTCUSDT",95,.92,1),Market("ETHUSDT",72,.65,8));

        var plan=AdaptiveStrategyPortfolioAllocator.Allocate(
            new Dictionary<string,StrategyCycleSelection>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=strong,
                ["ETHUSDT"]=weaker
            },
            evidence,
            3,
            new DateTimeOffset(2026,9,19,21,30,0,TimeSpan.Zero));

        Assert.Equal(2,plan.Allocations.Count);
        Assert.Equal(1d,plan.Allocations.Sum(x=>x.CapitalShare),10);
        var leader=Assert.IsType<AdaptivePortfolioAllocation>(plan.Leader);
        var weak=Assert.Single(plan.Allocations,x=>x.Symbol=="ETHUSDT");
        Assert.Equal("BTCUSDT",leader.Symbol);
        Assert.False(leader.IsExploration);
        Assert.Equal(3,leader.TargetTier);
        Assert.True(leader.OpportunityScore>weak.OpportunityScore);
        Assert.True(leader.CapitalShare>weak.CapitalShare);
        Assert.True(leader.RiskMultiplier>weak.RiskMultiplier);
    }

    [Fact]
    public void NewRegimeOrSparseExecutionUsesSmallExplorationBudgetInsteadOfZeroingOpportunity()
    {
        var selection=Selection("SOLUSDT","sol-new-regime",StrategyFamily.TrendBreakout,.92,.82,.80,MarketRegime.Transition,
            0,0,.5,0,.5);
        var plan=AdaptiveStrategyPortfolioAllocator.Allocate(
            new Dictionary<string,StrategyCycleSelection>{{"SOLUSDT",selection}},
            Evidence(Market("SOLUSDT",90,.90,2)),
            4);

        var allocation=Assert.Single(plan.Allocations);
        Assert.True(allocation.IsExploration);
        Assert.Equal(1,allocation.TargetTier);
        Assert.InRange(allocation.RiskMultiplier,.10,AdaptiveStrategyPortfolioAllocator.MaximumExplorationRiskMultiplier);
        Assert.True(allocation.OpportunityScore>0);
        Assert.Equal(1d,allocation.CapitalShare,10);
    }

    [Fact]
    public void FailureStreakDecaysOpportunityWithoutHardDisablingStrategy()
    {
        var healthy=Selection("BTCUSDT","healthy",StrategyFamily.TrendBreakout,.90,.80,.80,MarketRegime.Trending,
            StrategyGovernor.MinimumRegimeCalibrationObservations,.02,.85,StrategyGovernor.MinimumExecutionFeedbackTrades,.70,0);
        var struggling=Selection("ETHUSDT","struggling",StrategyFamily.TrendBreakout,.90,.80,.80,MarketRegime.Trending,
            StrategyGovernor.MinimumRegimeCalibrationObservations,.02,.85,StrategyGovernor.MinimumExecutionFeedbackTrades,.70,5);
        var plan=AdaptiveStrategyPortfolioAllocator.Allocate(
            new Dictionary<string,StrategyCycleSelection>
            {
                ["BTCUSDT"]=healthy,
                ["ETHUSDT"]=struggling
            },
            Evidence(Market("BTCUSDT",90,.85,2),Market("ETHUSDT",90,.85,2)),
            3);

        var good=Assert.Single(plan.Allocations,x=>x.Symbol=="BTCUSDT");
        var bad=Assert.Single(plan.Allocations,x=>x.Symbol=="ETHUSDT");
        Assert.True(good.OpportunityScore>bad.OpportunityScore);
        Assert.True(bad.OpportunityScore>0);
        Assert.True(good.CapitalShare>bad.CapitalShare);
    }

    [Fact]
    public void ContinuousRiskBudgetMultiplierChangesActualPlannedQuantity()
    {
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="BTCUSDT",
            TargetTier=3,
            Confidence=.8,
            EntryPrice=100m,
            StopLossPrice=98m,
            TakeProfitPrice=104m,
            Reason="adaptive allocation"
        };
        var evidence=Evidence(Market("BTCUSDT",95,.9,1));
        var limits=new RiskLimits
        {
            MarginTiers=[.10m,.20m,.35m],
            MaxMargin=.90m,
            Leverage=10,
            MaxRiskPerTrade=.02m,
            MaxSymbolExposure=.90m,
            MaxAccountExposure=.90m,
            MinimumRiskReward=1.8
        };
        var rule=new TradingRule("BTCUSDT",.001m,.01m,.001m,5m,20);
        var planner=new RiskAndPositionPlanner();

        var full=planner.Plan(decision,evidence,rule,limits,10_000m,riskBudgetMultiplier:1);
        var exploration=planner.Plan(decision,evidence,rule,limits,10_000m,riskBudgetMultiplier:.25);

        var fullIntent=Assert.Single(full.Intents);
        var explorationIntent=Assert.Single(exploration.Intents);
        Assert.True(fullIntent.Quantity>explorationIntent.Quantity);
        Assert.Equal(90m,fullIntent.Quantity);
        Assert.Equal(25m,explorationIntent.Quantity);
    }

    [Fact]
    public async Task PortfolioScoresCannotTriggerEntryWithoutADirectCandleStructureTrigger()
    {
        var btc=Assessment("BTCUSDT",.90,DecisionAction.OpenLong);
        var eth=Assessment("ETHUSDT",.65,DecisionAction.OpenLong);
        var plan=new AdaptiveStrategyPortfolioPlan(DateTimeOffset.UtcNow,
        [
            new("ETHUSDT","eth-edge",StrategyFamily.TrendBreakout,1,MarketRegime.Trending,.88,.70,.84,3,false,1,"leader"),
            new("BTCUSDT","btc-edge",StrategyFamily.TrendBreakout,1,MarketRegime.Trending,.45,.30,.38,1,false,2,"secondary")
        ]);
        var context=new AgentContext("WPE Local Brain",false,null,Array.Empty<StructuredOutcomeMemory>(),[btc,eth],0,null,plan);
        var evidence=Evidence(Market("BTCUSDT",90,.8,2),Market("ETHUSDT",90,.8,2));

        var result=await new DeterministicBrainProvider().DecideAsync(evidence,context,CancellationToken.None);

        Assert.Equal(DecisionAction.Hold,result.Decision.Action);
        Assert.Equal(0,result.Decision.TargetTier);
        Assert.Equal("market-observation",result.Decision.DecisionContextKind);
        Assert.Contains("No direct candle-structure trigger",result.Decision.Reason,StringComparison.OrdinalIgnoreCase);
    }

    private static StrategyCycleSelection Selection(
        string symbol,string id,StrategyFamily family,double quality,double confidence,double agreement,MarketRegime regime,
        int regimeObservations,double regimeExpectancy,double calibration,int executionTrades,double executionPosterior,int failureStreak=0)
    {
        var profile=new StrategyProfile
        {
            Id=id,Version=id+"-v1",Symbol=symbol,Family=family,Lifecycle=StrategyLifecycle.Active,
            QualityScore=quality,Expectancy=regimeExpectancy,FailureStreak=failureStreak
        };
        var signal=new StrategySignal(id,symbol,1,confidence,"test");
        return new(profile,signal,regime,quality*confidence,agreement,regimeObservations,regimeExpectancy,calibration,executionTrades,executionPosterior);
    }

    private static EvidencePack Evidence(params MarketEvidence[] markets)
        =>new()
        {
            Completeness=100,
            Account=new(10_000m,10_000m,10_000m,DateTime.UtcNow),
            Markets=markets.ToDictionary(x=>x.Symbol,StringComparer.OrdinalIgnoreCase)
        };

    private static MarketEvidence Market(string symbol,int quality,double liquidity,double spread)
        =>new(symbol,100m,95m,105m,55,.01,.02,.03,new(0,1,1,1,1,1.1m,0),DateTime.UtcNow)
        {
            Quality=new()
            {
                QualityScore=quality,LiquidityScore=liquidity,SpreadBps=spread,AtrPercent=.01,RelativeVolume=1.2,OrderBookImbalance=.1
            }
        };

    private static MarketDecisionAssessment Assessment(string symbol,double confidence,DecisionAction action)
        =>new()
        {
            Symbol=symbol,Regime=MarketRegime.Trending,NetScore=action==DecisionAction.OpenShort?-.7:.7,
            Confidence=confidence,ConflictRatio=.05,Fresh=true,EntryReady=true,RecommendedAction=action,
            Signals=[new("strategy","portfolio",1,.2,.2,action==DecisionAction.OpenShort?"SHORT":"LONG","test")],
            Summary=symbol+" assessment"
        };
}

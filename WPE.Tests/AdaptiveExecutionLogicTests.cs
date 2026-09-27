using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class AdaptiveExecutionLogicTests
{
    [Fact]
    public void WideSpreadCanPassOnlyWhenSmallRelativeToTradeRiskAndMarketQualityIsStrong()
    {
        var limits=Limits();
        var quality=Quality(spread:13.09,liquidity:1,quality:80);

        var result=ExecutionCostPolicy.AssessSpread(
            limits,quality,.3055m,.28658m,6.55,true);

        Assert.True(result.Allowed);
        Assert.True(result.Adaptive);
        Assert.Equal("adaptive-pass",result.Mode);
        Assert.InRange(result.EffectiveLimitBps,14.9,15.0);
    }

    [Fact]
    public void AdaptiveSpreadStillHasHardCeiling()
    {
        var result=ExecutionCostPolicy.AssessSpread(
            Limits(),Quality(spread:15.01,liquidity:1,quality:90),.3055m,.28658m,2,true);

        Assert.False(result.Allowed);
        Assert.Equal(15,result.HardLimitBps);
    }

    [Fact]
    public void AdaptiveSpreadRequiresStrongLiquidityQualityAndLowSlippage()
    {
        var limits=Limits();
        Assert.False(ExecutionCostPolicy.AssessSpread(limits,Quality(12,.79,90),.3055m,.28658m,2,true).Allowed);
        Assert.False(ExecutionCostPolicy.AssessSpread(limits,Quality(12,1,74),.3055m,.28658m,2,true).Allowed);
        Assert.False(ExecutionCostPolicy.AssessSpread(limits,Quality(12,1,90),.3055m,.28658m,8.01,true).Allowed);
    }

    [Fact]
    public void ExplicitlyTighterConfiguredSpreadRemainsHard()
    {
        var limits=Limits();
        limits.MaximumSpreadBps=5;

        var result=ExecutionCostPolicy.AssessSpread(
            limits,Quality(6,1,100),.3055m,.28658m,1,true);

        Assert.False(result.Allowed);
        Assert.Equal("configured-hard",result.Mode);
    }

    [Fact]
    public void StructuralTargetBelowMinimumRrBecomesHoldInsteadOfNoisyOpenAttempt()
    {
        var source=new DecisionPlan
        {
            Action=DecisionAction.OpenShort,
            Instrument="XRPUSDT",
            EntryPrice=1.5286m,
            StopLossPrice=1.54236430m,
            TakeProfitPrice=1.514680m,
            EvidenceReferences=["target_geometry=structural-opposite-boundary"],
            DecisionContextKind=DirectMarketStructureDecisionSkill.DecisionContextKind,
            StrategyVersion=DirectMarketStructureDecisionSkill.Version
        };
        var market=new MarketEvidence(
            "XRPUSDT",1.5286m,1.514m,1.542m,50,0,0,0,new(0,0,0,0,0,0,0),DateTime.UtcNow)
        {
            Quality=Quality(2,1,100)
        };

        var result=new DeterministicPlanSkill().Complete(source,market,new RiskLimits{MinimumRiskReward=1.8});

        Assert.Equal(DecisionAction.Hold,result.Action);
        Assert.InRange(result.RiskRewardRatio,1.0,1.1);
        Assert.Contains(result.MissingConditions,x=>x.Contains("wait for a better entry",StringComparison.Ordinal));
    }

    [Fact]
    public void MissingStructuralTargetCanStillUseExactMinimumRrFallback()
    {
        var source=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,
            Instrument="SOONUSDT",
            EntryPrice=.3039m,
            StopLossPrice=.2865789285714286m,
            TakeProfitPrice=0,
            EvidenceReferences=["target_geometry=deterministic-rr-fallback"],
            DecisionContextKind=DirectMarketStructureDecisionSkill.DecisionContextKind,
            StrategyVersion=DirectMarketStructureDecisionSkill.Version
        };
        var market=new MarketEvidence(
            "SOONUSDT",.3039m,.2865m,.3038m,50,0,0,0,new(0,0,0,0,0,0,0),DateTime.UtcNow)
        {
            Quality=Quality(2,1,100)
        };

        var result=new DeterministicPlanSkill().Complete(source,market,new RiskLimits{MinimumRiskReward=1.8});

        Assert.Equal(DecisionAction.OpenLong,result.Action);
        Assert.Equal(1.8,result.RiskRewardRatio);
        Assert.True(result.TakeProfitPrice>result.EntryPrice);
    }

    private static RiskLimits Limits()=>new()
    {
        TestnetHighOpportunityMode=true,
        MaximumSpreadBps=8,
        MaximumSlippageBps=12,
        MinimumLiquidityScore=.55
    };

    private static MarketQualityEvidence Quality(double spread,double liquidity,int quality)=>new()
    {
        SpreadBps=spread,
        LiquidityScore=liquidity,
        QualityScore=quality,
        BestBid=100,
        BestAsk=100,
        AtrPercent=.02
    };
}

using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyIdentityRiskTests
{
    private static readonly DateTimeOffset Now=new(2026,9,18,12,0,0,TimeSpan.Zero);

    [Fact]
    public void ExactDecisionAssessmentAndResearchIdentityCanPassIndependentRisk()
    {
        var review=Review(Research("strategy-alpha","candidate-v7"));

        Assert.True(review.Approved);
        Assert.Contains("strategy_identity",review.Checks);
        Assert.Contains("strategy_binding",review.Checks);
        Assert.Contains("research_identity",review.Checks);
        Assert.Contains("research_fresh",review.Checks);
    }

    [Theory]
    [InlineData("other-strategy","candidate-v7")]
    [InlineData("strategy-alpha","candidate-v8")]
    public void DifferentResearchIdentityCannotAuthorizeSameDecision(string strategyId,string strategyVersion)
    {
        var review=Review(Research(strategyId,strategyVersion));

        Assert.False(review.Approved);
        Assert.DoesNotContain("research_identity",review.Checks);
    }

    private static IndependentRiskReview Review(ResearchValidationResult research)
    {
        var decision=new DecisionPlan
        {
            Action=DecisionAction.OpenLong,Instrument="BTCUSDT",EntryPrice=100_000m,StopLossPrice=98_000m,
            TakeProfitPrice=104_000m,RiskRewardRatio=2,StrategyId="strategy-alpha",StrategyVersion="candidate-v7"
        };
        var assessment=new MarketDecisionAssessment
        {
            Symbol="BTCUSDT",StrategyId="strategy-alpha",StrategyVersion="candidate-v7",
            Fresh=true,EntryReady=true,RecommendedAction=DecisionAction.OpenLong
        };
        var market=new MarketEvidence("BTCUSDT",100_000m,98_000m,104_000m,55,.01,.01,.01,
            new(0,1m,1m,1m,1m,1m,0),Now.AddMinutes(-1).UtcDateTime)
        {
            Quality=new(){QualityScore=100,LiquidityScore=1,SpreadBps=1,AtrPercent=.01}
        };
        var evidence=new EvidencePack
        {
            CollectedAt=Now.AddMinutes(-1).UtcDateTime,Completeness=100,
            Account=new(10_000m,9_000m,10_000m,Now.AddMinutes(-1).UtcDateTime),
            Markets=new Dictionary<string,MarketEvidence>{{"BTCUSDT",market}}
        };
        var intent=new ExecutionIntent("BTCUSDT",PositionSide.Long,.001m,false,98_000m,104_000m,
            "identity-risk","test",DecisionAction.OpenLong,ExpectedPrice:100_000m);
        var portfolio=new PortfolioRiskAssessment{Approved=true,GrossExposure=100m,Summary="ok"};

        return new IndependentRiskManagerSkill(()=>Now).Review(
            decision,evidence,assessment,[intent],new RiskLimits(),new(0,0,0,false),research,portfolio);
    }

    private static ResearchValidationResult Research(string strategyId,string strategyVersion)=>new()
    {
        ValidatedAtUtc=Now.AddHours(-1),StrategyId=strategyId,StrategyVersion=strategyVersion,Symbol="BTCUSDT",
        SampleSize=1000,Trades=80,OutOfSampleTrades=30,CoverageDays=365,QualityScore=.8,Approved=true,Promoted=true,
        MaxDrawdown=.10,WalkForwardScore=.75,MonteCarloLossProbability=.20
    };
}

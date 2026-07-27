using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyRegimeValidationTests
{
    [Fact]
    public void StablePerformanceAcrossChronologicalRegimesPasses()
    {
        var returns=Enumerable.Range(0,400).Select(i=>(i%10==0?-.001:.0005,true)).ToArray();

        var result=HistoricalResearchEngine.EvaluateRobustness(returns);

        Assert.True(result.Passed);Assert.Equal(4,result.PassingRegimes);Assert.Equal(4,result.EvaluatedRegimes);Assert.InRange(result.TrainTestExpectancyGap,0,.003);
    }

    [Fact]
    public void PerformanceConcentratedInEarlyHistoryFailsOverfitGate()
    {
        var returns=Enumerable.Range(0,400).Select(i=>(i<260?.002:-.01,true)).ToArray();

        var result=HistoricalResearchEngine.EvaluateRobustness(returns);

        Assert.False(result.Passed);Assert.True(result.PassingRegimes<3||result.WorstRegimeReturn<-.12||result.TrainTestExpectancyGap>.003);Assert.True(result.TrainTestExpectancyGap>.003);
    }

    [Fact]
    public void RobustnessEvaluationIsDeterministic()
    {
        var returns=Enumerable.Range(0,517).Select(i=>(Math.Sin(i/13d)*.002,i%3!=0)).ToArray();
        Assert.Equal(HistoricalResearchEngine.EvaluateRobustness(returns),HistoricalResearchEngine.EvaluateRobustness(returns));
    }

    [Fact]
    public void LegacyPassedFlagCannotBypassRegimePromotionGate()
    {
        var profile=new 币安量化机器人.Core.Strategy.StrategyProfile();
        var legacy=new 币安量化机器人.Core.Strategy.StrategyValidation("legacy",1000,80,.6,1.5,.002,.1,1,.1,.7,.2,.8,true,"legacy passed flag");
        Assert.False(new StrategyGovernor().CanPromote(profile,legacy));
    }
}

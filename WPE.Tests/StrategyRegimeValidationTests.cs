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

    [Fact]
    public void MeanReversionIsGatedDuringStrongTrend()
    {
        var parameters=币安量化机器人.Core.Strategy.LocalStrategyParameters.For(币安量化机器人.Core.Strategy.StrategyFamily.MeanReversion,0);
        var start=DateTime.UtcNow.AddMinutes(-100);
        var candles=Enumerable.Range(0,100).Select(i=>
        {
            var open=100m+i;var close=open+1m;
            return new CandleEvidence(start.AddMinutes(i),open,close+.2m,open-.2m,close,100m,10000m,10,50m);
        }).ToArray();
        var state=MeanReversionRegimeAnalyzer.Analyze(candles,parameters);
        var registry=new DeterministicStrategyRegistry();
        var profile=new 币安量化机器人.Core.Strategy.StrategyProfile{Id="trend-gate",Version=registry.BindProfileVersion(币安量化机器人.Core.Strategy.StrategyFamily.MeanReversion,"mean-regime-test"),Symbol="BTCUSDT",Family=币安量化机器人.Core.Strategy.StrategyFamily.MeanReversion,Parameters=parameters};
        var market=new MarketEvidence("BTCUSDT",candles[^1].Close,candles[^1].Low,candles[^1].High,50,1,1,1,new(0,0,1,1,1,1,0),candles[^1].OpenTime){Candles=candles};
        var signal=new HistoricalResearchEngine(strategies:registry).Signal(profile,market,Array.Empty<NewsEvidence>());
        Assert.Equal(币安量化机器人.Core.Strategy.MeanReversionRegime.Trend,state.Regime);
        Assert.Equal(0,signal.Direction);
        Assert.Contains("regime=Trend",signal.Reason,StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorationIsDeterministicValidAndDistinct()
    {
        var family=币安量化机器人.Core.Strategy.StrategyFamily.MeanReversion;
        var first=Enumerable.Range(6,200).Select(i=>StrategyResearchAgent.Explore(family,i)).ToArray();
        var second=Enumerable.Range(6,200).Select(i=>StrategyResearchAgent.Explore(family,i)).ToArray();
        Assert.Equal(first,second);
        Assert.All(first,x=>Assert.True(币安量化机器人.Core.Strategy.LocalStrategyParameters.IsValid(family,x)));
        Assert.Equal(first.Length,first.Select(币安量化机器人.Core.Strategy.LocalStrategyParameters.Hash).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ExhaustedUnqualifiedShadowRetiresButYoungShadowContinues()
    {
        var now=DateTime.UtcNow;var governor=new StrategyGovernor();
        var old=new 币安量化机器人.Core.Strategy.StrategyProfile{Lifecycle=币安量化机器人.Core.Strategy.StrategyLifecycle.Shadow,ShadowObservations=StrategyGovernor.MaximumUnqualifiedShadowObservations,QualityScore=.4,Expectancy=-.001,MaxDrawdown=.1,StateChangedAtUtc=now-StrategyGovernor.MinimumShadowEvaluationTime-TimeSpan.FromMinutes(1)};
        var young=new 币安量化机器人.Core.Strategy.StrategyProfile{Lifecycle=币安量化机器人.Core.Strategy.StrategyLifecycle.Shadow,ShadowObservations=StrategyGovernor.MaximumUnqualifiedShadowObservations,QualityScore=.4,Expectancy=-.001,MaxDrawdown=.1,StateChangedAtUtc=now-TimeSpan.FromMinutes(5)};
        Assert.True(governor.ShouldRetireShadow(old,now));
        Assert.False(governor.ShouldRetireShadow(young,now));
    }
}

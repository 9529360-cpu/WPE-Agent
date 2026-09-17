using WpeAgent.CrossAssetResearch;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ResearchRealityModelTests
{
    [Fact]
    public void VariableCostsApplyToTurnoverNotEveryHeldBar()
    {
        var model=new ResearchRealityModel(new ResearchCostModel(.0004m,.0003m));

        var enter=model.Apply(0,1,.01);
        var hold=model.Apply(1,1,.01);
        var reverse=model.Apply(1,-1,.01);
        var exit=model.Apply(-1,0,.01);

        Assert.Equal(1,enter.Turnover);Assert.False(enter.CompletedTrade);Assert.Equal(.0093,enter.NetReturn,10);
        Assert.Equal(0,hold.Turnover);Assert.False(hold.CompletedTrade);Assert.Equal(.01,hold.NetReturn,10);
        Assert.Equal(2,reverse.Turnover);Assert.True(reverse.CompletedTrade);Assert.Equal(-.0114,reverse.NetReturn,10);
        Assert.Equal(1,exit.Turnover);Assert.True(exit.CompletedTrade);Assert.Equal(-.0007,exit.NetReturn,10);
    }

    [Fact]
    public void TrendSimulationChargesOneRoundTripForOneContinuousPosition()
    {
        var profile=new StrategyProfile
        {
            Id="trend-cost-test",
            Version="v1",
            Symbol="BTCUSDT",
            Family=StrategyFamily.TrendBreakout,
            Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0)
        };
        var candles=RisingCandles(220);
        var free=new HistoricalResearchEngine(new ResearchRealityModel(new ResearchCostModel(0,0))).Simulate(profile,candles,[]);
        var costly=new HistoricalResearchEngine().Simulate(profile,candles,[]);

        Assert.NotEmpty(costly);
        Assert.Equal(1,costly.Count(x=>x.Trade));
        Assert.Equal((double)ResearchRealityModel.DefaultCosts.RoundTripVariableRate,free.Sum(x=>x.Return)-costly.Sum(x=>x.Return),10);
    }

    [Fact]
    public void NormalizedResearchRejectsUnprovableFixedOrCarryCosts()
    {
        Assert.Throws<ArgumentException>(()=>new ResearchRealityModel(new ResearchCostModel(.0004m,.0003m,1m,0)));
        Assert.Throws<ArgumentException>(()=>new ResearchRealityModel(new ResearchCostModel(.0004m,.0003m,0,.0001m)));
    }

    private static IReadOnlyList<CandleEvidence> RisingCandles(int count)
    {
        var start=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        return Enumerable.Range(0,count).Select(i=>
        {
            var close=100m+i;
            return new CandleEvidence(start.AddHours(i),close-.5m,close,close-1m,close,1000m,100000m,100,500m);
        }).ToArray();
    }
}

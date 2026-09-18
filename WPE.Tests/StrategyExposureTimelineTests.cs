using WpeAgent.CrossAssetResearch;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyExposureTimelineTests
{
    [Fact]
    public void DirectionalDecisionUsesOnlyPriorClosedCandlesAndTradesCurrentBar()
    {
        var registry=new DeterministicStrategyRegistry();
        var profile=Profile(StrategyFamily.TrendBreakout,registry);
        var original=Candles(260);
        var changed=original.ToArray();
        var executionIndex=Math.Max(StrategyExposureTimelineV1.MinimumWarmupBars,profile.Parameters.SlowPeriod+1);
        changed[executionIndex]=changed[executionIndex] with { Close=changed[executionIndex].Close*5 };

        var first=registry.Resolve(profile.Family).BuildResearchTimeline(profile,original,[]);
        var second=registry.Resolve(profile.Family).BuildResearchTimeline(profile,changed,[]);

        Assert.NotEmpty(first);
        Assert.Equal(first[0].TargetExposure,second[0].TargetExposure);
        Assert.Equal(new DateTimeOffset(original[executionIndex-1].OpenTime),first[0].SourceCandleOpenTimeUtc);
        Assert.Equal(new DateTimeOffset(original[executionIndex].OpenTime),first[0].EvidenceAvailableAtUtc);
        Assert.Equal(first[0].EvidenceAvailableAtUtc,first[0].SignalGeneratedAtUtc);
        Assert.Equal(first[0].SignalGeneratedAtUtc,first[0].TradableAtUtc);
        Assert.Equal(original[executionIndex].Open,first[0].ExecutionOpenPrice);
        Assert.Equal(original[executionIndex].Close,first[0].ExecutionClosePrice);
        Assert.NotEqual(first[0].ExecutionClosePrice,second[0].ExecutionClosePrice);
    }

    [Fact]
    public void RealityModelUsesExecutionOpenToCloseAndCentralizesCosts()
    {
        var profile=new StrategyProfile{Id="timeline",Version="timeline-v1",Symbol="BTCUSDT"};
        var source=new CandleEvidence(Utc(0),100,101,99,100,1000,100000,10,500);
        var execution=new CandleEvidence(Utc(1),100,111,99,110,1000,100000,10,500);
        var decision=StrategyExposureTimelineV1.Create(profile,0,source,execution,1);
        Assert.NotNull(decision);

        var free=new ResearchRealityModel(new ResearchCostModel(0,0)).Simulate([decision!]);
        var costly=new ResearchRealityModel(new ResearchCostModel(.0004m,.0003m)).Simulate([decision!]);

        Assert.Single(free);
        Assert.Equal(.10,free[0].Return,10);
        Assert.True(free[0].Trade);
        Assert.Equal(.10-(double)new ResearchCostModel(.0004m,.0003m).RoundTripVariableRate,costly[0].Return,10);
    }

    [Fact]
    public void AllBuiltInStrategiesProduceCanonicalIdentityBoundTimelines()
    {
        var registry=new DeterministicStrategyRegistry();
        var candles=Candles(220);
        foreach(var family in Enum.GetValues<StrategyFamily>())
        {
            var profile=Profile(family,registry);
            var timeline=registry.Resolve(family).BuildResearchTimeline(profile,candles,[]);
            Assert.NotEmpty(timeline);
            Assert.True(StrategyExposureTimelineV1.IsCanonical(timeline));
            Assert.All(timeline,x=>
            {
                Assert.Equal(profile.Id,x.StrategyId);
                Assert.Equal(profile.Version,x.StrategyVersion);
                Assert.Equal(profile.Symbol,x.Symbol);
                Assert.True(x.SourceCandleOpenTimeUtc<x.EvidenceAvailableAtUtc);
                Assert.True(x.EvidenceAvailableAtUtc<=x.SignalGeneratedAtUtc);
                Assert.True(x.SignalGeneratedAtUtc<=x.TradableAtUtc);
            });
        }
    }

    [Fact]
    public void NonUtcOrNonMonotonicBarsFailClosed()
    {
        var registry=new DeterministicStrategyRegistry();
        var profile=Profile(StrategyFamily.TrendBreakout,registry);
        var candles=Candles(260).ToArray();
        var executionIndex=Math.Max(StrategyExposureTimelineV1.MinimumWarmupBars,profile.Parameters.SlowPeriod+1);
        candles[executionIndex]=candles[executionIndex] with
        {
            OpenTime=DateTime.SpecifyKind(candles[executionIndex].OpenTime,DateTimeKind.Unspecified)
        };

        var timeline=registry.Resolve(profile.Family).BuildResearchTimeline(profile,candles,[]);

        Assert.Empty(timeline);
        Assert.Empty(new ResearchRealityModel().Simulate(timeline));
    }

    private static StrategyProfile Profile(StrategyFamily family,DeterministicStrategyRegistry registry)=>new()
    {
        Id="BTCUSDT-"+family+"-timeline",
        Version=registry.BindProfileVersion(family,"timeline-v1"),
        Symbol="BTCUSDT",
        Family=family,
        Parameters=LocalStrategyParameters.For(family,0)
    };

    private static IReadOnlyList<CandleEvidence> Candles(int count)
        =>Enumerable.Range(0,count).Select(i=>
        {
            var open=100m+i*.2m+(decimal)Math.Sin(i/7d);
            var close=open+(i%9<5?.35m:-.25m);
            return new CandleEvidence(Utc(i),open,Math.Max(open,close)+.4m,Math.Min(open,close)-.4m,close,1000m+i,100000m+i,100,500m);
        }).ToArray();

    private static DateTime Utc(int hour)=>new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc).AddHours(hour);
}

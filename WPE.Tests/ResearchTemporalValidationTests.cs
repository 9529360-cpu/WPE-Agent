using WpeAgent.CrossAssetResearch;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ResearchTemporalValidationTests
{
    [Fact]
    public void SharedBoundsApplyTrainPurgeOosAndEmbargoDeterministically()
    {
        var policy=new ResearchTemporalIsolationPolicy(.65,30,5,5);

        var bounds=ResearchTemporalValidationV1.Bounds(200,policy);

        Assert.Equal(130,bounds.TrainingEndExclusive);
        Assert.Equal(135,bounds.OosStart);
        Assert.Equal(195,bounds.OosEndExclusive);
        Assert.Equal(60,bounds.OosCount);
    }

    [Fact]
    public void LookAheadFailsClosedWhenSignalUsesUnavailableOrFutureEvidence()
    {
        var now=new DateTimeOffset(2026,1,1,2,0,0,TimeSpan.Zero);
        var policy=new ResearchTemporalIsolationPolicy(.65,1,0,0);
        var futureSignal=new ResearchTemporalPoint(now,now.AddMinutes(-1),now.AddMinutes(1));
        var unavailableEvidence=new ResearchTemporalPoint(now,now,now.AddMinutes(-1));

        var futureReasons=ResearchTemporalValidationV1.Validate([futureSignal],10,policy);
        var unavailableReasons=ResearchTemporalValidationV1.Validate([unavailableEvidence],10,policy);

        Assert.Contains("research.look-ahead-leakage",futureReasons);
        Assert.Contains("research.look-ahead-leakage",unavailableReasons);
    }

    [Fact]
    public void CanonicalTimelinePassesTheSameSharedTemporalPrimitive()
    {
        var registry=new DeterministicStrategyRegistry();
        var profile=new StrategyProfile
        {
            Id="temporal-trend",
            Version=registry.BindProfileVersion(StrategyFamily.TrendBreakout,"temporal-v1"),
            Symbol="BTCUSDT",
            Family=StrategyFamily.TrendBreakout,
            Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0)
        };
        var candles=Candles(700);
        var timeline=registry.Resolve(profile.Family).BuildResearchTimeline(profile,candles,[]);
        var points=timeline.Select(x=>new ResearchTemporalPoint(x.TradableAtUtc,x.EvidenceAvailableAtUtc,x.SignalGeneratedAtUtc)).ToArray();

        var reasons=ResearchTemporalValidationV1.Validate(points,timeline.Count,HistoricalResearchEngine.PromotionTemporalPolicy);

        Assert.Empty(reasons);
    }

    [Fact]
    public void ProductionValidationPersistsPurgeAndEmbargoSemantics()
    {
        var registry=new DeterministicStrategyRegistry();
        var profile=new StrategyProfile
        {
            Id="temporal-validation",
            Version=registry.BindProfileVersion(StrategyFamily.TrendBreakout,"temporal-v1"),
            Symbol="BTCUSDT",
            Family=StrategyFamily.TrendBreakout,
            Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0)
        };

        var validation=new HistoricalResearchEngine(strategies:registry).Validate(profile,Candles(700),[],new RiskLimits{MinimumBacktestTrades=1});

        Assert.Equal(HistoricalResearchEngine.PromotionTemporalPolicy.OosPurgeObservations,validation.OosPurgeObservations);
        Assert.Equal(HistoricalResearchEngine.PromotionTemporalPolicy.OosEmbargoObservations,validation.OosEmbargoObservations);
        Assert.Contains("purge=5",validation.Summary,StringComparison.Ordinal);
        Assert.Contains("embargo=5",validation.Summary,StringComparison.Ordinal);
    }

    [Fact]
    public void CrossAssetResearchUsesTheSharedTemporalPrimitive()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","Agent","CrossAssetResearchService.cs"));

        Assert.Contains("ResearchTemporalValidationV1.Validate",source,StringComparison.Ordinal);
        Assert.Contains("ResearchTemporalValidationV1.Bounds",source,StringComparison.Ordinal);
        Assert.DoesNotContain("private static (int Start, int EndExclusive, int Count) OosBounds",source,StringComparison.Ordinal);
    }

    private static IReadOnlyList<CandleEvidence> Candles(int count)
        =>Enumerable.Range(0,count).Select(i=>
        {
            var open=100m+i*.08m+(decimal)Math.Sin(i/11d);
            var close=open+(i%7<4?.3m:-.2m);
            return new CandleEvidence(
                new DateTime(2025,1,1,0,0,0,DateTimeKind.Utc).AddHours(i),
                open,Math.Max(open,close)+.5m,Math.Min(open,close)-.5m,close,1000m+i,100000m+i,100,500m);
        }).ToArray();
}

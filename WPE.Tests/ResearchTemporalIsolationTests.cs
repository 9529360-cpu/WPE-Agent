using WpeAgent.CrossAssetResearch;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ResearchTemporalIsolationTests
{
    [Fact]
    public void SharedOosWindowAppliesTrainingPurgeAndEmbargoExactlyOnce()
    {
        var policy=new CrossAssetValidationPolicy(
            TrainingFraction:.65,
            MinimumOutOfSampleObservations:20,
            OosPurgeObservations:5,
            OosEmbargoObservations:7);

        var window=ResearchTemporalIsolationV1.OosWindow(100,policy);

        Assert.Equal(65,window.TrainingEndExclusive);
        Assert.Equal(70,window.Start);
        Assert.Equal(93,window.EndExclusive);
        Assert.Equal(23,window.Count);
    }

    [Fact]
    public void SharedTemporalValidationRejectsFutureEvidenceAndInsufficientIsolatedOos()
    {
        var at=new DateTimeOffset(2026,9,18,12,0,0,TimeSpan.Zero);
        var observations=new[]
        {
            new ResearchTemporalObservationV1(at,at.AddSeconds(1),at)
        };
        var policy=new CrossAssetValidationPolicy(
            TrainingFraction:.65,
            MinimumOutOfSampleObservations:30,
            OosPurgeObservations:5,
            OosEmbargoObservations:5);

        var reasons=ResearchTemporalIsolationV1.Validate(observations,20,policy);

        Assert.Contains(ResearchTemporalIsolationV1.LookAheadReason,reasons);
        Assert.Contains(ResearchTemporalIsolationV1.TemporalIsolationReason,reasons);
    }

    [Fact]
    public void CrossAssetTimelineAndProductionResearchUseOneTemporalAuthority()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var cross=File.ReadAllText(Path.Combine(root,"Services","Agent","CrossAssetResearchService.cs"));
        var timeline=File.ReadAllText(Path.Combine(root,"Services","Agent","StrategyExposureTimelineV1.cs"));
        var strategy=File.ReadAllText(Path.Combine(root,"Services","Agent","StrategyResearchAgent.cs"));

        Assert.Contains("ResearchTemporalIsolationV1.OosWindow",cross,StringComparison.Ordinal);
        Assert.Contains("ResearchTemporalIsolationV1.Validate",cross,StringComparison.Ordinal);
        Assert.Contains("ResearchTemporalIsolationV1.HasLookAhead",timeline,StringComparison.Ordinal);
        Assert.Contains("ResearchTemporalIsolationV1.OosWindow",strategy,StringComparison.Ordinal);
        Assert.Contains("ResearchTemporalIsolationV1.Validate",strategy,StringComparison.Ordinal);
        Assert.DoesNotContain("private static (int Start, int EndExclusive, int Count) OosBounds",cross,StringComparison.Ordinal);
        Assert.DoesNotContain("ResearchTemporalValidationV1",cross,StringComparison.Ordinal);
        Assert.DoesNotContain("ResearchTemporalValidationV1",strategy,StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionValidationPersistsTimelinePurgeAndEmbargoSemantics()
    {
        var registry=new DeterministicStrategyRegistry();
        var profile=new StrategyProfile
        {
            Id="temporal-validation",
            Version=registry.BindProfileVersion(StrategyFamily.TrendBreakout,"temporal-v2"),
            Symbol="BTCUSDT",
            Family=StrategyFamily.TrendBreakout,
            Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0)
        };

        var engine=new HistoricalResearchEngine(strategies:registry);
        var validation=engine.Validate(profile,Candles(800),[],new RiskLimits{MinimumBacktestTrades=1});
        var artifact=engine.TimelineArtifact(profile,Candles(800),[]);

        Assert.NotNull(artifact);
        Assert.Equal(artifact!.CanonicalSha256,validation.TimelineSha256);
        Assert.Equal(HistoricalResearchEngine.ProductionTemporalPolicy.OosPurgeObservations,validation.OosPurgeObservations);
        Assert.Equal(HistoricalResearchEngine.ProductionTemporalPolicy.OosEmbargoObservations,validation.OosEmbargoObservations);
        Assert.Contains("oos_purge=5",validation.Summary,StringComparison.Ordinal);
        Assert.Contains("oos_embargo=5",validation.Summary,StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionTemporalPolicyIsExplicitAndStable()
    {
        Assert.Equal(.65,HistoricalResearchEngine.ProductionTemporalPolicy.TrainingFraction,10);
        Assert.Equal(30,HistoricalResearchEngine.ProductionTemporalPolicy.MinimumOutOfSampleObservations);
        Assert.Equal(5,HistoricalResearchEngine.ProductionTemporalPolicy.OosPurgeObservations);
        Assert.Equal(5,HistoricalResearchEngine.ProductionTemporalPolicy.OosEmbargoObservations);
    }

    private static IReadOnlyList<CandleEvidence> Candles(int count)
        =>Enumerable.Range(0,count).Select(i=>
        {
            var open=100m+i*.08m+(decimal)Math.Sin(i/11d);
            var close=open+(i%7<4?.3m:-.2m);
            return new CandleEvidence(
                new DateTime(2025,1,1,0,0,0,DateTimeKind.Utc).AddHours(i),
                open,
                Math.Max(open,close)+.5m,
                Math.Min(open,close)-.5m,
                close,
                1000m+i,
                100000m+i,
                100,
                500m);
        }).ToArray();
}

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
        var policy=new CrossAssetValidationPolicy(MinimumOutOfSampleObservations:30,OosPurgeObservations:5,OosEmbargoObservations:5);

        var reasons=ResearchTemporalIsolationV1.Validate(observations,20,policy);

        Assert.Contains(ResearchTemporalIsolationV1.LookAheadReason,reasons);
        Assert.Contains(ResearchTemporalIsolationV1.TemporalIsolationReason,reasons);
    }

    [Fact]
    public void CrossAssetAndProductionStrategyResearchUseTheSameTemporalAuthority()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var cross=File.ReadAllText(Path.Combine(root,"Services","Agent","CrossAssetResearchService.cs"));
        var strategy=File.ReadAllText(Path.Combine(root,"Services","Agent","StrategyResearchAgent.cs"));

        Assert.Contains("ResearchTemporalIsolationV1.OosWindow",cross,StringComparison.Ordinal);
        Assert.Contains("ResearchTemporalIsolationV1.Validate",cross,StringComparison.Ordinal);
        Assert.Contains("ResearchTemporalIsolationV1.OosWindow",strategy,StringComparison.Ordinal);
        Assert.Contains("ResearchTemporalIsolationV1.Validate",strategy,StringComparison.Ordinal);
        Assert.DoesNotContain("private static (int Start, int EndExclusive, int Count) OosBounds",cross,StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionTemporalPolicyKeepsExplicitPurgeAndEmbargo()
    {
        Assert.Equal(5,HistoricalResearchEngine.ProductionTemporalPolicy.OosPurgeObservations);
        Assert.Equal(5,HistoricalResearchEngine.ProductionTemporalPolicy.OosEmbargoObservations);
        Assert.True(HistoricalResearchEngine.ProductionTemporalPolicy.MinimumOutOfSampleObservations>=30);
    }
}

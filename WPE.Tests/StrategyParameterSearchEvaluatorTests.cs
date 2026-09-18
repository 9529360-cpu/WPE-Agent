using WpeAgent.CrossAssetResearch;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyParameterSearchEvaluatorTests
{
    [Fact]
    public void ProductionTrialBudgetMatchesCrossAssetPolicy()
    {
        Assert.Equal(new CrossAssetValidationPolicy().MaximumParameterTrials,StrategyParameterSearchEvaluatorV1.MaximumTrials);
    }

    [Fact]
    public void RetiredTrialsCountAndDuplicateParametersDoNotCreateFakeNewTrials()
    {
        var selected=Profile("selected",0,DateTime.UtcNow.AddMinutes(-3),StrategyLifecycle.Draft);
        var retired=Profile("retired",1,DateTime.UtcNow.AddMinutes(-2),StrategyLifecycle.Retired);
        var duplicate=Profile("duplicate",0,DateTime.UtcNow.AddMinutes(-1),StrategyLifecycle.Retired);
        var timeline=Timeline(selected,400);
        var returns=PositiveReturns(400);
        var window=ResearchTemporalIsolationV1.OosWindow(returns.Count,HistoricalResearchEngine.ProductionTemporalPolicy);

        var evidence=StrategyParameterSearchEvaluatorV1.Evaluate(selected,[selected,retired,duplicate],timeline,returns,window);

        Assert.Equal(2,evidence.TrialCount);
        Assert.Equal(0,evidence.SelectedTrialIndex);
        Assert.True(StrategyParameterSearchEvaluatorV1.IsQualified(evidence));
        Assert.Equal(StrategyParameterSearchEvaluatorV1.NominalAlpha/2,evidence.CorrectedSignificanceThreshold,12);
    }

    [Fact]
    public void HistoricalOosReturnsCannotImproveTrainingSignificance()
    {
        var selected=Profile("selected",0,DateTime.UtcNow,StrategyLifecycle.Draft);
        var timeline=Timeline(selected,400);
        var window=ResearchTemporalIsolationV1.OosWindow(400,HistoricalResearchEngine.ProductionTemporalPolicy);
        var goodOos=PositiveReturns(400).ToArray();
        var badOos=PositiveReturns(400).ToArray();
        for(var i=window.Start;i<window.EndExclusive;i++)badOos[i]=(-.50,false);

        var first=StrategyParameterSearchEvaluatorV1.Evaluate(selected,[selected],timeline,goodOos,window);
        var second=StrategyParameterSearchEvaluatorV1.Evaluate(selected,[selected],timeline,badOos,window);

        Assert.Equal(first.SelectedTrialPValue,second.SelectedTrialPValue,12);
        Assert.Equal(first.SelectionDatasetHash,second.SelectionDatasetHash);
        Assert.Equal(first.HistoricalOosDatasetHash,second.HistoricalOosDatasetHash);
        Assert.NotEqual(first.SelectionDatasetHash,first.HistoricalOosDatasetHash);
    }

    [Fact]
    public void MoreThanTwentyPersistedUniqueTrialsFailsClosed()
    {
        var now=DateTime.UtcNow;
        var trials=Enumerable.Range(0,21).Select(i=>
        {
            var p=StrategyResearchAgent.Explore(StrategyFamily.TrendBreakout,i);
            return new StrategyProfile
            {
                Id="trial-"+i,Version="trial-"+i,Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,
                Parameters=p,ParametersHash=LocalStrategyParameters.Hash(p),Lifecycle=StrategyLifecycle.Retired,
                CreatedAtUtc=now.AddMinutes(i)
            };
        }).ToArray();
        var selected=trials[0];
        var timeline=Timeline(selected,400);
        var returns=PositiveReturns(400);
        var window=ResearchTemporalIsolationV1.OosWindow(400,HistoricalResearchEngine.ProductionTemporalPolicy);

        var evidence=StrategyParameterSearchEvaluatorV1.Evaluate(selected,trials,timeline,returns,window);

        Assert.Equal(21,evidence.TrialCount);
        Assert.False(StrategyParameterSearchEvaluatorV1.IsQualified(evidence));
    }

    [Fact]
    public void PassedFlagAndRegimeMetricsCannotBypassMissingTrialEvidence()
    {
        var validation=new StrategyValidation(
            "legacy",1000,80,.6,1.5,.002,.1,1,.1,.7,.2,.8,true,"legacy without search evidence",
            -.02,.0005,4,4);
        Assert.False(new StrategyGovernor().CanPromote(new StrategyProfile(),validation));
    }

    [Fact]
    public void ParameterVariantsShareTheSameResearchStartBoundary()
    {
        var registry=new DeterministicStrategyRegistry();
        var candles=Candles(500);
        var first=Bound(Profile("first",0,DateTime.UtcNow,StrategyLifecycle.Draft),registry);
        var second=Bound(Profile("second",1,DateTime.UtcNow,StrategyLifecycle.Draft),registry);

        var a=registry.Resolve(StrategyFamily.TrendBreakout).BuildResearchTimeline(first,candles,[]);
        var b=registry.Resolve(StrategyFamily.TrendBreakout).BuildResearchTimeline(second,candles,[]);

        Assert.NotEmpty(a);
        Assert.NotEmpty(b);
        Assert.Equal(a[0].TradableAtUtc,b[0].TradableAtUtc);
        Assert.Equal(new DateTimeOffset(candles[StrategyExposureTimelineV1.MinimumWarmupBars].OpenTime),a[0].TradableAtUtc);
        Assert.Equal(0,a[0].Sequence);
    }

    private static StrategyProfile Profile(string id,int variant,DateTime created,StrategyLifecycle lifecycle)
    {
        var p=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,variant);
        return new StrategyProfile
        {
            Id=id,Version="candidate-"+variant,Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,
            Parameters=p,ParametersHash=LocalStrategyParameters.Hash(p),Lifecycle=lifecycle,CreatedAtUtc=created
        };
    }

    private static StrategyProfile Bound(StrategyProfile profile,DeterministicStrategyRegistry registry)=>new()
    {
        Id=profile.Id,Version=registry.BindProfileVersion(profile.Family,profile.Version),Symbol=profile.Symbol,Family=profile.Family,
        Parameters=profile.Parameters,ParametersHash=profile.ParametersHash,Lifecycle=profile.Lifecycle,CreatedAtUtc=profile.CreatedAtUtc
    };

    private static IReadOnlyList<StrategyExposureDecisionV1> Timeline(StrategyProfile profile,int count)
    {
        var start=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero);
        return Enumerable.Range(0,count).Select(i=>new StrategyExposureDecisionV1(
            i,profile.Id,profile.Version,profile.Symbol,start.AddHours(i),start.AddHours(i+1),start.AddHours(i+1),start.AddHours(i+1),
            1,100+i,101+i,1000+i)).ToArray();
    }

    private static IReadOnlyList<(double Return,bool Trade)> PositiveReturns(int count)
        =>Enumerable.Range(0,count).Select(i=>(.002,i%24==23)).ToArray();

    private static IReadOnlyList<CandleEvidence> Candles(int count)
    {
        var start=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        return Enumerable.Range(0,count).Select(i=>
        {
            var price=100m+i*.1m;
            return new CandleEvidence(start.AddHours(i),price,price+.5m,price-.5m,price+.2m,1000,100000,100,500);
        }).ToArray();
    }
}

using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyAdaptiveIntelligenceTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-strategy-intelligence-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    public StrategyAdaptiveIntelligenceTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public async Task HoldCyclesDoNotQualifyShadowEvidence()
    {
        var store=new AgentSqliteStore(DatabasePath);
        for(var index=0;index<40;index++)
            await store.RecordStrategyObservationAsync("hold-only","BTCUSDT",0,100m+index,0,MarketRegime.Ranging,CancellationToken.None);

        var performance=await store.GetStrategyObservationPerformanceAsync("hold-only",CancellationToken.None);

        Assert.Equal(0,performance.Observations);
        Assert.Equal(40,performance.RawObservations);
        Assert.Equal(0,performance.Expectancy);
        Assert.Contains("actionable",performance.Summary,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SilentShadowCanRetireWithoutCountingHoldsAsQualification()
    {
        var now=DateTime.UtcNow;
        var profile=new StrategyProfile
        {
            Lifecycle=StrategyLifecycle.Shadow,
            ShadowObservations=0,
            QualityScore=.8,
            Expectancy=.01,
            MaxDrawdown=.05,
            StateChangedAtUtc=now-StrategyGovernor.MinimumShadowEvaluationTime-TimeSpan.FromMinutes(1)
        };

        Assert.True(new StrategyGovernor().ShouldRetireShadow(
            profile,
            now,
            StrategyGovernor.MaximumUnqualifiedShadowObservations));
        Assert.False(new StrategyGovernor().CanActivateFromShadow(profile));
    }

    [Fact]
    public async Task CalibrationSeparatesReliableAndUnreliableRegimes()
    {
        var store=new AgentSqliteStore(DatabasePath);
        for(var index=0;index<StrategyGovernor.MinimumRegimeCalibrationObservations;index++)
        {
            await store.RecordStrategyObservationAsync("regime-aware","BTCUSDT",1,100m,.9,MarketRegime.Trending,CancellationToken.None);
            await store.RecordStrategyObservationAsync("regime-aware","BTCUSDT",0,101m,0,MarketRegime.Trending,CancellationToken.None);
        }
        for(var index=0;index<StrategyGovernor.MinimumRegimeCalibrationObservations;index++)
        {
            await store.RecordStrategyObservationAsync("regime-aware","BTCUSDT",1,100m,.9,MarketRegime.Ranging,CancellationToken.None);
            await store.RecordStrategyObservationAsync("regime-aware","BTCUSDT",0,99m,0,MarketRegime.Ranging,CancellationToken.None);
        }

        var performance=await store.GetStrategyObservationPerformanceAsync("regime-aware",CancellationToken.None);
        var trending=Assert.Single(performance.Regimes!,value=>value.Regime==MarketRegime.Trending.ToString());
        var ranging=Assert.Single(performance.Regimes!,value=>value.Regime==MarketRegime.Ranging.ToString());

        Assert.Equal(StrategyGovernor.MinimumRegimeCalibrationObservations,trending.Observations);
        Assert.Equal(StrategyGovernor.MinimumRegimeCalibrationObservations,ranging.Observations);
        Assert.True(trending.Expectancy>0);
        Assert.True(ranging.Expectancy<0);
        Assert.True(trending.CalibrationScore>ranging.CalibrationScore);
        Assert.True(trending.HitRate>ranging.HitRate);
    }

    [Fact]
    public void SelectorUsesQualityTimesCalibratedConfidenceAndLocksDirection()
    {
        var first=new StrategyProfile{Id="first",Symbol="BTCUSDT",Lifecycle=StrategyLifecycle.Active,QualityScore=.80,Expectancy=.01};
        var second=new StrategyProfile{Id="second",Symbol="BTCUSDT",Lifecycle=StrategyLifecycle.Active,QualityScore=.90,Expectancy=.02};
        var firstSelection=new StrategyCycleSelection(first,new("first","BTCUSDT",1,.75,"calibrated"),MarketRegime.Trending,.80*.75);
        var secondSelection=new StrategyCycleSelection(second,new("second","BTCUSDT",1,.50,"calibrated"),MarketRegime.Trending,.90*.50);

        var selected=AdaptiveStrategySelector.SelectBest([secondSelection,firstSelection]);

        Assert.NotNull(selected);
        Assert.Equal("first",selected!.Profile.Id);
        Assert.True(AdaptiveStrategySelector.DirectionMatches(selected,DecisionAction.OpenLong));
        Assert.False(AdaptiveStrategySelector.DirectionMatches(selected,DecisionAction.OpenShort));
    }

    [Fact]
    public void StrategyDisagreementCanOnlyReduceOrWithholdLocalBacking()
    {
        var longProfile=new StrategyProfile{Id="long",Symbol="BTCUSDT",Lifecycle=StrategyLifecycle.Active,QualityScore=.8,Expectancy=.01};
        var shortProfile=new StrategyProfile{Id="short",Symbol="BTCUSDT",Lifecycle=StrategyLifecycle.Active,QualityScore=.8,Expectancy=.01};
        var longSignal=new StrategyCycleSelection(longProfile,new("long","BTCUSDT",1,.75,"long"),MarketRegime.Trending,.60);
        var shortSignal=new StrategyCycleSelection(shortProfile,new("short","BTCUSDT",-1,.50,"short"),MarketRegime.Trending,.40);

        var consensus=AdaptiveStrategySelector.SelectConsensus([longSignal,shortSignal]);

        Assert.NotNull(consensus);
        Assert.Equal("long",consensus!.Profile.Id);
        Assert.Equal(.60,consensus.StrategyAgreement,6);
        Assert.True(consensus.Signal.Confidence<.75);
        Assert.Contains("strategy_consensus=0.60",consensus.Signal.Reason,StringComparison.Ordinal);
    }

    [Fact]
    public void BalancedOpposingStrategiesWithholdRiskIncreasingBacking()
    {
        var longProfile=new StrategyProfile{Id="long",Symbol="BTCUSDT",Lifecycle=StrategyLifecycle.Active,QualityScore=.8};
        var shortProfile=new StrategyProfile{Id="short",Symbol="BTCUSDT",Lifecycle=StrategyLifecycle.Active,QualityScore=.8};
        var longSignal=new StrategyCycleSelection(longProfile,new("long","BTCUSDT",1,.75,"long"),MarketRegime.Transition,.50);
        var shortSignal=new StrategyCycleSelection(shortProfile,new("short","BTCUSDT",-1,.75,"short"),MarketRegime.Transition,.50);

        Assert.Null(AdaptiveStrategySelector.SelectConsensus([longSignal,shortSignal]));
    }

    [Fact]
    public async Task NewlyDemotedStrategyCannotRemainSelectableForCurrentCycle()
    {
        var store=new AgentSqliteStore(DatabasePath);
        var parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0);
        var profile=new StrategyProfile
        {
            Id="demote-now",
            Version="trend-v1",
            Symbol="BTCUSDT",
            Family=StrategyFamily.TrendBreakout,
            Parameters=parameters,
            ParametersHash=LocalStrategyParameters.Hash(parameters),
            LineageHash=StrategyLineage.Hash("BTCUSDT",StrategyFamily.TrendBreakout,null,null,0,LocalStrategyParameters.Hash(parameters)),
            Lifecycle=StrategyLifecycle.Active,
            QualityScore=.8,
            Expectancy=.01,
            ShadowObservations=StrategyGovernor.MinimumShadowObservations
        };
        await store.UpsertStrategyAsync(profile,CancellationToken.None);

        for(var index=0;index<StrategyGovernor.MinimumShadowObservations;index++)
        {
            var profitable=index<StrategyGovernor.MinimumShadowObservations-3;
            await store.RecordStrategyObservationAsync("demote-now","BTCUSDT",1,100m,.8,MarketRegime.Trending,CancellationToken.None);
            await store.RecordStrategyObservationAsync("demote-now","BTCUSDT",0,profitable?101m:99m,0,MarketRegime.Trending,CancellationToken.None);
        }

        var start=DateTime.UtcNow.AddMinutes(-80);
        var candles=Enumerable.Range(0,80).Select(index=>
        {
            var close=100m+index*.1m;
            return new CandleEvidence(start.AddMinutes(index),close-.1m,close+.1m,close-.2m,close,100m,10000m,10,50m);
        }).ToArray();
        var market=new MarketEvidence("BTCUSDT",candles[^1].Close,candles[^1].Close-5,candles[^1].Close+5,50,.001,.005,.01,new(0,1,1,1,1,1,0),DateTime.UtcNow)
        {
            Candles=candles,
            Quality=new(){QualityScore=90,AtrPercent=.01,LiquidationIntensity=0}
        };
        var evidence=new EvidencePack
        {
            Completeness=100,
            Markets=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",market}}
        };
        var research=new StrategyResearchAgent(store);
        research.SetSchedulerHealth(true);

        await research.ObserveAsync(evidence,CancellationToken.None);
        var reloaded=await store.GetStrategiesAsync(CancellationToken.None);
        var demoted=Assert.Single(reloaded,value=>value.Id=="demote-now");

        Assert.Equal(StrategyLifecycle.Degraded,demoted.Lifecycle);
        Assert.True(demoted.FailureStreak>=3);
        var selected=await AdaptiveStrategySelector.SelectAsync(reloaded,evidence,research,CancellationToken.None);
        Assert.Empty(selected);
    }

    [Fact]
    public async Task BadRegimeCalibrationCanOnlyReduceLiveSignalConfidence()
    {
        var store=new AgentSqliteStore(DatabasePath);
        for(var index=0;index<StrategyGovernor.MinimumRegimeCalibrationObservations;index++)
        {
            await store.RecordStrategyObservationAsync("adaptive","BTCUSDT",1,100m,.9,MarketRegime.Ranging,CancellationToken.None);
            await store.RecordStrategyObservationAsync("adaptive","BTCUSDT",0,99m,0,MarketRegime.Ranging,CancellationToken.None);
        }

        var profile=new StrategyProfile
        {
            Id="adaptive",
            Version="trend-v1",
            Symbol="BTCUSDT",
            Family=StrategyFamily.TrendBreakout,
            Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0),
            Lifecycle=StrategyLifecycle.Active,
            QualityScore=.8,
            Expectancy=.01
        };
        var start=DateTime.UtcNow.AddMinutes(-80);
        var candles=Enumerable.Range(0,80).Select(index=>
        {
            var close=100m+index*.25m;
            return new CandleEvidence(start.AddMinutes(index),close-.1m,close,close-.2m,close,100m,10000m,10,50m);
        }).ToArray();
        var market=new MarketEvidence(
            "BTCUSDT",
            candles[^1].Close,
            candles[^1].Close-5,
            candles[^1].Close+5,
            50,
            0,
            0,
            0,
            new(0,1,1,1,1,1,0),
            DateTime.UtcNow)
        {
            Candles=candles,
            Quality=new(){QualityScore=90,AtrPercent=.01,LiquidationIntensity=0}
        };
        var agent=new StrategyResearchAgent(store);
        agent.SetSchedulerHealth(true);

        var raw=agent.GetSignal(profile,market,Array.Empty<NewsEvidence>());
        var adaptive=await agent.GetAdaptiveSignalAsync(profile,market,Array.Empty<NewsEvidence>(),CancellationToken.None);

        Assert.Equal(1,raw.Direction);
        Assert.Equal(MarketRegime.Ranging,MarketRegimeClassifier.Detect(market));
        Assert.True(raw.Confidence>0);
        Assert.True(adaptive.Confidence<raw.Confidence);
        Assert.Contains("calibration=",adaptive.Reason,StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

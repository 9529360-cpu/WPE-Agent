using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyShadowQualificationTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,9,19,2,0,0,TimeSpan.Zero);
    private static readonly DateTimeOffset ValidationAt=Now.AddMinutes(-40);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-shadow-qualification-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");

    [Fact]
    public void ReadyDecisionBindsExactCanonicalEvidenceSetAndRoundTrips()
    {
        var profile=Profile();
        var evidence=Evidence(profile,StrategyGovernor.MinimumShadowObservations);

        var decision=StrategyShadowQualificationV1.Evaluate(
            profile.Id,profile.Version,profile.Symbol,evidence,Now);

        Assert.Equal(StrategyShadowQualificationStateV1.Ready,decision.State);
        Assert.True(decision.Qualified);
        Assert.Equal(StrategyGovernor.MinimumShadowObservations,decision.ObservationCount);
        Assert.Equal(evidence.Count,decision.EvidenceCanonicalSha256.Count);
        Assert.Empty(decision.ReasonCodes);
        Assert.True(StrategyShadowQualificationV1.IsCanonical(decision));
        Assert.True(StrategyShadowQualificationV1.TryDeserializeCanonical(
            decision.CanonicalBytes,decision.CanonicalSha256,out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(decision.EvidenceSetSha256,parsed!.EvidenceSetSha256);
        Assert.Equal(decision.CanonicalSha256,parsed.CanonicalSha256);
    }

    [Fact]
    public void DuplicateMixedOrStaleEvidenceNeverQualifies()
    {
        var profile=Profile();
        var evidence=Evidence(profile,StrategyGovernor.MinimumShadowObservations).ToList();

        var duplicate=StrategyShadowQualificationV1.Evaluate(
            profile.Id,profile.Version,profile.Symbol,
            [..evidence,evidence[0]],
            Now);
        Assert.Equal(StrategyShadowQualificationStateV1.Invalid,duplicate.State);
        Assert.False(duplicate.Qualified);
        Assert.Contains("evidence.duplicate",duplicate.ReasonCodes);
        Assert.True(StrategyShadowQualificationV1.IsCanonical(duplicate));

        var mixed=evidence.ToArray();
        mixed[^1]=Observation(profile,mixed.Length-1,timelineVariant:1);
        var mixedDecision=StrategyShadowQualificationV1.Evaluate(
            profile.Id,profile.Version,profile.Symbol,mixed,Now);
        Assert.Equal(StrategyShadowQualificationStateV1.Invalid,mixedDecision.State);
        Assert.Contains("evidence.mixed-timeline",mixedDecision.ReasonCodes);

        var stale=StrategyShadowQualificationV1.Evaluate(
            profile.Id,profile.Version,profile.Symbol,evidence,
            evidence[^1].MarketCollectedAtUtc+StrategyShadowQualificationV1.DefaultPolicy.MaximumEvidenceAge+TimeSpan.FromSeconds(1));
        Assert.Equal(StrategyShadowQualificationStateV1.Insufficient,stale.State);
        Assert.Contains("freshness.stale",stale.ReasonCodes);
    }

    [Fact]
    public void InsufficientDecisionIsStillCanonicalButCannotGrantAuthority()
    {
        var profile=Profile();
        var decision=StrategyShadowQualificationV1.Evaluate(
            profile.Id,profile.Version,profile.Symbol,[],Now);

        Assert.Equal(StrategyShadowQualificationStateV1.Insufficient,decision.State);
        Assert.False(decision.Qualified);
        Assert.Contains("sample.observations",decision.ReasonCodes);
        Assert.True(StrategyShadowQualificationV1.IsCanonical(decision));
    }

    [Fact]
    public async Task QualificationReceiptSurvivesRestartAndIsAppendOnly()
    {
        var profile=Profile();
        var store=await SeedAuthority(profile);
        foreach(var value in Evidence(profile,StrategyGovernor.MinimumShadowObservations))
            Assert.True(await store.SaveStrategyShadowObservationAsync(value,default));

        var persistedEvidence=await store.GetStrategyShadowObservationsAsync(profile.Id,profile.Version,default);
        var decision=StrategyShadowQualificationV1.Evaluate(
            profile.Id,profile.Version,profile.Symbol,persistedEvidence,Now);

        Assert.True(await store.SaveStrategyShadowQualificationAsync(decision,default));
        var restarted=new AgentSqliteStore(Database,()=>Now);
        var restored=await restarted.GetLatestStrategyShadowQualificationAsync(profile.Id,profile.Version,default);
        Assert.NotNull(restored);
        Assert.True(restored!.Qualified);
        Assert.Equal(decision.CanonicalSha256,restored.CanonicalSha256);
        Assert.False(await restarted.SaveStrategyShadowQualificationAsync(decision,default));

        var conflicting=StrategyShadowQualificationV1.Evaluate(
            profile.Id,profile.Version,profile.Symbol,persistedEvidence,Now.AddSeconds(1));
        Assert.True(conflicting.Qualified);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            restarted.SaveStrategyShadowQualificationAsync(conflicting,default));

        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        foreach(var sql in new[]
        {
            "UPDATE strategy_shadow_qualification_decisions SET qualified=0",
            "DELETE FROM strategy_shadow_qualification_decisions"
        })
        {
            await using var command=connection.CreateCommand();
            command.CommandText=sql;
            await Assert.ThrowsAsync<SqliteException>(()=>command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task RestartReusesPersistedReadyReceiptAfterCrashBeforeLifecycleCommit()
    {
        var profile=Profile();
        var store=await SeedAuthority(profile);
        foreach(var value in Evidence(profile,StrategyGovernor.MinimumShadowObservations))
            await store.SaveStrategyShadowObservationAsync(value,default);

        var performance=await store.GetStrategyShadowObservationPerformanceAsync(profile.Id,profile.Version,default);
        profile.ShadowObservations=performance.Observations;
        profile.Expectancy=performance.Expectancy;
        profile.MaxDrawdown=performance.MaxDrawdown;
        profile.QualityScore=performance.QualityScore;
        profile.FailureStreak=performance.FailureStreak;
        await store.UpsertStrategyAsync(profile,default);

        var persistedEvidence=await store.GetStrategyShadowObservationsAsync(profile.Id,profile.Version,default);
        var receipt=StrategyShadowQualificationV1.Evaluate(
            profile.Id,profile.Version,profile.Symbol,persistedEvidence,Now);
        Assert.True(receipt.Qualified);
        Assert.True(await store.SaveStrategyShadowQualificationAsync(receipt,default));

        // Simulate a crash after receipt persistence but before the Shadow -> Active profile update.
        var restarted=new AgentSqliteStore(Database,()=>Now.AddMinutes(1));
        var agent=new StrategyResearchAgent(restarted,utcNow:()=>Now.AddMinutes(1).UtcDateTime);
        await agent.ObserveAsync(new EvidencePack{Completeness=100},default);

        var active=(await restarted.GetStrategiesAsync(default)).Single(x=>x.Id==profile.Id);
        Assert.Equal(StrategyLifecycle.Active,active.Lifecycle);
        var restored=await restarted.GetLatestStrategyShadowQualificationAsync(profile.Id,profile.Version,default);
        Assert.NotNull(restored);
        Assert.Equal(receipt.CanonicalSha256,restored!.CanonicalSha256);
    }

    [Fact]
    public async Task DeterministicFailoverRequiresCanonicalQualificationReceipt()
    {
        var profile=Profile();
        var store=await SeedAuthority(profile);
        foreach(var value in Evidence(profile,StrategyGovernor.MinimumShadowObservations))
            await store.SaveStrategyShadowObservationAsync(value,default);

        var performance=await store.GetStrategyShadowObservationPerformanceAsync(profile.Id,profile.Version,default);
        profile.ShadowObservations=performance.Observations;
        profile.Expectancy=performance.Expectancy;
        profile.MaxDrawdown=performance.MaxDrawdown;
        profile.QualityScore=performance.QualityScore;
        profile.FailureStreak=performance.FailureStreak;
        await store.UpsertStrategyAsync(profile,default);

        var agent=new StrategyResearchAgent(store,utcNow:()=>Now.UtcDateTime);
        await agent.ObserveAsync(new EvidencePack{Completeness=100},default);

        var active=(await store.GetStrategiesAsync(default)).Single(x=>x.Id==profile.Id);
        Assert.Equal(StrategyLifecycle.Active,active.Lifecycle);
        var receipt=await store.GetLatestStrategyShadowQualificationAsync(profile.Id,profile.Version,default);
        Assert.NotNull(receipt);
        Assert.True(receipt!.Qualified);
        Assert.Equal(performance.Observations,receipt.ObservationCount);
    }

    [Fact]
    public async Task QualifiedLookingLegacyProfileCannotBypassMissingCanonicalEvidence()
    {
        var profile=Profile();
        profile.ShadowObservations=StrategyGovernor.MinimumShadowObservations;
        profile.Expectancy=.01;
        profile.MaxDrawdown=.01;
        profile.QualityScore=.9;
        profile.FailureStreak=0;
        var store=new AgentSqliteStore(Database,()=>Now);
        await store.UpsertStrategyAsync(profile,default);

        var agent=new StrategyResearchAgent(store,utcNow:()=>Now.UtcDateTime);
        await agent.ObserveAsync(new EvidencePack{Completeness=100},default);

        var restored=(await store.GetStrategiesAsync(default)).Single(x=>x.Id==profile.Id);
        Assert.Equal(StrategyLifecycle.Shadow,restored.Lifecycle);
        Assert.Null(await store.GetLatestStrategyShadowQualificationAsync(profile.Id,profile.Version,default));
    }

    private async Task<AgentSqliteStore> SeedAuthority(StrategyProfile profile)
    {
        var store=new AgentSqliteStore(Database,()=>ValidationAt);
        await store.UpsertStrategyAsync(profile,default);
        var timeline=Timeline(profile,0);
        await store.SaveStrategyExposureTimelineAsync(timeline,default);
        var validation=Validation(profile,timeline.CanonicalSha256);
        await store.SaveStrategyValidationAsync(validation,default);
        await store.SaveBacktestRunAsync(new PersistedBacktestRun(
            "shadow-qualification-backtest",
            profile.Id,
            profile.Version,
            profile.Symbol,
            "PASSED",
            ValidationAt.UtcDateTime,
            30,
            validation.Trades,
            validation.OutOfSampleReturn,
            validation.MaxDrawdown,
            validation.Sharpe),default);
        return store;
    }

    private static IReadOnlyList<StrategyShadowObservationV1> Evidence(
        StrategyProfile profile,
        int count)
        =>Enumerable.Range(0,count).Select(i=>Observation(profile,i)).ToArray();

    private static StrategyShadowObservationV1 Observation(
        StrategyProfile profile,
        int index,
        int timelineVariant=0)
    {
        var timeline=Timeline(profile,timelineVariant);
        var validation=ValidationFact(profile,ValidationAt);
        var marketAt=ValidationAt.AddMinutes(index+1);
        var price=100m+index;
        var market=Market(price,marketAt);
        return StrategyShadowObservationCanonicalizerV1.Create(
            profile,
            new StrategySignal(profile.Id,profile.Symbol,1,.8,"qualification",profile.Version),
            market,
            validation,
            timeline,
            marketAt.AddSeconds(10));
    }

    private static StrategyValidation Validation(
        StrategyProfile profile,
        string timelineSha256)=>new(
            profile.Id,
            800,
            40,
            .58,
            1.5,
            .002,
            .10,
            1.2,
            .08,
            .70,
            .20,
            .80,
            true,
            "shadow qualification validation",
            -.02,
            .001,
            StrategyGovernor.RequiredEvaluatedRegimes,
            StrategyGovernor.RequiredEvaluatedRegimes,
            profile.Version,
            15,
            .10,
            .02,
            timelineSha256);

    private static BacktestValidationFactV1 ValidationFact(
        StrategyProfile profile,
        DateTimeOffset validatedAt)
    {
        var validation=Validation(profile,Timeline(profile,0).CanonicalSha256);
        return BacktestValidationCanonicalizerV1.Create(
            profile.Symbol,
            profile.Id,
            profile.Version,
            validatedAt,
            validation.SampleSize,
            validation.Trades,
            validation.OutOfSampleTrades,
            30,
            validation.WinRate,
            validation.ProfitFactor,
            validation.Expectancy,
            validation.MaxDrawdown,
            validation.Sharpe,
            validation.OutOfSampleReturn,
            validation.WalkForwardScore,
            validation.MonteCarloLossProbability,
            validation.QualityScore,
            approved:true,
            promoted:false);
    }

    private static StrategyExposureTimelineArtifactV1 Timeline(
        StrategyProfile profile,
        int variant)
    {
        var source=new CandleEvidence(
            ValidationAt.AddHours(-2).UtcDateTime,
            100m,101m,99m,100m,1000m,100000m,10,500m);
        var execution=new CandleEvidence(
            ValidationAt.AddHours(-1).UtcDateTime,
            100m,102m,99m,101m+variant*.01m,1100m,110000m,12,550m);
        var decision=StrategyExposureTimelineV1.Create(profile,0,source,execution,1)
            ??throw new InvalidOperationException("Qualification test timeline decision is invalid.");
        return StrategyExposureTimelineV1.CreateArtifact([decision])
            ??throw new InvalidOperationException("Qualification test timeline artifact is invalid.");
    }

    private static StrategyProfile Profile()
    {
        var registry=new DeterministicStrategyRegistry();
        var family=StrategyFamily.TrendBreakout;
        var parameters=LocalStrategyParameters.For(family,0);
        return new StrategyProfile
        {
            Id="BTCUSDT-shadow-qualification",
            Version=registry.BindProfileVersion(family,"qualification-v1"),
            Symbol="BTCUSDT",
            Family=family,
            Parameters=parameters,
            ParametersHash=LocalStrategyParameters.Hash(parameters),
            LineageHash=StrategyLineage.Hash("BTCUSDT",family,null,null,0,LocalStrategyParameters.Hash(parameters)),
            Lifecycle=StrategyLifecycle.Shadow,
            QualityScore=.8,
            Expectancy=.01,
            MaxDrawdown=.1,
            ValidationTrades=40,
            LastReason="shadow qualification test"
        };
    }

    private static MarketEvidence Market(decimal price,DateTimeOffset collectedAt)
    {
        var market=new MarketEvidence(
            "BTCUSDT",
            price,
            price-.5m,
            price+.5m,
            55,
            .01,
            .02,
            .03,
            new(0,1m,1m,1m,1m,1m,0),
            collectedAt.UtcDateTime)
        {
            Quality=new(){QualityScore=90,LiquidityScore=.9,SpreadBps=1,AtrPercent=.01}
        };
        return market with
        {
            Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(
                market,"binance-futures","Testnet")
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }
}

using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Security.Cryptography;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyShadowObservationProvenanceTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,9,19,1,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-shadow-provenance-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");

    [Fact]
    public void CanonicalObservationBindsValidationTimelineAndMarket()
    {
        var profile=Profile();
        var timeline=Timeline(profile);
        var validation=ValidationFact(profile,Now.AddMinutes(-10));
        var market=Market(Now.AddMinutes(-1));
        var signal=new StrategySignal(profile.Id,profile.Symbol,1,.75,"test",profile.Version);

        var value=StrategyShadowObservationCanonicalizerV1.Create(
            profile,signal,market,validation,timeline,Now);

        Assert.True(StrategyShadowObservationCanonicalizerV1.IsCanonical(value));
        Assert.Equal(validation.CanonicalSha256,value.BacktestValidationSha256);
        Assert.Equal(timeline.CanonicalSha256,value.TimelineSha256);
        Assert.Equal(market.Provenance!.CanonicalSha256,value.MarketProvenanceSha256);
        Assert.Equal("Testnet",value.Environment);
        Assert.False(StrategyShadowObservationCanonicalizerV1.IsCanonical(
            value with{TimelineSha256=new string('0',64)}));
        Assert.False(StrategyShadowObservationCanonicalizerV1.IsCanonical(
            value with{CanonicalBytes=[..value.CanonicalBytes,0]}));
        Assert.False(StrategyShadowObservationCanonicalizerV1.IsCanonical(
            value with{MarketProvenanceCanonicalBytes=[..value.MarketProvenanceCanonicalBytes,0]}));
        Assert.False(StrategyShadowObservationCanonicalizerV1.IsCanonical(
            value with{BacktestValidationCanonicalBytes=[..value.BacktestValidationCanonicalBytes,0]}));
    }

    [Fact]
    public void SelfConsistentOuterHashCannotHideDifferentCanonicalMarketSource()
    {
        var profile=Profile();
        var value=StrategyShadowObservationCanonicalizerV1.Create(
            profile,
            new StrategySignal(profile.Id,profile.Symbol,1,.75,"test",profile.Version),
            Market(Now.AddMinutes(-1)),
            ValidationFact(profile,Now.AddMinutes(-10)),
            Timeline(profile),
            Now);
        var otherMarket=Market(Now.AddMinutes(-1)) with{Price=123m,Provenance=null};
        otherMarket=otherMarket with
        {
            Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(
                otherMarket,"binance-futures","Testnet")
        };
        var forged=Rehash(value with
        {
            MarketProvenanceSha256=otherMarket.Provenance!.CanonicalSha256,
            MarketProvenanceCanonicalBytes=otherMarket.Provenance.CanonicalBytes
        });

        Assert.False(StrategyShadowObservationCanonicalizerV1.IsCanonical(forged));
    }

    [Fact]
    public async Task PersistedShadowReadReplaysValidationAgainstDurableAuthority()
    {
        var profile=Profile();
        var validationAt=Now.AddMinutes(-10);
        var store=await SeedAuthority(validationAt,profile,includeTimeline:true);
        var validation=ValidationFact(profile,validationAt);
        var value=StrategyShadowObservationCanonicalizerV1.Create(
            profile,
            new StrategySignal(profile.Id,profile.Symbol,1,.75,"test",profile.Version),
            Market(Now.AddMinutes(-1)),
            validation,
            Timeline(profile),
            Now);
        Assert.True(await store.SaveStrategyShadowObservationAsync(value,default));

        var other=BacktestValidationCanonicalizerV1.Create(
            validation.Symbol,validation.StrategyId,validation.StrategyVersion,validation.ValidatedAtUtc,
            validation.SampleSize,validation.Trades,validation.OutOfSampleTrades,validation.CoverageDays,
            validation.WinRate,validation.ProfitFactor,validation.Expectancy+.001,validation.MaxDrawdown,
            validation.Sharpe,validation.OutOfSampleReturn,validation.WalkForwardScore,
            validation.MonteCarloLossProbability,validation.QualityScore,validation.Approved,validation.Promoted);
        var forged=Rehash(value with
        {
            BacktestValidationSha256=other.CanonicalSha256,
            BacktestValidationCanonicalBytes=other.CanonicalBytes
        });
        Assert.True(StrategyShadowObservationCanonicalizerV1.IsCanonical(forged));

        await using(var connection=new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            await using(var drop=connection.CreateCommand())
            {
                drop.CommandText="DROP TRIGGER strategy_shadow_observation_no_update";
                await drop.ExecuteNonQueryAsync();
            }
            await using var update=connection.CreateCommand();
            update.CommandText="""
                UPDATE strategy_shadow_observation_artifacts
                SET canonical_sha256=$hash,
                    backtest_validation_sha256=$validationHash,
                    backtest_validation_bytes=$validationBytes,
                    canonical_bytes=$bytes
                WHERE strategy_id=$strategy AND strategy_version=$version;
                """;
            update.Parameters.AddWithValue("$hash",forged.CanonicalSha256);
            update.Parameters.AddWithValue("$validationHash",forged.BacktestValidationSha256);
            update.Parameters.Add("$validationBytes",SqliteType.Blob).Value=forged.BacktestValidationCanonicalBytes;
            update.Parameters.Add("$bytes",SqliteType.Blob).Value=forged.CanonicalBytes;
            update.Parameters.AddWithValue("$strategy",profile.Id);
            update.Parameters.AddWithValue("$version",profile.Version);
            Assert.Equal(1,await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.GetStrategyShadowObservationPerformanceAsync(profile.Id,profile.Version,default));
    }

    [Fact]
    public void ValidationMustPredateTheForwardMarketObservation()
    {
        var profile=Profile();
        var market=Market(Now.AddMinutes(-10));
        var validation=ValidationFact(profile,Now.AddMinutes(-5));
        var signal=new StrategySignal(profile.Id,profile.Symbol,0,.5,"hold",profile.Version);

        Assert.Throws<InvalidOperationException>(()=>
            StrategyShadowObservationCanonicalizerV1.Create(
                profile,signal,market,validation,Timeline(profile),Now));
    }

    [Fact]
    public void HistoricalTimelineMustPredateValidation()
    {
        var profile=Profile();
        var signal=new StrategySignal(profile.Id,profile.Symbol,0,.5,"hold",profile.Version);
        var validation=ValidationFact(profile,Now.AddMinutes(-40));

        Assert.Throws<InvalidOperationException>(()=>
            StrategyShadowObservationCanonicalizerV1.Create(
                profile,
                signal,
                Market(Now.AddMinutes(-1)),
                validation,
                Timeline(profile),
                Now));
    }

    [Fact]
    public void MainnetMarketCannotCreateShadowEvidence()
    {
        var profile=Profile();
        var market=Market(Now.AddMinutes(-1),"Mainnet");
        var signal=new StrategySignal(profile.Id,profile.Symbol,0,.5,"hold",profile.Version);

        Assert.Throws<InvalidOperationException>(()=>
            StrategyShadowObservationCanonicalizerV1.Create(
                profile,signal,market,ValidationFact(profile,Now.AddMinutes(-10)),Timeline(profile),Now));
    }

    [Fact]
    public void ActiveStrategyCannotBeEncodedAsShadowQualificationEvidence()
    {
        var profile=Profile();
        profile.Lifecycle=StrategyLifecycle.Active;
        var signal=new StrategySignal(profile.Id,profile.Symbol,0,.5,"active",profile.Version);

        Assert.Throws<InvalidOperationException>(()=>
            StrategyShadowObservationCanonicalizerV1.Create(
                profile,
                signal,
                Market(Now.AddMinutes(-1)),
                ValidationFact(profile,Now.AddMinutes(-10)),
                Timeline(profile),
                Now));
    }

    [Fact]
    public async Task ActiveMonitoringDoesNotDependOnShadowProvenanceAuthority()
    {
        var profile=Profile();
        profile.Lifecycle=StrategyLifecycle.Active;
        var store=new AgentSqliteStore(Database,()=>Now);
        await store.UpsertStrategyAsync(profile,default);
        var agent=new StrategyResearchAgent(store,utcNow:()=>Now.UtcDateTime);
        agent.SetSchedulerHealth(true);

        await agent.ObserveAsync(Pack(Market(Now.AddMinutes(-1))),default);

        Assert.Equal(1,await Count("strategy_observations"));
        Assert.Equal(0,await CountIfExists("strategy_shadow_observation_artifacts"));
    }

    [Fact]
    public async Task ProductionObserveCountsOneCanonicalMarketOnlyOnce()
    {
        var profile=Profile();
        var validationAt=Now.AddMinutes(-10);
        var store=await SeedAuthority(validationAt,profile,includeTimeline:true);
        var agent=new StrategyResearchAgent(store,utcNow:()=>Now.UtcDateTime);
        agent.SetSchedulerHealth(true);
        var market=Market(Now.AddMinutes(-1));
        var evidence=Pack(market);

        await agent.ObserveAsync(evidence,default);
        await agent.ObserveAsync(evidence,default);

        Assert.Equal(1,await Count("strategy_shadow_observation_artifacts"));
        Assert.Equal(1,await Count("strategy_observations"));
        var restored=(await store.GetStrategiesAsync(default)).Single(x=>x.Id==profile.Id);
        Assert.Equal(1,restored.ShadowObservations);
    }

    [Fact]
    public async Task LegacyObservationRowsCannotInflateCanonicalShadowQualification()
    {
        var profile=Profile();
        profile.ShadowObservations=StrategyGovernor.MinimumShadowObservations-1;
        var store=await SeedAuthority(Now.AddMinutes(-10),profile,includeTimeline:true);
        for(var i=0;i<StrategyGovernor.MinimumShadowObservations-1;i++)
            await store.RecordStrategyObservationAsync(
                profile.Id,
                profile.Symbol,
                i%2==0?1:-1,
                100m+i,
                .6,
                default);

        var agent=new StrategyResearchAgent(store,utcNow:()=>Now.UtcDateTime);
        agent.SetSchedulerHealth(true);
        await agent.ObserveAsync(Pack(Market(Now.AddMinutes(-1))),default);

        Assert.Equal(1,await Count("strategy_shadow_observation_artifacts"));
        Assert.Equal(StrategyGovernor.MinimumShadowObservations,await Count("strategy_observations"));
        var restored=(await store.GetStrategiesAsync(default)).Single(x=>x.Id==profile.Id);
        Assert.Equal(1,restored.ShadowObservations);
        Assert.Equal(StrategyLifecycle.Shadow,restored.Lifecycle);
    }

    [Fact]
    public async Task MissingTimelineProvenanceDoesNotCountShadowObservation()
    {
        var profile=Profile();
        var store=await SeedAuthority(Now.AddMinutes(-10),profile,includeTimeline:false);
        var agent=new StrategyResearchAgent(store,utcNow:()=>Now.UtcDateTime);
        agent.SetSchedulerHealth(true);

        await agent.ObserveAsync(Pack(Market(Now.AddMinutes(-1))),default);

        Assert.Equal(0,await CountIfExists("strategy_shadow_observation_artifacts"));
        Assert.Equal(0,await Count("strategy_observations"));
        var restored=(await store.GetStrategiesAsync(default)).Single(x=>x.Id==profile.Id);
        Assert.Equal(0,restored.ShadowObservations);
    }

    [Fact]
    public async Task ShadowArtifactsAreAppendOnlyAndConflictingReplayFailsClosed()
    {
        var profile=Profile();
        var timeline=Timeline(profile);
        var validation=ValidationFact(profile,Now.AddMinutes(-10));
        var market=Market(Now.AddMinutes(-1));
        var value=StrategyShadowObservationCanonicalizerV1.Create(
            profile,
            new StrategySignal(profile.Id,profile.Symbol,0,.5,"hold",profile.Version),
            market,
            validation,
            timeline,
            Now);
        var store=await SeedAuthority(Now.AddMinutes(-10),profile,includeTimeline:true);

        Assert.True(await store.SaveStrategyShadowObservationAsync(value,default));
        Assert.False(await new AgentSqliteStore(Database,()=>Now).SaveStrategyShadowObservationAsync(value,default));

        var conflicting=StrategyShadowObservationCanonicalizerV1.Create(
            profile,
            new StrategySignal(profile.Id,profile.Symbol,1,.75,"changed",profile.Version),
            market,
            validation,
            timeline,
            Now);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.SaveStrategyShadowObservationAsync(conflicting,default));

        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        foreach(var sql in new[]
        {
            "UPDATE strategy_shadow_observation_artifacts SET lifecycle='Active'",
            "DELETE FROM strategy_shadow_observation_artifacts"
        })
        {
            await using var command=connection.CreateCommand();
            command.CommandText=sql;
            await Assert.ThrowsAsync<SqliteException>(()=>command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public void PersistedTimelineParserRejectsHashOrByteTampering()
    {
        var artifact=Timeline(Profile());

        Assert.True(StrategyExposureTimelineV1.TryDeserializeArtifact(
            artifact.CanonicalBytes,artifact.CanonicalSha256,out var parsed));
        Assert.NotNull(parsed);
        Assert.False(StrategyExposureTimelineV1.TryDeserializeArtifact(
            [..artifact.CanonicalBytes,0],artifact.CanonicalSha256,out _));
        Assert.False(StrategyExposureTimelineV1.TryDeserializeArtifact(
            artifact.CanonicalBytes,new string('0',64),out _));
    }

    private async Task<AgentSqliteStore> SeedAuthority(
        DateTimeOffset validationAt,
        StrategyProfile profile,
        bool includeTimeline)
    {
        var store=new AgentSqliteStore(Database,()=>validationAt);
        await store.UpsertStrategyAsync(profile,default);
        var timeline=Timeline(profile);
        if(includeTimeline)
            await store.SaveStrategyExposureTimelineAsync(timeline,default);

        var validation=Validation(profile,timeline.CanonicalSha256);
        await store.SaveStrategyValidationAsync(validation,default);
        await store.SaveBacktestRunAsync(new PersistedBacktestRun(
            "backtest-"+profile.Id,
            profile.Id,
            profile.Version,
            profile.Symbol,
            "PASSED",
            validationAt.UtcDateTime,
            30,
            validation.Trades,
            validation.OutOfSampleReturn,
            validation.MaxDrawdown,
            validation.Sharpe),default);
        return store;
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
            "provenance-bound validation",
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
        var validation=Validation(profile,Timeline(profile).CanonicalSha256);
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

    private static StrategyExposureTimelineArtifactV1 Timeline(StrategyProfile profile)
    {
        var source=new CandleEvidence(
            Now.AddMinutes(-30).UtcDateTime,
            100m,101m,99m,100m,1000m,100000m,10,500m);
        var execution=new CandleEvidence(
            Now.AddMinutes(-29).UtcDateTime,
            100m,102m,99m,101m,1100m,110000m,12,550m);
        var decision=StrategyExposureTimelineV1.Create(profile,0,source,execution,1)
            ??throw new InvalidOperationException("Test timeline decision is invalid.");
        return StrategyExposureTimelineV1.CreateArtifact([decision])
            ??throw new InvalidOperationException("Test timeline artifact is invalid.");
    }

    private static StrategyProfile Profile()
    {
        var registry=new DeterministicStrategyRegistry();
        var family=StrategyFamily.TrendBreakout;
        var parameters=LocalStrategyParameters.For(family,0);
        return new StrategyProfile
        {
            Id="BTCUSDT-shadow-provenance",
            Version=registry.BindProfileVersion(family,"shadow-v1"),
            Symbol="BTCUSDT",
            Family=family,
            Parameters=parameters,
            ParametersHash=LocalStrategyParameters.Hash(parameters),
            LineageHash=StrategyLineage.Hash("BTCUSDT",family,null,null,0,LocalStrategyParameters.Hash(parameters)),
            Lifecycle=StrategyLifecycle.Shadow,
            QualityScore=.80,
            Expectancy=.01,
            MaxDrawdown=.10,
            ValidationTrades=40,
            LastReason="test shadow provenance"
        };
    }

    private static MarketEvidence Market(DateTimeOffset collectedAt,string environment="Testnet")
    {
        var market=new MarketEvidence(
            "BTCUSDT",
            100m,
            95m,
            105m,
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
                market,"binance-futures",environment)
        };
    }

    private static StrategyShadowObservationV1 Rehash(StrategyShadowObservationV1 value)
    {
        var method=typeof(StrategyShadowObservationCanonicalizerV1)
            .GetMethod("Serialize",BindingFlags.NonPublic|BindingFlags.Static)
            ??throw new InvalidOperationException("Shadow observation serializer is unavailable.");
        var bytes=(byte[]?)method.Invoke(null,new object?[]{value})
            ??throw new InvalidOperationException("Shadow observation serialization failed.");
        return value with
        {
            CanonicalBytes=bytes,
            CanonicalSha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        };
    }

    private static EvidencePack Pack(MarketEvidence market)=>new()
    {
        CollectedAt=market.CollectedAt,
        Completeness=100,
        Markets=new Dictionary<string,MarketEvidence>(StringComparer.Ordinal)
        {
            [market.Symbol]=market
        }
    };

    private async Task<int> Count(string table)
    {
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText=$"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountIfExists(string table)
    {
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var exists=connection.CreateCommand();
        exists.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        exists.Parameters.AddWithValue("$name",table);
        if(Convert.ToInt32(await exists.ExecuteScalarAsync())==0)return 0;
        await using var count=connection.CreateCommand();
        count.CommandText=$"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await count.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }
}

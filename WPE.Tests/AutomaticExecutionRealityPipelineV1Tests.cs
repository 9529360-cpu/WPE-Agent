using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class AutomaticExecutionRealityPipelineV1Tests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-auto-reality-"+Guid.NewGuid().ToString("N"));
    private readonly Clock _clock=new(new(2026,9,19,2,0,0,TimeSpan.Zero));
    private string Database=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task MarketAutomaticExecutionPersistsSimulationObservedRealityAndComparison()
    {
        var store=Store();
        var artifact=Artifact(ExecutionOrderType.Market);
        await Approve(store,"exec-market",artifact);
        var gateway=new EvidenceGateway(_clock)
        {
            Order=new ExchangeOrder(
                "BTCUSDT","venue-1","WPE-REALITY","FILLED",
                1m,100.20m,"MARKET",PositionSide.Long,false,_clock.Now.UtcDateTime)
        };
        var processor=new AutomaticExecutionProcessor(store,new Validator(),gateway,()=>_clock.Now);

        var result=await processor.ProcessNextAsync("worker",default);

        Assert.True(result.Handled);
        Assert.Equal("automatic.succeeded",result.Code);
        Assert.Equal(1,gateway.ExecuteCount);
        Assert.Equal(1,gateway.SimulationReadCount);
        Assert.True(gateway.SimulationObservedBeforeExecute);

        var source=await store.GetExecutionSimulationSourceAsync(artifact.CorrelationId,"WPE-REALITY",default);
        Assert.NotNull(source);
        Assert.Equal(ExecutionSimulationSourceStateV1.Available,source!.State);
        Assert.True(ExecutionSimulationSourceCanonicalizerV1.IsCanonical(source));

        var fill=await store.GetExecutionSimulationFillAsync(artifact.CorrelationId,"WPE-REALITY",default);
        Assert.NotNull(fill);
        Assert.Equal(ExecutionSimulationFillStateV1.Filled,fill!.State);
        Assert.Equal(1m,fill.ExecutedQuantity);
        Assert.Equal(100.03m,fill.AveragePrice);
        Assert.Equal(ExecutionSimulationFeeRoleV1.Taker,fill.FeeRole);
        Assert.False(fill.LatencyModeled);
        Assert.Equal(ExecutionRealityCostAuthorityV1.Version,fill.CostModelVersion);

        var drift=await store.GetRecentExecutionRealityDriftAsync(10,default);
        var observed=Assert.Single(drift);
        Assert.Equal(artifact.CorrelationId,observed.CorrelationId);
        Assert.Equal("WPE-REALITY",observed.ClientOrderId);
        Assert.Equal(artifact.StrategyId,observed.StrategyId);
        Assert.Equal(artifact.StrategyVersion,observed.StrategyVersion);
        Assert.Equal(ExecutionRealityCostAuthorityV1.Version,observed.CostModelVersion);
        Assert.Equal(100.20m,observed.AveragePrice);

        var comparisons=await store.GetRecentExecutionSimulationComparisonsAsync(10,default);
        var comparison=Assert.Single(comparisons);
        Assert.Equal(fill.CanonicalSha256,comparison.SimulatedCanonicalSha256);
        Assert.Equal(observed.CanonicalSha256,comparison.ObservedCanonicalSha256);
        Assert.True(comparison.PriceComparable);
        Assert.True(comparison.PriceDriftBps>0);
        Assert.False(comparison.FeeComparable);
    }

    [Fact]
    public async Task LimitOrderSimulationFailsClosedAsUnsupportedButObservedComparisonStillPersists()
    {
        var store=Store();
        var artifact=Artifact(ExecutionOrderType.Limit);
        await Approve(store,"exec-limit",artifact);
        var gateway=new EvidenceGateway(_clock)
        {
            Order=new ExchangeOrder(
                "BTCUSDT","venue-limit","WPE-REALITY","FILLED",
                1m,100m,"LIMIT",PositionSide.Long,false,_clock.Now.UtcDateTime)
        };
        var processor=new AutomaticExecutionProcessor(store,new Validator(),gateway,()=>_clock.Now);

        var result=await processor.ProcessNextAsync("worker",default);

        Assert.True(result.Handled);
        var fill=await store.GetExecutionSimulationFillAsync(artifact.CorrelationId,"WPE-REALITY",default);
        Assert.NotNull(fill);
        Assert.Equal(ExecutionSimulationFillStateV1.Unsupported,fill!.State);
        Assert.Equal("simulation-order-type-unsupported",fill.ReasonCode);
        Assert.Equal(0m,fill.ExecutedQuantity);

        var comparison=Assert.Single(await store.GetRecentExecutionSimulationComparisonsAsync(10,default));
        Assert.Equal("simulation-unsupported",comparison.ReasonCode);
        Assert.False(comparison.PriceComparable);
        Assert.False(comparison.FeeComparable);
        Assert.False(comparison.TotalComparable);
    }

    [Fact]
    public async Task MissingRealityReaderNeverBlocksAuthorizedMutationAndPersistsUnsupportedSimulation()
    {
        var store=Store();
        var artifact=Artifact(ExecutionOrderType.Market);
        await Approve(store,"exec-no-reader",artifact);
        var gateway=new ExecutionOnlyGateway();
        var processor=new AutomaticExecutionProcessor(store,new Validator(),gateway,()=>_clock.Now);

        var result=await processor.ProcessNextAsync("worker",default);

        Assert.True(result.Handled);
        Assert.Equal("automatic.succeeded",result.Code);
        Assert.Equal(1,gateway.ExecuteCount);
        var source=await store.GetExecutionSimulationSourceAsync(artifact.CorrelationId,"WPE-REALITY",default);
        Assert.NotNull(source);
        Assert.Equal(ExecutionSimulationSourceStateV1.Unavailable,source!.State);
        Assert.Equal("simulation-reader-unavailable",source.ReasonCode);
        var fill=await store.GetExecutionSimulationFillAsync(artifact.CorrelationId,"WPE-REALITY",default);
        Assert.Equal(ExecutionSimulationFillStateV1.Unsupported,fill!.State);
        Assert.Empty(await store.GetRecentExecutionRealityDriftAsync(10,default));
        Assert.Empty(await store.GetRecentExecutionSimulationComparisonsAsync(10,default));
    }

    [Fact]
    public async Task RepeatedPipelineCaptureReusesExactSourceAndFill()
    {
        var store=Store();
        var artifact=Artifact(ExecutionOrderType.Market);
        await Approve(store,"exec-idempotent",artifact);
        var item=await store.GetAutomaticExecutionAsync("exec-idempotent",default);
        Assert.NotNull(item);
        var gateway=new EvidenceGateway(_clock);
        var pipeline=new AutomaticExecutionRealityPipelineV1(store,gateway,()=>_clock.Now);

        var first=await pipeline.CaptureBeforeMutationAsync(item!,default);
        _clock.Now=_clock.Now.AddSeconds(1);
        var second=await pipeline.CaptureBeforeMutationAsync(item!,default);

        Assert.Equal(1,first.Simulated);
        Assert.Equal(1,second.Simulated);
        Assert.Equal(1,second.Skipped);
        Assert.Equal(1,gateway.SimulationReadCount);
        Assert.Equal(1,await Count("execution_simulation_sources"));
        Assert.Equal(1,await Count("execution_simulated_fills"));
    }

    [Fact]
    public async Task SimulationSourceIdentityConflictFailsClosedAndTablesAreAppendOnly()
    {
        var store=Store();
        var artifact=Artifact(ExecutionOrderType.Market);
        var intent=artifact.Intents.Single();
        var observation=Observation(_clock.Now,100m);
        var first=ExecutionSimulationSourceCanonicalizerV1.Create(artifact,intent,observation,_clock.Now);
        var second=ExecutionSimulationSourceCanonicalizerV1.Create(
            artifact,intent,Observation(_clock.Now.AddSeconds(1),101m),_clock.Now.AddSeconds(1));

        Assert.True(await store.SaveExecutionSimulationSourceAsync(first,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveExecutionSimulationSourceAsync(second,default));

        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        foreach(var sql in new[]
        {
            "UPDATE execution_simulation_sources SET state='Unavailable'",
            "DELETE FROM execution_simulation_sources"
        })
        {
            await using var command=connection.CreateCommand();
            command.CommandText=sql;
            await Assert.ThrowsAsync<SqliteException>(()=>command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public void CostAuthorityIsExactlyTheResearchRealityDefault()
    {
        var costs=ExecutionRealityCostAuthorityV1.Current;

        Assert.Equal(ResearchRealityModel.DefaultCosts.CommissionRate,costs.CommissionRate);
        Assert.Equal(ResearchRealityModel.DefaultCosts.SlippageRate,costs.SlippageRate);
        Assert.Equal(ExecutionRealityCostAuthorityV1.Version,costs.Version);
        Assert.StartsWith(ExecutionRealityCostAuthorityV1.Schema+":",costs.Version,StringComparison.Ordinal);
    }

    private AgentSqliteStore Store()=>new(Database,()=>_clock.Now);

    private async Task Approve(AgentSqliteStore store,string id,DurableExecutionArtifactV2 artifact)
    {
        Assert.True((await store.SaveAutomaticExecutionAsync(id,artifact,default)).Succeeded);
        Assert.True((await store.RecordAutomaticRiskDecisionAsync(id,Receipt(artifact),default)).Succeeded);
    }

    private DurableExecutionArtifactV2 Artifact(ExecutionOrderType orderType)=>new(
        DurableExecutionArtifactV2.Version,
        "cycle-reality",
        [new(
            0,
            "BTCUSDT",
            "Long",
            1m,
            false,
            90m,
            120m,
            "WPE-REALITY",
            "strategy.entry",
            "OpenLong",
            orderType.ToString(),
            orderType==ExecutionOrderType.Limit?100m:0m,
            100m)],
        5,
        true,
        "binance",
        "Testnet",
        "strategy-reality",
        "v7",
        _clock.Now.AddSeconds(-20),
        "market-authority-v1",
        _clock.Now.AddSeconds(-10),
        _clock.Now.AddMinutes(5));

    private DeterministicRiskReceipt Receipt(DurableExecutionArtifactV2 artifact)
    {
        var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        return new(
            "risk-reality",
            artifact.CorrelationId,
            hashes.IntentHash,
            true,
            _clock.Now.AddSeconds(-1),
            _clock.Now.AddMinutes(1),
            null,
            hashes.ArtifactHash);
    }

    private static AutomaticExecutionSimulationObservationV1 Observation(
        DateTimeOffset observedAt,
        decimal price)
    {
        var collected=observedAt.AddSeconds(-1);
        var market=new MarketEvidence(
            "BTCUSDT",price,price-5,price+5,50,0,0,0,
            new(0,1,1,1,1,1,0),
            collected.UtcDateTime)
        {
            Quality=new(){QualityScore=90,LiquidityScore=.9,SpreadBps=1,AtrPercent=.01}
        };
        market=market with
        {
            Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"binance","Testnet")
        };
        return new(
            true,
            "simulation.source-available",
            "binance",
            "Testnet",
            market,
            new TradingRule("BTCUSDT",.001m,.1m,.001m,5m,20),
            observedAt);
    }

    private async Task<int> Count(string table)
    {
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText=$"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class Clock(DateTimeOffset now)
    {
        public DateTimeOffset Now{get;set;}=now;
    }

    private sealed class Validator:IAutomaticExecutionReadOnlyValidator
    {
        public Task<AutomaticExecutionRuntimeFacts> ValidateAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct)=>
            Task.FromResult(new AutomaticExecutionRuntimeFacts(
                AutomaticFactState.True,
                AutomaticFactState.True,
                AutomaticFactState.True,
                AutomaticFactState.True,
                "ok"));
    }

    private sealed class EvidenceGateway(Clock clock):IAutomaticExecutionGateway,IAutomaticExecutionRealityEvidenceReader
    {
        public bool IsTestnet=>true;
        public int ExecuteCount{get;private set;}
        public int SimulationReadCount{get;private set;}
        public bool SimulationObservedBeforeExecute{get;private set;}
        public ExchangeOrder? Order{get;set;}

        public Task<AutomaticGatewayExecutionResult> ExecuteAsync(
            DurableExecutionArtifactV2 artifact,
            DeterministicRiskReceipt receipt,
            CancellationToken ct)
        {
            ExecuteCount++;
            SimulationObservedBeforeExecute=SimulationReadCount>0;
            return Task.FromResult(new AutomaticGatewayExecutionResult(AutomaticGatewayExecutionState.Succeeded,"ok"));
        }

        public Task<AutomaticGatewayReconciliationResult> ReconcileAsync(
            DurableExecutionArtifactV2 artifact,
            CancellationToken ct)=>
            Task.FromResult(new AutomaticGatewayReconciliationResult(AutomaticGatewayReconciliationState.Succeeded,"ok"));

        public Task<AutomaticExecutionSimulationObservationV1> ObserveSimulationInputAsync(
            DurableExecutionArtifactV2 artifact,
            DurableExecutionIntentSnapshotV1 intent,
            CancellationToken ct)
        {
            SimulationReadCount++;
            return Task.FromResult(Observation(clock.Now,100m));
        }

        public Task<ExchangeOrder?> ObserveOrderAsync(
            DurableExecutionArtifactV2 artifact,
            DurableExecutionIntentSnapshotV1 intent,
            CancellationToken ct)=>
            Task.FromResult(Order);
    }

    private sealed class ExecutionOnlyGateway:IAutomaticExecutionGateway
    {
        public bool IsTestnet=>true;
        public int ExecuteCount{get;private set;}
        public Task<AutomaticGatewayExecutionResult> ExecuteAsync(
            DurableExecutionArtifactV2 artifact,
            DeterministicRiskReceipt receipt,
            CancellationToken ct)
        {
            ExecuteCount++;
            return Task.FromResult(new AutomaticGatewayExecutionResult(AutomaticGatewayExecutionState.Succeeded,"ok"));
        }
        public Task<AutomaticGatewayReconciliationResult> ReconcileAsync(
            DurableExecutionArtifactV2 artifact,
            CancellationToken ct)=>
            Task.FromResult(new AutomaticGatewayReconciliationResult(AutomaticGatewayReconciliationState.Succeeded,"ok"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }
}

using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PostTradePnlDriftTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "wpe-post-trade-pnl-drift-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory,"agent.db");
    private static readonly DateTimeOffset Now = new(2026,9,19,6,0,0,TimeSpan.Zero);
    private const string StrategyId = "strategy-pnl";
    private const string StrategyVersion = "v1";

    [Fact]
    public async Task AutomaticClosePersistsGrossPnlDriftFromExactSimulationPath()
    {
        var store=Store();
        var open=Intent("open",false,1m,100m);
        var close=Intent("close",true,1m,110m);
        var openArtifact=await Bind(store,"cycle-open",open);
        var closeArtifact=await Bind(store,"cycle-close",close);
        var openSimulation=await SaveSimulation(store,openArtifact,bestBid:99m,bestAsk:100m,bidQuantity:5m,askQuantity:5m);
        var closeSimulation=await SaveSimulation(store,closeArtifact,bestBid:111m,bestAsk:112m,bidQuantity:5m,askQuantity:5m);

        await store.RecordExecutionAsync(
            "cycle-open",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);

        var fact=Assert.Single(await store.GetRecentPostTradePnlDriftAsync(10,default));

        Assert.Equal("wpe.post-trade-pnl-drift/1.1",fact.Schema);
        Assert.Equal(PostTradePnlDriftStateV1.GrossComparable,fact.State);
        Assert.Equal(100m,fact.ActualEntryPrice);
        Assert.Equal(110m,fact.ActualExitPrice);
        Assert.Equal(10m,fact.ActualGrossPnl);
        Assert.Equal(openSimulation.Fill.AveragePrice,fact.SimulatedEntryPrice);
        Assert.Equal(closeSimulation.Fill.AveragePrice,fact.SimulatedExitPrice);
        var expectedSimulatedGross=(closeSimulation.Fill.AveragePrice-openSimulation.Fill.AveragePrice);
        Assert.Equal(expectedSimulatedGross,fact.SimulatedGrossPnl);
        Assert.Equal(10m-expectedSimulatedGross,fact.ObservedMinusSimulatedGrossPnl);
        Assert.False(fact.FeeAdjustedPnlComparable);
        Assert.Null(fact.ObservedMinusSimulatedFeeAdjustedPnl);
        Assert.False(fact.NetPnlComparable);
        Assert.Null(fact.ObservedMinusSimulatedNetPnl);
        Assert.Equal("gross-pnl-comparable-fee-or-funding-unavailable",fact.ReasonCode);
        Assert.True(PostTradePnlDriftCanonicalizerV1.IsCanonical(fact));

        var proof=await store.GetPostTradeEntryLedgerProofAsync("close",default);
        Assert.NotNull(proof);
        Assert.Equal(proof!.CanonicalSha256,fact.EntryLedgerSha256);
        var entrySource=Assert.Single(fact.EntrySimulationSources);
        Assert.Equal(openSimulation.Source.CanonicalSha256,entrySource.SimulationSourceSha256);
        Assert.Equal(openSimulation.Fill.CanonicalSha256,entrySource.SimulatedFillSha256);
        Assert.Equal(closeSimulation.Source.CanonicalSha256,fact.CloseSimulationSourceSha256);
        Assert.Equal(closeSimulation.Fill.CanonicalSha256,fact.CloseSimulationSha256);
    }

    [Fact]
    public async Task ReportedActualFeesEnableFeeAdjustedPnlDeltaButFundingStaysUnmodeled()
    {
        var store=Store();
        var open=Intent("open-fee",false,1m,100m);
        var close=Intent("close-fee",true,1m,110m);
        var openArtifact=await Bind(store,"cycle-open-fee",open);
        var closeArtifact=await Bind(store,"cycle-close-fee",close);
        var openSimulation=await SaveSimulation(store,openArtifact,bestBid:99m,bestAsk:100m,bidQuantity:5m,askQuantity:5m);
        var closeSimulation=await SaveSimulation(store,closeArtifact,bestBid:111m,bestAsk:112m,bidQuantity:5m,askQuantity:5m);
        await SeedFee("open-fee",0.04m,Now.AddMinutes(-4));
        await SeedFee("close-fee",0.044m,Now.AddSeconds(-1));

        await store.RecordExecutionAsync(
            "cycle-open-fee",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close-fee",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);

        var fact=Assert.Single(await store.GetRecentPostTradePnlDriftAsync(10,default));

        Assert.Equal(PostTradePnlDriftStateV1.FeeAdjustedComparable,fact.State);
        Assert.Equal("exchange-reported-usdt",fact.ActualFeeBasis);
        Assert.Equal(0.084m,fact.ActualFees);
        Assert.True(fact.SimulatedFeeEvidenceComplete);
        var expectedSimulatedFees=openSimulation.Fill.FeeAmount+closeSimulation.Fill.FeeAmount;
        Assert.Equal(expectedSimulatedFees,fact.SimulatedFees);
        Assert.True(fact.FeeAdjustedPnlComparable);
        var expectedFeeAdjustedDelta=(fact.ActualGrossPnl-fact.ActualFees)-(fact.SimulatedGrossPnl-expectedSimulatedFees);
        Assert.Equal(expectedFeeAdjustedDelta,fact.ObservedMinusSimulatedFeeAdjustedPnl);
        Assert.False(fact.NetPnlComparable);
        Assert.Equal("fee-adjusted-pnl-comparable-funding-unmodeled",fact.ReasonCode);
    }

    [Fact]
    public async Task SimulationQuantityPathDivergenceFailsClosedWithoutPnlFact()
    {
        var store=Store();
        var open=Intent("open-partial",false,1m,100m);
        var close=Intent("close-partial",true,1m,110m);
        var openArtifact=await Bind(store,"cycle-open-partial",open);
        var closeArtifact=await Bind(store,"cycle-close-partial",close);
        var partial=await SaveSimulation(store,openArtifact,bestBid:99m,bestAsk:100m,bidQuantity:5m,askQuantity:0.5m);
        Assert.Equal(ExecutionSimulationFillStateV1.Partial,partial.Fill.State);
        await SaveSimulation(store,closeArtifact,bestBid:110m,bestAsk:111m,bidQuantity:5m,askQuantity:5m);

        await store.RecordExecutionAsync(
            "cycle-open-partial",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close-partial",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);

        Assert.Empty(await store.GetRecentPostTradePnlDriftAsync(10,default));
        Assert.Null(await store.TryBuildAndSavePostTradePnlDriftAsync("close-partial",default));
    }

    [Fact]
    public async Task CanonicalFillWithoutSimulationSourceCannotProducePnlFact()
    {
        var store=Store();
        var open=Intent("open-unbound",false,1m,100m);
        var close=Intent("close-unbound",true,1m,110m);
        await Bind(store,"cycle-open-unbound",open);
        await Bind(store,"cycle-close-unbound",close);
        await SaveUnboundFill(store,"cycle-open-unbound",open,100m);
        await SaveUnboundFill(store,"cycle-close-unbound",close,110m);

        await store.RecordExecutionAsync(
            "cycle-open-unbound",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close-unbound",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);

        Assert.Empty(await store.GetRecentPostTradePnlDriftAsync(10,default));
        Assert.Null(await store.TryBuildAndSavePostTradePnlDriftAsync("close-unbound",default));
    }

    [Fact]
    public async Task StartupBackfillRepairsMissingPnlDriftAfterDurableCloseCrashWindow()
    {
        var store=Store();
        var open=Intent("open-restart",false,1m,100m);
        var close=Intent("close-restart",true,1m,110m);
        var openArtifact=await Bind(store,"cycle-open-restart",open);
        var closeArtifact=await Bind(store,"cycle-close-restart",close);
        await SaveSimulation(store,openArtifact,bestBid:99m,bestAsk:100m,bidQuantity:5m,askQuantity:5m);
        await SaveSimulation(store,closeArtifact,bestBid:110m,bestAsk:111m,bidQuantity:5m,askQuantity:5m);
        await store.RecordExecutionAsync(
            "cycle-open-restart",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close-restart",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);
        Assert.Single(await store.GetRecentPostTradePnlDriftAsync(10,default));

        await using(var connection=new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            await using(var drop=connection.CreateCommand())
            {
                drop.CommandText="DROP TRIGGER post_trade_pnl_drift_no_delete";
                await drop.ExecuteNonQueryAsync();
            }
            await using(var delete=connection.CreateCommand())
            {
                delete.CommandText="DELETE FROM post_trade_pnl_drift WHERE close_client_order_id='close-restart'";
                Assert.Equal(1,await delete.ExecuteNonQueryAsync());
            }
        }

        var restarted=new AgentSqliteStore(Database,()=>Now);
        Assert.Empty(await restarted.GetRecentPostTradePnlDriftAsync(10,default));
        var processor=new AutomaticExecutionProcessor(
            restarted,new BackfillValidator(),new BackfillGateway(),()=>Now);

        await processor.BackfillObservationsAsync(default);

        var repaired=Assert.Single(await restarted.GetRecentPostTradePnlDriftAsync(10,default));
        Assert.Equal("close-restart",repaired.CloseClientOrderId);
        Assert.Equal("cycle-close-restart",repaired.CloseCycleId);
    }

    [Fact]
    public async Task PersistedPnlDriftIsAppendOnlyAndSourceTamperingFailsRestartReplay()
    {
        var store=Store();
        var open=Intent("open-tamper",false,1m,100m);
        var close=Intent("close-tamper",true,1m,110m);
        var openArtifact=await Bind(store,"cycle-open-tamper",open);
        var closeArtifact=await Bind(store,"cycle-close-tamper",close);
        await SaveSimulation(store,openArtifact,bestBid:99m,bestAsk:100m,bidQuantity:5m,askQuantity:5m);
        await SaveSimulation(store,closeArtifact,bestBid:110m,bestAsk:111m,bidQuantity:5m,askQuantity:5m);
        await store.RecordExecutionAsync(
            "cycle-open-tamper",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close-tamper",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);

        Assert.Single(await store.GetRecentPostTradePnlDriftAsync(10,default));

        await using(var connection=new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            foreach(var sql in new[]
            {
                "UPDATE post_trade_pnl_drift SET state='GrossComparable'",
                "DELETE FROM post_trade_pnl_drift"
            })
            {
                await using var command=connection.CreateCommand();
                command.CommandText=sql;
                await Assert.ThrowsAsync<SqliteException>(()=>command.ExecuteNonQueryAsync());
            }

            await using var tamper=connection.CreateCommand();
            tamper.CommandText="UPDATE trade_outcomes SET gross_pnl='999' WHERE client_order_id='close-tamper'";
            Assert.Equal(1,await tamper.ExecuteNonQueryAsync());
        }

        var restarted=new AgentSqliteStore(Database,()=>Now);
        await Assert.ThrowsAsync<InvalidOperationException>(
            ()=>restarted.GetRecentPostTradePnlDriftAsync(10,default));
    }

    private AgentSqliteStore Store()=>new(Database,()=>Now);

    private async Task<DurableExecutionArtifactV2> Bind(
        AgentSqliteStore store,
        string cycle,
        ExecutionIntent intent)
    {
        var artifact=new DurableExecutionArtifactV2(
            DurableExecutionArtifactV2.Version,
            cycle,
            [new DurableExecutionIntentSnapshotV1(
                0,
                intent.Symbol,
                intent.Side.ToString(),
                intent.Quantity,
                intent.ReduceOnly,
                intent.StopLoss,
                intent.TakeProfit,
                intent.ClientOrderId,
                "automatic.risk-approved",
                intent.Action.ToString(),
                intent.OrderType.ToString(),
                intent.LimitPrice,
                intent.ExpectedPrice)],
            1,
            true,
            "binance",
            "Testnet",
            StrategyId,
            StrategyVersion,
            Now.AddSeconds(-20),
            "market-v1",
            Now.AddSeconds(-10),
            Now.AddMinutes(5));
        Assert.True((await store.SaveAutomaticExecutionAsync(cycle,artifact,default)).Succeeded);
        return artifact;
    }

    private static async Task<(ExecutionSimulationSourceV1 Source,ExecutionSimulationFillV1 Fill)> SaveSimulation(
        AgentSqliteStore store,
        DurableExecutionArtifactV2 artifact,
        decimal bestBid,
        decimal bestAsk,
        decimal bidQuantity,
        decimal askQuantity)
    {
        var qualification=Qualification(artifact);
        var observedAt=Now.AddSeconds(-2);
        var marketPrice=(bestBid+bestAsk)/2m;
        var collectedAt=observedAt.AddSeconds(-1);
        var market=new MarketEvidence(
            artifact.Intents[0].Symbol,
            marketPrice,
            marketPrice-5m,
            marketPrice+5m,
            50,
            0,
            0,
            0,
            new(0,1,1,1,1,1,0),
            collectedAt.UtcDateTime)
        {
            Quality=new(){QualityScore=90,LiquidityScore=0.9,SpreadBps=1,AtrPercent=0.01}
        };
        market=market with
        {
            Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,artifact.ProviderId,"Testnet")
        };
        var book=new RealtimeMarketSnapshot(
            artifact.Intents[0].Symbol,
            marketPrice,
            bestBid,
            bestAsk,
            bidQuantity,
            askQuantity,
            10m,
            9m,
            2m,
            collectedAt.UtcDateTime,
            10,
            true);
        var observation=new AutomaticExecutionSimulationObservationV1(
            true,
            "simulation.source-available",
            artifact.ProviderId,
            "Testnet",
            market,
            new TradingRule(artifact.Intents[0].Symbol,0.001m,0.1m,0.001m,5m,20),
            book,
            observedAt);
        var source=ExecutionSimulationSourceCanonicalizerV1.Create(
            artifact,
            artifact.Intents.Single(),
            qualification,
            observation,
            observedAt);
        Assert.True(await store.SaveExecutionSimulationSourceAsync(source,default));
        var fill=AutomaticExecutionSimulationModelV1.CreateFill(source);
        Assert.True((await store.SaveExecutionSimulationFillAsync(fill,default)).Succeeded);
        return (source,fill);
    }

    private static AutomaticStrategyQualificationEvidenceV1 Qualification(DurableExecutionArtifactV2 artifact)
    {
        var bytes=QualificationBytes(artifact);
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.True(
            AutomaticStrategyQualificationEvidenceVerifierV1.TryParse(bytes,hash,out var value,out var code),
            code);
        return value!;
    }

    private static byte[] QualificationBytes(DurableExecutionArtifactV2 artifact)
    {
        var evaluated=artifact.CreatedAtUtc.AddSeconds(-1);
        var last=evaluated.AddMinutes(-1);
        var first=last.AddMinutes(-(StrategyGovernor.MinimumShadowObservations-1));
        var hashes=Enumerable.Range(0,StrategyGovernor.MinimumShadowObservations)
            .Select(i=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                $"{artifact.StrategyId}|{artifact.StrategyVersion}|{artifact.Intents[0].Symbol}|{i}"))).ToLowerInvariant())
            .ToArray();
        string evidenceSet;
        using(var evidenceStream=new MemoryStream())
        {
            using(var evidenceWriter=new Utf8JsonWriter(evidenceStream))
            {
                evidenceWriter.WriteStartArray();
                foreach(var hash in hashes)evidenceWriter.WriteStringValue(hash);
                evidenceWriter.WriteEndArray();
            }
            evidenceSet=Convert.ToHexString(SHA256.HashData(evidenceStream.ToArray())).ToLowerInvariant();
        }

        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("backtest_validation_sha256",new string('a',64));
            writer.WriteString("environment","Testnet");
            writer.WriteString("evaluated_at_utc",evaluated);
            writer.WriteString("evidence_set_sha256",evidenceSet);
            writer.WritePropertyName("evidence_sha256");
            writer.WriteStartArray();
            foreach(var hash in hashes)writer.WriteStringValue(hash);
            writer.WriteEndArray();
            writer.WriteNumber("failure_streak",0);
            writer.WriteString("first_market_at_utc",first);
            writer.WriteNumber("max_drawdown",0.10d);
            writer.WriteString("last_market_at_utc",last);
            writer.WriteString("market_provider_id",artifact.ProviderId);
            writer.WriteNumber("observation_count",hashes.Length);
            writer.WriteNumber("observation_window_seconds",(last-first).TotalSeconds);
            writer.WriteString("policy_sha256",AutomaticStrategyQualificationEvidenceVerifierV1.ExpectedPolicySha256);
            writer.WriteString("policy_version",AutomaticStrategyQualificationEvidenceVerifierV1.PolicyVersion);
            writer.WriteNumber("quality_score",0.80d);
            writer.WriteBoolean("qualified",true);
            writer.WritePropertyName("reason_codes");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteString("schema",AutomaticStrategyQualificationEvidenceVerifierV1.Schema);
            writer.WriteString("state","ready");
            writer.WriteString("strategy_id",artifact.StrategyId);
            writer.WriteString("strategy_version",artifact.StrategyVersion);
            writer.WriteString("symbol",artifact.Intents[0].Symbol);
            writer.WriteString("timeline_sha256",new string('b',64));
            writer.WriteNumber("expectancy",0.01d);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static async Task SaveUnboundFill(
        AgentSqliteStore store,
        string cycle,
        ExecutionIntent intent,
        decimal averagePrice)
    {
        var costs=ExecutionRealityCostAuthorityV1.Current;
        var fee=averagePrice*intent.Quantity*costs.CommissionRate;
        var fill=ExecutionSimulationFillCanonicalizerV1.Create(
            cycle,
            intent.ClientOrderId,
            StrategyId,
            StrategyVersion,
            costs.Version,
            AutomaticExecutionSimulationModelV1.Version,
            "wpe.venue-rule/1.0:"+new string('c',64),
            intent.Symbol,
            intent.Side,
            intent.ReduceOnly,
            intent.OrderType,
            intent.Quantity,
            ExecutionSimulationFillStateV1.Filled,
            intent.Quantity,
            averagePrice,
            fee,
            ExecutionSimulationFeeRoleV1.Taker,
            false,
            0,
            Now.AddSeconds(-3),
            Now.AddSeconds(-2),
            "unbound-test-fill");
        Assert.True((await store.SaveExecutionSimulationFillAsync(fill,default)).Succeeded);
    }

    private async Task SeedFee(string clientOrderId,decimal fee,DateTimeOffset observedAt)
    {
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="""
            INSERT INTO exchange_order_fee_evidence(
                evidence_id,schema,provider_id,environment,symbol,order_id,client_order_id,
                fill_count,executed_quantity,fee_amount,fee_asset,observed_at,state,
                canonical_sha256,canonical_bytes)
            VALUES(
                $id,'test','binance','Testnet','BTCUSDT',$order,$client,
                1,'1',$fee,'USDT',$observed,'Confirmed',$hash,$bytes);
            """;
        command.Parameters.AddWithValue("$id","fee-"+clientOrderId);
        command.Parameters.AddWithValue("$order","venue-"+clientOrderId);
        command.Parameters.AddWithValue("$client",clientOrderId);
        command.Parameters.AddWithValue("$fee",fee.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$observed",observedAt.ToString("O"));
        command.Parameters.AddWithValue("$hash",new string(clientOrderId.Contains("close",StringComparison.Ordinal)?'b':'a',64));
        command.Parameters.Add("$bytes",SqliteType.Blob).Value=new byte[]{1};
        await command.ExecuteNonQueryAsync();
    }

    private static ExecutionIntent Intent(
        string clientOrderId,
        bool reduceOnly,
        decimal quantity,
        decimal expectedPrice)=>
        new(
            "BTCUSDT",
            PositionSide.Long,
            quantity,
            reduceOnly,
            90m,
            170m,
            clientOrderId,
            "test",
            reduceOnly?DecisionAction.CloseLong:DecisionAction.OpenLong,
            ExecutionOrderType.Market,
            0m,
            expectedPrice);

    private static ExchangeOrder Order(
        ExecutionIntent intent,
        string status,
        decimal quantity,
        decimal price,
        DateTimeOffset updatedAt)=>
        new(
            intent.Symbol,
            "venue-"+intent.ClientOrderId,
            intent.ClientOrderId,
            status,
            quantity,
            price,
            "MARKET",
            intent.Side,
            intent.ReduceOnly,
            updatedAt.UtcDateTime);

    private sealed class BackfillValidator:IAutomaticExecutionReadOnlyValidator
    {
        public Task<AutomaticExecutionRuntimeFacts> ValidateAsync(
            DurableExecutionArtifactV2 artifact,CancellationToken ct)=>
            Task.FromResult(new AutomaticExecutionRuntimeFacts(
                AutomaticFactState.False,AutomaticFactState.False,
                AutomaticFactState.False,AutomaticFactState.False,"backfill-only"));
    }

    private sealed class BackfillGateway:IAutomaticExecutionGateway
    {
        public bool IsTestnet=>true;
        public Task<AutomaticGatewayExecutionResult> ExecuteAsync(
            DurableExecutionArtifactV2 artifact,DeterministicRiskReceipt receipt,CancellationToken ct)=>
            Task.FromResult(new AutomaticGatewayExecutionResult(
                AutomaticGatewayExecutionState.Rejected,"backfill-only"));
        public Task<AutomaticGatewayReconciliationResult> ReconcileAsync(
            DurableExecutionArtifactV2 artifact,CancellationToken ct)=>
            Task.FromResult(new AutomaticGatewayReconciliationResult(
                AutomaticGatewayReconciliationState.Failed,"backfill-only"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))
            Directory.Delete(_directory,true);
    }
}

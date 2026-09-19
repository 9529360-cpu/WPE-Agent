using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using WpeAgent.TradingAuthorization;
using WpeAgent.CrossAssetResearch;
using 币安量化机器人.Core.Strategy;
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
        Assert.Equal("wpe.execution-simulation-source/1.3",source.Schema);
        Assert.Equal(TradingRealityCostAuthorityV1.Identity,source.CostModelVersion);
        Assert.Equal(TradingRealityCostAuthorityV1.Default.CommissionRate,source.CommissionRate);
        Assert.Equal(TradingRealityCostAuthorityV1.Default.SlippageRate,source.SlippageRate);
        Assert.Equal(TradingRealityCostAuthorityV1.Default.FixedCostPerTrade,source.FixedCostPerTrade);
        Assert.Equal(TradingRealityCostAuthorityV1.Default.BorrowRatePerDay,source.BorrowRatePerDay);
        Assert.True(source.StrategyQualificationAvailable);
        Assert.Equal(64,source.StrategyQualificationSha256.Length);
        Assert.Equal(AutomaticStrategyQualificationEvidenceVerifierV1.ExpectedPolicySha256,source.StrategyQualificationPolicySha256);
        Assert.NotEmpty(source.StrategyQualificationCanonicalBytes);
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
    public void TopOfBookDepthCapsMarketFillAndFeeToProvenQuantity()
    {
        var artifact=Artifact(ExecutionOrderType.Market);
        var intent=artifact.Intents.Single();
        var source=ExecutionSimulationSourceCanonicalizerV1.Create(
            artifact,
            intent,
            Qualification(artifact),
            Observation(_clock.Now,100m,bestAsk:101m,askQuantity:0.4m),
            _clock.Now);

        Assert.Equal("wpe.execution-simulation-source/1.3",source.Schema);
        Assert.True(source.TopOfBookAvailable);
        Assert.Equal(101m,source.BestAsk);
        Assert.Equal(0.4m,source.AskQuantity);
        Assert.True(ExecutionSimulationSourceCanonicalizerV1.IsCanonical(source));

        var fill=AutomaticExecutionSimulationModelV1.CreateFill(source);
        var costs=ExecutionRealityCostAuthorityV1.Current;
        var expectedPrice=101m*(1m+costs.SlippageRate);

        Assert.Equal("wpe.execution-simulation-model/1.2",fill.SimulationModelVersion);
        Assert.Equal(ExecutionSimulationFillStateV1.Partial,fill.State);
        Assert.Equal(0.4m,fill.ExecutedQuantity);
        Assert.Equal(expectedPrice,fill.AveragePrice);
        Assert.Equal(expectedPrice*0.4m*costs.CommissionRate,fill.FeeAmount);
        Assert.Equal("top-of-book-partial-taker-cost-authority",fill.ReasonCode);
    }

    [Fact]
    public void CostSourceIsCanonicalAndTamperingFailsClosed()
    {
        var artifact=Artifact(ExecutionOrderType.Market);
        var source=ExecutionSimulationSourceCanonicalizerV1.Create(
            artifact,
            artifact.Intents.Single(),
            Qualification(artifact),
            Observation(_clock.Now,100m),
            _clock.Now);

        var model=TradingRealityCostAuthorityV1.Default;
        Assert.Equal(TradingRealityCostAuthorityV1.IdentityFor(model),source.CostModelVersion);
        Assert.True(TradingRealityCostAuthorityV1.MatchesIdentity(model,source.CostModelVersion));
        Assert.True(ExecutionSimulationSourceCanonicalizerV1.IsCanonical(source));

        Assert.False(ExecutionSimulationSourceCanonicalizerV1.IsCanonical(
            source with { CommissionRate=source.CommissionRate+0.0001m }));
        Assert.False(ExecutionSimulationSourceCanonicalizerV1.IsCanonical(
            source with { CostModelVersion=TradingRealityCostAuthorityV1.Schema+":"+new string('0',64) }));
    }

    [Fact]
    public void PreviousTopOfBookSourceSchemaStillRestartsCanonicallyButCannotBeRepriced()
    {
        var artifact=Artifact(ExecutionOrderType.Market);
        var current=ExecutionSimulationSourceCanonicalizerV1.Create(
            artifact,
            artifact.Intents.Single(),
            Qualification(artifact),
            Observation(_clock.Now,100m),
            _clock.Now);
        var legacyBytes=PreviousSourceBytes(current);
        var legacyHash=Convert.ToHexString(SHA256.HashData(legacyBytes)).ToLowerInvariant();

        Assert.True(
            ExecutionSimulationSourceCanonicalizerV1.TryDeserialize(
                legacyBytes,
                legacyHash,
                out var previous));
        Assert.NotNull(previous);
        Assert.Equal(ExecutionSimulationSourceCanonicalizerV1.PreviousSchema,previous!.Schema);
        Assert.True(previous.TopOfBookAvailable);
        Assert.Equal(string.Empty,previous.CostModelVersion);
        Assert.Equal(0m,previous.CommissionRate);
        Assert.True(ExecutionSimulationSourceCanonicalizerV1.IsCanonical(previous));

        var fill=AutomaticExecutionSimulationModelV1.CreateFill(previous);
        Assert.Equal(ExecutionSimulationFillStateV1.Unsupported,fill.State);
        Assert.Equal("simulation-cost-source-unavailable",fill.ReasonCode);
        Assert.Equal("wpe.trading-reality-cost/1.0:unavailable",fill.CostModelVersion);
    }

    [Fact]
    public void TopOfBookSellSideUsesBidAndZeroDepthDoesNotInventAFill()
    {
        var opening=Artifact(ExecutionOrderType.Market);
        var shortIntent=opening.Intents.Single() with
        {
            Side="Short",
            Action="OpenShort",
            StopLoss=120m,
            TakeProfit=90m
        };
        var shortArtifact=opening with { Intents=[shortIntent] };
        var source=ExecutionSimulationSourceCanonicalizerV1.Create(
            shortArtifact,
            shortIntent,
            Qualification(shortArtifact),
            Observation(_clock.Now,100m,bestBid:99m,bestAsk:101m,bidQuantity:0.6m,askQuantity:5m),
            _clock.Now);
        var fill=AutomaticExecutionSimulationModelV1.CreateFill(source);
        var costs=ExecutionRealityCostAuthorityV1.Current;

        Assert.Equal(ExecutionSimulationFillStateV1.Partial,fill.State);
        Assert.Equal(0.6m,fill.ExecutedQuantity);
        Assert.Equal(99m*(1m-costs.SlippageRate),fill.AveragePrice);

        var emptySource=ExecutionSimulationSourceCanonicalizerV1.Create(
            opening,
            opening.Intents.Single(),
            Qualification(opening),
            Observation(_clock.Now,100m,bestAsk:101m,askQuantity:0m),
            _clock.Now);
        var emptyFill=AutomaticExecutionSimulationModelV1.CreateFill(emptySource);

        Assert.Equal(ExecutionSimulationFillStateV1.NotFilled,emptyFill.State);
        Assert.Equal(0m,emptyFill.ExecutedQuantity);
        Assert.Equal(0m,emptyFill.FeeAmount);
        Assert.Equal(ExecutionSimulationFeeRoleV1.Unavailable,emptyFill.FeeRole);
        Assert.Equal("top-of-book-zero-quantity",emptyFill.ReasonCode);
    }

    [Fact]
    public void MissingOrStaleTopOfBookFailsClosedWithoutDiscardingQualifiedMarketEvidence()
    {
        var artifact=Artifact(ExecutionOrderType.Market);
        var intent=artifact.Intents.Single();
        var missing=ExecutionSimulationSourceCanonicalizerV1.Create(
            artifact,
            intent,
            Qualification(artifact),
            Observation(_clock.Now,100m) with { TopOfBook=null },
            _clock.Now);

        Assert.Equal(ExecutionSimulationSourceStateV1.Available,missing.State);
        Assert.False(missing.TopOfBookAvailable);
        Assert.Equal("top-of-book-unavailable",missing.TopOfBookReasonCode);
        Assert.Equal(
            ExecutionSimulationFillStateV1.Unsupported,
            AutomaticExecutionSimulationModelV1.CreateFill(missing).State);

        var stale=ExecutionSimulationSourceCanonicalizerV1.Create(
            artifact,
            intent,
            Qualification(artifact),
            Observation(_clock.Now,100m,topOfBookAt:_clock.Now.AddSeconds(-16)),
            _clock.Now);

        Assert.Equal(ExecutionSimulationSourceStateV1.Available,stale.State);
        Assert.False(stale.TopOfBookAvailable);
        Assert.Equal("top-of-book-invalid-or-stale",stale.TopOfBookReasonCode);
        var staleFill=AutomaticExecutionSimulationModelV1.CreateFill(stale);
        Assert.Equal(ExecutionSimulationFillStateV1.Unsupported,staleFill.State);
        Assert.Equal("top-of-book-invalid-or-stale",staleFill.ReasonCode);
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
    public async Task MissingStrategyQualificationNeverBlocksAuthorizedMutationButSimulationFailsClosed()
    {
        var store=Store();
        var artifact=Artifact(ExecutionOrderType.Market);
        await ApproveWithoutQualification(store,"exec-no-qualification",artifact);
        var gateway=new EvidenceGateway(_clock)
        {
            Order=new ExchangeOrder(
                "BTCUSDT","venue-no-qualification","WPE-REALITY","FILLED",
                1m,100m,"MARKET",PositionSide.Long,false,_clock.Now.UtcDateTime)
        };
        var processor=new AutomaticExecutionProcessor(store,new Validator(),gateway,()=>_clock.Now);

        var result=await processor.ProcessNextAsync("worker",default);

        Assert.True(result.Handled);
        Assert.Equal("automatic.succeeded",result.Code);
        Assert.Equal(1,gateway.ExecuteCount);
        var source=await store.GetExecutionSimulationSourceAsync(artifact.CorrelationId,"WPE-REALITY",default);
        Assert.NotNull(source);
        Assert.False(source!.StrategyQualificationAvailable);
        Assert.Equal(ExecutionSimulationSourceStateV1.Unavailable,source.State);
        Assert.Equal("strategy-qualification-table-missing",source.ReasonCode);
        var fill=await store.GetExecutionSimulationFillAsync(artifact.CorrelationId,"WPE-REALITY",default);
        Assert.NotNull(fill);
        Assert.Equal(ExecutionSimulationFillStateV1.Unsupported,fill!.State);
        Assert.Equal("strategy-qualification-table-missing",fill.ReasonCode);
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
        var qualification=Qualification(artifact);
        var first=ExecutionSimulationSourceCanonicalizerV1.Create(artifact,intent,qualification,observation,_clock.Now);
        var second=ExecutionSimulationSourceCanonicalizerV1.Create(
            artifact,intent,qualification,Observation(_clock.Now.AddSeconds(1),101m),_clock.Now.AddSeconds(1));

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

        Assert.Equal(TradingRealityCostAuthorityV1.Default.CommissionRate,costs.CommissionRate);
        Assert.Equal(TradingRealityCostAuthorityV1.Default.SlippageRate,costs.SlippageRate);
        Assert.Equal(ExecutionRealityCostAuthorityV1.Version,costs.Version);
        Assert.Equal(TradingRealityCostAuthorityV1.Identity,costs.Version);
        Assert.Equal(TradingRealityCostAuthorityV1.CanonicalSha256For(TradingRealityCostAuthorityV1.Default),TradingRealityCostAuthorityV1.CanonicalSha256);
        Assert.True(TradingRealityCostAuthorityV1.MatchesIdentity(TradingRealityCostAuthorityV1.Default,costs.Version));
    }

    private static byte[] PreviousSourceBytes(ExecutionSimulationSourceV1 current)
    {
        using var document=JsonDocument.Parse(current.CanonicalBytes);
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach(var property in document.RootElement.EnumerateObject())
            {
                if(property.Name is "borrow_rate_per_day" or "commission_rate" or "cost_model_version" or "fixed_cost_per_trade" or "slippage_rate")
                    continue;
                if(property.Name=="schema")
                {
                    writer.WriteString("schema",ExecutionSimulationSourceCanonicalizerV1.PreviousSchema);
                    continue;
                }
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private AgentSqliteStore Store()=>new(Database,()=>_clock.Now);

    private async Task Approve(AgentSqliteStore store,string id,DurableExecutionArtifactV2 artifact)
    {
        await SeedQualification(artifact);
        await ApproveWithoutQualification(store,id,artifact);
    }

    private async Task ApproveWithoutQualification(AgentSqliteStore store,string id,DurableExecutionArtifactV2 artifact)
    {
        Assert.True((await store.SaveAutomaticExecutionAsync(id,artifact,default)).Succeeded);
        Assert.True((await store.RecordAutomaticRiskDecisionAsync(id,Receipt(artifact),default)).Succeeded);
    }

    private AutomaticStrategyQualificationEvidenceV1 Qualification(DurableExecutionArtifactV2 artifact)
    {
        var bytes=QualificationBytes(artifact);
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.True(AutomaticStrategyQualificationEvidenceVerifierV1.TryParse(bytes,hash,out var value,out var code),code);
        return value!;
    }

    private async Task SeedQualification(DurableExecutionArtifactV2 artifact)
    {
        var value=Qualification(artifact);
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS strategy_shadow_qualification_decisions(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                market_provider_id TEXT NOT NULL,
                environment TEXT NOT NULL CHECK(environment='Testnet'),
                backtest_validation_sha256 TEXT NOT NULL,
                timeline_sha256 TEXT NOT NULL,
                policy_version TEXT NOT NULL,
                policy_sha256 TEXT NOT NULL,
                state TEXT NOT NULL,
                qualified INTEGER NOT NULL CHECK(qualified IN (0,1)),
                observation_count INTEGER NOT NULL CHECK(observation_count>=0),
                first_market_at TEXT NOT NULL,
                last_market_at TEXT NOT NULL,
                evaluated_at TEXT NOT NULL,
                evidence_set_sha256 TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(strategy_id,strategy_version,evidence_set_sha256,policy_sha256));
            INSERT INTO strategy_shadow_qualification_decisions(
                canonical_sha256,schema,strategy_id,strategy_version,symbol,
                market_provider_id,environment,backtest_validation_sha256,timeline_sha256,
                policy_version,policy_sha256,state,qualified,observation_count,
                first_market_at,last_market_at,evaluated_at,evidence_set_sha256,canonical_bytes)
            VALUES(
                $hash,$schema,$strategy,$version,$symbol,
                $provider,$environment,$validation,$timeline,
                $policyVersion,$policyHash,'Ready',1,$count,
                $first,$last,$evaluated,$evidenceSet,$bytes);
            """;
        command.Parameters.AddWithValue("$hash",value.CanonicalSha256);
        command.Parameters.AddWithValue("$schema",AutomaticStrategyQualificationEvidenceVerifierV1.Schema);
        command.Parameters.AddWithValue("$strategy",value.StrategyId);
        command.Parameters.AddWithValue("$version",value.StrategyVersion);
        command.Parameters.AddWithValue("$symbol",value.Symbol);
        command.Parameters.AddWithValue("$provider",value.MarketProviderId);
        command.Parameters.AddWithValue("$environment",value.Environment);
        command.Parameters.AddWithValue("$validation",value.BacktestValidationSha256);
        command.Parameters.AddWithValue("$timeline",value.TimelineSha256);
        command.Parameters.AddWithValue("$policyVersion",value.PolicyVersion);
        command.Parameters.AddWithValue("$policyHash",value.PolicySha256);
        command.Parameters.AddWithValue("$count",value.ObservationCount);
        command.Parameters.AddWithValue("$first",value.FirstMarketAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$last",value.LastMarketAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$evaluated",value.EvaluatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$evidenceSet",value.EvidenceSetSha256);
        command.Parameters.AddWithValue("$bytes",value.CanonicalBytes);
        await command.ExecuteNonQueryAsync();
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
            writer.WriteNumber("max_drawdown",.10d);
            writer.WriteString("last_market_at_utc",last);
            writer.WriteString("market_provider_id",artifact.ProviderId);
            writer.WriteNumber("observation_count",hashes.Length);
            writer.WriteNumber("observation_window_seconds",(last-first).TotalSeconds);
            writer.WriteString("policy_sha256",AutomaticStrategyQualificationEvidenceVerifierV1.ExpectedPolicySha256);
            writer.WriteString("policy_version",AutomaticStrategyQualificationEvidenceVerifierV1.PolicyVersion);
            writer.WriteNumber("quality_score",.80d);
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
            writer.WriteNumber("expectancy",.01d);
            writer.WriteEndObject();
        }
        return stream.ToArray();
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
        decimal price,
        decimal bestBid=99m,
        decimal bestAsk=100m,
        decimal bidQuantity=5m,
        decimal askQuantity=5m,
        DateTimeOffset? topOfBookAt=null)
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
        var bookTime=topOfBookAt??collected;
        var topOfBook=new RealtimeMarketSnapshot(
            "BTCUSDT",
            price,
            bestBid,
            bestAsk,
            bidQuantity,
            askQuantity,
            10m,
            9m,
            2m,
            bookTime.UtcDateTime,
            10,
            true);
        return new(
            true,
            "simulation.source-available",
            "binance",
            "Testnet",
            market,
            new TradingRule("BTCUSDT",.001m,.1m,.001m,5m,20),
            topOfBook,
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

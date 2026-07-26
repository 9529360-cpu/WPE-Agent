using Microsoft.Data.Sqlite;
using WpeAgent.ModelOff;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffExecutionObservationWriterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 15, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-execution-observation-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "agent.db");
    public ModelOffExecutionObservationWriterTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_directory, true); } catch (IOException) { } }

    [Fact]
    public async Task SucceededExecutionPersistsCanonicalExecutionRecoveryAndAudit()
    {
        var store = Store();
        var item = await Succeeded(store, "observed-success");
        await SeedConfirmedIntent(store, item.Artifact!);
        var events = await store.GetAutomaticExecutionEventsAsync(item.ExecutionId, 100, CancellationToken.None);
        var result = await new ModelOffExecutionObservationWriterV1(store).WriteAsync(item, events, Evidence(item), Now, CancellationToken.None);

        Assert.True(result.Persisted);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(result.Execution));
        Assert.Equal("no_recovery_required", result.Recovery.Decision.Action);
        Assert.False(result.Recovery.Facts.GetProperty("resubmit_allowed").GetBoolean());
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(result.Audit));
        Assert.Equal(3, (await store.GetModelOffCanonicalAuditsAsync(item.ExecutionId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task UnknownOutcomeIsQuarantinedAndNeverResubmitted()
    {
        var store = Store();
        var item = await Executing(store, "observed-unknown");
        Assert.True((await store.TryTransitionAutomaticExecutionAsync(item.ExecutionId,
            AutomaticExecutionQueueStatus.Executing, AutomaticExecutionQueueStatus.UnknownOutcome,
            "worker", "automatic.unknown-outcome", CancellationToken.None)).Succeeded);
        item = (await store.GetAutomaticExecutionAsync(item.ExecutionId, CancellationToken.None))!;
        var events = await store.GetAutomaticExecutionEventsAsync(item.ExecutionId, 100, CancellationToken.None);
        var result = await new ModelOffExecutionObservationWriterV1(store).WriteAsync(item, events, Evidence(item), Now, CancellationToken.None);

        Assert.True(result.Persisted);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(result.Execution));
        Assert.Equal("quarantine_pending_reconciliation", result.Recovery.Decision.Action);
        Assert.True(result.Recovery.Facts.GetProperty("quarantined").GetBoolean());
        Assert.False(result.Recovery.Facts.GetProperty("resubmit_allowed").GetBoolean());
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(result.Audit));
    }

    [Fact]
    public async Task QueueSuccessWithoutConfirmedOrderIntentIsQuarantined()
    {
        var store = Store();
        var item = await Succeeded(store, "observed-missing-order");
        var events = await store.GetAutomaticExecutionEventsAsync(item.ExecutionId, 100, CancellationToken.None);
        var result = await new ModelOffExecutionObservationWriterV1(store).WriteAsync(item, events, Evidence(item), Now, CancellationToken.None);

        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(result.Execution));
        Assert.Contains("execution.intent-missing", result.Execution.Decision.ReasonCodes);
        Assert.Equal("quarantine_order_correlation", result.Recovery.Decision.Action);
        Assert.True(result.Recovery.Facts.GetProperty("quarantined").GetBoolean());
        Assert.False(result.Recovery.Facts.GetProperty("resubmit_allowed").GetBoolean());
    }

    [Fact]
    public async Task ConflictingPersistedIntentCannotConfirmQueueSuccess()
    {
        var store = Store();
        var item = await Succeeded(store, "observed-conflicting-order");
        var expected = Assert.Single(item.Artifact!.Intents);
        var conflicting = new ExecutionIntent("ETHUSDT", PositionSide.Short, expected.Quantity * 2,
            expected.ReduceOnly, expected.StopLoss, expected.TakeProfit, expected.ClientOrderId, "conflicting",
            DecisionAction.OpenShort, ExecutionOrderType.Limit, expected.LimitPrice, expected.ExpectedPrice);
        await store.SaveIntentAsync(item.Artifact.CorrelationId, conflicting, "PROTECTED", "order-conflicting", CancellationToken.None);
        var events = await store.GetAutomaticExecutionEventsAsync(item.ExecutionId, 100, CancellationToken.None);

        var result = await new ModelOffExecutionObservationWriterV1(store).WriteAsync(item, events, Evidence(item), Now, CancellationToken.None);

        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(result.Execution));
        Assert.Contains("execution.intent-conflicting", result.Execution.Decision.ReasonCodes);
        Assert.Equal("quarantine_order_correlation", result.Recovery.Decision.Action);
        Assert.False(result.Recovery.Facts.GetProperty("resubmit_allowed").GetBoolean());
    }

    [Theory]
    [InlineData(ModelOffExchangeOrderEvidenceStateV1.Missing, "execution.exchange-order-missing")]
    [InlineData(ModelOffExchangeOrderEvidenceStateV1.Conflicting, "execution.exchange-order-conflicting")]
    [InlineData(ModelOffExchangeOrderEvidenceStateV1.Unknown, "execution.exchange-observation-unknown")]
    public async Task ExchangeEvidenceMustConfirmPersistedSuccess(
        ModelOffExchangeOrderEvidenceStateV1 state,string reason)
    {
        var store=Store();var item=await Succeeded(store,"observed-exchange-"+state.ToString().ToLowerInvariant());
        await SeedConfirmedIntent(store,item.Artifact!);
        var events=await store.GetAutomaticExecutionEventsAsync(item.ExecutionId,100,CancellationToken.None);

        var result=await new ModelOffExecutionObservationWriterV1(store).WriteAsync(
            item,events,Evidence(item,state),Now,CancellationToken.None);

        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(result.Execution));
        Assert.Contains(reason,result.Execution.Decision.ReasonCodes);
        Assert.Equal("quarantine_order_correlation",result.Recovery.Decision.Action);
        Assert.False(result.Recovery.Facts.GetProperty("resubmit_allowed").GetBoolean());
    }

    [Fact]
    public async Task TamperedExchangeEvidenceHashCannotConfirmExecution()
    {
        var store=Store();var item=await Succeeded(store,"observed-exchange-tampered");
        await SeedConfirmedIntent(store,item.Artifact!);
        var evidence=Evidence(item) with{EvidenceSha256="sha256:"+new string('a',64)};
        var events=await store.GetAutomaticExecutionEventsAsync(item.ExecutionId,100,CancellationToken.None);

        var result=await new ModelOffExecutionObservationWriterV1(store).WriteAsync(item,events,evidence,Now,CancellationToken.None);

        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(result.Execution));
        Assert.Contains("execution.exchange-observation-unknown",result.Execution.Decision.ReasonCodes);
        Assert.Equal("quarantine_order_correlation",result.Recovery.Decision.Action);
    }

    [Fact]
    public async Task RepeatedObservationIsIdempotentAndLaterStateAppendsNewIdentity()
    {
        var store = Store();
        var executing = await Executing(store, "observed-history");
        var writer = new ModelOffExecutionObservationWriterV1(store);
        var events = await store.GetAutomaticExecutionEventsAsync(executing.ExecutionId, 100, CancellationToken.None);
        Assert.True((await writer.WriteAsync(executing, events, Evidence(executing), Now, CancellationToken.None)).Persisted);
        Assert.True((await writer.WriteAsync(executing, events.Reverse().ToArray(), Evidence(executing), Now, CancellationToken.None)).Persisted);
        Assert.True((await store.TryTransitionAutomaticExecutionAsync(executing.ExecutionId,
            AutomaticExecutionQueueStatus.Executing, AutomaticExecutionQueueStatus.Succeeded,
            "worker", "automatic.succeeded", CancellationToken.None)).Succeeded);
        var succeeded = (await store.GetAutomaticExecutionAsync(executing.ExecutionId, CancellationToken.None))!;
        events = await store.GetAutomaticExecutionEventsAsync(succeeded.ExecutionId, 100, CancellationToken.None);
        Assert.True((await writer.WriteAsync(succeeded, events, Evidence(succeeded), Now, CancellationToken.None)).Persisted);
        Assert.Equal(6, (await store.GetModelOffCanonicalAuditsAsync(executing.ExecutionId, CancellationToken.None)).Count);
    }

    [Fact]
    public void ProcessorObservationCannotCallGatewayOrExecutor()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Services", "Agent", "ModelOffExecutionObservationWriterV1.cs"));
        Assert.DoesNotContain("ExecuteAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReconcileAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReliableOrderExecutor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TradingExecutionGateway", source, StringComparison.Ordinal);
        var processor = File.ReadAllText(Path.Combine(ProjectRoot(), "Services", "Agent", "AutomaticExecutionProcessor.cs"));
        Assert.Contains("new ModelOffExecutionObservationWriterV1(_store).WriteAsync", processor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionProcessorAutomaticallyPersistsThreeAgentObservations()
    {
        var store = Store();
        var artifact = Artifact("processor-observed");
        await SeedConfirmedIntent(store, artifact);
        Assert.True((await store.SaveAutomaticExecutionAsync(artifact.CorrelationId, artifact, CancellationToken.None)).Succeeded);
        Assert.True((await store.RecordAutomaticRiskDecisionAsync(artifact.CorrelationId, Receipt(artifact), CancellationToken.None)).Succeeded);
        var processor = new AutomaticExecutionProcessor(store, new ValidRuntime(), new SuccessfulGateway(), () => Now);

        var processed = await processor.ProcessNextAsync("production-worker", CancellationToken.None);

        Assert.True(processed.Handled);
        Assert.Equal("automatic.succeeded", processed.Code);
        var audits = await store.GetModelOffCanonicalAuditsAsync(artifact.CorrelationId, CancellationToken.None);
        Assert.Equal(3, audits.Count);
        Assert.Equal(new[] { "audit", "execution", "recovery" }, audits.Select(x => x.OutputKind).Order(StringComparer.Ordinal));
    }

    private AgentSqliteStore Store() => new(DatabasePath, () => Now);
    private static DurableExecutionArtifactV2 Artifact(string id) => new(2, id,
        [new(0, "BTCUSDT", "Long", .01m, false, 98_000m, 104_000m, "WPE-OBS", "strategy.entry", "OpenLong", "Limit", 100_000m, 100_100m)],
        3, true, "binance", "Testnet", "strategy", "v1", Now.AddSeconds(-20), "market-v1", Now.AddSeconds(-10), Now.AddMinutes(2));
    private static DeterministicRiskReceipt Receipt(DurableExecutionArtifactV2 artifact)
    { var hashes = DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact); return new("risk-" + artifact.CorrelationId,
        artifact.CorrelationId, hashes.IntentHash, true, Now.AddSeconds(-1), Now.AddMinutes(1), null, hashes.ArtifactHash); }
    private static async Task<PersistedAutomaticExecution> Executing(AgentSqliteStore store, string id)
    {
        var artifact = Artifact(id);
        Assert.True((await store.SaveAutomaticExecutionAsync(id, artifact, CancellationToken.None)).Succeeded);
        Assert.True((await store.RecordAutomaticRiskDecisionAsync(id, Receipt(artifact), CancellationToken.None)).Succeeded);
        Assert.True((await store.TryClaimAutomaticExecutionAsync(id, "worker", TimeSpan.FromSeconds(30), CancellationToken.None)).Claimed);
        Assert.True((await store.TryTransitionAutomaticExecutionAsync(id, AutomaticExecutionQueueStatus.Claimed,
            AutomaticExecutionQueueStatus.Executing, "worker", "automatic.executing", CancellationToken.None)).Succeeded);
        return (await store.GetAutomaticExecutionAsync(id, CancellationToken.None))!;
    }
    private static async Task<PersistedAutomaticExecution> Succeeded(AgentSqliteStore store, string id)
    {
        var item = await Executing(store, id);
        Assert.True((await store.TryTransitionAutomaticExecutionAsync(id, AutomaticExecutionQueueStatus.Executing,
            AutomaticExecutionQueueStatus.Succeeded, "worker", "automatic.succeeded", CancellationToken.None)).Succeeded);
        return (await store.GetAutomaticExecutionAsync(id, CancellationToken.None))!;
    }
    private static Task SeedConfirmedIntent(AgentSqliteStore store, DurableExecutionArtifactV2 artifact)
    {
        var value = Assert.Single(artifact.Intents);
        var intent = new ExecutionIntent(value.Symbol, Enum.Parse<PositionSide>(value.Side), value.Quantity,
            value.ReduceOnly, value.StopLoss, value.TakeProfit, value.ClientOrderId, "confirmed", Enum.Parse<DecisionAction>(value.Action),
            Enum.Parse<ExecutionOrderType>(value.OrderType), value.LimitPrice, value.ExpectedPrice);
        return store.SaveIntentAsync(artifact.CorrelationId, intent, value.ReduceOnly ? "COMPLETED" : "PROTECTED", "order-confirmed", CancellationToken.None);
    }
    private static string ProjectRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private sealed class ValidRuntime : IAutomaticExecutionReadOnlyValidator
    {
        public Task<AutomaticExecutionRuntimeFacts> ValidateAsync(DurableExecutionArtifactV2 artifact, CancellationToken ct) =>
            Task.FromResult(new AutomaticExecutionRuntimeFacts(AutomaticFactState.True, AutomaticFactState.True,
                AutomaticFactState.True, AutomaticFactState.True, "automatic.runtime-ready"));
    }

    private static ModelOffExchangeOrderEvidenceV1 Evidence(PersistedAutomaticExecution item,
        ModelOffExchangeOrderEvidenceStateV1 state=ModelOffExchangeOrderEvidenceStateV1.Confirmed)
    {
        var expected=item.Artifact?.Intents.Count??0;
        var entries=item.Artifact?.Intents.OrderBy(x=>x.Sequence).Select(x=>new ModelOffExchangeOrderEvidenceEntryV1(
            x.Sequence,state==ModelOffExchangeOrderEvidenceStateV1.Confirmed?"confirmed":state.ToString().ToLowerInvariant(),
            state==ModelOffExchangeOrderEvidenceStateV1.Confirmed?"FILLED":"none",
            state==ModelOffExchangeOrderEvidenceStateV1.Confirmed?x.Quantity:0)).ToArray()??[];
        return ModelOffExchangeOrderEvidenceContractV1.Create(item.ExecutionId,state,expected,
            state==ModelOffExchangeOrderEvidenceStateV1.Confirmed?expected:0,Now,entries);
    }

    private sealed class SuccessfulGateway : IAutomaticExecutionGateway, IAutomaticExecutionOrderEvidenceReader
    {
        public bool IsTestnet => true;
        public Task<AutomaticGatewayExecutionResult> ExecuteAsync(DurableExecutionArtifactV2 artifact,
            DeterministicRiskReceipt receipt, CancellationToken ct) =>
            Task.FromResult(new AutomaticGatewayExecutionResult(AutomaticGatewayExecutionState.Succeeded, "automatic.succeeded"));
        public Task<AutomaticGatewayReconciliationResult> ReconcileAsync(DurableExecutionArtifactV2 artifact,
            CancellationToken ct) =>
            Task.FromResult(new AutomaticGatewayReconciliationResult(AutomaticGatewayReconciliationState.Succeeded, "automatic.reconcile-succeeded"));
        public Task<ModelOffExchangeOrderEvidenceV1> ObserveOrdersAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct) =>
            Task.FromResult(ModelOffExchangeOrderEvidenceContractV1.Create(artifact.CorrelationId,
                ModelOffExchangeOrderEvidenceStateV1.Confirmed,artifact.Intents.Count,artifact.Intents.Count,Now,
                artifact.Intents.Select(x=>new ModelOffExchangeOrderEvidenceEntryV1(x.Sequence,"confirmed","FILLED",x.Quantity)).ToArray()));
    }
}

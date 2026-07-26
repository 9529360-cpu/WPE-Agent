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
        var events = await store.GetAutomaticExecutionEventsAsync(item.ExecutionId, 100, CancellationToken.None);
        var result = await new ModelOffExecutionObservationWriterV1(store).WriteAsync(item, events, Now, CancellationToken.None);

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
        var result = await new ModelOffExecutionObservationWriterV1(store).WriteAsync(item, events, Now, CancellationToken.None);

        Assert.True(result.Persisted);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(result.Execution));
        Assert.Equal("quarantine_pending_reconciliation", result.Recovery.Decision.Action);
        Assert.True(result.Recovery.Facts.GetProperty("quarantined").GetBoolean());
        Assert.False(result.Recovery.Facts.GetProperty("resubmit_allowed").GetBoolean());
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(result.Audit));
    }

    [Fact]
    public async Task RepeatedObservationIsIdempotentAndLaterStateAppendsNewIdentity()
    {
        var store = Store();
        var executing = await Executing(store, "observed-history");
        var writer = new ModelOffExecutionObservationWriterV1(store);
        var events = await store.GetAutomaticExecutionEventsAsync(executing.ExecutionId, 100, CancellationToken.None);
        Assert.True((await writer.WriteAsync(executing, events, Now, CancellationToken.None)).Persisted);
        Assert.True((await writer.WriteAsync(executing, events.Reverse().ToArray(), Now, CancellationToken.None)).Persisted);
        Assert.True((await store.TryTransitionAutomaticExecutionAsync(executing.ExecutionId,
            AutomaticExecutionQueueStatus.Executing, AutomaticExecutionQueueStatus.Succeeded,
            "worker", "automatic.succeeded", CancellationToken.None)).Succeeded);
        var succeeded = (await store.GetAutomaticExecutionAsync(executing.ExecutionId, CancellationToken.None))!;
        events = await store.GetAutomaticExecutionEventsAsync(succeeded.ExecutionId, 100, CancellationToken.None);
        Assert.True((await writer.WriteAsync(succeeded, events, Now, CancellationToken.None)).Persisted);
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
    private static string ProjectRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private sealed class ValidRuntime : IAutomaticExecutionReadOnlyValidator
    {
        public Task<AutomaticExecutionRuntimeFacts> ValidateAsync(DurableExecutionArtifactV2 artifact, CancellationToken ct) =>
            Task.FromResult(new AutomaticExecutionRuntimeFacts(AutomaticFactState.True, AutomaticFactState.True,
                AutomaticFactState.True, AutomaticFactState.True, "automatic.runtime-ready"));
    }

    private sealed class SuccessfulGateway : IAutomaticExecutionGateway
    {
        public bool IsTestnet => true;
        public Task<AutomaticGatewayExecutionResult> ExecuteAsync(DurableExecutionArtifactV2 artifact,
            DeterministicRiskReceipt receipt, CancellationToken ct) =>
            Task.FromResult(new AutomaticGatewayExecutionResult(AutomaticGatewayExecutionState.Succeeded, "automatic.succeeded"));
        public Task<AutomaticGatewayReconciliationResult> ReconcileAsync(DurableExecutionArtifactV2 artifact,
            CancellationToken ct) =>
            Task.FromResult(new AutomaticGatewayReconciliationResult(AutomaticGatewayReconciliationState.Succeeded, "automatic.reconcile-succeeded"));
    }
}

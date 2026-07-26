using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingReviewRestartRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset InitialNow = new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-review-restart-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(InitialNow);
    private string DatabasePath => Path.Combine(_directory, "agent.db");

    [Fact]
    public async Task ApprovedReview_SurvivesRestartAndExecutesExactlyOnce()
    {
        var executor = new RecordingExecutor();
        var reconciler = new RecordingReconciler();
        var requestId = await SaveAndApproveAsync("approved");

        SqliteConnection.ClearAllPools();
        var restarted = Runtime(executor, reconciler);
        var first = await restarted.Processor.ProcessNextApprovedAsync("worker-after-restart", CancellationToken.None);
        var replay = await Runtime(executor, reconciler).Processor.ProcessNextApprovedAsync("worker-replay", CancellationToken.None);

        Assert.Equal("review.succeeded", first.Code);
        Assert.Equal("review.no-approved-work", replay.Code);
        Assert.Equal(1, executor.MutationCount);
        Assert.Equal(TradingReviewQueueStatus.Succeeded, (await ItemAsync(requestId))!.Status);
    }

    [Fact]
    public async Task ClaimedReview_WithExpiredLease_IsClaimedIdempotentlyAfterRestart()
    {
        var executor = new RecordingExecutor();
        var reconciler = new RecordingReconciler();
        var requestId = await SaveAndApproveAsync("claimed");
        var beforeCrash = Store();
        Assert.True((await beforeCrash.TryClaimTradingReviewAsync(requestId, "crashed-worker", TimeSpan.FromSeconds(30), CancellationToken.None)).Claimed);
        await ExpireLeaseAsync(requestId);

        SqliteConnection.ClearAllPools();
        var restarted = Runtime(executor, reconciler);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            restarted.Processor.ProcessNextApprovedAsync($"restart-worker-{index}", CancellationToken.None)));

        Assert.Equal(1, results.Count(value => value.Code == "review.succeeded"));
        Assert.Equal(1, executor.MutationCount);
        var item = await ItemAsync(requestId);
        Assert.Equal(TradingReviewQueueStatus.Succeeded, item!.Status);
        Assert.Equal(2, item.AttemptCount);
    }

    [Fact]
    public async Task AmbiguousExecution_AfterRestartReconcilesWithoutResubmission()
    {
        var executor = new RecordingExecutor { ThrowAfterMutation = true };
        var reconciler = new RecordingReconciler { Result = new(DurableReviewReconciliationState.Succeeded, "found") };
        var requestId = await SaveAndApproveAsync("ambiguous");
        var beforeCrash = Runtime(executor, reconciler);
        var ambiguous = await beforeCrash.Processor.ProcessNextApprovedAsync("crashed-worker", CancellationToken.None);
        Assert.Equal("review.execution-ambiguous", ambiguous.Code);
        await ExpireLeaseAsync(requestId);

        SqliteConnection.ClearAllPools();
        executor.ThrowAfterMutation = false;
        var recovered = await Runtime(executor, reconciler).Processor.ReconcileNextAsync("recovery-worker", CancellationToken.None);

        Assert.Equal("review.reconcile-succeeded", recovered.Code);
        Assert.Equal(1, executor.MutationCount);
        Assert.Equal(1, reconciler.CallCount);
        Assert.Equal(new[] { "WPE-RESTART-1", "WPE-RESTART-2" }, reconciler.ClientOrderIds);
        Assert.Equal(TradingReviewQueueStatus.Succeeded, (await ItemAsync(requestId))!.Status);
    }

    [Fact]
    public async Task ExpiredApprovedReview_AfterRestartNeverExecutes()
    {
        var executor = new RecordingExecutor();
        var reconciler = new RecordingReconciler();
        var requestId = await SaveAndApproveAsync("expired");
        _clock.Now = InitialNow.AddMinutes(10);

        SqliteConnection.ClearAllPools();
        var result = await Runtime(executor, reconciler).Processor.ProcessNextApprovedAsync("late-worker", CancellationToken.None);

        Assert.False(result.Handled);
        Assert.Equal("review.claim-unavailable", result.Code);
        Assert.Equal(0, executor.MutationCount);
        Assert.Equal(0, reconciler.CallCount);
        Assert.Equal(TradingReviewQueueStatus.Approved, (await ItemAsync(requestId))!.Status);
    }

    [Fact]
    public async Task TerminalReview_IsNotReplayedAfterRestart()
    {
        var executor = new RecordingExecutor();
        var reconciler = new RecordingReconciler();
        var requestId = await SaveAndApproveAsync("terminal");
        Assert.Equal("review.succeeded", (await Runtime(executor, reconciler).Processor.ProcessNextApprovedAsync("first-worker", CancellationToken.None)).Code);

        SqliteConnection.ClearAllPools();
        var restarted = Runtime(executor, reconciler);
        var process = await restarted.Processor.ProcessNextApprovedAsync("restart-worker", CancellationToken.None);
        var reconcile = await restarted.Processor.ReconcileNextAsync("restart-worker", CancellationToken.None);

        Assert.Equal("review.no-approved-work", process.Code);
        Assert.Equal("review.no-reconciliation-work", reconcile.Code);
        Assert.Equal(1, executor.MutationCount);
        Assert.Equal(0, reconciler.CallCount);
        Assert.Equal(TradingReviewQueueStatus.Succeeded, (await ItemAsync(requestId))!.Status);
    }

    [Fact]
    public async Task ConcurrentRestartReconcilers_ReconcileAmbiguousReviewAtMostOnce()
    {
        var executor = new RecordingExecutor { ThrowAfterMutation = true };
        var reconciler = new RecordingReconciler { Result = new(DurableReviewReconciliationState.Unknown, "unknown") };
        var requestId = await SaveAndApproveAsync("reconcile-once");
        Assert.Equal("review.execution-ambiguous", (await Runtime(executor, reconciler).Processor.ProcessNextApprovedAsync("crashed-worker", CancellationToken.None)).Code);
        await ExpireLeaseAsync(requestId);

        SqliteConnection.ClearAllPools();
        var processors = Enumerable.Range(0, 12).Select(_ => Runtime(executor, reconciler).Processor).ToArray();
        var results = await Task.WhenAll(processors.Select((processor, index) =>
            processor.ReconcileNextAsync($"reconciler-{index}", CancellationToken.None)));

        Assert.Equal(1, results.Count(value => value.Handled));
        Assert.Equal(1, reconciler.CallCount);
        Assert.Equal(1, executor.MutationCount);
        Assert.Equal(TradingReviewQueueStatus.PolicyBlocked, (await ItemAsync(requestId))!.Status);
    }

    private async Task<string> SaveAndApproveAsync(string suffix)
    {
        var artifact = Artifact();
        var hashes = DurableReviewArtifactCanonicalizer.ComputeHashes(artifact);
        var requestId = "request-restart-" + suffix;
        var request = new TradingApprovalRequest(requestId, TradingAuthorizationMode.Review, "cycle-restart-" + suffix,
            hashes.IntentHash, "user", "device", "session", artifact.CreatedAtUtc, artifact.ExpiresAtUtc,
            ArtifactHash: hashes.ArtifactHash);
        var store = Store();
        Assert.True((await store.SaveTradingReviewQueueAsync(request, artifact, CancellationToken.None)).Succeeded);
        var approval = await new TradingReviewApprovalService(store, new ContextProvider()).HandleAsync(
            new(requestId, TradingReviewApprovalAction.Approve, "operator approval"), CancellationToken.None);
        Assert.True(approval.Applied);
        return requestId;
    }

    private RuntimeState Runtime(RecordingExecutor executor, RecordingReconciler reconciler)
    {
        var store = Store();
        var gateway = new TradingExecutionGateway(executor, store, () => _clock.Now);
        var processor = new TradingReviewExecutionProcessor(store, gateway, new RuntimeValidator(), new RiskValidator(_clock), reconciler, () => _clock.Now);
        return new(store, processor);
    }

    private AgentSqliteStore Store() => new(DatabasePath, () => _clock.Now);

    private Task<PersistedTradingReviewQueueItem?> ItemAsync(string requestId) =>
        Store().GetTradingReviewQueueItemAsync(requestId, CancellationToken.None);

    private async Task ExpireLeaseAsync(string requestId)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trading_review_execution_queue SET lease_expires_at=$expired WHERE request_id=$request";
        command.Parameters.AddWithValue("$expired", _clock.Now.AddSeconds(-1).ToString("O"));
        command.Parameters.AddWithValue("$request", requestId);
        await command.ExecuteNonQueryAsync();
    }

    private static DurableReviewExecutionArtifactV1 Artifact() => new(
        DurableReviewExecutionArtifactV1.Version,
        [
            new(0, "BTCUSDT", "Long", .01m, false, 49_000m, 53_000m, "WPE-RESTART-1", "strategy.entry", "OpenLong", "Limit", 50_000m, 50_100m),
            new(1, "ETHUSDT", "Short", .2m, false, 2_650m, 2_400m, "WPE-RESTART-2", "strategy.hedge", "OpenShort", "Market", 0m, 2_500m)
        ],
        5, true, "binance", "Testnet", "strategy-alpha", "v1", InitialNow.AddMinutes(-2), "book-v4",
        InitialNow.AddMinutes(-1), InitialNow.AddMinutes(5));

    private sealed class TestClock(DateTimeOffset now) { public DateTimeOffset Now { get; set; } = now; }

    private sealed class ContextProvider : ITrustedTradingReviewContextProvider
    {
        public ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct) =>
            ValueTask.FromResult<TrustedTradingReviewContext?>(new("user", "device", "session", TradingAuthorizationMode.Review, true, "binance"));
    }

    private sealed class RuntimeValidator : ITradingReviewRuntimeValidator
    {
        public Task<TradingReviewRuntimeValidation> ValidateAsync(TradingApprovalRequest request, DurableReviewExecutionArtifactV1 artifact, CancellationToken ct) =>
            Task.FromResult(new TradingReviewRuntimeValidation(true, "review.runtime-valid"));
    }

    private sealed class RiskValidator(TestClock clock) : ITradingReviewRiskValidator
    {
        public Task<TradingReviewRiskValidation> ValidateAsync(TradingApprovalRequest request, DurableReviewExecutionArtifactV1 artifact, DurableReviewArtifactHashes hashes, CancellationToken ct) =>
            Task.FromResult(new TradingReviewRiskValidation(true, "review.risk-valid", new DeterministicRiskReceipt(
                "risk-" + request.RequestId, request.CorrelationId, hashes.IntentHash, true, clock.Now, clock.Now.AddMinutes(1), ArtifactHash: hashes.ArtifactHash)));
    }

    private sealed class RecordingExecutor : ITradingMutationExecutor
    {
        private int _mutationCount;
        public bool IsTestnet => true;
        public bool ThrowAfterMutation { get; set; }
        public int MutationCount => Volatile.Read(ref _mutationCount);
        public Task<string> ExecutePlanAsync(string correlationId, IReadOnlyList<ExecutionIntent> intents, int leverage, bool isolated, CancellationToken ct)
        {
            Interlocked.Increment(ref _mutationCount);
            if (ThrowAfterMutation) throw new InvalidOperationException("submission outcome unknown");
            return Task.FromResult("submitted");
        }
    }

    private sealed class RecordingReconciler : IDurableReviewExecutionReconciler
    {
        private int _callCount;
        public DurableReviewReconciliationResult Result { get; set; } = new(DurableReviewReconciliationState.Succeeded, "found");
        public int CallCount => Volatile.Read(ref _callCount);
        public string[] ClientOrderIds { get; private set; } = [];
        public Task<DurableReviewReconciliationResult> ReconcileAsync(DurableReviewExecutionArtifactV1 artifact, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            ClientOrderIds = artifact.Intents.Select(value => value.ClientOrderId).ToArray();
            return Task.FromResult(Result);
        }
    }

    private sealed record RuntimeState(AgentSqliteStore Store, TradingReviewExecutionProcessor Processor);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

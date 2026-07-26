using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Agent;

namespace WPE.Tests;

public sealed class TradingReviewEndToEndTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-review-e2e-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "agent.db");

    [Fact]
    public async Task PendingApproveClaimExecute_ReachesTerminalThroughPersistedGatewayChain()
    {
        var setup = await SetupAsync("complete");
        Assert.Equal(TradingReviewQueueStatus.Pending, (await ItemAsync(setup))!.Status);

        var approval = await setup.Approvals.HandleAsync(
            new(setup.RequestId, TradingReviewApprovalAction.Approve, "operator reviewed deterministic plan"),
            CancellationToken.None);
        var execution = await setup.Worker.RunOnceAsync("worker-primary", CancellationToken.None);

        Assert.True(approval.Applied);
        Assert.True(execution.Handled);
        Assert.Equal("review.succeeded", execution.Code);
        Assert.Equal(TradingReviewQueueStatus.Succeeded, (await ItemAsync(setup))!.Status);
        Assert.Equal(1, setup.Risk.CallCount);
        Assert.Equal(1, setup.Executor.MutationCount);
        Assert.Equal(new[] { "WPE-E2E-1", "WPE-E2E-2" }, setup.Executor.ClientOrderIds);
        Assert.NotNull((await setup.Store.GetTradingApprovalRequestAsync(setup.RequestId, CancellationToken.None))!.ConsumedAtUtc);

        var events = await setup.Store.GetTradingReviewQueueEventsAsync(setup.RequestId, 100, CancellationToken.None);
        Assert.Equal(
            new[]
            {
                TradingReviewQueueStatus.Pending,
                TradingReviewQueueStatus.Approved,
                TradingReviewQueueStatus.Claimed,
                TradingReviewQueueStatus.Executing,
                TradingReviewQueueStatus.Succeeded
            },
            events.Select(value => value.ToStatus));
    }

    [Fact]
    public async Task RejectedRevokedAndExpiredReviews_NeverReachRiskGateOrGateway()
    {
        var rejected = await SetupAsync("rejected");
        Assert.True((await rejected.Approvals.HandleAsync(
            new(rejected.RequestId, TradingReviewApprovalAction.Reject, "operator rejected plan"),
            CancellationToken.None)).Applied);
        Assert.Equal("review.worker-idle", (await rejected.Worker.RunOnceAsync("worker-rejected", CancellationToken.None)).Code);

        var revoked = await SetupAsync("revoked");
        Assert.True((await revoked.Approvals.HandleAsync(
            new(revoked.RequestId, TradingReviewApprovalAction.Approve, "operator approved plan"),
            CancellationToken.None)).Applied);
        Assert.True((await revoked.Approvals.HandleAsync(
            new(revoked.RequestId, TradingReviewApprovalAction.Revoke, "operator revoked approval"),
            CancellationToken.None)).Applied);
        Assert.Equal("review.worker-idle", (await revoked.Worker.RunOnceAsync("worker-revoked", CancellationToken.None)).Code);

        var expired = await SetupAsync("expired", Artifact(Now.AddMinutes(-10), Now.AddMinutes(-5)));
        var expiredApproval = await expired.Approvals.HandleAsync(
            new(expired.RequestId, TradingReviewApprovalAction.Approve, "late operator approval"),
            CancellationToken.None);
        Assert.False(expiredApproval.Applied);
        Assert.Equal("review.expired", expiredApproval.Code);
        Assert.Equal("review.worker-idle", (await expired.Worker.RunOnceAsync("worker-expired", CancellationToken.None)).Code);

        Assert.Equal(TradingReviewQueueStatus.Rejected, (await ItemAsync(rejected))!.Status);
        Assert.Equal(TradingReviewQueueStatus.Revoked, (await ItemAsync(revoked))!.Status);
        Assert.Equal(TradingReviewQueueStatus.Expired, (await ItemAsync(expired))!.Status);
        Assert.Equal(0, rejected.Risk.CallCount + revoked.Risk.CallCount + expired.Risk.CallCount);
        Assert.Equal(0, rejected.Executor.MutationCount + revoked.Executor.MutationCount + expired.Executor.MutationCount);
    }

    [Fact]
    public async Task RepeatedWorkerRuns_DoNotExecuteTerminalReviewAgain()
    {
        var setup = await SetupAsync("repeat");
        Assert.True((await setup.Approvals.HandleAsync(
            new(setup.RequestId, TradingReviewApprovalAction.Approve, "operator approved once"),
            CancellationToken.None)).Applied);

        var first = await setup.Worker.RunOnceAsync("worker-1", CancellationToken.None);
        var second = await setup.Worker.RunOnceAsync("worker-2", CancellationToken.None);
        var third = await setup.Worker.RunOnceAsync("worker-1", CancellationToken.None);

        Assert.Equal("review.succeeded", first.Code);
        Assert.Equal("review.worker-idle", second.Code);
        Assert.Equal("review.worker-idle", third.Code);
        Assert.Equal(1, setup.Risk.CallCount);
        Assert.Equal(1, setup.Executor.MutationCount);
        Assert.Equal(TradingReviewQueueStatus.Succeeded, (await ItemAsync(setup))!.Status);
    }

    [Fact]
    public async Task TamperedPersistedArtifact_FailsClosedBeforeRiskGateAndGateway()
    {
        var setup = await SetupAsync("tampered");
        Assert.True((await setup.Approvals.HandleAsync(
            new(setup.RequestId, TradingReviewApprovalAction.Approve, "operator approved intact artifact"),
            CancellationToken.None)).Applied);
        await TamperArtifactAsync(setup.RequestId);

        var result = await setup.Worker.RunOnceAsync("worker-tamper", CancellationToken.None);

        Assert.False(result.Handled);
        Assert.Equal("review.claim-unavailable", result.Code);
        var item = await ItemAsync(setup);
        Assert.Equal(TradingReviewQueueStatus.ArtifactInvalid, item!.Status);
        Assert.False(item.ArtifactValid);
        Assert.Null(item.Artifact);
        Assert.Equal(0, setup.Risk.CallCount);
        Assert.Equal(0, setup.Executor.MutationCount);
        Assert.Null((await setup.Store.GetTradingApprovalRequestAsync(setup.RequestId, CancellationToken.None))!.ConsumedAtUtc);
    }

    private async Task<SetupResult> SetupAsync(string suffix, DurableReviewExecutionArtifactV1? artifact = null)
    {
        artifact ??= Artifact();
        var hashes = DurableReviewArtifactCanonicalizer.ComputeHashes(artifact);
        var requestId = "request-e2e-" + suffix;
        var request = new TradingApprovalRequest(
            requestId,
            TradingAuthorizationMode.Review,
            "cycle-e2e-" + suffix,
            hashes.IntentHash,
            "user-e2e",
            "device-e2e",
            "session-e2e",
            artifact.CreatedAtUtc,
            artifact.ExpiresAtUtc,
            ArtifactHash: hashes.ArtifactHash);
        var store = new AgentSqliteStore(DatabasePath, () => Now);
        Assert.True((await store.SaveTradingReviewQueueAsync(request, artifact, CancellationToken.None)).Succeeded);

        var context = new ContextProvider();
        var approvals = new TradingReviewApprovalService(store, context);
        var executor = new RecordingTestnetExecutor();
        var gateway = new TradingExecutionGateway(executor, store, () => Now);
        var risk = new RecordingRiskGate();
        var processor = new TradingReviewExecutionProcessor(
            store,
            gateway,
            new AllowingRuntimeValidator(),
            risk,
            new NonSubmittingReconciler(),
            () => Now);
        return new(requestId, store, approvals, risk, executor, new TradingReviewExecutionWorker(context, processor));
    }

    private static DurableReviewExecutionArtifactV1 Artifact(DateTimeOffset? created = null, DateTimeOffset? expires = null)
    {
        var createdAt = created ?? Now.AddMinutes(-1);
        return new(
            DurableReviewExecutionArtifactV1.Version,
            [
                new(0, "BTCUSDT", "Long", .01m, false, 49_000m, 53_000m, "WPE-E2E-1", "strategy.entry", "OpenLong", "Limit", 50_000m, 50_100m),
                new(1, "ETHUSDT", "Short", .2m, false, 2_650m, 2_400m, "WPE-E2E-2", "strategy.hedge", "OpenShort", "Market", 0m, 2_500m)
            ],
            5,
            true,
            "binance",
            "Testnet",
            "strategy-alpha",
            "v1",
            createdAt.AddSeconds(-10),
            "book-v4",
            createdAt,
            expires ?? Now.AddMinutes(5));
    }

    private static Task<PersistedTradingReviewQueueItem?> ItemAsync(SetupResult setup) =>
        setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId, CancellationToken.None);

    private async Task TamperArtifactAsync(string requestId)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trading_review_execution_queue SET artifact_hash='tampered' WHERE request_id=$request";
        command.Parameters.AddWithValue("$request", requestId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ContextProvider : ITrustedTradingReviewContextProvider
    {
        public ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<TrustedTradingReviewContext?>(
                new("user-e2e", "device-e2e", "session-e2e", TradingAuthorizationMode.Review, true, "binance"));
        }
    }

    private sealed class AllowingRuntimeValidator : ITradingReviewRuntimeValidator
    {
        public Task<TradingReviewRuntimeValidation> ValidateAsync(
            TradingApprovalRequest request,
            DurableReviewExecutionArtifactV1 artifact,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new TradingReviewRuntimeValidation(true, "review.runtime-valid"));
        }
    }

    private sealed class RecordingRiskGate : ITradingReviewRiskValidator
    {
        public int CallCount { get; private set; }

        public Task<TradingReviewRiskValidation> ValidateAsync(
            TradingApprovalRequest request,
            DurableReviewExecutionArtifactV1 artifact,
            DurableReviewArtifactHashes hashes,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            var receipt = new DeterministicRiskReceipt(
                "risk-e2e-" + request.RequestId,
                request.CorrelationId,
                hashes.IntentHash,
                true,
                Now,
                Now.AddMinutes(1),
                ArtifactHash: hashes.ArtifactHash);
            return Task.FromResult(new TradingReviewRiskValidation(true, "review.risk-valid", receipt));
        }
    }

    private sealed class RecordingTestnetExecutor : ITradingMutationExecutor
    {
        public bool IsTestnet => true;
        public int MutationCount { get; private set; }
        public string[] ClientOrderIds { get; private set; } = [];

        public Task<string> ExecutePlanAsync(
            string correlationId,
            IReadOnlyList<ExecutionIntent> intents,
            int leverage,
            bool isolated,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MutationCount++;
            ClientOrderIds = intents.Select(value => value.ClientOrderId).ToArray();
            return Task.FromResult("testnet-submitted");
        }
    }

    private sealed class NonSubmittingReconciler : IDurableReviewExecutionReconciler
    {
        public Task<DurableReviewReconciliationResult> ReconcileAsync(
            DurableReviewExecutionArtifactV1 artifact,
            CancellationToken ct) =>
            throw new InvalidOperationException("Reconciliation is not expected in these deterministic cases.");
    }

    private sealed record SetupResult(
        string RequestId,
        AgentSqliteStore Store,
        TradingReviewApprovalService Approvals,
        RecordingRiskGate Risk,
        RecordingTestnetExecutor Executor,
        TradingReviewExecutionWorker Worker);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

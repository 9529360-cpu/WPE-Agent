using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingReviewFailureInjectionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-review-failure-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "agent.db");

    [Fact]
    public async Task SqliteTransientWriteFailure_LeavesApprovalDurableAndDoesNotMutate()
    {
        var setup = await SetupAsync("sqlite-busy");
        await ApproveAsync(setup);
        await RenameQueueTableAsync("trading_review_execution_queue", "trading_review_execution_queue_outage");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(
                () => setup.Processor.ProcessNextApprovedAsync("worker", CancellationToken.None));
        }
        finally
        {
            await RenameQueueTableAsync("trading_review_execution_queue_outage", "trading_review_execution_queue");
        }

        var item = await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId, CancellationToken.None);
        Assert.Equal(TradingReviewQueueStatus.Approved, item!.Status);
        Assert.Equal(0, setup.Executor.MutationCount);
        Assert.Equal(new[] { "review.pending", "review.approved" }, await EventCodesAsync(setup));
    }

    [Fact]
    public async Task ContextProviderException_DeniesApprovalWithoutFallbackExecution()
    {
        var setup = await SetupAsync("context");
        setup.Context.Throw = true;

        var result = await setup.Approvals.HandleAsync(
            new(setup.RequestId, TradingReviewApprovalAction.Approve, "operator approval"), CancellationToken.None);
        var processing = await setup.Processor.ProcessNextApprovedAsync("worker", CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("review.context-unavailable", result.Code);
        Assert.Equal("review.no-approved-work", processing.Code);
        Assert.Equal(TradingReviewQueueStatus.Pending, (await ItemAsync(setup))!.Status);
        Assert.Equal(0, setup.Executor.MutationCount);
        Assert.Equal(new[] { "review.pending" }, await EventCodesAsync(setup));
    }

    [Fact]
    public async Task StaleCapability_BlocksTerminallyBeforeRiskOrGateway()
    {
        var setup = await SetupAsync("capability-stale");
        await ApproveAsync(setup);
        setup.Runtime.Result = new(false, "review.capability-stale");

        var result = await setup.Processor.ProcessNextApprovedAsync("worker", CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("review.policy-blocked", result.Code);
        Assert.Equal(TradingReviewQueueStatus.PolicyBlocked, (await ItemAsync(setup))!.Status);
        Assert.Equal(0, setup.Risk.CallCount);
        Assert.Equal(0, setup.Executor.MutationCount);
        Assert.Contains("review.policy-blocked", await EventCodesAsync(setup));
    }

    [Fact]
    public async Task RiskGateRejection_BlocksTerminallyWithoutGatewayMutation()
    {
        var setup = await SetupAsync("risk-rejected");
        await ApproveAsync(setup);
        setup.Risk.Allowed = false;

        var result = await setup.Processor.ProcessNextApprovedAsync("worker", CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("review.risk-blocked", result.Code);
        Assert.Equal(TradingReviewQueueStatus.PolicyBlocked, (await ItemAsync(setup))!.Status);
        Assert.Equal(1, setup.Risk.CallCount);
        Assert.Equal(0, setup.Executor.MutationCount);
        Assert.Contains("review.risk-blocked", await EventCodesAsync(setup));
    }

    [Fact]
    public async Task GatewayTimeout_StaysExecutingAndNeverFallsBackOrRetries()
    {
        var setup = await SetupAsync("gateway-timeout");
        await ApproveAsync(setup);
        setup.Executor.Failure = new TimeoutException("testnet gateway timed out before acknowledgement");

        var first = await setup.Processor.ProcessNextApprovedAsync("worker", CancellationToken.None);
        var retry = await setup.Processor.ProcessNextApprovedAsync("fallback-worker", CancellationToken.None);

        Assert.Equal("review.execution-ambiguous", first.Code);
        Assert.Equal("review.no-approved-work", retry.Code);
        Assert.Equal(TradingReviewQueueStatus.Executing, (await ItemAsync(setup))!.Status);
        Assert.Equal(1, setup.Executor.CallCount);
        Assert.Equal(0, setup.Executor.MutationCount);
        Assert.Contains("review.executing", await EventCodesAsync(setup));
    }

    [Fact]
    public async Task AmbiguousExecution_ReconcilesWithoutDuplicateMutation()
    {
        var setup = await SetupAsync("ambiguous");
        await ApproveAsync(setup);
        setup.Executor.MutateBeforeFailure = true;
        setup.Executor.Failure = new InvalidOperationException("submission acknowledgement lost");

        var processing = await setup.Processor.ProcessNextApprovedAsync("worker", CancellationToken.None);
        await ExpireLeaseAsync(setup.RequestId);
        setup.Reconciler.Result = new(DurableReviewReconciliationState.Succeeded, "found by client order id");
        var reconciliation = await setup.Processor.ReconcileNextAsync("reconciler", CancellationToken.None);

        Assert.Equal("review.execution-ambiguous", processing.Code);
        Assert.Equal("review.reconcile-succeeded", reconciliation.Code);
        Assert.Equal(TradingReviewQueueStatus.Succeeded, (await ItemAsync(setup))!.Status);
        Assert.Equal(1, setup.Executor.MutationCount);
        Assert.Equal(1, setup.Reconciler.CallCount);
        Assert.Equal(new[] { "WPE-FAIL-1" }, setup.Reconciler.ClientOrderIds);
        Assert.Contains("review.reconcile-succeeded", await EventCodesAsync(setup));
    }

    [Fact]
    public async Task ReconcileQueryFailure_BecomesAuditableManualRequiredWithoutResubmission()
    {
        var setup = await SetupAsync("reconcile-query-failed");
        await PrepareExecutingAsync(setup);
        setup.Reconciler.Failure = new InvalidOperationException("provider query unavailable");

        var result = await setup.Processor.ReconcileNextAsync("reconciler", CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("review.reconcile-manual-required", result.Code);
        Assert.Equal(TradingReviewQueueStatus.PolicyBlocked, (await ItemAsync(setup))!.Status);
        Assert.Equal(1, setup.Reconciler.CallCount);
        Assert.Equal(0, setup.Executor.CallCount);
        Assert.Contains("review.reconcile-manual-required", await EventCodesAsync(setup));
    }

    [Fact]
    public async Task CancellationAfterMutation_LeavesAuditableUnknownForProcessRestartReconciliation()
    {
        var setup = await SetupAsync("cancel-exit");
        await ApproveAsync(setup);
        using var cancellation = new CancellationTokenSource();
        setup.Executor.MutateBeforeFailure = true;
        setup.Executor.OnCall = cancellation.Cancel;
        setup.Executor.FailureFactory = () => new OperationCanceledException(cancellation.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => setup.Processor.ProcessNextApprovedAsync("exiting-worker", cancellation.Token));
        Assert.Equal(TradingReviewQueueStatus.Executing, (await ItemAsync(setup))!.Status);
        Assert.Equal(1, setup.Executor.MutationCount);
        Assert.Contains("review.executing", await EventCodesAsync(setup));

        await ExpireLeaseAsync(setup.RequestId);
        setup.Reconciler.Result = new(DurableReviewReconciliationState.Unknown, "exchange result unknown");
        var restarted = CreateProcessor(setup);
        var result = await restarted.ReconcileNextAsync("worker-after-restart", CancellationToken.None);

        Assert.Equal("review.reconcile-manual-required", result.Code);
        Assert.Equal(TradingReviewQueueStatus.PolicyBlocked, (await ItemAsync(setup))!.Status);
        Assert.Equal(1, setup.Executor.MutationCount);
        Assert.Equal(1, setup.Reconciler.CallCount);
    }

    private async Task<SetupState> SetupAsync(string suffix)
    {
        var artifact = Artifact();
        var hashes = DurableReviewArtifactCanonicalizer.ComputeHashes(artifact);
        var requestId = "request-failure-" + suffix;
        var request = new TradingApprovalRequest(requestId, TradingAuthorizationMode.Review, "cycle-" + suffix,
            hashes.IntentHash, "user", "device", "session", artifact.CreatedAtUtc, artifact.ExpiresAtUtc,
            ArtifactHash: hashes.ArtifactHash);
        var store = new AgentSqliteStore(DatabasePath, () => Now);
        Assert.True((await store.SaveTradingReviewQueueAsync(request, artifact, CancellationToken.None)).Succeeded);
        var context = new ContextProvider();
        var executor = new FailureExecutor();
        var runtime = new RuntimeValidator();
        var risk = new RiskValidator();
        var reconciler = new FailureReconciler();
        var setup = new SetupState(requestId, store, context, new(store, context), executor, runtime, risk, reconciler, null!);
        return setup with { Processor = CreateProcessor(setup) };
    }

    private static TradingReviewExecutionProcessor CreateProcessor(SetupState setup)
    {
        var gateway = new TradingExecutionGateway(setup.Executor, setup.Store, () => Now);
        return new(setup.Store, gateway, setup.Runtime, setup.Risk, setup.Reconciler, () => Now);
    }

    private static async Task ApproveAsync(SetupState setup)
    {
        var result = await setup.Approvals.HandleAsync(
            new(setup.RequestId, TradingReviewApprovalAction.Approve, "operator approval"), CancellationToken.None);
        Assert.True(result.Applied);
    }

    private async Task PrepareExecutingAsync(SetupState setup)
    {
        await ApproveAsync(setup);
        Assert.True((await setup.Store.TryClaimTradingReviewAsync(setup.RequestId, "crashed-worker", TimeSpan.FromSeconds(30), CancellationToken.None)).Claimed);
        Assert.True((await setup.Store.TryTransitionTradingReviewAsync(setup.RequestId, TradingReviewQueueStatus.Claimed,
            TradingReviewQueueStatus.Executing, "crashed-worker", "review.executing", CancellationToken.None)).Succeeded);
        await ExpireLeaseAsync(setup.RequestId);
    }

    private async Task ExpireLeaseAsync(string requestId)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trading_review_execution_queue SET lease_expires_at=$expired WHERE request_id=$request";
        command.Parameters.AddWithValue("$expired", Now.AddSeconds(-1).ToString("O"));
        command.Parameters.AddWithValue("$request", requestId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RenameQueueTableAsync(string currentName, string nextName)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {currentName} RENAME TO {nextName}";
        await command.ExecuteNonQueryAsync();
    }

    private static Task<PersistedTradingReviewQueueItem?> ItemAsync(SetupState setup) =>
        setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId, CancellationToken.None);

    private static async Task<string[]> EventCodesAsync(SetupState setup) =>
        (await setup.Store.GetTradingReviewQueueEventsAsync(setup.RequestId, 100, CancellationToken.None))
        .Select(value => value.EventCode).ToArray();

    private static DurableReviewExecutionArtifactV1 Artifact() => new(
        DurableReviewExecutionArtifactV1.Version,
        [new(0, "BTCUSDT", "Long", .01m, false, 49_000m, 53_000m, "WPE-FAIL-1", "strategy.entry", "OpenLong", "Limit", 50_000m, 50_100m)],
        5, true, "binance", "Testnet", "strategy-alpha", "v1", Now.AddMinutes(-2), "book-v4",
        Now.AddMinutes(-1), Now.AddMinutes(5));

    private sealed class ContextProvider : ITrustedTradingReviewContextProvider
    {
        public bool Throw { get; set; }
        public ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Throw) throw new InvalidOperationException("trusted context unavailable");
            return ValueTask.FromResult<TrustedTradingReviewContext?>(new(
                "user", "device", "session", TradingAuthorizationMode.Review, true, "binance"));
        }
    }

    private sealed class RuntimeValidator : ITradingReviewRuntimeValidator
    {
        public TradingReviewRuntimeValidation Result { get; set; } = new(true, "review.runtime-valid");
        public Task<TradingReviewRuntimeValidation> ValidateAsync(TradingApprovalRequest request,
            DurableReviewExecutionArtifactV1 artifact, CancellationToken ct) => Task.FromResult(Result);
    }

    private sealed class RiskValidator : ITradingReviewRiskValidator
    {
        public bool Allowed { get; set; } = true;
        public int CallCount { get; private set; }
        public Task<TradingReviewRiskValidation> ValidateAsync(TradingApprovalRequest request,
            DurableReviewExecutionArtifactV1 artifact, DurableReviewArtifactHashes hashes, CancellationToken ct)
        {
            CallCount++;
            var receipt = Allowed ? new DeterministicRiskReceipt("risk-" + request.RequestId, request.CorrelationId,
                hashes.IntentHash, true, Now, Now.AddMinutes(1), ArtifactHash: hashes.ArtifactHash) : null;
            return Task.FromResult(new TradingReviewRiskValidation(Allowed, Allowed ? "review.risk-valid" : "review.risk-blocked", receipt));
        }
    }

    private sealed class FailureExecutor : ITradingMutationExecutor
    {
        public bool IsTestnet => true;
        public bool MutateBeforeFailure { get; set; }
        public Exception? Failure { get; set; }
        public Func<Exception>? FailureFactory { get; set; }
        public Action? OnCall { get; set; }
        public int CallCount { get; private set; }
        public int MutationCount { get; private set; }
        public Task<string> ExecutePlanAsync(string correlationId, IReadOnlyList<ExecutionIntent> intents,
            int leverage, bool isolated, CancellationToken ct)
        {
            CallCount++;
            if (MutateBeforeFailure) MutationCount++;
            OnCall?.Invoke();
            var failure = FailureFactory?.Invoke() ?? Failure;
            if (failure is not null) return Task.FromException<string>(failure);
            if (!MutateBeforeFailure) MutationCount++;
            return Task.FromResult("submitted");
        }
    }

    private sealed class FailureReconciler : IDurableReviewExecutionReconciler
    {
        public DurableReviewReconciliationResult Result { get; set; } = new(DurableReviewReconciliationState.Unknown, "unknown");
        public Exception? Failure { get; set; }
        public int CallCount { get; private set; }
        public string[] ClientOrderIds { get; private set; } = [];
        public Task<DurableReviewReconciliationResult> ReconcileAsync(DurableReviewExecutionArtifactV1 artifact, CancellationToken ct)
        {
            CallCount++;
            ClientOrderIds = artifact.Intents.Select(value => value.ClientOrderId).ToArray();
            return Failure is null ? Task.FromResult(Result) : Task.FromException<DurableReviewReconciliationResult>(Failure);
        }
    }

    private sealed record SetupState(string RequestId, AgentSqliteStore Store, ContextProvider Context,
        TradingReviewApprovalService Approvals, FailureExecutor Executor, RuntimeValidator Runtime,
        RiskValidator Risk, FailureReconciler Reconciler, TradingReviewExecutionProcessor Processor);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

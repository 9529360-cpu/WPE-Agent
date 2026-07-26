using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Agent;

namespace WPE.Tests;

public sealed class TradingReviewAuditTraceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-review-audit-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "agent.db");

    [Fact]
    public async Task SuccessfulReview_PreservesCorrelationAcrossRequestApprovalClaimExecutionAndTerminalAudit()
    {
        var setup = await SetupAsync("success");
        var approval = await setup.Approvals.HandleAsync(new(setup.RequestId, TradingReviewApprovalAction.Approve, "operator approved api_key=sk-audit-secret-123456789"), CancellationToken.None);
        var result = await setup.Processor.ProcessNextApprovedAsync("worker-audit", CancellationToken.None);
        var request = await setup.Store.GetTradingApprovalRequestAsync(setup.RequestId, CancellationToken.None);
        var receipt = await setup.Store.GetTradingApprovalReceiptForRequestAsync(setup.RequestId, CancellationToken.None);
        var events = await setup.Store.GetTradingReviewQueueEventsAsync(setup.RequestId, 100, CancellationToken.None);

        Assert.True(approval.Applied); Assert.True(result.Handled); Assert.Equal("review.succeeded", result.Code);
        Assert.Equal(setup.CorrelationId, request!.CorrelationId); Assert.Equal(setup.CorrelationId, receipt!.Receipt.CorrelationId); Assert.Equal(setup.CorrelationId, setup.Executor.CorrelationId);
        Assert.NotNull(request.ConsumedAtUtc); Assert.NotNull(receipt.Receipt.ConsumedAtUtc);
        AssertTrace(events,
            (null, TradingReviewQueueStatus.Pending, "review.pending"),
            (TradingReviewQueueStatus.Pending, TradingReviewQueueStatus.Approved, "review.approved"),
            (TradingReviewQueueStatus.Approved, TradingReviewQueueStatus.Claimed, "review.claimed"),
            (TradingReviewQueueStatus.Claimed, TradingReviewQueueStatus.Executing, "review.executing"),
            (TradingReviewQueueStatus.Executing, TradingReviewQueueStatus.Succeeded, "review.succeeded"));
        Assert.All(events, value => Assert.Equal(setup.RequestId, value.RequestId));
        AssertAuditDoesNotLeak(events, "sk-audit-secret-123456789", "user-private-42", "device-private-42", "session-private-42");
    }

    [Fact]
    public async Task ExpiredExecutingLease_ReconciliationIsCorrelatedAndRecordsTheObservedTerminalFact()
    {
        var setup = await SetupAsync("reconcile");
        Assert.True((await setup.Approvals.HandleAsync(new(setup.RequestId, TradingReviewApprovalAction.Approve, "operator approved"), CancellationToken.None)).Applied);
        Assert.True((await setup.Store.TryClaimTradingReviewAsync(setup.RequestId, "crashed-worker", TimeSpan.FromSeconds(30), CancellationToken.None)).Claimed);
        Assert.True((await setup.Store.TryTransitionTradingReviewAsync(setup.RequestId, TradingReviewQueueStatus.Claimed, TradingReviewQueueStatus.Executing, "crashed-worker", "review.executing", CancellationToken.None)).Succeeded);
        await ExpireLeaseAsync(setup.RequestId);
        setup.Reconciler.Result = new(DurableReviewReconciliationState.NotSubmitted, "provider-secret-detail");

        var result = await setup.Processor.ReconcileNextAsync("recovery-worker", CancellationToken.None);
        var request = await setup.Store.GetTradingApprovalRequestAsync(setup.RequestId, CancellationToken.None);
        var events = await setup.Store.GetTradingReviewQueueEventsAsync(setup.RequestId, 100, CancellationToken.None);

        Assert.True(result.Handled); Assert.Equal("review.reconcile-not-submitted", result.Code); Assert.Equal(setup.CorrelationId, request!.CorrelationId);
        Assert.Equal(1, setup.Reconciler.CallCount); Assert.Equal(0, setup.Executor.MutationCount);
        Assert.Contains(events, value => value.FromStatus == TradingReviewQueueStatus.Executing && value.ToStatus == TradingReviewQueueStatus.Reconciling && value.EventCode == "review.reconciling");
        Assert.Equal(TradingReviewQueueStatus.FailedTerminal, events[^1].ToStatus); Assert.Equal("review.reconcile-not-submitted", events[^1].EventCode);
        Assert.DoesNotContain(events, value => value.ToStatus == TradingReviewQueueStatus.Succeeded);
        AssertAuditDoesNotLeak(events, "provider-secret-detail", "user-private-42", "device-private-42", "session-private-42");
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("revoke")]
    [InlineData("policy-failure")]
    public async Task RejectedRevokedAndFailedReviews_RecordFactsWithoutExecutionOrFalseSuccess(string scenario)
    {
        const string secret = "sk-negative-audit-secret-123456789";
        var setup = await SetupAsync(scenario);
        if (scenario == "reject")
            Assert.True((await setup.Approvals.HandleAsync(new(setup.RequestId, TradingReviewApprovalAction.Reject, "rejected api_key=" + secret), CancellationToken.None)).Applied);
        else
        {
            Assert.True((await setup.Approvals.HandleAsync(new(setup.RequestId, TradingReviewApprovalAction.Approve, "approved api_key=" + secret), CancellationToken.None)).Applied);
            if (scenario == "revoke")
                Assert.True((await setup.Approvals.HandleAsync(new(setup.RequestId, TradingReviewApprovalAction.Revoke, "revoked token=" + secret), CancellationToken.None)).Applied);
            else
            {
                setup.Runtime.Result = new(false, "review.strategy-invalid");
                Assert.Equal("review.strategy-invalid", (await setup.Processor.ProcessNextApprovedAsync("worker-failure", CancellationToken.None)).Code);
            }
        }

        var item = await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId, CancellationToken.None);
        var events = await setup.Store.GetTradingReviewQueueEventsAsync(setup.RequestId, 100, CancellationToken.None);
        var expected = scenario switch { "reject" => TradingReviewQueueStatus.Rejected, "revoke" => TradingReviewQueueStatus.Revoked, _ => TradingReviewQueueStatus.StrategyInvalid };
        Assert.Equal(expected, item!.Status); Assert.Equal(expected, events[^1].ToStatus); Assert.Equal(0, setup.Executor.MutationCount);
        Assert.DoesNotContain(events, value => value.ToStatus == TradingReviewQueueStatus.Succeeded || value.EventCode == "review.succeeded");
        AssertAuditDoesNotLeak(events, secret, "user-private-42", "device-private-42", "session-private-42");
    }

    [Fact]
    public async Task ExpiredReview_RecordsTerminalFactWithoutExecutionOrFalseSuccess()
    {
        var created = Now.AddMinutes(-10); var expiredAt = Now.AddMinutes(-5);
        var artifact = Artifact("expired") with { MarketCollectedAtUtc = created.AddSeconds(-10), CreatedAtUtc = created, ExpiresAtUtc = expiredAt };
        var hashes = DurableReviewArtifactCanonicalizer.ComputeHashes(artifact); const string requestId = "request-audit-expired"; const string correlationId = "correlation-audit-expired";
        var request = new TradingApprovalRequest(requestId, TradingAuthorizationMode.Review, correlationId, hashes.IntentHash, "user-private-42", "device-private-42", "session-private-42", created, expiredAt, ArtifactHash: hashes.ArtifactHash);
        var store = new AgentSqliteStore(DatabasePath, () => Now); Assert.True((await store.SaveTradingReviewQueueAsync(request, artifact, CancellationToken.None)).Succeeded);

        Assert.True((await store.TryExpireTradingReviewAsync(requestId, TradingReviewQueueStatus.Pending, CancellationToken.None)).Succeeded);
        var saved = await store.GetTradingApprovalRequestAsync(requestId, CancellationToken.None); var events = await store.GetTradingReviewQueueEventsAsync(requestId, 100, CancellationToken.None);

        Assert.Equal(correlationId, saved!.CorrelationId); Assert.Equal(TradingReviewQueueStatus.Expired, events[^1].ToStatus); Assert.Equal("review.expired", events[^1].EventCode);
        Assert.DoesNotContain(events, value => value.ToStatus == TradingReviewQueueStatus.Succeeded || value.EventCode == "review.succeeded");
        AssertAuditDoesNotLeak(events, "user-private-42", "device-private-42", "session-private-42");
    }

    private async Task<Setup> SetupAsync(string suffix)
    {
        var artifact = Artifact(suffix); var hashes = DurableReviewArtifactCanonicalizer.ComputeHashes(artifact);
        var requestId = "request-audit-" + suffix; var correlationId = "correlation-audit-" + suffix;
        var request = new TradingApprovalRequest(requestId, TradingAuthorizationMode.Review, correlationId, hashes.IntentHash, "user-private-42", "device-private-42", "session-private-42", artifact.CreatedAtUtc, artifact.ExpiresAtUtc, ArtifactHash: hashes.ArtifactHash);
        var store = new AgentSqliteStore(DatabasePath, () => Now); Assert.True((await store.SaveTradingReviewQueueAsync(request, artifact, CancellationToken.None)).Succeeded);
        var context = new ContextProvider(); var executor = new RecordingExecutor(); var runtime = new RuntimeValidator(); var reconciler = new RecordingReconciler();
        var gateway = new TradingExecutionGateway(executor, store, () => Now);
        var processor = new TradingReviewExecutionProcessor(store, gateway, runtime, new RiskValidator(), reconciler, () => Now);
        return new(requestId, correlationId, store, new(store, context), processor, runtime, executor, reconciler);
    }

    private async Task ExpireLeaseAsync(string requestId)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}"); await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trading_review_execution_queue SET lease_expires_at=$expired WHERE request_id=$request";
        command.Parameters.AddWithValue("$expired", Now.AddSeconds(-1).ToString("O")); command.Parameters.AddWithValue("$request", requestId); await command.ExecuteNonQueryAsync();
    }

    private static void AssertTrace(IReadOnlyList<PersistedTradingReviewQueueEvent> actual, params (TradingReviewQueueStatus? From, TradingReviewQueueStatus To, string Code)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++) { Assert.Equal(index + 1, actual[index].Sequence); Assert.Equal(expected[index].From, actual[index].FromStatus); Assert.Equal(expected[index].To, actual[index].ToStatus); Assert.Equal(expected[index].Code, actual[index].EventCode); }
    }

    private static void AssertAuditDoesNotLeak(IEnumerable<PersistedTradingReviewQueueEvent> events, params string[] sensitiveValues)
    {
        var auditText = string.Join("\n", events.Select(value => $"{value.EventCode}|{value.ActorKind}|{value.Reason}"));
        foreach (var sensitive in sensitiveValues) Assert.DoesNotContain(sensitive, auditText, StringComparison.Ordinal);
    }

    private static DurableReviewExecutionArtifactV1 Artifact(string suffix)
    {
        var created = Now.AddMinutes(-1);
        return new(DurableReviewExecutionArtifactV1.Version, [new(0, "BTCUSDT", "Long", .01m, false, 49_000m, 53_000m, "WPE-AUDIT-" + suffix.ToUpperInvariant(), "strategy.entry", "OpenLong", "Limit", 50_000m, 50_100m)], 5, true, "binance", "Testnet", "strategy-alpha", "v1", created.AddSeconds(-10), "book-v4", created, Now.AddMinutes(5));
    }

    private sealed class ContextProvider : ITrustedTradingReviewContextProvider
    {
        public ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct) => ValueTask.FromResult<TrustedTradingReviewContext?>(new("user-private-42", "device-private-42", "session-private-42", TradingAuthorizationMode.Review, true, "binance"));
    }

    private sealed class RuntimeValidator : ITradingReviewRuntimeValidator
    {
        public TradingReviewRuntimeValidation Result { get; set; } = new(true, "review.runtime-valid");
        public Task<TradingReviewRuntimeValidation> ValidateAsync(TradingApprovalRequest request, DurableReviewExecutionArtifactV1 artifact, CancellationToken ct) => Task.FromResult(Result);
    }

    private sealed class RiskValidator : ITradingReviewRiskValidator
    {
        public Task<TradingReviewRiskValidation> ValidateAsync(TradingApprovalRequest request, DurableReviewExecutionArtifactV1 artifact, DurableReviewArtifactHashes hashes, CancellationToken ct) => Task.FromResult(new TradingReviewRiskValidation(true, "review.risk-valid", new("risk-" + request.RequestId, request.CorrelationId, hashes.IntentHash, true, Now, Now.AddMinutes(1), ArtifactHash: hashes.ArtifactHash)));
    }

    private sealed class RecordingExecutor : ITradingMutationExecutor
    {
        public bool IsTestnet => true; public int MutationCount { get; private set; } public string? CorrelationId { get; private set; }
        public Task<string> ExecutePlanAsync(string correlationId, IReadOnlyList<ExecutionIntent> intents, int leverage, bool isolated, CancellationToken ct) { MutationCount++; CorrelationId = correlationId; return Task.FromResult("testnet-submitted"); }
    }

    private sealed class RecordingReconciler : IDurableReviewExecutionReconciler
    {
        public DurableReviewReconciliationResult Result { get; set; } = new(DurableReviewReconciliationState.Unknown, "review.reconcile-manual-required"); public int CallCount { get; private set; }
        public Task<DurableReviewReconciliationResult> ReconcileAsync(DurableReviewExecutionArtifactV1 artifact, CancellationToken ct) { CallCount++; return Task.FromResult(Result); }
    }

    private sealed record Setup(string RequestId, string CorrelationId, AgentSqliteStore Store, TradingReviewApprovalService Approvals, TradingReviewExecutionProcessor Processor, RuntimeValidator Runtime, RecordingExecutor Executor, RecordingReconciler Reconciler);

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

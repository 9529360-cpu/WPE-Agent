using System.Text;
using Microsoft.Data.Sqlite;
using WpeAgent.FinancialEvidence;

namespace WPE.Tests;

public sealed class FinancialEvidenceSecurityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-fe-sec-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "evidence.sqlite");

    [Theory]
    [InlineData("{\"b\":1,\"a\":2}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1,}")]
    public void NonCanonicalDuplicateOrMalformedPayloadFailsClosed(string payload)
    {
        var draft = FinancialEvidenceRecordContractRedTests.CreateRecord().Draft with { Payload = Encoding.UTF8.GetBytes(payload) };
        Assert.Throws<FinancialEvidenceValidationException>(() => FinancialEvidenceRecordV1.Create(draft));
    }

    [Fact]
    public void HashMutationAndUnknownEnumsFailClosed()
    {
        var record = FinancialEvidenceRecordContractRedTests.CreateRecord();
        Assert.Throws<FinancialEvidenceValidationException>(() => (record with { ContentHash = "sha256:" + new string('A', 64) }).VerifyIntegrity());
        Assert.Throws<FinancialEvidenceValidationException>(() => FinancialEvidenceRecordV1.Create(record.Draft with { CollectionType = (FinancialEvidenceCollectionTypeV1)999 }));
    }

    [Fact]
    public void FreshnessEntitlementScopeAndSchemaBoundariesDeny()
    {
        var gate = new FinancialEvidenceRetrievalGateV1(); var record = FinancialEvidenceRecordContractRedTests.CreateRecord(); var request = FinancialEvidenceRecordContractRedTests.CreateRequest();
        Assert.Contains("evidence.stale", gate.Evaluate(record, request with { AsOf = record.Draft.ExpiresAt!.Value }).ReasonCodes);
        Assert.Contains("evidence.stale", gate.Evaluate(record, request with { AsOf = record.ObservedAt + request.MaximumAge + TimeSpan.FromTicks(1) }).ReasonCodes);
        Assert.Contains("evidence.unentitled", gate.Evaluate(record, request with { EntitlementScope = "wrong" }).ReasonCodes);
        Assert.Contains("evidence.scope-mismatch", gate.Evaluate(record, request with { Venue = "wrong" }).ReasonCodes);
        Assert.Contains("evidence.schema-incompatible", gate.Evaluate(record, request with { CompatibleSchemaVersions = new HashSet<string> { "2" } }).ReasonCodes);
        Assert.Contains("evidence.stale", gate.Evaluate(record, request with { AsOf = DateTimeOffset.MaxValue, MaximumAge = TimeSpan.MaxValue }).ReasonCodes);
    }

    [Theory]
    [InlineData(FinancialEvidenceEntitlementAuthorityStateV1.Unknown, "evidence.entitlement-unknown")]
    [InlineData(FinancialEvidenceEntitlementAuthorityStateV1.Error, "evidence.entitlement-error")]
    [InlineData(FinancialEvidenceEntitlementAuthorityStateV1.Unentitled, "evidence.unentitled")]
    public async Task ExternalEntitlementNonSuccessFailsClosedAndIsAudited(FinancialEvidenceEntitlementAuthorityStateV1 state, string reason)
    {
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: new FixedAuthority(state));
        await store.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var result = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, result.Outcome); Assert.Empty(result.Records);
        Assert.Contains(reason, result.ReasonCodes); Assert.NotNull(result.Audit);
    }

    [Fact]
    public async Task ExternalEntitlementTimeoutAndExceptionFailClosed()
    {
        var timeoutStore = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: new TimeoutAuthority(), authorityTimeout: TimeSpan.FromMilliseconds(20));
        await timeoutStore.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var timeout = await timeoutStore.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, timeout.Outcome); Assert.Empty(timeout.Records); Assert.Contains("evidence.entitlement-timeout", timeout.ReasonCodes);

        Dispose();
        var errorStore = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: new ThrowingAuthority());
        await errorStore.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var error = await errorStore.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, error.Outcome); Assert.Empty(error.Records); Assert.Contains("evidence.entitlement-error", error.ReasonCodes);
    }

    [Fact]
    public async Task MissingAuthorityFailsClosedWithAuditAndNoPayload()
    {
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath);
        await store.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var result = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, result.Outcome);
        Assert.Empty(result.Records);
        Assert.Contains("evidence.entitlement-authority-missing", result.ReasonCodes);
        Assert.Equal(1, (await store.ScanIntegrityAsync()).AuditCount);
    }

    [Fact]
    public async Task CancellationIgnoringAuthorityDoesNotHoldSqliteOrBlockWriterAndCleansUp()
    {
        var authority = new IgnoringCancellationAuthority();
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: authority, authorityTimeout: TimeSpan.FromMilliseconds(250));
        await store.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());

        var retrieval = store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        await authority.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using (var writer = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await writer.OpenAsync();
            await using var command = writer.CreateCommand();
            command.CommandText = "INSERT INTO financial_evidence_tombstones VALUES('concurrent-writer',x'01')";
            await command.ExecuteNonQueryAsync();
        }
        started.Stop();
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(200), $"Writer was blocked for {started.Elapsed}.");

        var result = await retrieval.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, result.Outcome);
        Assert.Empty(result.Records);
        Assert.Contains("evidence.entitlement-timeout", result.ReasonCodes);
        Assert.Equal(1, (await store.ScanIntegrityAsync()).AuditCount);

        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath);
        Assert.False(File.Exists(DatabasePath));
        authority.Release.TrySetResult();
        await authority.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAuthorityCapacityRecoveryAsync();
    }

    [Fact]
    public async Task ProcessWideAuthorityAdmissionBoundsStormAndRecoversAfterOrphansComplete()
    {
        await WaitForAuthorityCapacityRecoveryAsync();
        var authority = new StormAuthority();
        var stores = Enumerable.Range(0, 12)
            .Select(index => new FinancialEvidenceSqliteStoreV1(
                Path.Combine(_directory, $"storm-{index}.sqlite"),
                entitlementAuthority: authority,
                authorityTimeout: TimeSpan.FromMilliseconds(300)))
            .ToArray();
        foreach (var store in stores) await store.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var exhaustedStore = new FinancialEvidenceSqliteStoreV1(
            Path.Combine(_directory, "exhausted.sqlite"),
            entitlementAuthority: authority,
            authorityTimeout: TimeSpan.FromMilliseconds(300));
        await exhaustedStore.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());

        var requests = stores.Select(store => store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest())).ToArray();
        await authority.CapacityReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var exhaustedAt = System.Diagnostics.Stopwatch.StartNew();
        var exhausted = await exhaustedStore.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest()).WaitAsync(TimeSpan.FromSeconds(1));
        exhaustedAt.Stop();

        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Error, exhausted.Outcome);
        Assert.Contains("evidence.entitlement-capacity-exhausted", exhausted.ReasonCodes);
        Assert.Empty(exhausted.Records);
        Assert.Null(exhausted.Audit);
        Assert.True(exhaustedAt.Elapsed < TimeSpan.FromMilliseconds(150), $"Capacity rejection took {exhaustedAt.Elapsed}.");
        Assert.All(stores, store => Assert.Equal(0, store.ActiveRetrievalConnections));
        Assert.All(stores, store => Assert.Equal(0, store.ActiveRetrievalTransactions));
        Assert.Equal(0, exhaustedStore.ActiveRetrievalConnections);
        Assert.Equal(0, exhaustedStore.ActiveRetrievalTransactions);
        Assert.Equal(0, (await exhaustedStore.ScanIntegrityAsync()).AuditCount);
        Assert.InRange(FinancialEvidenceSqliteStoreV1.ActiveAuthorityWork, 1, FinancialEvidenceSqliteStoreV1.AuthorityConcurrencyCapacity);
        Assert.InRange(FinancialEvidenceSqliteStoreV1.PeakAuthorityWork, 1, FinancialEvidenceSqliteStoreV1.AuthorityConcurrencyCapacity);

        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(results, result => Assert.Empty(result.Records));
        Assert.Equal(FinancialEvidenceSqliteStoreV1.AuthorityConcurrencyCapacity, FinancialEvidenceSqliteStoreV1.OrphanedAuthorityWork);
        Assert.Equal(FinancialEvidenceSqliteStoreV1.AuthorityConcurrencyCapacity, authority.Peak);

        authority.Release.TrySetResult();
        await authority.AllCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAuthorityCapacityRecoveryAsync();
        Assert.Equal(0, FinancialEvidenceSqliteStoreV1.OrphanedAuthorityWork);

        var recoveredAuthority = new FixedAuthority(FinancialEvidenceEntitlementAuthorityStateV1.Entitled);
        var recoveredStore = new FinancialEvidenceSqliteStoreV1(Path.Combine(_directory, "recovered.sqlite"), entitlementAuthority: recoveredAuthority);
        await recoveredStore.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var recovered = await recoveredStore.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Returned, recovered.Outcome);
    }

    [Fact]
    public async Task MutationDuringAuthorityPreflightFailsWithZeroPayloadAndZeroPartialAudit()
    {
        var authority = new MutatingAuthority(DatabasePath);
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: authority);
        await store.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var result = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Error, result.Outcome);
        Assert.Empty(result.Records);
        Assert.Null(result.Audit);
        Assert.Equal(0, (await store.ScanIntegrityAsync()).AuditCount);
    }

    [Fact]
    public async Task AuthorityCancellationAndIgnoredCallerCancellationBothFailClosed()
    {
        var cancelledStore = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: new CancelledAuthority());
        await cancelledStore.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var authorityCancelled = await cancelledStore.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, authorityCancelled.Outcome);
        Assert.Empty(authorityCancelled.Records);
        Assert.Contains("evidence.entitlement-cancelled", authorityCancelled.ReasonCodes);
        Assert.Equal(1, (await cancelledStore.ScanIntegrityAsync()).AuditCount);

        Dispose();
        var ignoring = new IgnoringCancellationAuthority();
        var callerCancelledStore = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: ignoring, authorityTimeout: TimeSpan.FromSeconds(1));
        await callerCancelledStore.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        using var cancellation = new CancellationTokenSource();
        var retrieval = callerCancelledStore.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest(), cancellation.Token);
        await ignoring.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var callerCancelled = await retrieval.WaitAsync(TimeSpan.FromMilliseconds(250));
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Error, callerCancelled.Outcome);
        Assert.Empty(callerCancelled.Records);
        Assert.Null(callerCancelled.Audit);
        Assert.Equal(0, (await callerCancelledStore.ScanIntegrityAsync()).AuditCount);
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath);
        Assert.False(File.Exists(DatabasePath));
        ignoring.Release.TrySetResult();
        await ignoring.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAuthorityCapacityRecoveryAsync();
    }

    [Fact]
    public async Task TamperingIsDetectedAndNoSecretCanaryIsPersistedOnRejectedWrite()
    {
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath); var record = FinancialEvidenceRecordContractRedTests.CreateRecord(); await store.AppendAsync(record);
        await using (var c = new SqliteConnection($"Data Source={DatabasePath};Pooling=False")) { await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = "DROP TRIGGER fe_records_no_update; UPDATE financial_evidence_records SET content_hash='sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'"; await q.ExecuteNonQueryAsync(); }
        Assert.False((await store.ScanIntegrityAsync()).Valid);
        var canary = "credential_canary_" + Guid.NewGuid().ToString("N");
        var invalid = record with { Draft = record.Draft with { RecordId = "secret-row", SourceProvider = canary } };
        await Assert.ThrowsAsync<FinancialEvidenceValidationException>(() => store.AppendAsync(invalid));
        var bytes = await File.ReadAllBytesAsync(DatabasePath); Assert.DoesNotContain(canary, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedPreExistingSchemaFailsClosed()
    {
        Directory.CreateDirectory(_directory); await using (var c = new SqliteConnection($"Data Source={DatabasePath};Pooling=False")) { await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = "CREATE TABLE financial_evidence_schema(version TEXT PRIMARY KEY); INSERT INTO financial_evidence_schema VALUES('future-v9')"; await q.ExecuteNonQueryAsync(); }
        Assert.ThrowsAny<Exception>(() => new FinancialEvidenceSqliteStoreV1(DatabasePath));
    }

    private sealed class FixedAuthority(FinancialEvidenceEntitlementAuthorityStateV1 state) : IFinancialEvidenceEntitlementAuthorityV1
    {
        public Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken) =>
            Task.FromResult(new FinancialEvidenceEntitlementAuthorityResultV1(state, "authority-result"));
    }

    private sealed class TimeoutAuthority : IFinancialEvidenceEntitlementAuthorityV1
    {
        public async Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
    }

    private sealed class ThrowingAuthority : IFinancialEvidenceEntitlementAuthorityV1
    {
        public Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken) => throw new IOException("authority unavailable");
    }

    private sealed class IgnoringCancellationAuthority : IFinancialEvidenceEntitlementAuthorityV1
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            Completed.TrySetResult();
            return new(FinancialEvidenceEntitlementAuthorityStateV1.Entitled, "authority.entitled");
        }
    }

    private sealed class StormAuthority : IFinancialEvidenceEntitlementAuthorityV1
    {
        private int _active;
        private int _completed;
        private int _peak;
        public int Peak => Volatile.Read(ref _peak);
        public TaskCompletionSource CapacityReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdatePeak(active);
            if (active == FinancialEvidenceSqliteStoreV1.AuthorityConcurrencyCapacity) CapacityReached.TrySetResult();
            try
            {
                await Release.Task;
                if (Interlocked.Increment(ref _completed) == FinancialEvidenceSqliteStoreV1.AuthorityConcurrencyCapacity)
                    AllCompleted.TrySetResult();
                throw new IOException("late authority failure");
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        private void UpdatePeak(int active)
        {
            var observed = Volatile.Read(ref _peak);
            while (active > observed)
            {
                var prior = Interlocked.CompareExchange(ref _peak, active, observed);
                if (prior == observed) return;
                observed = prior;
            }
        }
    }

    private sealed class CancelledAuthority : IFinancialEvidenceEntitlementAuthorityV1
    {
        public Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken) =>
            Task.FromCanceled<FinancialEvidenceEntitlementAuthorityResultV1>(new CancellationToken(canceled: true));
    }

    private sealed class MutatingAuthority(string databasePath) : IFinancialEvidenceEntitlementAuthorityV1
    {
        public async Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fe_records_no_update; UPDATE financial_evidence_records SET record_version='mutated' WHERE record_id=$id";
            command.Parameters.AddWithValue("$id", record.RecordId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new(FinancialEvidenceEntitlementAuthorityStateV1.Entitled, "authority.entitled");
        }
    }

    private static async Task WaitForAuthorityCapacityRecoveryAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (FinancialEvidenceSqliteStoreV1.ActiveAuthorityWork != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(0, FinancialEvidenceSqliteStoreV1.ActiveAuthorityWork);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

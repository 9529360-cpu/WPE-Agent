using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WpeAgent.FinancialEvidence;

public enum FinancialEvidenceStoreFailpointV1
{
    None,
    BeforeAuditInsert,
    AuditSerialization,
    AuditDiskFull,
    AuditLocked,
    AuditCancellation,
    BeforeCommit
}

public sealed record FinancialEvidenceIntegrityScanV1(bool Valid, IReadOnlyList<string> ReasonCodes, int RecordCount, int AuditCount);

public sealed class FinancialEvidenceSqliteStoreV1
{
    public const string SchemaVersion = "financial-evidence-sqlite-v1";
    internal const int AuthorityConcurrencyCapacity = 4;
    private static readonly TimeSpan DefaultAuthorityTimeout = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim AuthorityAdmission = new(AuthorityConcurrencyCapacity, AuthorityConcurrencyCapacity);
    private static int _activeAuthorityWork;
    private static int _peakAuthorityWork;
    private static int _orphanedAuthorityWork;
    private readonly string _connectionString;
    private readonly FinancialEvidenceRetrievalGateV1 _gate;
    private readonly FinancialEvidenceStoreFailpointV1 _failpoint;
    private readonly IFinancialEvidenceEntitlementAuthorityV1? _entitlementAuthority;
    private readonly TimeSpan _authorityTimeout;
    private int _activeRetrievalConnections;
    private int _activeRetrievalTransactions;

    internal int ActiveRetrievalConnections => Volatile.Read(ref _activeRetrievalConnections);
    internal int ActiveRetrievalTransactions => Volatile.Read(ref _activeRetrievalTransactions);
    internal static int ActiveAuthorityWork => Volatile.Read(ref _activeAuthorityWork);
    internal static int PeakAuthorityWork => Volatile.Read(ref _peakAuthorityWork);
    internal static int OrphanedAuthorityWork => Volatile.Read(ref _orphanedAuthorityWork);

    public FinancialEvidenceSqliteStoreV1(
        string databasePath,
        FinancialEvidenceRetrievalGateV1? gate = null,
        FinancialEvidenceStoreFailpointV1 failpoint = FinancialEvidenceStoreFailpointV1.None,
        IFinancialEvidenceEntitlementAuthorityV1? entitlementAuthority = null,
        TimeSpan? authorityTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A database path is required.", nameof(databasePath));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        _gate = gate ?? new();
        _failpoint = failpoint;
        _entitlementAuthority = entitlementAuthority;
        _authorityTimeout = authorityTimeout ?? DefaultAuthorityTimeout;
        if (_authorityTimeout <= TimeSpan.Zero || _authorityTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(authorityTimeout));
        Initialize();
    }

    public async Task AppendAsync(FinancialEvidenceRecordV1 record, CancellationToken cancellationToken = default)
    {
        record.VerifyIntegrity();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ValidateReferencesAsync(connection, transaction, record, cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO financial_evidence_records(record_id,record_version,content_hash,record_hash,envelope,inserted_at) VALUES($id,$version,$content,$record,$envelope,$at)";
                command.Parameters.AddWithValue("$id", record.RecordId);
                command.Parameters.AddWithValue("$version", record.RecordVersion);
                command.Parameters.AddWithValue("$content", record.ContentHash);
                command.Parameters.AddWithValue("$record", record.RecordHash);
                command.Parameters.AddWithValue("$envelope", FinancialEvidenceCanonicalizerV1.SerializeRecord(record));
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertLinksAsync(connection, transaction, record, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (FinancialEvidenceValidationException) { await RollbackQuietlyAsync(transaction); throw; }
        catch (Exception ex) { await RollbackQuietlyAsync(transaction); throw new FinancialEvidenceValidationException("evidence.append-failed", ex); }
    }

    public async Task<FinancialEvidenceRetrievalResultV1> RetrieveAsync(FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken = default)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        try { FinancialEvidenceRetrievalGateV1.ValidateRequest(request); }
        catch { return await AuditFailureAsync(request, requestedAt, FinancialEvidenceRetrievalOutcomeV1.Rejected, ["evidence.request-invalid"], cancellationToken); }

        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken);
            var snapshotEvaluation = EvaluateCandidates(snapshot, request);
            IReadOnlyDictionary<string, AuthorityDecision> authorityDecisions;
            try { authorityDecisions = await ResolveWholeRequestAuthorityAsync(snapshotEvaluation.Eligible, request, cancellationToken); }
            catch (AuthorityCapacityExhaustedException) { return Error("evidence.entitlement-capacity-exhausted"); }

            await using var connectionLease = await OpenRetrievalConnectionAsync(cancellationToken);
            var connection = connectionLease.Connection;
            await using var transactionLease = await BeginRetrievalTransactionAsync(connection, cancellationToken);
            var transaction = transactionLease.Transaction;
            try
            {
                var candidates = await ReadCandidatesAsync(connection, transaction, cancellationToken);
                EnsureSnapshotUnchanged(snapshot, candidates);
                var finalEvaluation = EvaluateCandidates(candidates, request);
                var returned = new List<FinancialEvidenceRecordV1>();
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                var requestHash = FinancialEvidenceCanonicalizerV1.RequestHash(request);
                foreach (var candidate in candidates)
                {
                    var candidateReasons = finalEvaluation.Reasons[candidate.RecordId].ToList();
                    if (candidateReasons.Count == 0)
                    {
                        if (!authorityDecisions.TryGetValue(candidate.RecordId, out var decision) ||
                            decision.RecordRef != candidate.ExactRef() ||
                            !string.Equals(decision.RequestHash, requestHash, StringComparison.Ordinal))
                            throw new FinancialEvidenceValidationException("evidence.authority-scope-changed");
                        if (decision.ReasonCode is not null) candidateReasons.Add(decision.ReasonCode);
                    }
                    if (candidateReasons.Count == 0) returned.Add(candidate);
                    else foreach (var reason in candidateReasons.Distinct(StringComparer.Ordinal)) counts[reason] = counts.GetValueOrDefault(reason) + 1;
                }
                if (returned.Count > request.MaximumResults) throw new FinancialEvidenceValidationException("evidence.result-limit");

                var outcome = returned.Count > 0 ? FinancialEvidenceRetrievalOutcomeV1.Returned : candidates.Count == 0 ? FinancialEvidenceRetrievalOutcomeV1.Empty : FinancialEvidenceRetrievalOutcomeV1.Rejected;
                var reasons = counts.Keys.Order(StringComparer.Ordinal).ToArray();
                var audit = CreateAudit(request, requestedAt, candidates.Count, counts, returned, outcome, reasons);
                var auditBytes = SerializeAuditBounded(audit);
                InjectAuditFailure();
                await InsertAuditAsync(connection, transaction, audit, auditBytes, cancellationToken);
                if (_failpoint == FinancialEvidenceStoreFailpointV1.BeforeCommit) throw new IOException("transaction commit unavailable");
                await transaction.CommitAsync(cancellationToken);

                // Payload becomes observable only after the exact audit transaction commits.
                return new(outcome, returned, audit, reasons);
            }
            catch (OperationCanceledException) { await RollbackQuietlyAsync(transaction); return Error("evidence.cancelled"); }
            catch { await RollbackQuietlyAsync(transaction); return Error("evidence.audit-failed"); }
        }
        catch (OperationCanceledException) { return Error("evidence.cancelled"); }
        catch { return Error("evidence.audit-failed"); }
    }

    public async Task<FinancialEvidenceIntegrityScanV1> ScanIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var reasons = new List<string>();
        var records = 0;
        var audits = 0;
        await using var connection = await OpenAsync(cancellationToken);
        var loaded = new List<FinancialEvidenceRecordV1>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT record_id,record_version,content_hash,record_hash,envelope FROM financial_evidence_records";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records++;
                try
                {
                    var record = FinancialEvidenceCanonicalizerV1.DeserializeRecord((byte[])reader[4]);
                    record.VerifyIntegrity();
                    if (record.RecordId != reader.GetString(0) || record.RecordVersion != reader.GetString(1) || record.ContentHash != reader.GetString(2) || record.RecordHash != reader.GetString(3)) throw new InvalidDataException();
                    loaded.Add(record);
                }
                catch { reasons.Add("evidence.record-tampered"); }
            }
        }
        if (loaded.Count == records && ValidateGraph(loaded).Count > 0) reasons.Add("evidence.graph-invalid");
        var expectedLinks = loaded.SelectMany(record =>
            record.Draft.SupersedesRecordRefs.Select((reference, index) => ($"{record.RecordId}:supersedes:{index}", record.RecordId, reference.RecordId, "supersedes", JsonSerializer.SerializeToUtf8Bytes(reference)))
                .Concat(record.Draft.SupersededByRecordRefs.Select((reference, index) => ($"{record.RecordId}:superseded-by:{index}", record.RecordId, reference.RecordId, "superseded-by", JsonSerializer.SerializeToUtf8Bytes(reference)))))
            .ToDictionary(x => x.Item1, StringComparer.Ordinal);
        var observedLinks = 0;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT link_id,source_id,target_id,kind,exact_ref FROM financial_evidence_links";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                observedLinks++;
                var id = reader.GetString(0);
                if (!expectedLinks.TryGetValue(id, out var expected) || reader.GetString(1) != expected.Item2 || reader.GetString(2) != expected.Item3 || reader.GetString(3) != expected.Item4 || !((byte[])reader[4]).AsSpan().SequenceEqual(expected.Item5))
                    reasons.Add("evidence.link-tampered");
            }
        }
        if (observedLinks != expectedLinks.Count) reasons.Add("evidence.link-tampered");
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT audit_json,audit_hash FROM financial_evidence_audits";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                audits++;
                var bytes = (byte[])reader[0];
                if (bytes.Length > FinancialEvidenceRecordV1.MaxAuditBytes || FinancialEvidenceCanonicalizerV1.Hash(bytes) != reader.GetString(1)) reasons.Add("evidence.audit-tampered");
            }
        }
        return new(reasons.Count == 0, reasons.Distinct(StringComparer.Ordinal).ToArray(), records, audits);
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS financial_evidence_schema(version TEXT PRIMARY KEY CHECK(version='financial-evidence-sqlite-v1'));
            INSERT OR IGNORE INTO financial_evidence_schema(version) VALUES('financial-evidence-sqlite-v1');
            CREATE TABLE IF NOT EXISTS financial_evidence_records(record_id TEXT PRIMARY KEY,record_version TEXT NOT NULL,content_hash TEXT NOT NULL,record_hash TEXT NOT NULL,envelope BLOB NOT NULL,inserted_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS financial_evidence_links(link_id TEXT PRIMARY KEY,source_id TEXT NOT NULL,target_id TEXT NOT NULL,kind TEXT NOT NULL,exact_ref BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS financial_evidence_audits(audit_id TEXT PRIMARY KEY,audit_json BLOB NOT NULL,audit_hash TEXT NOT NULL,inserted_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS financial_evidence_tombstones(tombstone_id TEXT PRIMARY KEY,body BLOB NOT NULL);
            CREATE TRIGGER IF NOT EXISTS fe_records_no_update BEFORE UPDATE ON financial_evidence_records BEGIN SELECT RAISE(ABORT,'append-only'); END;
            CREATE TRIGGER IF NOT EXISTS fe_records_no_delete BEFORE DELETE ON financial_evidence_records BEGIN SELECT RAISE(ABORT,'append-only'); END;
            CREATE TRIGGER IF NOT EXISTS fe_links_no_update BEFORE UPDATE ON financial_evidence_links BEGIN SELECT RAISE(ABORT,'append-only'); END;
            CREATE TRIGGER IF NOT EXISTS fe_links_no_delete BEFORE DELETE ON financial_evidence_links BEGIN SELECT RAISE(ABORT,'append-only'); END;
            CREATE TRIGGER IF NOT EXISTS fe_audits_no_update BEFORE UPDATE ON financial_evidence_audits BEGIN SELECT RAISE(ABORT,'append-only'); END;
            CREATE TRIGGER IF NOT EXISTS fe_audits_no_delete BEFORE DELETE ON financial_evidence_audits BEGIN SELECT RAISE(ABORT,'append-only'); END;
            CREATE TRIGGER IF NOT EXISTS fe_tombstones_no_update BEFORE UPDATE ON financial_evidence_tombstones BEGIN SELECT RAISE(ABORT,'append-only'); END;
            CREATE TRIGGER IF NOT EXISTS fe_tombstones_no_delete BEFORE DELETE ON financial_evidence_tombstones BEGIN SELECT RAISE(ABORT,'append-only'); END;
            """;
        command.ExecuteNonQuery();
        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT version FROM financial_evidence_schema";
        if (!string.Equals(verify.ExecuteScalar()?.ToString(), SchemaVersion, StringComparison.Ordinal)) throw new FinancialEvidenceValidationException("evidence.schema-incompatible");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=250";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private async Task<List<FinancialEvidenceRecordV1>> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var lease = await OpenRetrievalConnectionAsync(cancellationToken);
        return await ReadCandidatesAsync(lease.Connection, null, cancellationToken);
    }

    private async Task<RetrievalConnectionLease> OpenRetrievalConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        Interlocked.Increment(ref _activeRetrievalConnections);
        return new(this, connection);
    }

    private async Task<RetrievalTransactionLease> BeginRetrievalTransactionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        Interlocked.Increment(ref _activeRetrievalTransactions);
        return new(this, transaction);
    }

    private sealed class RetrievalConnectionLease(FinancialEvidenceSqliteStoreV1 owner, SqliteConnection connection) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public async ValueTask DisposeAsync()
        {
            try { await Connection.DisposeAsync(); }
            finally { Interlocked.Decrement(ref owner._activeRetrievalConnections); }
        }
    }

    private sealed class RetrievalTransactionLease(FinancialEvidenceSqliteStoreV1 owner, SqliteTransaction transaction) : IAsyncDisposable
    {
        public SqliteTransaction Transaction { get; } = transaction;
        public async ValueTask DisposeAsync()
        {
            try { await Transaction.DisposeAsync(); }
            finally { Interlocked.Decrement(ref owner._activeRetrievalTransactions); }
        }
    }

    private static async Task<List<FinancialEvidenceRecordV1>> ReadCandidatesAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var candidates = new List<FinancialEvidenceRecordV1>();
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"SELECT record_id,record_version,content_hash,record_hash,envelope FROM financial_evidence_records ORDER BY inserted_at,record_id LIMIT {FinancialEvidenceRecordV1.MaxCandidates + 1}";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = FinancialEvidenceCanonicalizerV1.DeserializeRecord((byte[])reader[4]);
            record.VerifyIntegrity();
            if (record.RecordId != reader.GetString(0) || record.RecordVersion != reader.GetString(1) || record.ContentHash != reader.GetString(2) || record.RecordHash != reader.GetString(3))
                throw new FinancialEvidenceValidationException("evidence.integrity-failed");
            candidates.Add(record);
        }
        if (candidates.Count > FinancialEvidenceRecordV1.MaxCandidates) throw new FinancialEvidenceValidationException("evidence.candidate-limit");
        return candidates;
    }

    private sealed record CandidateEvaluation(
        IReadOnlyDictionary<string, IReadOnlyList<string>> Reasons,
        IReadOnlyList<FinancialEvidenceRecordV1> Eligible);

    private sealed record AuthorityDecision(FinancialEvidenceRecordRefV1 RecordRef, string RequestHash, string? ReasonCode);

    private CandidateEvaluation EvaluateCandidates(IReadOnlyList<FinancialEvidenceRecordV1> candidates, FinancialEvidenceRetrievalRequestV1 request)
    {
        var graphReasons = ValidateGraph(candidates);
        var reasons = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var eligible = new List<FinancialEvidenceRecordV1>();
        foreach (var candidate in candidates)
        {
            var candidateReasons = _gate.Evaluate(candidate, request).ReasonCodes.ToList();
            if (graphReasons.TryGetValue(candidate.RecordId, out var graphReason)) candidateReasons.Add(graphReason);
            var exactReasons = candidateReasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            reasons.Add(candidate.RecordId, exactReasons);
            if (exactReasons.Length == 0) eligible.Add(candidate);
        }
        return new(reasons, eligible);
    }

    private async Task<IReadOnlyDictionary<string, AuthorityDecision>> ResolveWholeRequestAuthorityAsync(
        IReadOnlyList<FinancialEvidenceRecordV1> candidates,
        FinancialEvidenceRetrievalRequestV1 request,
        CancellationToken callerToken)
    {
        var requestHash = FinancialEvidenceCanonicalizerV1.RequestHash(request);
        if (candidates.Count == 0) return new Dictionary<string, AuthorityDecision>(StringComparer.Ordinal);
        if (_entitlementAuthority is null)
            return candidates.ToDictionary(x => x.RecordId, x => new AuthorityDecision(x.ExactRef(), requestHash, "evidence.entitlement-authority-missing"), StringComparer.Ordinal);
        if (!AuthorityAdmission.Wait(0)) throw new AuthorityCapacityExhaustedException();

        var active = Interlocked.Increment(ref _activeAuthorityWork);
        UpdatePeakAuthorityWork(active);
        var releaseOnExit = true;
        using var authorityCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);

        try
        {
            var authorityTask = ResolveAllAsync();
            var timeoutTask = Task.Delay(_authorityTimeout, CancellationToken.None);
            var callerCancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, callerToken);
            var completed = await Task.WhenAny(authorityTask, timeoutTask, callerCancellationTask);
            if (completed == callerCancellationTask)
            {
                authorityCancellation.Cancel();
                releaseOnExit = false;
                ObserveAndReleaseInBackground(authorityTask);
                throw new OperationCanceledException(callerToken);
            }
            if (completed == timeoutTask)
            {
                authorityCancellation.Cancel();
                releaseOnExit = false;
                ObserveAndReleaseInBackground(authorityTask);
                return candidates.ToDictionary(x => x.RecordId, x => new AuthorityDecision(x.ExactRef(), requestHash, "evidence.entitlement-timeout"), StringComparer.Ordinal);
            }
            return await authorityTask;
        }
        finally
        {
            if (releaseOnExit) ReleaseAuthorityAdmission();
        }

        async Task<IReadOnlyDictionary<string, AuthorityDecision>> ResolveAllAsync()
        {
            var decisions = new Dictionary<string, AuthorityDecision>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                authorityCancellation.Token.ThrowIfCancellationRequested();
                string? reason;
                try
                {
                    var result = await _entitlementAuthority.ResolveAsync(candidate, request, authorityCancellation.Token);
                    reason = result is null ? "evidence.entitlement-error" : result.State switch
                    {
                        FinancialEvidenceEntitlementAuthorityStateV1.Entitled => null,
                        FinancialEvidenceEntitlementAuthorityStateV1.Unentitled => "evidence.unentitled",
                        FinancialEvidenceEntitlementAuthorityStateV1.Unknown => "evidence.entitlement-unknown",
                        _ => "evidence.entitlement-error"
                    };
                }
                catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { reason = "evidence.entitlement-cancelled"; }
                catch { reason = "evidence.entitlement-error"; }
                decisions.Add(candidate.RecordId, new(candidate.ExactRef(), requestHash, reason));
            }
            return decisions;
        }
    }

    private static void ObserveAndReleaseInBackground(Task task)
    {
        Interlocked.Increment(ref _orphanedAuthorityWork);
        _ = task.ContinueWith(
        completed =>
        {
            _ = completed.Exception;
            Interlocked.Decrement(ref _orphanedAuthorityWork);
            ReleaseAuthorityAdmission();
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
    }

    private static void ReleaseAuthorityAdmission()
    {
        Interlocked.Decrement(ref _activeAuthorityWork);
        AuthorityAdmission.Release();
    }

    private static void UpdatePeakAuthorityWork(int active)
    {
        var observed = Volatile.Read(ref _peakAuthorityWork);
        while (active > observed)
        {
            var prior = Interlocked.CompareExchange(ref _peakAuthorityWork, active, observed);
            if (prior == observed) return;
            observed = prior;
        }
    }

    private sealed class AuthorityCapacityExhaustedException : Exception;

    private static void EnsureSnapshotUnchanged(IReadOnlyList<FinancialEvidenceRecordV1> snapshot, IReadOnlyList<FinancialEvidenceRecordV1> current)
    {
        if (snapshot.Count != current.Count) throw new FinancialEvidenceValidationException("evidence.snapshot-changed");
        for (var index = 0; index < snapshot.Count; index++)
            if (snapshot[index].ExactRef() != current[index].ExactRef())
                throw new FinancialEvidenceValidationException("evidence.snapshot-changed");
    }

    private static Dictionary<string, string> ValidateGraph(IReadOnlyList<FinancialEvidenceRecordV1> records)
    {
        var invalid = new Dictionary<string, string>(StringComparer.Ordinal);
        var byId = records.ToDictionary(x => x.RecordId, StringComparer.Ordinal);
        foreach (var record in records)
        {
            foreach (var predecessorRef in record.Draft.SupersedesRecordRefs)
            {
                if (!byId.TryGetValue(predecessorRef.RecordId, out var predecessor) || predecessor.ExactRef() != predecessorRef || !predecessor.Draft.SupersededByRecordRefs.Contains(record.ExactRef()))
                    invalid[record.RecordId] = "evidence.supersession-link-invalid";
            }
            foreach (var successorRef in record.Draft.SupersededByRecordRefs)
            {
                if (!byId.TryGetValue(successorRef.RecordId, out var successor) || successor.ExactRef() != successorRef || !successor.Draft.SupersedesRecordRefs.Contains(record.ExactRef()))
                    invalid[record.RecordId] = "evidence.supersession-link-invalid";
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records) Walk(record, 0);

        var undirected = records.ToDictionary(x => x.RecordId, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var record in records)
        foreach (var next in record.Draft.SupersededByRecordRefs.Where(x => byId.ContainsKey(x.RecordId)))
        { undirected[record.RecordId].Add(next.RecordId); undirected[next.RecordId].Add(record.RecordId); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!seen.Add(record.RecordId)) continue;
            var component = new List<FinancialEvidenceRecordV1>();
            var queue = new Queue<string>(); queue.Enqueue(record.RecordId);
            while (queue.Count > 0) { var id = queue.Dequeue(); component.Add(byId[id]); foreach (var neighbor in undirected[id]) if (seen.Add(neighbor)) queue.Enqueue(neighbor); }
            var leaves = component.Count(x => x.Draft.SupersessionState == SupersessionStateV1.Current && x.Draft.SupersededByRecordRefs.Count == 0);
            if (component.Count > 1 && leaves != 1) foreach (var item in component) invalid[item.RecordId] = "evidence.multiple-current-leaves";
        }
        return invalid;

        void Walk(FinancialEvidenceRecordV1 record, int depth)
        {
            if (depth > FinancialEvidenceRecordV1.MaxTraversalDepth) { invalid[record.RecordId] = "evidence.traversal-depth"; return; }
            if (visited.Contains(record.RecordId)) return;
            if (!visiting.Add(record.RecordId)) { invalid[record.RecordId] = "evidence.supersession-cycle"; return; }
            foreach (var next in record.Draft.SupersededByRecordRefs)
                if (byId.TryGetValue(next.RecordId, out var successor)) Walk(successor, depth + 1);
            visiting.Remove(record.RecordId); visited.Add(record.RecordId);
        }
    }

    private static async Task ValidateReferencesAsync(SqliteConnection connection, SqliteTransaction transaction, FinancialEvidenceRecordV1 record, CancellationToken cancellationToken)
    {
        if (record.Draft.SupersedesRecordRefs.Any(x => x.RecordId == record.RecordId) || record.Draft.SupersededByRecordRefs.Any(x => x.RecordId == record.RecordId))
            throw new FinancialEvidenceValidationException("evidence.supersession-cycle");
        foreach (var reference in record.Draft.InputRecordRefs.Concat(record.Draft.SupersedesRecordRefs).Concat(record.Draft.ContradictionRefs).Concat(record.Draft.ConflictResolutionRecordRefs))
        {
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT record_version,content_hash,record_hash FROM financial_evidence_records WHERE record_id=$id";
            query.Parameters.AddWithValue("$id", reference.RecordId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != reference.RecordVersion || reader.GetString(1) != reference.ContentHash || reader.GetString(2) != reference.RecordHash)
                throw new FinancialEvidenceValidationException("evidence.reference-invalid");
        }
    }

    private static async Task InsertLinksAsync(SqliteConnection connection, SqliteTransaction transaction, FinancialEvidenceRecordV1 record, CancellationToken cancellationToken)
    {
        var links = record.Draft.SupersedesRecordRefs.Select(x => (Kind: "supersedes", Ref: x)).Concat(record.Draft.SupersededByRecordRefs.Select(x => (Kind: "superseded-by", Ref: x))).ToArray();
        for (var index = 0; index < links.Length; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO financial_evidence_links(link_id,source_id,target_id,kind,exact_ref) VALUES($link,$source,$target,$kind,$ref)";
            command.Parameters.AddWithValue("$link", $"{record.RecordId}:{links[index].Kind}:{index}");
            command.Parameters.AddWithValue("$source", record.RecordId);
            command.Parameters.AddWithValue("$target", links[index].Ref.RecordId);
            command.Parameters.AddWithValue("$kind", links[index].Kind);
            command.Parameters.AddWithValue("$ref", JsonSerializer.SerializeToUtf8Bytes(links[index].Ref));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static FinancialEvidenceRetrievalAuditV1 CreateAudit(FinancialEvidenceRetrievalRequestV1 request, DateTimeOffset start, int candidates, IReadOnlyDictionary<string, int> counts, IReadOnlyList<FinancialEvidenceRecordV1> records, FinancialEvidenceRetrievalOutcomeV1 outcome, IReadOnlyList<string> reasons) =>
        new(Guid.NewGuid().ToString("N"), start, DateTimeOffset.UtcNow, "financial-evidence-gate-v1", SchemaVersion, "financial-evidence-rule-v1", request.ConsumerId, request.ConsumerCorrelationId, request.Purpose, request.Jurisdiction, request.CallerTraceId, FinancialEvidenceCanonicalizerV1.RequestHash(request), request.AsOf, request.MaximumAge, candidates, counts, records.Select(x => x.ExactRef()).ToArray(), outcome, reasons);

    private byte[] SerializeAuditBounded(FinancialEvidenceRetrievalAuditV1 audit)
    {
        if (_failpoint == FinancialEvidenceStoreFailpointV1.AuditSerialization) throw new JsonException("audit serialization unavailable");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(audit);
        if (bytes.Length > FinancialEvidenceRecordV1.MaxAuditBytes) throw new FinancialEvidenceValidationException("evidence.audit-size-limit");
        return bytes;
    }

    private void InjectAuditFailure()
    {
        switch (_failpoint)
        {
            case FinancialEvidenceStoreFailpointV1.BeforeAuditInsert: throw new IOException("audit persistence unavailable");
            case FinancialEvidenceStoreFailpointV1.AuditDiskFull: throw new IOException("audit disk unavailable");
            case FinancialEvidenceStoreFailpointV1.AuditLocked: throw new SqliteException("audit store locked", 5);
            case FinancialEvidenceStoreFailpointV1.AuditCancellation: throw new OperationCanceledException("audit cancelled");
        }
    }

    private static async Task InsertAuditAsync(SqliteConnection connection, SqliteTransaction transaction, FinancialEvidenceRetrievalAuditV1 audit, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO financial_evidence_audits(audit_id,audit_json,audit_hash,inserted_at) VALUES($id,$json,$hash,$at)";
        command.Parameters.AddWithValue("$id", audit.AuditId);
        command.Parameters.AddWithValue("$json", bytes);
        command.Parameters.AddWithValue("$hash", FinancialEvidenceCanonicalizerV1.Hash(bytes));
        command.Parameters.AddWithValue("$at", audit.CompletedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<FinancialEvidenceRetrievalResultV1> AuditFailureAsync(FinancialEvidenceRetrievalRequestV1 request, DateTimeOffset start, FinancialEvidenceRetrievalOutcomeV1 outcome, IReadOnlyList<string> reasons, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var audit = CreateAudit(request, start, 0, new Dictionary<string, int>(), [], outcome, reasons);
            var bytes = SerializeAuditBounded(audit);
            InjectAuditFailure();
            await InsertAuditAsync(connection, transaction, audit, bytes, cancellationToken);
            if (_failpoint == FinancialEvidenceStoreFailpointV1.BeforeCommit) throw new IOException("transaction commit unavailable");
            await transaction.CommitAsync(cancellationToken);
            return new(outcome, [], audit, reasons);
        }
        catch { return Error("evidence.audit-failed"); }
    }

    private static FinancialEvidenceRetrievalResultV1 Error(string reason) => new(FinancialEvidenceRetrievalOutcomeV1.Error, [], null, [reason]);
    private static async Task RollbackQuietlyAsync(SqliteTransaction transaction) { try { await transaction.RollbackAsync(CancellationToken.None); } catch { } }
}

using Microsoft.Data.Sqlite;
using WpeAgent.FinancialEvidence;

namespace WPE.Tests;

public sealed class FinancialEvidenceStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-fe-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "evidence.sqlite");

    [Fact]
    public async Task AppendRejectsDuplicateIdAndDirectMutation()
    {
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath); var record = FinancialEvidenceRecordContractRedTests.CreateRecord();
        await store.AppendAsync(record);
        await Assert.ThrowsAsync<FinancialEvidenceValidationException>(() => store.AppendAsync(record));
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"); await connection.OpenAsync();
        foreach (var sql in new[] { "UPDATE financial_evidence_records SET record_version='x'", "DELETE FROM financial_evidence_records", "UPDATE financial_evidence_links SET kind='x'", "DELETE FROM financial_evidence_links", "UPDATE financial_evidence_audits SET audit_hash='x'", "DELETE FROM financial_evidence_tombstones" })
        { await using var command = connection.CreateCommand(); command.CommandText = sql; if (sql.Contains("audits") || sql.Contains("tombstones") || sql.Contains("links")) { await SeedProtectedTable(connection, sql); } await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync()); }
    }

    [Fact]
    public async Task RetrievalReturnsExactRecordAndCommitsExactAuditAtomically()
    {
        var gate = new FinancialEvidenceRetrievalGateV1(); var store = new FinancialEvidenceSqliteStoreV1(DatabasePath, gate, entitlementAuthority: FinancialEvidenceRecordContractRedTests.EntitledAuthority); var record = FinancialEvidenceRecordContractRedTests.CreateRecord(); await store.AppendAsync(record);
        var result = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Returned, result.Outcome); Assert.Same(record.GetType(), Assert.Single(result.Records).GetType());
        Assert.Equal(record.ExactRef(), Assert.Single(result.Audit!.ReturnedRecordRefs)); Assert.True(gate.InvocationCount > 0);
        var scan = await store.ScanIntegrityAsync(); Assert.True(scan.Valid); Assert.Equal(1, scan.RecordCount); Assert.Equal(1, scan.AuditCount);
    }

    [Fact]
    public async Task ReturnedRecordsAndAuditTuplesPreserveDeterministicExactOrder()
    {
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: FinancialEvidenceRecordContractRedTests.EntitledAuthority);
        var first = FinancialEvidenceRecordContractRedTests.CreateRecord(id: "ordered-a");
        var second = FinancialEvidenceRecordContractRedTests.CreateRecord(id: "ordered-b");
        await store.AppendAsync(first); await store.AppendAsync(second);
        var result = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal([first.RecordId, second.RecordId], result.Records.Select(x => x.RecordId));
        Assert.Equal(result.Records.Select(x => x.ExactRef()), result.Audit!.ReturnedRecordRefs);
    }

    [Theory]
    [InlineData(FinancialEvidenceStoreFailpointV1.BeforeAuditInsert)]
    [InlineData(FinancialEvidenceStoreFailpointV1.AuditSerialization)]
    [InlineData(FinancialEvidenceStoreFailpointV1.AuditDiskFull)]
    [InlineData(FinancialEvidenceStoreFailpointV1.AuditLocked)]
    [InlineData(FinancialEvidenceStoreFailpointV1.AuditCancellation)]
    [InlineData(FinancialEvidenceStoreFailpointV1.BeforeCommit)]
    public async Task AuditFailureReleasesNoPayloadAndLeavesNoAudit(FinancialEvidenceStoreFailpointV1 failpoint)
    {
        var writer = new FinancialEvidenceSqliteStoreV1(DatabasePath); await writer.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var failing = new FinancialEvidenceSqliteStoreV1(DatabasePath, failpoint: failpoint); var result = await failing.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Error, result.Outcome); Assert.Empty(result.Records); Assert.Null(result.Audit);
        var scan = await writer.ScanIntegrityAsync(); Assert.Equal(0, scan.AuditCount);
    }

    [Fact]
    public async Task EmptyAndRejectedAttemptsAreAudited()
    {
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: FinancialEvidenceRecordContractRedTests.EntitledAuthority);
        var empty = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest()); Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Empty, empty.Outcome); Assert.NotNull(empty.Audit);
        await store.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord("Quarantined"));
        var rejected = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest()); Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, rejected.Outcome); Assert.Contains("evidence.quarantined", rejected.ReasonCodes); Assert.Empty(rejected.Records);
        Assert.Equal(2, (await store.ScanIntegrityAsync()).AuditCount);
    }

    [Fact]
    public async Task ReferenceMismatchAndSelfSupersessionFailWithoutPartialRows()
    {
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath); var record = FinancialEvidenceRecordContractRedTests.CreateRecord();
        var bad = FinancialEvidenceRecordV1.Create(record.Draft with { RecordId = "child", InputRecordRefs = [new("missing", record.ContentHash, record.RecordHash, record.RecordVersion)] });
        await Assert.ThrowsAsync<FinancialEvidenceValidationException>(() => store.AppendAsync(bad));
        var selfDraft = record.Draft with { SupersedesRecordRefs = [record.ExactRef()] }; var self = FinancialEvidenceRecordV1.Create(selfDraft);
        await Assert.ThrowsAsync<FinancialEvidenceValidationException>(() => store.AppendAsync(self)); Assert.Equal(0, (await store.ScanIntegrityAsync()).RecordCount);
    }

    [Fact]
    public async Task ValidBidirectionalCorrectionChainReturnsExactlyCurrentLeaf()
    {
        var (predecessor, leaf) = CreateValidPair("valid");
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: FinancialEvidenceRecordContractRedTests.EntitledAuthority);
        await store.AppendAsync(predecessor); await store.AppendAsync(leaf);
        var result = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Returned, result.Outcome);
        Assert.Equal(leaf.RecordId, Assert.Single(result.Records).RecordId);
        Assert.True((await store.ScanIntegrityAsync()).Valid);
    }

    [Fact]
    public async Task BrokenBidirectionalChainAndMultipleCurrentLeavesAreRejected()
    {
        var rootDraft = FinancialEvidenceRecordContractRedTests.CreateRecord(id: "root-broken").Draft with { SupersessionState = SupersessionStateV1.Superseded };
        var root = FinancialEvidenceRecordV1.Create(rootDraft);
        var child = FinancialEvidenceRecordV1.Create(rootDraft with { RecordId = "child-broken", SupersessionState = SupersessionStateV1.Current, SupersedesRecordRefs = [root.ExactRef()] });
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath);
        await store.AppendAsync(root); await store.AppendAsync(child);
        var broken = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, broken.Outcome); Assert.Empty(broken.Records);

        Dispose();
        var baseRoot = FinancialEvidenceRecordV1.Create(rootDraft with { RecordId = "root-fork" });
        var leafOne = FinancialEvidenceRecordV1.Create(rootDraft with { RecordId = "leaf-one", SupersessionState = SupersessionStateV1.Current, SupersedesRecordRefs = [baseRoot.ExactRef()] });
        var leafTwo = FinancialEvidenceRecordV1.Create(rootDraft with { RecordId = "leaf-two", SupersessionState = SupersessionStateV1.Current, SupersedesRecordRefs = [baseRoot.ExactRef()] });
        var forkRoot = FinancialEvidenceRecordV1.Create(baseRoot.Draft with { SupersededByRecordRefs = [leafOne.ExactRef(), leafTwo.ExactRef()] });
        store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: FinancialEvidenceRecordContractRedTests.EntitledAuthority);
        await store.AppendAsync(forkRoot); await store.AppendAsync(leafOne); await store.AppendAsync(leafTwo);
        var fork = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, fork.Outcome); Assert.Empty(fork.Records);
        Assert.Contains("evidence.multiple-current-leaves", fork.ReasonCodes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task TwoNodeAndDeepSupersessionCyclesFailClosed(int count)
    {
        var records = Enumerable.Range(0, count).Select(i => FinancialEvidenceRecordContractRedTests.CreateRecord(id: $"cycle-{i}")).ToArray();
        records = records.Select((record, i) => FinancialEvidenceRecordV1.Create(record.Draft with { SupersededByRecordRefs = [records[(i + 1) % count].ExactRef()] })).ToArray();
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: FinancialEvidenceRecordContractRedTests.EntitledAuthority);
        foreach (var record in records) await store.AppendAsync(record);
        var result = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, result.Outcome); Assert.Empty(result.Records);
        Assert.False((await store.ScanIntegrityAsync()).Valid);
    }

    [Fact]
    public async Task TraversalDepthAndAuditSizeBoundsFailClosed()
    {
        var records = CreateChain(FinancialEvidenceRecordV1.MaxTraversalDepth + 2);
        var store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: FinancialEvidenceRecordContractRedTests.EntitledAuthority);
        foreach (var record in records) await store.AppendAsync(record);
        var deep = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Rejected, deep.Outcome); Assert.Empty(deep.Records);

        Dispose();
        store = new FinancialEvidenceSqliteStoreV1(DatabasePath, entitlementAuthority: FinancialEvidenceRecordContractRedTests.EntitledAuthority);
        await store.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
        var oversized = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest(new string('x', FinancialEvidenceRecordV1.MaxAuditBytes)));
        Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Error, oversized.Outcome); Assert.Empty(oversized.Records); Assert.Null(oversized.Audit);
        Assert.Equal(0, (await store.ScanIntegrityAsync()).AuditCount);
    }

    private static (FinancialEvidenceRecordV1 Predecessor, FinancialEvidenceRecordV1 Leaf) CreateValidPair(string suffix)
    {
        var predecessor = FinancialEvidenceRecordV1.Create(FinancialEvidenceRecordContractRedTests.CreateRecord(id: "predecessor-" + suffix).Draft with { SupersessionState = SupersessionStateV1.Superseded });
        var leaf = FinancialEvidenceRecordV1.Create(FinancialEvidenceRecordContractRedTests.CreateRecord(id: "leaf-" + suffix).Draft with { SupersedesRecordRefs = [predecessor.ExactRef()] });
        predecessor = FinancialEvidenceRecordV1.Create(predecessor.Draft with { SupersededByRecordRefs = [leaf.ExactRef()] });
        return (predecessor, leaf);
    }

    private static IReadOnlyList<FinancialEvidenceRecordV1> CreateChain(int count)
    {
        var records = new List<FinancialEvidenceRecordV1>();
        for (var i = 0; i < count; i++)
        {
            var draft = FinancialEvidenceRecordContractRedTests.CreateRecord(id: $"depth-{i}").Draft with
            {
                SupersessionState = i == count - 1 ? SupersessionStateV1.Current : SupersessionStateV1.Superseded,
                SupersedesRecordRefs = i == 0 ? [] : [records[i - 1].ExactRef()]
            };
            records.Add(FinancialEvidenceRecordV1.Create(draft));
        }
        for (var i = 0; i < count - 1; i++) records[i] = FinancialEvidenceRecordV1.Create(records[i].Draft with { SupersededByRecordRefs = [records[i + 1].ExactRef()] });
        return records;
    }

    private static async Task SeedProtectedTable(SqliteConnection c, string sql)
    {
        await using var q = c.CreateCommand();
        q.CommandText = sql.Contains("audits") ? "INSERT OR IGNORE INTO financial_evidence_audits VALUES('seed',x'7b7d','sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-01-01T00:00:00Z')"
            : sql.Contains("links") ? "INSERT OR IGNORE INTO financial_evidence_links VALUES('seed','source','target','seed',x'7b7d')"
            : "INSERT OR IGNORE INTO financial_evidence_tombstones VALUES('seed',x'00')";
        await q.ExecuteNonQueryAsync();
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

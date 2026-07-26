using System.Text;
using WpeAgent.FinancialEvidence;

namespace WPE.Tests;

public sealed class FinancialEvidenceRecordContractRedTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 22, 1, 2, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = ObservedAt.AddMinutes(1);

    public static TheoryData<string, string> IneligibleRecords => new()
    {
        { "Quarantined", "evidence.quarantined" }, { "Stale", "evidence.stale" },
        { "Withdrawn", "evidence.withdrawn" }, { "Conflicted", "evidence.conflicted" },
        { "Unapproved", "evidence.unapproved" }, { "Unentitled", "evidence.unentitled" },
        { "IncompatibleSchema", "evidence.schema-incompatible" }
    };

    [Theory]
    [MemberData(nameof(IneligibleRecords))]
    public void RetrievalGate_RejectsIneligibleFinancialEvidence(string fixtureState, string expectedReasonCode)
    {
        var decision = ContractAdapter.EvaluateEligibility(CreateRecord(fixtureState), CreateRequest());
        Assert.False(decision.Eligible);
        Assert.Contains(expectedReasonCode, decision.ReasonCodes);
    }

    [Fact]
    public void EligibleApprovedRecord_PreservesExactIdentityIntegrityTimeAndTrace()
    {
        var source = CreateRecord("Approved");
        var record = ContractAdapter.RoundTripRecord(source);
        Assert.Equal("evidence-record-0001", record.RecordId);
        Assert.Equal(source.ContentHash, record.ContentHash);
        Assert.Equal(source.RecordHash, record.RecordHash);
        Assert.Equal("7", record.RecordVersion);
        Assert.Equal(ObservedAt, record.ObservedAt);
        Assert.Equal("trace-exact", record.TraceId);
    }

    [Fact]
    public void RetrievalAudit_RequiresConsumerCorrelationAndExactReturnedIdsAndHashes()
    {
        var source = CreateRecord("Approved");
        var audit = ContractAdapter.CaptureRetrievalAudit(CreateRequest(), [source]);
        Assert.Equal("consumer-correlation-exact", audit.ConsumerCorrelationId);
        var returned = Assert.Single(audit.ReturnedRecordRefs);
        Assert.Equal("evidence-record-0001", returned.RecordId);
        Assert.Equal(source.ContentHash, returned.ContentHash);
        Assert.Equal(source.RecordHash, returned.RecordHash);
        Assert.Equal("7", returned.RecordVersion);
    }

    internal static FinancialEvidenceRecordV1 CreateRecord(string state = "Approved", string id = "evidence-record-0001", string trace = "trace-exact")
    {
        var draft = new FinancialEvidenceRecordDraftV1(id, "7", FinancialEvidenceCollectionTypeV1.ApprovedFact, "wpe.financial-evidence", "1", "application/json", Encoding.UTF8.GetBytes("{\"fact\":\"bounded\"}"), ObservedAt.AddSeconds(-1), ObservedAt, ObservedAt,
            state == "Stale" ? AsOf : AsOf.AddHours(1), "UTC", "local-fixture", "dataset-1", "publisher-1", "scope-1", "license-1", "fixture", "1", "BTC-USDT", "BTCUSDT", "Bitcoin", "Crypto", "BINANCE", "USDT", "test-agent", "1", "none", "rule-1", [], 1m, "none", [],
            state switch { "Withdrawn" => LifecycleStateV1.Withdrawn, "Unapproved" => LifecycleStateV1.Unapproved, _ => LifecycleStateV1.Approved },
            state == "Quarantined" ? CustodyStateV1.Quarantined : CustodyStateV1.Released,
            state == "Conflicted" ? ConflictStateV1.Conflicted : ConflictStateV1.Clear,
            state == "Unentitled" ? EntitlementStateV1.Unentitled : EntitlementStateV1.Entitled,
            SupersessionStateV1.Current, trace, "source-correlation-exact", [], [], null, [], ObservedAt, "test-recorder");
        var record = FinancialEvidenceRecordV1.Create(draft);
        return state == "IncompatibleSchema" ? record with { Draft = record.Draft with { SchemaVersion = "2" } } : record;
    }

    internal static FinancialEvidenceRetrievalRequestV1 CreateRequest(string correlation = "consumer-correlation-exact", DateTimeOffset? asOf = null) => new("strategy-research", correlation, "research", "JP", new HashSet<FinancialEvidenceCollectionTypeV1> { FinancialEvidenceCollectionTypeV1.ApprovedFact }, "BTC-USDT", "BINANCE", ObservedAt.AddHours(-1), ObservedAt.AddHours(1), asOf ?? AsOf, TimeSpan.FromHours(1), new HashSet<string> { "1" }, "scope-1", "caller-trace", 10);

    private static class ContractAdapter
    {
        private static readonly FinancialEvidenceRetrievalGateV1 Gate = new();
        public static FinancialEvidenceEligibilityV1 EvaluateEligibility(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request) => Gate.Evaluate(record, request);
        public static FinancialEvidenceRecordV1 RoundTripRecord(FinancialEvidenceRecordV1 record) => ThroughAtomicStore(record, CreateRequest()).Records.Single();
        public static FinancialEvidenceRetrievalAuditV1 CaptureRetrievalAudit(FinancialEvidenceRetrievalRequestV1 request, IReadOnlyList<FinancialEvidenceRecordV1> records)
        {
            var result = ThroughAtomicStore(records.Single(), request);
            return result.Audit ?? throw new Xunit.Sdk.XunitException("Production atomic retrieval did not commit an audit.");
        }
        private static FinancialEvidenceRetrievalResultV1 ThroughAtomicStore(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request)
        {
            var directory = Path.Combine(Path.GetTempPath(), "wpe-fe-contract-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FinancialEvidenceSqliteStoreV1(Path.Combine(directory, "contract.sqlite"), Gate, entitlementAuthority: EntitledAuthority);
                store.AppendAsync(record).GetAwaiter().GetResult();
                return store.RetrieveAsync(request).GetAwaiter().GetResult();
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }

    internal static IFinancialEvidenceEntitlementAuthorityV1 EntitledAuthority { get; } = new FixedEntitledAuthority();

    private sealed class FixedEntitledAuthority : IFinancialEvidenceEntitlementAuthorityV1
    {
        public Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken) =>
            Task.FromResult(new FinancialEvidenceEntitlementAuthorityResultV1(FinancialEvidenceEntitlementAuthorityStateV1.Entitled, "authority.entitled"));
    }
}

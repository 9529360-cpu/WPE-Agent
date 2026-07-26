using WpeAgent.FinancialEvidence;

namespace WPE.Tests;

public sealed class FinancialEvidenceAntiStubTests
{
    [Fact]
    public void Gate_UsesTypedProductionStateAndVariedOpaqueValues()
    {
        var gate = new FinancialEvidenceRetrievalGateV1();
        var first = FinancialEvidenceRecordContractRedTests.CreateRecord(id: "opaque-" + Guid.NewGuid().ToString("N"), trace: Guid.NewGuid().ToString("N"));
        var second = FinancialEvidenceRecordContractRedTests.CreateRecord("Quarantined", "opaque-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        Assert.True(gate.Evaluate(first, FinancialEvidenceRecordContractRedTests.CreateRequest(Guid.NewGuid().ToString("N"))).Eligible);
        var denied = gate.Evaluate(second, FinancialEvidenceRecordContractRedTests.CreateRequest(Guid.NewGuid().ToString("N")));
        Assert.False(denied.Eligible); Assert.Contains("evidence.quarantined", denied.ReasonCodes);
        Assert.NotEqual(first.RecordHash, second.RecordHash); Assert.True(gate.InvocationCount >= 2);
    }

    [Fact]
    public void Gate_ReportsSimultaneousDefectsWithoutFixtureReasonEcho()
    {
        var gate = new FinancialEvidenceRetrievalGateV1();
        var record = FinancialEvidenceRecordContractRedTests.CreateRecord("Withdrawn") with
        {
            Draft = FinancialEvidenceRecordContractRedTests.CreateRecord("Withdrawn").Draft with { CustodyState = CustodyStateV1.Quarantined, EntitlementState = EntitlementStateV1.Unentitled }
        };
        var result = gate.Evaluate(record, FinancialEvidenceRecordContractRedTests.CreateRequest());
        Assert.Contains("evidence.withdrawn", result.ReasonCodes); Assert.Contains("evidence.quarantined", result.ReasonCodes); Assert.Contains("evidence.unentitled", result.ReasonCodes); Assert.Contains("evidence.integrity-failed", result.ReasonCodes);
    }

    [Fact]
    public void PublicStoreSurface_HasNoUngatedRecordQuery()
    {
        var methods = typeof(FinancialEvidenceSqliteStoreV1).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly).Select(x => x.Name).ToArray();
        Assert.DoesNotContain(methods, x => x.Contains("Get", StringComparison.OrdinalIgnoreCase) || x.Contains("Query", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(FinancialEvidenceSqliteStoreV1.RetrieveAsync), methods);
    }

    [Fact]
    public async Task AtomicStoreInvokesRealEntitlementAuthorityBeforeRelease()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpe-fe-authority-" + Guid.NewGuid().ToString("N"));
        var authority = new WitnessAuthority();
        try
        {
            var store = new FinancialEvidenceSqliteStoreV1(Path.Combine(directory, "authority.sqlite"), entitlementAuthority: authority);
            authority.ResourceProbe = () => (store.ActiveRetrievalConnections, store.ActiveRetrievalTransactions);
            await store.AppendAsync(FinancialEvidenceRecordContractRedTests.CreateRecord());
            var result = await store.RetrieveAsync(FinancialEvidenceRecordContractRedTests.CreateRequest(Guid.NewGuid().ToString("N")));
            Assert.Equal(FinancialEvidenceRetrievalOutcomeV1.Returned, result.Outcome); Assert.Single(result.Records); Assert.Equal(1, authority.Invocations);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class WitnessAuthority : IFinancialEvidenceEntitlementAuthorityV1
    {
        public int Invocations { get; private set; }
        public Func<(int Connections, int Transactions)>? ResourceProbe { get; set; }
        public Task<FinancialEvidenceEntitlementAuthorityResultV1> ResolveAsync(FinancialEvidenceRecordV1 record, FinancialEvidenceRetrievalRequestV1 request, CancellationToken cancellationToken)
        {
            Invocations++;
            Assert.Equal((0, 0), ResourceProbe!());
            return Task.FromResult(new FinancialEvidenceEntitlementAuthorityResultV1(FinancialEvidenceEntitlementAuthorityStateV1.Entitled, "authority.entitled"));
        }
    }
}

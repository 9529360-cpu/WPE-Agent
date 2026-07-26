using System.Text;
using WpeAgent.FinancialEvidence;
using 币安量化机器人.Services.MarketData;

namespace WPE.Tests;

public sealed class ModelOffCollectionCorpusTests
{
    [Fact]
    public void AuthorizedLocalMarketFixturesAreDeterministicAndDeduplicated()
    {
        var record = MarketRecord();
        var first = PublicMarketRuntime.CollectAuthorizedLocalFixtures([record, record], Request());
        var second = PublicMarketRuntime.CollectAuthorizedLocalFixtures([record], Request());
        Assert.True(first.Accepted);
        Assert.Equal(second.Accepted, first.Accepted);
        Assert.Equal(Assert.Single(second.Items), Assert.Single(first.Items));
        Assert.Equal("BTCUSDT", Assert.Single(first.Items).Symbol);
    }

    [Theory]
    [InlineData("Quarantined", "evidence.quarantined")]
    [InlineData("Stale", "evidence.stale")]
    [InlineData("Withdrawn", "evidence.withdrawn")]
    [InlineData("Conflicted", "evidence.conflicted")]
    [InlineData("Unapproved", "evidence.unapproved")]
    [InlineData("Unentitled", "evidence.unentitled")]
    [InlineData("IncompatibleSchema", "evidence.schema-incompatible")]
    public void KnownRejectionFixturesRejectTheWholeCorpus(string state, string reason)
    {
        var rejected = state == "IncompatibleSchema"
            ? FinancialEvidenceRecordContractRedTests.CreateRecord(state, "bad")
            : MarketRecord(state, "bad");
        var result = new AuthorizedLocalCorpusV1().Collect([MarketRecord(), rejected], Request());
        Assert.False(result.Accepted);
        Assert.Empty(result.Records);
        Assert.Contains(reason, result.ReasonCodes);
    }

    [Fact]
    public void EmptyTamperedMalformedAndConflictingInputsFailClosed()
    {
        Assert.False(new AuthorizedLocalCorpusV1().Collect([], Request()).Accepted);
        Assert.False(new AuthorizedLocalCorpusV1().Collect(null, Request()).Accepted);
        var valid = MarketRecord();
        var tampered = valid with { Draft = valid.Draft with { Payload = Encoding.UTF8.GetBytes("{}") } };
        Assert.False(PublicMarketRuntime.CollectAuthorizedLocalFixtures([tampered], Request()).Accepted);
        var other = FinancialEvidenceRecordV1.Create(valid.Draft with { Payload = Encoding.UTF8.GetBytes("{\"changePercent\":2,\"price\":50001,\"symbol\":\"BTCUSDT\",\"volume\":43}") });
        var conflict = new AuthorizedLocalCorpusV1().Collect([valid, other], Request());
        Assert.False(conflict.Accepted);
        Assert.Contains("evidence.corpus-conflict", conflict.ReasonCodes);
    }

    [Fact]
    public void SupersededAndMissingAuthorizationScopeFailClosed()
    {
        var current = MarketRecord();
        var superseded = FinancialEvidenceRecordV1.Create(current.Draft with { RecordId = "old", SupersessionState = SupersessionStateV1.Superseded });
        Assert.Contains("evidence.superseded", new AuthorizedLocalCorpusV1().Collect([superseded], Request()).ReasonCodes);
        var request = Request() with { EntitlementScope = "" };
        var missing = new AuthorizedLocalCorpusV1().Collect([current], request);
        Assert.False(missing.Accepted);
        Assert.Contains("evidence.scope-mismatch", missing.ReasonCodes);
        Assert.Contains("evidence.unentitled", missing.ReasonCodes);
    }

    internal static FinancialEvidenceRecordV1 MarketRecord(string state = "Approved", string id = "market-1")
    {
        var source = FinancialEvidenceRecordContractRedTests.CreateRecord(state, id);
        return FinancialEvidenceRecordV1.Create(source.Draft with { Payload = Encoding.UTF8.GetBytes("{\"changePercent\":1.5,\"price\":50000.25,\"symbol\":\"BTCUSDT\",\"volume\":42}") });
    }

    internal static FinancialEvidenceRetrievalRequestV1 Request() => FinancialEvidenceRecordContractRedTests.CreateRequest();
}

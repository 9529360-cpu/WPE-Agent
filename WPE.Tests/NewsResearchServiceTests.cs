using System.Text;
using WpeAgent.FinancialEvidence;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class NewsResearchServiceTests
{
    [Fact]
    public void AuthorizedLocalNewsFixtureProjectsWithoutConstructingService()
    {
        var source = FinancialEvidenceRecordContractRedTests.CreateRecord(id: "news-1");
        var record = FinancialEvidenceRecordV1.Create(source.Draft with { Payload = Encoding.UTF8.GetBytes("{\"affectedAssets\":[\"BTC\"],\"bodySummary\":\"Approved local fixture.\",\"reliability\":\"official\",\"title\":\"Market update\"}") });
        var result = NewsResearchService.CollectAuthorizedLocalFixtures([record], ModelOffCollectionCorpusTests.Request());
        Assert.True(result.Accepted);
        Assert.Equal(record.ContentHash, Assert.Single(result.Items).DuplicateGroup);
    }

    [Fact]
    public void MalformedAndRevokedNewsFixturesProduceNoPartialOutput()
    {
        var revoked = FinancialEvidenceRecordContractRedTests.CreateRecord("Withdrawn", "news-revoked");
        Assert.False(NewsResearchService.CollectAuthorizedLocalFixtures([revoked], ModelOffCollectionCorpusTests.Request()).Accepted);
        var source = FinancialEvidenceRecordContractRedTests.CreateRecord(id: "news-malformed");
        var malformed = FinancialEvidenceRecordV1.Create(source.Draft with { Payload = Encoding.UTF8.GetBytes("{\"title\":\"missing fields\"}") });
        var result = NewsResearchService.CollectAuthorizedLocalFixtures([malformed], ModelOffCollectionCorpusTests.Request());
        Assert.False(result.Accepted);
        Assert.Empty(result.Items);
        Assert.Contains("evidence.payload-malformed", result.ReasonCodes);
    }
}

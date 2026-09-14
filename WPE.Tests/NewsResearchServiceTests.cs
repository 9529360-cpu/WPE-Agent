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

    [Theory]
    [InlineData("https://www.sec.gov/news/press-release", "sec.gov", true)]
    [InlineData("https://sec.gov/news/press-release", "sec.gov", true)]
    [InlineData("https://www.coindesk.com/markets/article", "coindesk.com", true)]
    [InlineData("http://www.sec.gov/news/press-release", "sec.gov", false)]
    [InlineData("https://sec.gov.attacker.example/news", "sec.gov", false)]
    [InlineData("https://attacker.example/?next=sec.gov", "sec.gov", false)]
    [InlineData("https://user@www.sec.gov/news", "sec.gov", false)]
    [InlineData("https://www.sec.gov:444/news", "sec.gov", false)]
    [InlineData("file:///etc/passwd", "sec.gov", false)]
    public void NetworkEvidence_OnlyAcceptsHttpsWithinConfiguredSourceDomain(string uri,string trustedDomain,bool expected)
    {
        Assert.Equal(expected,NewsResearchService.IsTrustedContentUri(uri,trustedDomain));
    }

    [Fact]
    public void NetworkEvidence_DisablesAmbientRedirectsAndBoundsXmlParsing()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","Agent","NewsResearchService.cs"));

        Assert.Contains("AllowAutoRedirect=false",source,StringComparison.Ordinal);
        Assert.Contains("UseCookies=false",source,StringComparison.Ordinal);
        Assert.Contains("DtdProcessing=DtdProcessing.Prohibit",source,StringComparison.Ordinal);
        Assert.Contains("XmlResolver=null",source,StringComparison.Ordinal);
        Assert.Contains("MaximumFeedBytes",source,StringComparison.Ordinal);
        Assert.Contains("MaximumArticleBytes",source,StringComparison.Ordinal);
        Assert.Contains("News redirect left the trusted source domain",source,StringComparison.Ordinal);
    }
}
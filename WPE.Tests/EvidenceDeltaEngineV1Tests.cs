using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class EvidenceDeltaEngineV1Tests
{
    [Fact]
    public void FirstEvidencePackEstablishesBaselineWithoutInventingChanges()
    {
        var current=Pack(
            DateTime.UtcNow,
            price:100m,
            sourceState:EvidenceSourceRunState.Available);

        var delta=EvidenceDeltaEngineV1.Compare(current,null);

        Assert.False(delta.HasBaseline);
        Assert.Empty(delta.Signals);
        Assert.False(delta.HasMaterialChange);
    }

    [Fact]
    public void DetectsMaterialPriceMoveAndSourceDegradation()
    {
        var now=DateTime.UtcNow;
        var previous=Pack(
            now.AddMinutes(-15),
            price:100m,
            sourceState:EvidenceSourceRunState.Available);
        var current=Pack(
            now,
            price:102m,
            sourceState:EvidenceSourceRunState.Degraded);

        var delta=EvidenceDeltaEngineV1.Compare(current,previous,priceMoveThresholdPercent:.75m);

        Assert.True(delta.HasBaseline);
        Assert.Contains(delta.Signals,x=>x.Kind==EvidenceDeltaKind.PriceMove&&x.Subject=="BTCUSDT");
        Assert.Contains(delta.Signals,x=>x.Kind==EvidenceDeltaKind.SourceDegradation&&x.Subject=="BTCUSDT:market");
        Assert.Equal(1,delta.SourceDegradationCount);
    }

    [Fact]
    public void NewBreakingNewsUsesStableIdentityAndDoesNotRepeatExistingItem()
    {
        var now=DateTime.UtcNow;
        var existing=new NewsEvidence(
            "source-a","Existing item","https://example.test/existing",now.AddMinutes(-20),now.AddMinutes(-20),
            "high","group-existing",["BTCUSDT"],IsBreaking:true);
        var fresh=new NewsEvidence(
            "source-b","Fresh item","https://example.test/fresh",now,now,
            "high","group-fresh",["BTCUSDT"],IsBreaking:true);
        var previous=Pack(now.AddMinutes(-15),100m,EvidenceSourceRunState.Available,[existing]);
        var current=Pack(now,100m,EvidenceSourceRunState.Available,[existing,fresh]);

        var delta=EvidenceDeltaEngineV1.Compare(current,previous);

        var signal=Assert.Single(delta.Signals.Where(x=>x.Kind==EvidenceDeltaKind.BreakingNews));
        Assert.Contains("Fresh item",signal.Detail,StringComparison.Ordinal);
        Assert.DoesNotContain(delta.Signals,x=>x.Key=="news:group-existing");
    }

    private static EvidencePack Pack(
        DateTime collectedAt,
        decimal price,
        EvidenceSourceRunState sourceState,
        IReadOnlyList<NewsEvidence>? news=null)
    {
        var market=new MarketEvidence(
            "BTCUSDT",price,price*.98m,price*1.02m,50,0,0,0,
            new DerivativesSnapshot(0,1,1,1,1,1,0),collectedAt);

        return new EvidencePack
        {
            CollectedAt=collectedAt,
            Markets=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"]=market
            },
            SourceRuns=
            [
                new EvidenceSourceRunV1(
                    "BTCUSDT:market",
                    "test-provider",
                    sourceState,
                    collectedAt,
                    sourceState.ToString())
            ],
            News=news??Array.Empty<NewsEvidence>()
        };
    }
}

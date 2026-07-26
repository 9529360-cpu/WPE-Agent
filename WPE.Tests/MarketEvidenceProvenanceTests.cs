using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class MarketEvidenceProvenanceTests
{
    private static readonly DateTime Now=new(2026,7,27,12,0,0,DateTimeKind.Utc);
    [Fact]
    public void CanonicalProvenanceBindsProviderEnvironmentSymbolTimeAndFacts()
    {
        var market=Market();var bound=market with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"binance-futures","Testnet")};
        Assert.True(MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(bound));
        Assert.False(MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(bound with{Price=101}));
        Assert.False(MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(bound with{Symbol="ETHUSDT"}));
        Assert.False(MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(bound with{Provenance=bound.Provenance! with{Environment="Mainnet"}}));
    }
    [Fact]
    public void MissingOrTamperedCanonicalBytesFailClosed()
    {
        var market=Market();var provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"binance-futures","Testnet");
        Assert.False(MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market));
        Assert.False(MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market with{Provenance=provenance with{CanonicalBytes=[..provenance.CanonicalBytes,0]}}));
    }
    private static MarketEvidence Market()=>new("BTCUSDT",100,98,102,55,.1,.2,.3,new(0,1,1,1,1,1,0),Now){Candles=[new(Now.AddMinutes(-15),99,101,98,100,10,1000,5,4)]};
}

using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class CryptoInstrumentFundamentalTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,12,0,0,TimeSpan.Zero);

    [Fact]
    public void OfficialExchangeInfoProducesCanonicalTestnetInstrumentFacts()
    {
        using var document=JsonDocument.Parse("""{"symbols":[{"symbol":"BTCUSDT","status":"TRADING","contractType":"PERPETUAL","baseAsset":"BTC","quoteAsset":"USDT","marginAsset":"USDT","onboardDate":1569398400000},{"symbol":"OLDUSDT","status":"BREAK","contractType":"PERPETUAL","baseAsset":"OLD","quoteAsset":"USDT","marginAsset":"USDT","onboardDate":1569398400000}]}""");var facts=BinanceInstrumentFundamentalParserV1.Parse(document.RootElement,new Dictionary<string,string>{{"BTCUSDT","BTCUSDT"},{"OLDUSDT","OLDUSDT"}},Now);var fact=Assert.Single(facts);Assert.Equal("BTCUSDT",fact.Symbol);Assert.Equal("BTC",fact.BaseAsset);Assert.Equal("USDT",fact.MarginAsset);Assert.True(CryptoInstrumentFundamentalCanonicalizerV1.IsCanonical(fact,Now));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("wrong-margin")]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("tampered")]
    public void InvalidFundamentalEvidenceFailsClosed(string defect)
    {
        if(defect=="duplicate"){using var duplicate=JsonDocument.Parse("""{"symbols":[{"symbol":"BTCUSDT","status":"TRADING","contractType":"PERPETUAL","baseAsset":"BTC","quoteAsset":"USDT","marginAsset":"USDT","onboardDate":1569398400000},{"symbol":"BTCUSDT","status":"TRADING","contractType":"PERPETUAL","baseAsset":"BTC","quoteAsset":"USDT","marginAsset":"USDT","onboardDate":1569398400000}]}""");Assert.Throws<InvalidOperationException>(()=>BinanceInstrumentFundamentalParserV1.Parse(duplicate.RootElement,new Dictionary<string,string>{{"BTCUSDT","BTCUSDT"}},Now));return;}
        var fact=CryptoInstrumentFundamentalCanonicalizerV1.Create("binance-futures","Testnet","BTCUSDT","BTCUSDT","BTC","USDT","USDT","PERPETUAL","TRADING",Now.AddYears(-2),Now,new string('a',64));fact=defect switch{"wrong-margin"=>fact with{MarginAsset="BNB"},"future"=>CryptoInstrumentFundamentalCanonicalizerV1.Create("binance-futures","Testnet","BTCUSDT","BTCUSDT","BTC","USDT","USDT","PERPETUAL","TRADING",Now.AddYears(-2),Now.AddSeconds(1),new string('a',64)),"stale"=>CryptoInstrumentFundamentalCanonicalizerV1.Create("binance-futures","Testnet","BTCUSDT","BTCUSDT","BTC","USDT","USDT","PERPETUAL","TRADING",Now.AddYears(-2),Now.AddHours(-25),new string('a',64)),"tampered"=>fact with{CanonicalBytes=[..fact.CanonicalBytes,0]},_=>fact};Assert.False(CryptoInstrumentFundamentalCanonicalizerV1.IsCanonical(fact,Now));
    }
}

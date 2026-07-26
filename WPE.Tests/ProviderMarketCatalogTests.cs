using System.Text.Json;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class ProviderMarketCatalogTests
{
    [Fact]
    public void BinanceCatalogRoundTripsArbitrarySymbolWithoutGuessing()
    {
        using var json = JsonDocument.Parse("{\"symbols\":[{\"symbol\":\"SOLUSDT\",\"status\":\"TRADING\",\"contractType\":\"PERPETUAL\"},{\"symbol\":\"OLDUSDT\",\"status\":\"BREAK\",\"contractType\":\"PERPETUAL\"}]}");
        var markets = BinanceMarketCatalogParser.Parse(
            json.RootElement,
            "binance-futures",
            "binance-futures",
            symbol => symbol,
            true,
            true,
            DateTimeOffset.UtcNow);

        var sol = Assert.Single(markets);
        Assert.Equal("SOLUSDT", sol.Instrument.CanonicalSymbol);
        Assert.Equal("SOLUSDT", sol.Instrument.NativeSymbol);
        Assert.Equal(CapabilityStatus.Available, sol.Status);
    }

    [Fact]
    public void CatalogParserRejectsMissingSymbolsAsErrorAtBoundary()
    {
        using var json = JsonDocument.Parse("{}");
        Assert.Throws<InvalidOperationException>(() => BinanceMarketCatalogParser.Parse(
            json.RootElement,
            "binance-futures",
            "binance-futures",
            symbol => symbol,
            true,
            true,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void OkxCatalogRoundTripsOnlyLiveLinearUsdtSwaps()
    {
        using var json = JsonDocument.Parse("""
            {"code":"0","data":[
              {"instType":"SWAP","instId":"SOL-USDT-SWAP","ctType":"linear","settleCcy":"USDT","state":"live"},
              {"instType":"SWAP","instId":"BTC-USD-SWAP","ctType":"inverse","settleCcy":"BTC","state":"live"},
              {"instType":"SWAP","instId":"OLD-USDT-SWAP","ctType":"linear","settleCcy":"USDT","state":"suspend"},
              {"instType":"SPOT","instId":"ETH-USDT","ctType":"","settleCcy":"","state":"live"}
            ]}
            """);
        var markets = OkxMarketCatalogParser.Parse(
            json.RootElement,
            "okx",
            "okx",
            symbol => symbol.Replace("-USDT-SWAP", "USDT", StringComparison.OrdinalIgnoreCase),
            true,
            true,
            DateTimeOffset.UtcNow);

        var sol = Assert.Single(markets);
        Assert.Equal("SOLUSDT", sol.Instrument.CanonicalSymbol);
        Assert.Equal("SOL-USDT-SWAP", sol.Instrument.NativeSymbol);
        Assert.Equal(MarketType.Perpetual, sol.Instrument.MarketType);
        Assert.True(sol.TestnetAvailable);
    }

    [Fact]
    public void OkxCatalogParserRejectsMissingDataAtBoundary()
    {
        using var json = JsonDocument.Parse("{\"code\":\"0\"}");
        Assert.Throws<InvalidOperationException>(() => OkxMarketCatalogParser.Parse(
            json.RootElement,
            "okx",
            "okx",
            symbol => symbol,
            true,
            true,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BybitCatalogRoundTripsOnlyTradingLinearUsdtPerpetuals()
    {
        using var json = JsonDocument.Parse("""
            {"retCode":0,"result":{"category":"linear","list":[
              {"symbol":"SOLUSDT","contractType":"LinearPerpetual","settleCoin":"USDT","status":"Trading"},
              {"symbol":"BTCUSD","contractType":"InversePerpetual","settleCoin":"BTC","status":"Trading"},
              {"symbol":"OLDUSDT","contractType":"LinearPerpetual","settleCoin":"USDT","status":"Settling"},
              {"symbol":"ETHUSDC","contractType":"LinearPerpetual","settleCoin":"USDC","status":"Trading"},
              {"symbol":"SOLUSDT-30JUL26","contractType":"LinearFutures","settleCoin":"USDT","status":"Trading"}
            ],"nextPageCursor":""}}
            """);
        var markets = BybitMarketCatalogParser.Parse(
            json.RootElement,
            "bybit",
            "bybit",
            symbol => symbol,
            true,
            true,
            DateTimeOffset.UtcNow);

        var sol = Assert.Single(markets);
        Assert.Equal("SOLUSDT", sol.Instrument.CanonicalSymbol);
        Assert.Equal("SOLUSDT", sol.Instrument.NativeSymbol);
        Assert.Equal(MarketType.Perpetual, sol.Instrument.MarketType);
        Assert.True(sol.TestnetAvailable);
    }

    [Fact]
    public void BybitCatalogParserRejectsMissingResultListAtBoundary()
    {
        using var json = JsonDocument.Parse("{\"retCode\":0,\"result\":{}}");
        Assert.Throws<InvalidOperationException>(() => BybitMarketCatalogParser.Parse(
            json.RootElement,
            "bybit",
            "bybit",
            symbol => symbol,
            true,
            true,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void GateCatalogRoundTripsOnlyTradingDirectUsdtPerpetuals()
    {
        using var json = JsonDocument.Parse("""
            [
              {"name":"SOL_USDT","status":"trading","type":"direct","in_delisting":false},
              {"name":"OLD_USDT","status":"trading","type":"direct","in_delisting":true},
              {"name":"ETH_USDT","status":"prelaunch","type":"direct","in_delisting":false},
              {"name":"BTC_USD","status":"trading","type":"inverse","in_delisting":false},
              {"name":"BAD_PAIR_USDT","status":"trading","type":"direct","in_delisting":false}
            ]
            """);
        var markets = GateMarketCatalogParser.Parse(
            json.RootElement,
            "gate",
            "gate",
            symbol => symbol.Replace("_", string.Empty, StringComparison.Ordinal),
            true,
            true,
            DateTimeOffset.UtcNow);

        var sol = Assert.Single(markets);
        Assert.Equal("SOLUSDT", sol.Instrument.CanonicalSymbol);
        Assert.Equal("SOL_USDT", sol.Instrument.NativeSymbol);
        Assert.Equal(MarketType.Perpetual, sol.Instrument.MarketType);
        Assert.True(sol.TestnetAvailable);
    }

    [Fact]
    public void GateCatalogParserRejectsMalformedSchemaAtBoundary()
    {
        using var json = JsonDocument.Parse("[{\"name\":\"SOL_USDT\",\"status\":\"trading\"}]");
        Assert.Throws<InvalidOperationException>(() => GateMarketCatalogParser.Parse(
            json.RootElement,
            "gate",
            "gate",
            symbol => symbol.Replace("_", string.Empty, StringComparison.Ordinal),
            true,
            true,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task GateTestnetCatalogRejectsUnverifiedProfileEndpointBeforeHttp()
    {
        var profile = new ExchangeConnectionProfile
        {
            ProviderId = "gate",
            IsTestnet = true,
            Endpoint = "https://example.invalid"
        };
        await using var provider = new GateExchangeProvider(profile, "test-key", "test-secret");

        var catalog = await provider.DiscoverMarketCatalogAsync(CancellationToken.None);

        Assert.Equal(ProviderCatalogState.Unsupported, catalog.State);
        Assert.Empty(catalog.Markets);
        Assert.Contains("official api-testnet.gateapi.io", catalog.Failure);
    }

    [Fact]
    public void BitgetCatalogRoundTripsOnlyNormalUsdtPerpetuals()
    {
        using var json = JsonDocument.Parse("""
            {"code":"00000","data":[
              {"symbol":"SOLUSDT","baseCoin":"SOL","quoteCoin":"USDT","symbolType":"perpetual","symbolStatus":"normal"},
              {"symbol":"ETHUSDC","baseCoin":"ETH","quoteCoin":"USDC","symbolType":"perpetual","symbolStatus":"normal"},
              {"symbol":"OLDUSDT","baseCoin":"OLD","quoteCoin":"USDT","symbolType":"perpetual","symbolStatus":"restrictedAPI"},
              {"symbol":"BTCUSDT_260925","baseCoin":"BTC","quoteCoin":"USDT","symbolType":"delivery","symbolStatus":"normal"},
              {"symbol":"BADUSDT","baseCoin":"OTHER","quoteCoin":"USDT","symbolType":"perpetual","symbolStatus":"normal"},
              {"symbol":"\u9f99\u864eUSDT","baseCoin":"\u9f99\u864e","quoteCoin":"USDT","symbolType":"perpetual","symbolStatus":"normal"}
            ]}
            """);
        var markets = BitgetMarketCatalogParser.Parse(
            json.RootElement,
            "bitget",
            "bitget",
            symbol => symbol,
            true,
            true,
            DateTimeOffset.UtcNow);

        var sol = Assert.Single(markets);
        Assert.Equal("SOLUSDT", sol.Instrument.CanonicalSymbol);
        Assert.Equal("SOLUSDT", sol.Instrument.NativeSymbol);
        Assert.Equal(MarketType.Perpetual, sol.Instrument.MarketType);
        Assert.True(sol.TestnetAvailable);
    }

    [Fact]
    public void BitgetCatalogParserRejectsMalformedSchemaAtBoundary()
    {
        using var json = JsonDocument.Parse("{\"code\":\"00000\",\"data\":[{\"symbol\":\"SOLUSDT\",\"baseCoin\":\"SOL\"}]}");
        Assert.Throws<InvalidOperationException>(() => BitgetMarketCatalogParser.Parse(
            json.RootElement,
            "bitget",
            "bitget",
            symbol => symbol,
            true,
            true,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BitgetCatalogRequestUsesDemoHeaderOnlyForTestnet()
    {
        var factory = typeof(BitgetExchangeProvider).GetMethod("CreateMarketCatalogRequest", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(factory);
        using var testnet = Assert.IsType<HttpRequestMessage>(factory.Invoke(null, [true]));
        using var mainnet = Assert.IsType<HttpRequestMessage>(factory.Invoke(null, [false]));

        Assert.Equal("/api/v2/mix/market/contracts?productType=USDT-FUTURES", testnet.RequestUri?.ToString());
        Assert.Equal("1", Assert.Single(testnet.Headers.GetValues("paptrading")));
        Assert.False(mainnet.Headers.Contains("paptrading"));
    }

    [Theory]
    [InlineData("https://example.invalid")]
    [InlineData("http://api.bitget.com")]
    [InlineData("https://api.bitget.com.evil.example")]
    [InlineData("https://api.bitget.com/api")]
    public async Task BitgetCatalogRejectsUnverifiedProfileEndpointBeforeHttp(string endpoint)
    {
        var profile = new ExchangeConnectionProfile
        {
            ProviderId = "bitget",
            IsTestnet = true,
            Endpoint = endpoint
        };
        await using var provider = new BitgetExchangeProvider(profile, "test-key", "test-secret", "test-passphrase");

        var catalog = await provider.DiscoverMarketCatalogAsync(CancellationToken.None);

        Assert.Equal(ProviderCatalogState.Unsupported, catalog.State);
        Assert.Empty(catalog.Markets);
        Assert.Contains("official https://api.bitget.com", catalog.Failure);
    }
}

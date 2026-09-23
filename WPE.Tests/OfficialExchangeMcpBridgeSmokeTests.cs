using 币安量化机器人.Services.Exchange;
using 币安量化机器人.Services.Exchange.Mcp;

namespace WPE.Tests;

public sealed class OfficialExchangeMcpBridgeSmokeTests
{
    [Fact]
    public async Task OkxPublicBridgeCanDiscoverAndCallOfficialMcp()
    {
        if(!string.Equals(
            Environment.GetEnvironmentVariable("WPE_RUN_OKX_MCP_SMOKE"),
            "1",
            StringComparison.Ordinal))
            return;

        await using var client=OfficialExchangeMcpClientFactory.CreateOkxPublic();

        var tools=await client.ListToolsAsync(CancellationToken.None);

        Assert.Contains(tools,tool=>tool.Name=="market_get_ticker");
        Assert.Contains(tools,tool=>tool.Name=="market_get_instruments");
        Assert.DoesNotContain(tools,tool=>tool.Name=="swap_place_order");

        var result=await client.CallToolAsync(
            "market_get_ticker",
            new Dictionary<string,object?> { ["instId"]="BTC-USDT-SWAP" },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(
            "market_get_ticker",
            result.StructuredContent.Value.GetProperty("tool").GetString());
        Assert.True(result.StructuredContent.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BinanceLocalReadOnlyBridgeUsesExistingTestnetProvider()
    {
        if(!string.Equals(
            Environment.GetEnvironmentVariable("WPE_RUN_BINANCE_LOCAL_MCP_SMOKE"),
            "1",
            StringComparison.Ordinal))
            return;

        await using var client=OfficialExchangeMcpClientFactory.CreateBinanceLocalTestnet("binance-testnet-default");

        var tools=await client.ListToolsAsync(CancellationToken.None);

        Assert.Contains(tools,tool=>tool.Name=="exchange_health");
        Assert.Contains(tools,tool=>tool.Name=="exchange_get_market");
        Assert.DoesNotContain(tools,tool=>tool.Name=="exchange_place_market");
        Assert.DoesNotContain(tools,tool=>tool.Name=="exchange_cancel_order");

        var result=await client.CallToolAsync(
            "exchange_health",
            new Dictionary<string,object?>(),
            CancellationToken.None);

        Assert.False(result.IsError);
        using var document=System.Text.Json.JsonDocument.Parse(result.Text);
        Assert.Equal("binance-futures",document.RootElement.GetProperty("provider").GetString());
        Assert.Equal("Testnet",document.RootElement.GetProperty("environment").GetString());
        Assert.True(document.RootElement.GetProperty("readOnly").GetBoolean());
        Assert.True(document.RootElement.GetProperty("health").GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task BinanceLocalProviderCatalogTraversesMcpToNativeTestnet()
    {
        if(!string.Equals(
            Environment.GetEnvironmentVariable("WPE_RUN_BINANCE_LOCAL_MCP_SMOKE"),
            "1",
            StringComparison.Ordinal))
            return;

        var profile=new ExchangeConnectionProfile
        {
            Id="binance-mcp-smoke",
            ProviderId="binance-mcp-local",
            DisplayName="Binance Futures Testnet MCP Smoke",
            IsTestnet=true,
            Enabled=true,
            ExecutionEnabled=false,
            Endpoint="https://testnet.binancefuture.com",
            UpstreamConnectionId="binance-testnet-default"
        };

        await using var provider=new ExchangeProviderCatalog().Create(
            profile,
            new Dictionary<string,string>());

        var health=await provider.HealthCheckAsync(CancellationToken.None);
        var rules=await provider.GetRulesAsync("BTCUSDT",CancellationToken.None);

        Assert.Equal("binance-mcp-local",provider.ProviderId);
        Assert.True(health.Healthy);
        Assert.Equal("BTCUSDT",rules.Symbol);
        Assert.True(rules.StepSize>0);
        Assert.True(rules.TickSize>0);
    }
}

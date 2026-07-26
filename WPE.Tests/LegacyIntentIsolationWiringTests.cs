namespace WPE.Tests;

public sealed class LegacyIntentIsolationWiringTests
{
    [Fact]
    public void IsolationRunsBeforeRealtimeAndStartupRecoveryAssessment()
    {
        var source=ReadAutoTradingAgent();
        var create=source.IndexOf("Providers.Create(exchangeProfile,credentials)",StringComparison.Ordinal);
        var isolate=source.IndexOf("var legacyIsolation=await RunLegacyIntentIsolationAsync(exchange,ct)",StringComparison.Ordinal);
        var realtime=source.IndexOf("exchange.CreateRealtimeFeed",StringComparison.Ordinal);
        var recovery=source.IndexOf("AutomaticMutationPath.StartupRecovery",StringComparison.Ordinal);

        Assert.True(create>=0&&isolate>create,"Isolation must use the configured provider instance.");
        Assert.True(realtime>isolate,"Isolation must run before realtime startup.");
        Assert.True(recovery>isolate,"Isolation must run before startup recovery assessment.");
    }

    [Fact]
    public void IsolationCompositionIsTestnetOnlyAndReadOnly()
    {
        var source=ReadAutoTradingAgent();
        var start=source.IndexOf("internal sealed class TestnetLegacyIntentReadOnlyFacts",StringComparison.Ordinal);
        Assert.True(start>=0);
        var adapter=source[start..];

        Assert.Contains("exchange.Environment!=ExchangeEnvironment.Testnet",adapter,StringComparison.Ordinal);
        Assert.Contains("FindOrderAsync",adapter,StringComparison.Ordinal);
        Assert.Contains("GetPositionsAsync",adapter,StringComparison.Ordinal);
        Assert.Contains("GetOpenOrdersAsync",adapter,StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceMarketAsync",adapter,StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceLimitAsync",adapter,StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceProtectionAsync",adapter,StringComparison.Ordinal);
        Assert.DoesNotContain("CancelOrderAsync",adapter,StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredIsolationEntryIsInternalTestnetOnlyAndDoesNotStartAgentLoop()
    {
        var source=ReadAutoTradingAgent();
        var start=source.IndexOf("internal static async Task<LegacyIntentIsolationRunResult> RunConfiguredLegacyIntentIsolationAsync",StringComparison.Ordinal);
        var end=source.IndexOf("\n    }",start,StringComparison.Ordinal);
        Assert.True(start>=0&&end>start);
        var method=source[start..end];

        Assert.Contains("if(!profile.IsTestnet)",method,StringComparison.Ordinal);
        Assert.Contains("RunLegacyIntentIsolationAsync(exchange,ct)",method,StringComparison.Ordinal);
        Assert.DoesNotContain("StartDefault",method,StringComparison.Ordinal);
        Assert.DoesNotContain("Place",method,StringComparison.Ordinal);
        Assert.DoesNotContain("Submit",method,StringComparison.Ordinal);
    }

    private static string ReadAutoTradingAgent()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        return File.ReadAllText(Path.Combine(root,"Services","AutoTradingAgent.cs"));
    }
}

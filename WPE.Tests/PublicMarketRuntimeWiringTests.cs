namespace WPE.Tests;

public sealed class PublicMarketRuntimeWiringTests
{
    [Fact]
    public void RuntimeHost_StartsPublicMarketBeforeAccessReadinessAndOwnsShutdown()
    {
        var host = ReadSource(Path.Combine("Services", "TradingRuntimeHost.cs"));
        var app = ReadSource("App.xaml.cs");
        var start = host.IndexOf("_publicMarketStarted = await StartPublicMarketAsync()", StringComparison.Ordinal);
        var access = host.IndexOf("var ready = await RefreshAccessAsync()", StringComparison.Ordinal);

        Assert.True(start >= 0 && start < access, "Public market runtime must start before access readiness is evaluated.");
        Assert.Contains("await ServiceLocator.PublicMarket.StartAsync()", host, StringComparison.Ordinal);
        Assert.Contains("await ServiceLocator.DisposeAsync()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceLocator.PublicMarket.StartAsync()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceLocator_UsesOneCredentialFreePublicMarketRuntime()
    {
        var source = ReadSource(Path.Combine("Services", "ServiceLocator.cs"));

        Assert.Equal(1, Count(source, "Lazy<PublicMarketRuntime>"));
        Assert.Equal(1, Count(source, "new BinancePublicMarketClient(useTestnet: false)"));
        Assert.Contains("new BinanceStreamClient(useTestnet: false)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BinanceApiClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetApiCredentials", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SubmitOrder", source, StringComparison.Ordinal);
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private static string ReadSource(string relativePath)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, relativePath));
    }
}

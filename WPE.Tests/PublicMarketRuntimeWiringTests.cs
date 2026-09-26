namespace WPE.Tests;

public sealed class PublicMarketRuntimeWiringTests
{
    [Fact]
    public void RuntimeHost_StartsPublicMarketBeforeAccessReadinessAndOwnsShutdown()
    {
        var host = ReadSource(Path.Combine("Services", "TradingRuntimeHost.cs"));
        var app = ReadSource("App.xaml.cs");
        var initializeStart = host.IndexOf("public async Task<bool> InitializeAsync(", StringComparison.Ordinal);
        var initializeEnd = host.IndexOf("public Task<bool> RefreshAccessAsync()", initializeStart, StringComparison.Ordinal);
        Assert.True(initializeStart >= 0 && initializeEnd > initializeStart, "Could not locate TradingRuntimeHost.InitializeAsync.");
        var initialize = host[initializeStart..initializeEnd];

        var start = initialize.IndexOf("_publicMarketStarted = await StartPublicMarketAsync()", StringComparison.Ordinal);
        var headlessAccess = initialize.IndexOf("RefreshAccessProjectionAsync(persistSettings: false)", StringComparison.Ordinal);
        var localAccess = initialize.IndexOf("RefreshAccessAsync()", StringComparison.Ordinal);

        Assert.True(start >= 0, "Public market startup is missing from runtime initialization.");
        Assert.True(headlessAccess > start, "Public market runtime must start before headless observer access projection.");
        Assert.True(localAccess > start, "Public market runtime must start before local access readiness is evaluated.");
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

namespace WPE.Tests;

public sealed class PublicMarketRuntimeWiringTests
{
    [Fact]
    public void App_StartsPublicMarketBeforeSetupAndStopsItOnExit()
    {
        var source = ReadSource("App.xaml.cs");
        var start = source.IndexOf("_ = StartPublicMarketAsync();", StringComparison.Ordinal);
        var setup = source.IndexOf("if (!settings.SetupCompleted)", StringComparison.Ordinal);

        Assert.True(start >= 0 && start < setup, "Public market runtime must start independently of setup/access readiness.");
        Assert.Contains("await ServiceLocator.PublicMarket.StartAsync()", source, StringComparison.Ordinal);
        Assert.Contains("await ServiceLocator.DisposeAsync();", source, StringComparison.Ordinal);
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

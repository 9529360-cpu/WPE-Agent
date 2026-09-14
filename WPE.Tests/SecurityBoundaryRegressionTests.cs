using WpeAgent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class SecurityBoundaryRegressionTests
{
    [Theory]
    [InlineData("https://wpe-reference.local/index.html", true)]
    [InlineData("https://WPE-REFERENCE.LOCAL/monitoring", true)]
    [InlineData("https://wpe-reference.local:443/index.html", true)]
    [InlineData("http://wpe-reference.local/index.html", false)]
    [InlineData("https://wpe-reference.local.attacker.example/index.html", false)]
    [InlineData("https://attacker.example/index.html", false)]
    [InlineData("https://user@wpe-reference.local/index.html", false)]
    [InlineData("https://wpe-reference.local:444/index.html", false)]
    [InlineData("file:///C:/temp/index.html", false)]
    [InlineData("not-a-uri", false)]
    public void WebViewHostBridge_OnlyTrustsExactVirtualHostHttpsOrigin(string source, bool expected)
    {
        Assert.Equal(expected, ReferenceUiWindow.IsTrustedWebViewSource(source));
    }

    [Fact]
    public void WebViewHostBridge_EnforcesOriginAtMessageAndNavigationBoundaries()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "ReferenceUiWindow.xaml.cs"));

        Assert.Contains("IsTrustedWebViewSource(e.Source)", source, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2.NavigationStarting", source, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExchangeProviderCatalog_DefaultPathNeverLoadsExternalAssemblies()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Services", "Exchange", "ExchangeProviderCatalog.cs"));

        Assert.DoesNotContain("LoadFromAssemblyPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AssemblyLoadContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeAdapters", source, StringComparison.Ordinal);
        Assert.Contains("typeof(ExchangeProviderCatalog).Assembly", source, StringComparison.Ordinal);

        var catalog = new ExchangeProviderCatalog();
        Assert.NotEmpty(catalog.Installed);
        Assert.All(catalog.Installed, descriptor => Assert.False(descriptor.SupportsMainnet));
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}

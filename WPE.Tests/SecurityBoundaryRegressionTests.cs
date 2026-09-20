using Microsoft.Data.Sqlite;
using WpeAgent;
using 币安量化机器人.Infrastructure.Runtime;
using 币安量化机器人.Services.Agent;
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

    [Fact]
    public void BinanceSignedTransportAndModeChanges_FailClosedExceptDocumentedNoOpCodes()
    {
        var root = RepositoryRoot();
        var api = File.ReadAllText(Path.Combine(root, "Services", "BinanceApiClient.cs"));
        var adapter = File.ReadAllText(Path.Combine(root, "Services", "Agent", "BinanceFuturesAdapter.cs"));

        Assert.Contains("HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}", api, StringComparison.Ordinal);
        Assert.DoesNotContain("catch(HttpRequestException ex) when(ex.Message.Contains(\"400\"))", adapter, StringComparison.Ordinal);
        Assert.Equal(1, Count(adapter, "ExchangeCode==-4046"));
        Assert.Equal(1, Count(adapter, "ExchangeCode==-4059"));
    }

    [Fact]
    public async Task TradingKernel_ProcessLeaseBlocksSecondRuntimeEvenWithDifferentDatabases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpe-kernel-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var processLease = Path.Combine(directory, "autonomous-trading-kernel.lock");
        var first = new AgentRuntimeSupervisor(
            new AgentSqliteStore(Path.Combine(directory, "first.db")),
            new InProcessAgentEventBus(),
            processLease);
        var second = new AgentRuntimeSupervisor(
            new AgentSqliteStore(Path.Combine(directory, "second.db")),
            new InProcessAgentEventBus(),
            processLease);
        var firstDisposed = false;

        try
        {
            await first.StartAsync(CancellationToken.None);

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(
                () => second.StartAsync(CancellationToken.None));
            Assert.Contains("process lease", blocked.Message, StringComparison.OrdinalIgnoreCase);

            await first.DisposeAsync();
            firstDisposed = true;

            await second.StartAsync(CancellationToken.None);
            Assert.NotEqual("NOT_STARTED", second.Health.RecoveryStatus);
        }
        finally
        {
            if (!firstDisposed) await first.DisposeAsync();
            await second.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TradingKernel_WaitsForStaleDatabaseLeaseAfterProcessLeaseIsFree()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpe-kernel-stale-db-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = new AgentSqliteStore(Path.Combine(directory, "agent.db"));
        var processLease = Path.Combine(directory, "autonomous-trading-kernel.lock");
        var staleOwner = "stale-owner-" + Guid.NewGuid().ToString("N");
        var supervisor = new AgentRuntimeSupervisor(
            database,
            new InProcessAgentEventBus(),
            processLease,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(25));

        try
        {
            Assert.True(await database.TryAcquireRuntimeLeaseAsync(
                "autonomous-trading-kernel",
                staleOwner,
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None));

            await supervisor.StartAsync(CancellationToken.None);

            Assert.NotEqual("NOT_STARTED", supervisor.Health.RecoveryStatus);
            Assert.NotEqual(staleOwner, supervisor.Health.InstanceId);
        }
        finally
        {
            await supervisor.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }


    [Fact]
    public void TradingKernel_LeaseLossIsFailClosedAndNotOnlyTelemetry()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Infrastructure", "Runtime", "AgentRuntimeSupervisor.cs"));

        Assert.Contains("FileShare.None", source, StringComparison.Ordinal);
        Assert.Contains("runtime.lease-renewal-rejected", source, StringComparison.Ordinal);
        Assert.Contains("runtime.lease-renewal-failed", source, StringComparison.Ordinal);
        Assert.Contains("AutoTradingAgent.Pause();", source, StringComparison.Ordinal);
        Assert.Contains("EnsureRuntimeAuthority();", source, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}

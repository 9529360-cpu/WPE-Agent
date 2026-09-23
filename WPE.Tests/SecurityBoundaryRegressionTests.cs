using Microsoft.Data.Sqlite;
using WpeAgent;
using 币安量化机器人.Infrastructure.Runtime;
using 币安量化机器人.Core.Runtime;
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
    public async Task TradingKernel_DoesNotStealLiveDatabaseLeaseAndReleasesProcessLeaseAfterTimeout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpe-kernel-live-db-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "agent.db");
        var database = new AgentSqliteStore(databasePath);
        var processLease = Path.Combine(directory, "autonomous-trading-kernel.lock");
        var liveOwner = "live-owner-" + Guid.NewGuid().ToString("N");
        var contender = new AgentRuntimeSupervisor(
            database,
            new InProcessAgentEventBus(),
            processLease,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(25));
        var successor = new AgentRuntimeSupervisor(
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
                liveOwner,
                TimeSpan.FromSeconds(5),
                CancellationToken.None));

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(
                () => contender.StartAsync(CancellationToken.None));
            Assert.Contains("active runtime lease", blocked.Message, StringComparison.OrdinalIgnoreCase);

            await using(var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT owner_id FROM runtime_leases WHERE name='autonomous-trading-kernel'";
                Assert.Equal(liveOwner, (string?)await command.ExecuteScalarAsync());
            }

            await database.ReleaseRuntimeLeaseAsync("autonomous-trading-kernel", liveOwner, CancellationToken.None);
            await successor.StartAsync(CancellationToken.None);
            Assert.NotEqual("NOT_STARTED", successor.Health.RecoveryStatus);
        }
        finally
        {
            await contender.DisposeAsync();
            await successor.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TradingKernel_VerifiedNonExecutionInterruptionsCanBeRecoveredWithoutMutation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpe-kernel-metadata-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = new AgentSqliteStore(Path.Combine(directory, "agent.db"));
        var processLease = Path.Combine(directory, "autonomous-trading-kernel.lock");
        var supervisor = new AgentRuntimeSupervisor(database, new InProcessAgentEventBus(), processLease);

        try
        {
            await database.BeginWorkflowRunAsync("legacy", "cycle-observation", "{}", default);
            await database.BeginWorkflowRunAsync("old-run", "cycle-risk", "{}", default);
            await database.SaveWorkflowCheckpointAsync(
                new("old-run", "cycle-risk", WorkflowNode.Risk, CheckpointPhase.Entered, "{}", DateTime.UtcNow),
                default);

            await supervisor.StartAsync(default);
            Assert.Equal("RECOVERY_PENDING:2", supervisor.Health.RecoveryStatus);

            var recovered = await supervisor.RecoverNonExecutionInterruptedAsync(
                "startup-recovery.current-state-reconciled",
                default);

            Assert.Equal(2, recovered);
            Assert.Equal("RECOVERED", supervisor.Health.RecoveryStatus);
            Assert.Empty(await database.GetInterruptedWorkflowsAsync(default));
        }
        finally
        {
            await supervisor.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TradingKernel_MetadataRecoveryRefusesExecutionCapableInterruptions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpe-kernel-execution-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = new AgentSqliteStore(Path.Combine(directory, "agent.db"));
        var processLease = Path.Combine(directory, "autonomous-trading-kernel.lock");
        var supervisor = new AgentRuntimeSupervisor(database, new InProcessAgentEventBus(), processLease);

        try
        {
            await database.BeginWorkflowRunAsync("old-run", "cycle-execution", "{}", default);
            await database.SaveWorkflowCheckpointAsync(
                new("old-run", "cycle-execution", WorkflowNode.SafetyExecution, CheckpointPhase.Entered, "{}", DateTime.UtcNow),
                default);

            await supervisor.StartAsync(default);
            Assert.Equal("RECOVERY_PENDING:1", supervisor.Health.RecoveryStatus);

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(
                () => supervisor.RecoverNonExecutionInterruptedAsync("verified-current-state", default));

            Assert.Contains("execution-capable", blocked.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("RECOVERY_PENDING:1", supervisor.Health.RecoveryStatus);
            Assert.Single(await database.GetInterruptedWorkflowsAsync(default));
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

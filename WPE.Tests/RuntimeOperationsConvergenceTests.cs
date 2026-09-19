namespace WPE.Tests;

public sealed class RuntimeOperationsConvergenceTests
{
    [Fact]
    public void HeadlessAndMaintenanceShareOneExplicitDataRootAndLeaseAuthority()
    {
        var root = Root();
        var appData = File.ReadAllText(Path.Combine(root, "Services", "AppDataPaths.cs"));
        var bootstrap = File.ReadAllText(Path.Combine(root, "Services", "RuntimeProcessBootstrap.cs"));
        var headless = File.ReadAllText(Path.Combine(root, "WPE.Headless", "Program.cs"));
        var maintenance = File.ReadAllText(Path.Combine(root, "WPE.Maintenance", "Program.cs"));
        var backup = File.ReadAllText(Path.Combine(root, "Services", "Backup", "RuntimeStateBackupService.cs"));
        var restore = File.ReadAllText(Path.Combine(root, "Services", "Backup", "RuntimeStateRestoreService.cs"));

        Assert.Contains("WPE_AGENT_DATA_ROOT", appData, StringComparison.Ordinal);
        Assert.Contains("AppDataPaths.DataRootEnvironmentVariable", headless, StringComparison.Ordinal);
        Assert.Contains("RequireAbsolutePath(parsed.Options, \"data-root\")", maintenance, StringComparison.Ordinal);

        Assert.Contains("DataRootMaintenanceLease.AcquireProcessLease", bootstrap, StringComparison.Ordinal);
        Assert.Contains("DataRootMaintenanceLease.AcquireExclusiveMaintenanceLease", backup, StringComparison.Ordinal);
        Assert.Contains("DataRootMaintenanceLease.AcquireExclusiveMaintenanceLease", restore, StringComparison.Ordinal);

        var recovery = bootstrap.IndexOf("RuntimeStateRestoreRecovery.RecoverIfNeeded", StringComparison.Ordinal);
        var processLease = bootstrap.IndexOf("DataRootMaintenanceLease.AcquireProcessLease", StringComparison.Ordinal);
        Assert.True(recovery >= 0 && processLease > recovery);
    }

    [Fact]
    public void ServiceAndMaintenanceArtifactsRemainSeparatedFromDesktopPresentation()
    {
        var root = Root();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "dotnet.yml"));
        var product = File.ReadAllText(Path.Combine(root, "币安量化机器人.csproj"));
        var solution = File.ReadAllText(Path.Combine(root, "币安量化机器人.sln"));

        Assert.Contains("Publish headless candidate", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify headless boundary", workflow, StringComparison.Ordinal);
        Assert.Contains("Publish maintenance candidate", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify maintenance artifact boundary", workflow, StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"WPE.Headless\\**\\*.cs\" />", product, StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"WPE.Maintenance\\**\\*.cs\" />", product, StringComparison.Ordinal);
        Assert.Contains("WPE.Headless\\WPE.Headless.csproj", solution, StringComparison.Ordinal);
        Assert.Contains("WPE.Maintenance\\WPE.Maintenance.csproj", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void MaintenanceCannotBecomeAServiceOrTradingControlPlane()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "WPE.Maintenance", "Program.cs"));
        foreach (var forbidden in new[]
        {
            "AddWindowsService",
            "BackgroundService",
            "TradingRuntimeHost",
            "AutoTradingAgent",
            "ServiceLocator",
            "IExchangeAdapter",
            "HttpClient",
            "TcpListener",
            "NamedPipe",
            "Mainnet"
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    private static string Root() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", ".."));
}

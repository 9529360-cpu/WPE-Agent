namespace WPE.Tests;

public sealed class MaintenanceCliBoundaryTests
{
    [Fact]
    public void MaintenanceCliReusesOfflineBackupRestoreAuthorityOnly()
    {
        var root = Root();
        var program = File.ReadAllText(Path.Combine(root, "WPE.Maintenance", "Program.cs"));
        var project = File.ReadAllText(Path.Combine(root, "WPE.Maintenance", "WPE.Maintenance.csproj"));
        var mainProject = File.ReadAllText(Path.Combine(root, "币安量化机器人.csproj"));
        var solution = File.ReadAllText(Path.Combine(root, "币安量化机器人.sln"));

        Assert.Contains("RuntimeStateBackupService", program, StringComparison.Ordinal);
        Assert.Contains("RuntimeStateBackupVerifier", program, StringComparison.Ordinal);
        Assert.Contains("RuntimeStateRestoreService", program, StringComparison.Ordinal);
        Assert.Contains("confirm-backup-id", program, StringComparison.Ordinal);
        Assert.Contains("OperatingSystem.IsWindows()", program, StringComparison.Ordinal);
        var argumentPreflight = program.IndexOf("ValidateOperationArguments(parsed)", StringComparison.Ordinal);
        var layoutConstruction = program.IndexOf("new AppDataLayout(dataRoot)", StringComparison.Ordinal);
        Assert.True(argumentPreflight >= 0 && layoutConstruction > argumentPreflight);
        Assert.Contains("DeleteDirectoryOrFail(staging)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDeleteDirectory", program, StringComparison.Ordinal);
        var cleanup = program.IndexOf("DeleteDirectoryOrFail(staging)", StringComparison.Ordinal);
        var verifiedOutput = program.IndexOf("itemCount = result.Descriptor.Items.Count", cleanup, StringComparison.Ordinal);
        Assert.True(cleanup >= 0 && verifiedOutput > cleanup);
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains("..\\币安量化机器人.csproj", project, StringComparison.Ordinal);
        Assert.Contains("WPE.Maintenance\\**\\*.cs", mainProject, StringComparison.Ordinal);
        Assert.Contains("WPE.Maintenance", solution, StringComparison.Ordinal);

        foreach (var source in new[] { program, project })
        foreach (var forbidden in new[]
        {
            "RuntimeProcessBootstrap",
            "TradingRuntimeHost",
            "AutoTradingAgent",
            "ServiceLocator",
            "TradingExecutionGateway",
            "IExchangeAdapter",
            "HttpClient",
            "TcpListener",
            "NamedPipe",
            "ChangeAuthorizationModeAsync",
            "Mainnet",
            "UseWPF",
            "WebView2"
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreConfirmationIsCheckedAfterAuthenticationAndBeforeMutation()
    {
        var service = File.ReadAllText(Path.Combine(
            Root(), "Services", "Backup", "RuntimeStateRestoreService.cs"));

        var verified = service.IndexOf("VerifyToStagingAsync", StringComparison.Ordinal);
        var confirmation = service.IndexOf("explicit restore confirmation", StringComparison.Ordinal);
        var safety = service.IndexOf("CreateUnderExclusiveLeaseAsync", StringComparison.Ordinal);
        var swap = service.IndexOf(
            "Directory.Move(_layout.DataDirectory, rollback)",
            StringComparison.Ordinal);

        Assert.True(verified >= 0);
        Assert.True(confirmation > verified);
        Assert.True(safety > confirmation);
        Assert.True(swap > confirmation);
    }

    private static string Root() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", ".."));
}

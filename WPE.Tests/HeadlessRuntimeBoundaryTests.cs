namespace WPE.Tests;

public sealed class HeadlessRuntimeBoundaryTests
{
    [Fact]
    public void HeadlessProjectUsesUiNeutralRuntimeTargetAndNoWpf()
    {
        var root=Root();
        var project=File.ReadAllText(Path.Combine(root,"WPE.Headless","WPE.Headless.csproj"));
        var program=File.ReadAllText(Path.Combine(root,"WPE.Headless","Program.cs"));
        var process=File.ReadAllText(Path.Combine(root,"WPE.Headless","HeadlessRuntimeProcess.cs"));
        var solution=File.ReadAllText(Path.Combine(root,"币安量化机器人.sln"));
        var product=File.ReadAllText(Path.Combine(root,"币安量化机器人.csproj"));

        Assert.Contains("<TargetFramework>net8.0</TargetFramework>",project,StringComparison.Ordinal);
        Assert.Contains("<ProjectReference Include=\"..\\币安量化机器人.csproj\" />",project,StringComparison.Ordinal);
        Assert.DoesNotContain("UseWPF",project,StringComparison.Ordinal);
        Assert.DoesNotContain("WebView2",project,StringComparison.Ordinal);
        Assert.DoesNotContain("ScottPlot",project,StringComparison.Ordinal);
        Assert.Contains("HeadlessRuntimeProcess.RunAsync(args)",program,StringComparison.Ordinal);
        Assert.Contains("WPE.Headless\\WPE.Headless.csproj",solution,StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"WPE.Headless\\**\\*.cs\" />",product,StringComparison.Ordinal);

        foreach(var forbidden in new[]{"System.Windows","ReferenceUiWindow","SetupWindow","ActivationWindow","MainWindow"})
            Assert.DoesNotContain(forbidden,process,StringComparison.Ordinal);
    }

    [Fact]
    public void HeadlessStartupFailsClosedBeforeTradingAndSupervisesKernelHealth()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"WPE.Headless","HeadlessRuntimeProcess.cs"));
        var platform=source.IndexOf("OperatingSystem.IsWindows()",StringComparison.Ordinal);
        var bootstrap=source.IndexOf("RuntimeProcessBootstrap.Initialize",StringComparison.Ordinal);
        var license=source.IndexOf("new DeviceLicenseService().TryLoad()",StringComparison.Ordinal);
        var setup=source.IndexOf("!settings.SetupCompleted",StringComparison.Ordinal);
        var host=source.IndexOf("new TradingRuntimeHost(identity)",StringComparison.Ordinal);
        var start=source.IndexOf("InitializeAsync(startAgentWhenReady: true)",StringComparison.Ordinal);

        Assert.True(platform>=0&&bootstrap>platform&&license>bootstrap&&setup>license&&host>setup&&start>host);
        Assert.Contains("settingsStore.LastLoadDiagnostic is not null",source,StringComparison.Ordinal);
        Assert.Contains("headless.access-not-ready",source,StringComparison.Ordinal);
        Assert.Contains("health.LeaseLost",source,StringComparison.Ordinal);
        Assert.Contains("!health.AgentRunning",source,StringComparison.Ordinal);
        Assert.Contains("!health.HeartbeatFresh",source,StringComparison.Ordinal);
        Assert.Contains("AppDataPaths.RuntimeFile(\"headless-health-v1.json\")",source,StringComparison.Ordinal);
        Assert.Contains("File.Move(temp, path, true)",source,StringComparison.Ordinal);

        foreach(var forbidden in new[]{"Activate(","ChangeAuthorizationModeAsync","EmergencyCloseAllAsync","MainnetTradingConfirmed","IExchangeAdapter","ExchangeProviderCatalog"})
            Assert.DoesNotContain(forbidden,source,StringComparison.Ordinal);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

namespace WPE.Tests;

public sealed class HeadlessWindowsServiceBoundaryTests
{
    [Fact]
    public void ServiceModeRequiresExplicitDataRootBeforeHostConstruction()
    {
        var root=Root();
        var project=File.ReadAllText(Path.Combine(root,"WPE.Headless","WPE.Headless.csproj"));
        var program=File.ReadAllText(Path.Combine(root,"WPE.Headless","Program.cs"));
        var worker=File.ReadAllText(Path.Combine(root,"WPE.Headless","HeadlessRuntimeWorker.cs"));

        Assert.Contains("Microsoft.Extensions.Hosting\" Version=\"8.0.1\"",project,StringComparison.Ordinal);
        Assert.Contains("Microsoft.Extensions.Hosting.WindowsServices\" Version=\"8.0.1\"",project,StringComparison.Ordinal);
        var service=program.IndexOf("WindowsServiceHelpers.IsWindowsService()",StringComparison.Ordinal);
        var dataRoot=program.IndexOf("AppDataPaths.DataRootEnvironmentVariable",StringComparison.Ordinal);
        var builder=program.IndexOf("Host.CreateApplicationBuilder(args)",StringComparison.Ordinal);
        Assert.True(service>=0&&dataRoot>service&&builder>dataRoot);
        Assert.Contains("!Path.IsPathRooted(dataRoot)",program,StringComparison.Ordinal);
        Assert.Contains("ServiceDataRootRequiredExitCode",program,StringComparison.Ordinal);
        Assert.Contains("AddWindowsService(options => options.ServiceName = \"WPE Agent Headless\")",program,StringComparison.Ordinal);
        Assert.Contains("AddHostedService<HeadlessRuntimeWorker>()",program,StringComparison.Ordinal);

        Assert.Contains("WindowsServiceHelpers.IsWindowsService()",worker,StringComparison.Ordinal);
        Assert.Contains("Environment.ExitCode = exitCode;",worker,StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(exitCode);",worker,StringComparison.Ordinal);
        Assert.Contains("applicationLifetime.StopApplication();",worker,StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsServiceWorkerCannotBecomeATradingAuthority()
    {
        var worker=File.ReadAllText(Path.Combine(Root(),"WPE.Headless","HeadlessRuntimeWorker.cs"));

        Assert.Contains("HeadlessRuntimeProcess.RunAsync(stoppingToken)",worker,StringComparison.Ordinal);
        foreach(var forbidden in new[]{
            "TradingRuntimeHost","AutoTradingAgent","IExchangeAdapter","ExchangeProviderCatalog",
            "EmergencyCloseAllAsync","ChangeAuthorizationModeAsync","MainnetTradingConfirmed"
        })
            Assert.DoesNotContain(forbidden,worker,StringComparison.Ordinal);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

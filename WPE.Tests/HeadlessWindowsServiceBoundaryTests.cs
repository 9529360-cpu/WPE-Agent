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
        var dataRoot=program.IndexOf("HeadlessDataRootBootstrap.Resolve",StringComparison.Ordinal);
        var processEnvironment=program.IndexOf("EnvironmentVariableTarget.Process",StringComparison.Ordinal);
        var service=program.IndexOf("WindowsServiceHelpers.IsWindowsService()",StringComparison.Ordinal);
        var builder=program.IndexOf("Host.CreateApplicationBuilder(args)",StringComparison.Ordinal);
        Assert.True(dataRoot>=0&&processEnvironment>dataRoot&&service>processEnvironment&&builder>service);
        Assert.Contains("ServiceDataRootInvalidExitCode",program,StringComparison.Ordinal);
        Assert.Contains("ServiceDataRootRequiredExitCode",program,StringComparison.Ordinal);
        Assert.Contains("AddWindowsService(options => options.ServiceName = \"WPE Agent Headless\")",program,StringComparison.Ordinal);
        Assert.Contains("AddHostedService<HeadlessRuntimeWorker>()",program,StringComparison.Ordinal);
        var bootstrap=File.ReadAllText(Path.Combine(root,"WPE.Headless","HeadlessDataRootBootstrap.cs"));
        var appDataPaths=File.ReadAllText(Path.Combine(root,"Services","AppDataPaths.cs"));
        Assert.Contains("ArgumentName = \"--data-root\"",bootstrap,StringComparison.Ordinal);
        Assert.Contains("headless.data-root-conflict",bootstrap,StringComparison.Ordinal);
        Assert.Contains("headless.data-root-duplicate",bootstrap,StringComparison.Ordinal);
        Assert.Contains("DataRootPathPolicy.TryNormalizeFixedLocalRoot",bootstrap,StringComparison.Ordinal);
        Assert.Contains("Path.IsPathFullyQualified",appDataPaths,StringComparison.Ordinal);
        Assert.Contains("DriveType.Fixed",appDataPaths,StringComparison.Ordinal);
        Assert.Contains("candidate.StartsWith(@\"\\\\\"",appDataPaths,StringComparison.Ordinal);

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

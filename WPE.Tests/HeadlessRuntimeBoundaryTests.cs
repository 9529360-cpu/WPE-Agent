using System.Reflection;
using WpeAgent.Headless;

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
        Assert.Contains("builder.Services.AddHostedService<HeadlessRuntimeWorker>()",program,StringComparison.Ordinal);
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
        var setup=source.IndexOf("!settings.SetupCompleted",StringComparison.Ordinal);
        var host=source.IndexOf("new TradingRuntimeHost(settings.ActiveUser)",StringComparison.Ordinal);
        var start=source.IndexOf("InitializeAsync(startAgentWhenReady: true)",StringComparison.Ordinal);

        Assert.True(platform>=0&&bootstrap>platform&&setup>bootstrap&&host>setup&&start>host);
        Assert.DoesNotContain("new DeviceLicenseService().TryLoad()",source,StringComparison.Ordinal);
        Assert.DoesNotContain("headless.license-unavailable",source,StringComparison.Ordinal);
        Assert.Contains("settingsStore.LastLoadDiagnostic is not null",source,StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(settings.ActiveUser)",source,StringComparison.Ordinal);
        Assert.Contains("new TradingRuntimeHost(settings.ActiveUser)",source,StringComparison.Ordinal);
        Assert.DoesNotContain("\"DEVICE-\" + license.License.LicenseId",source,StringComparison.Ordinal);
        Assert.Contains("headless.access-not-ready",source,StringComparison.Ordinal);
        Assert.Contains("ResolveFatalReason(health, startedAt, now)",source,StringComparison.Ordinal);
        Assert.Contains("if (leaseLost) return \"lease-lost\";",source,StringComparison.Ordinal);
        Assert.Contains("if (!agentRunning) return \"agent-not-running\";",source,StringComparison.Ordinal);
        Assert.Contains("!heartbeatFresh) return \"heartbeat-stale\";",source,StringComparison.Ordinal);
        Assert.Contains("AppDataPaths.RuntimeFile(\"headless-health-v1.json\")",source,StringComparison.Ordinal);
        Assert.Contains("File.Move(temp, path, true)",source,StringComparison.Ordinal);
        Assert.Contains("RunAsync(CancellationToken shutdownToken)",source,StringComparison.Ordinal);
        Assert.DoesNotContain("Console.CancelKeyPress",source,StringComparison.Ordinal);
        Assert.DoesNotContain("AppDomain.CurrentDomain.ProcessExit",source,StringComparison.Ordinal);

        foreach(var forbidden in new[]{"Activate(","ChangeAuthorizationModeAsync","EmergencyCloseAllAsync","MainnetTradingConfirmed","IExchangeAdapter","ExchangeProviderCatalog"})
            Assert.DoesNotContain(forbidden,source,StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeUnhealthyReasonIsSpecificAndHeartbeatGetsStartupGrace()
    {
        var method=typeof(HeadlessRuntimeProcess).GetMethod(
            "ResolveFatalReason",
            BindingFlags.Static|BindingFlags.NonPublic,
            binder:null,
            types:[typeof(bool),typeof(bool),typeof(bool),typeof(DateTimeOffset),typeof(DateTimeOffset)],
            modifiers:null);
        Assert.NotNull(method);

        var started=DateTimeOffset.UtcNow;
        string? Reason(bool leaseLost,bool agentRunning,bool heartbeatFresh,TimeSpan elapsed)=>
            (string?)method!.Invoke(null,[leaseLost,agentRunning,heartbeatFresh,started,started+elapsed]);

        Assert.Equal("lease-lost",Reason(true,true,true,TimeSpan.FromSeconds(1)));
        Assert.Equal("agent-not-running",Reason(false,false,true,TimeSpan.FromSeconds(1)));
        Assert.Null(Reason(false,true,false,TimeSpan.FromSeconds(20)));
        Assert.Equal("heartbeat-stale",Reason(false,true,false,TimeSpan.FromSeconds(31)));
        Assert.Null(Reason(false,true,true,TimeSpan.FromMinutes(5)));
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

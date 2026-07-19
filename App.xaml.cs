using System.Windows;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Localization;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人;

public partial class App : global::System.Windows.Application
{
    private static IServiceProvider? _serviceProvider;

    public static IServiceProvider ServiceProvider
    {
        get => _serviceProvider ?? throw new InvalidOperationException("Service provider not initialized");
        set => _serviceProvider = value;
    }

    public App()
    {
        // Initialize Serilog for basic file logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File("logs\\app.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LocalizationService.Current.Initialize();

        if (e.Args.Any(x => string.Equals(x, "--i18n-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = LocalizationSelfTest.Run();
            Log.Information("Localization self-test completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 4);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--exchange-adapter-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await ExchangeAdapterTestRunner.RunAsync();
            Log.Information("Exchange adapter tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:12);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--exchange-public-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await ExchangePublicTestRunner.RunAsync();
            Log.Information("Exchange public endpoint tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:13);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--architecture-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await ArchitectureTestRunner.RunAsync();
            Log.Information("Runtime architecture tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:14);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--strategy-lifecycle-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var result=await StrategyLifecycleTestRunner.RunAsync();
            Log.Information("Strategy lifecycle tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:15);
            return;
        }

        // Configure dependency injection
        var services = new ServiceCollection();
        ServiceProvider = ServiceConfiguration.ConfigureServices(services);

        if (e.Args.Any(x => string.Equals(x, "--license-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await DeviceLicenseTestRunner.RunAsync();
            Log.Information("Device-license tests completed. Success={Success}; Report={Report}",result.Success,result.ReportPath);
            Shutdown(result.Success?0:10);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--device-code", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var path=Path.Combine(AppContext.BaseDirectory,"Data","device-code.txt");Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllTextAsync(path,DeviceLicenseService.GetCurrentDeviceCode());
            Shutdown(0);
            return;
        }

        var activationFileIndex=Array.FindIndex(e.Args,x=>string.Equals(x,"--activate-license",StringComparison.OrdinalIgnoreCase));
        if(activationFileIndex>=0)
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            if(activationFileIndex+1>=e.Args.Length||!File.Exists(e.Args[activationFileIndex+1])){Shutdown(11);return;}
            var activationResult=new DeviceLicenseService().Activate(await File.ReadAllTextAsync(e.Args[activationFileIndex+1]));
            Shutdown(activationResult.Success?0:11);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--access-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await AccessTestRunner.RunAsync();
            Log.Information("Access and credential security tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 8);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--access-live-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await AccessTestRunner.RunLiveAsync();
            Log.Information("Live access readiness test completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 9);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--llm-governance-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await LlmGovernanceTestRunner.RunAsync();
            Log.Information("LLM governance tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 12);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--autonomy-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await AutonomyTestRunner.RunAsync();
            Log.Information("Autonomy fault-injection tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 5);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--four-pillars-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await FourPillarsTestRunner.RunAsync();
            Log.Information("Four-pillar tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 6);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--four-pillars-live-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await FourPillarsTestRunner.RunLiveAsync();
            Log.Information("Four-pillar live tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 7);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await SmokeTestRunner.RunAsync();
            Log.Information("Testnet smoke test completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 2);
            return;
        }

        if (e.Args.Any(x => string.Equals(x, "--maturity-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await MaturityTestRunner.RunAsync();
            Log.Information("Maturity tests completed. Success={Success}; Report={Report}", result.Success, result.ReportPath);
            Shutdown(result.Success ? 0 : 3);
            return;
        }

        // 激活窗关闭后还要继续打开初始化向导或主控制台，不能让 WPF
        // 在两个窗口切换的间隙按“最后窗口关闭”自动终止应用。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var licenseService = new DeviceLicenseService();
        var license = licenseService.TryLoad();
        if (!license.Success)
        {
            var activation = new ActivationWindow(licenseService);
            if (activation.ShowDialog() != true || activation.ActivatedLicense is null) { Shutdown(); return; }
            license = new DeviceLicenseResult(true,"Activation.Valid",activation.ActivatedLicense);
        }
        var localIdentity = "DEVICE-" + license.License!.LicenseId;
        var settingsStore = new AgentSettingsStore();
        var settings = settingsStore.Load();
        settings.ActiveUser = localIdentity;
        settingsStore.Save(settings);
        if (!settings.SetupCompleted)
        {
            var setup = new SetupWindow(localIdentity);
            if (setup.ShowDialog() != true || !setup.SetupCompleted) { Shutdown(); return; }
        }
        var main = new MainWindow(localIdentity);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();

        // Agent 由用户在总控台明确启动，应用打开时不自动下单。
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            await AutoTradingAgent.StopAsync();
            if (ServiceProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}

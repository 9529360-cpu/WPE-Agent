using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Localization;

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

        // Configure dependency injection
        var services = new ServiceCollection();
        ServiceProvider = ServiceConfiguration.ConfigureServices(services);

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

        // 登录窗关闭后还要继续打开初始化向导或主控制台，不能让 WPF
        // 在两个窗口切换的间隙按“最后窗口关闭”自动终止应用。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var login = new LoginWindow();
        if (login.ShowDialog() != true || string.IsNullOrWhiteSpace(login.AuthenticatedUser)) { Shutdown(); return; }
        var settingsStore = new AgentSettingsStore();
        var settings = settingsStore.Load();
        settings.ActiveUser = login.AuthenticatedUser;
        settingsStore.Save(settings);
        if (!settings.SetupCompleted)
        {
            var setup = new SetupWindow(login.AuthenticatedUser);
            if (setup.ShowDialog() != true || !setup.SetupCompleted) { Shutdown(); return; }
        }
        var main = new MainWindow(login.AuthenticatedUser);
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

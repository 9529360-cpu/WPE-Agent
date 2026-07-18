using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

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

        // Configure dependency injection
        var services = new ServiceCollection();
        ServiceProvider = ServiceConfiguration.ConfigureServices(services);

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

        var main = new MainWindow();
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

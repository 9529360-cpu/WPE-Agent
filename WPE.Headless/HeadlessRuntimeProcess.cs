using System.Text.Json;
using Serilog;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.Headless;

public static class HeadlessRuntimeProcess
{
    public const int UnsupportedPlatformExitCode = 40;
    public const int LicenseUnavailableExitCode = 41;
    public const int SetupIncompleteExitCode = 42;
    public const int AccessNotReadyExitCode = 43;
    public const int RuntimeUnhealthyExitCode = 44;
    public const int FatalExitCode = 50;

    private static readonly TimeSpan SupervisionInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupHeartbeatGrace = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions HealthJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        _ = args;
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatformExitCode;

        RuntimeProcessBootstrap.Initialize("headless.log");
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        EventHandler processExit = (_, _) =>
        {
            try { shutdown.Cancel(); } catch (ObjectDisposedException) { }
        };
        Console.CancelKeyPress += cancel;
        AppDomain.CurrentDomain.ProcessExit += processExit;

        try
        {
            var license = new DeviceLicenseService().TryLoad();
            if (!license.Success || license.License is null)
            {
                await TryWriteHealthAsync("blocked", "headless.license-unavailable", null).ConfigureAwait(false);
                return LicenseUnavailableExitCode;
            }

            var settingsStore = new AgentSettingsStore();
            var settings = settingsStore.Load();
            if (settingsStore.LastLoadDiagnostic is not null || !settings.SetupCompleted)
            {
                await TryWriteHealthAsync("blocked", settingsStore.LastLoadDiagnostic is null ? "headless.setup-incomplete" : "headless.settings-invalid", null).ConfigureAwait(false);
                return SetupIncompleteExitCode;
            }

            var identity = "DEVICE-" + license.License.LicenseId;
            await using var host = new TradingRuntimeHost(identity);
            var startedAt = DateTimeOffset.UtcNow;
            var accessReady = await host.InitializeAsync(startAgentWhenReady: true).ConfigureAwait(false);
            var initial = host.ReadHealth();
            await WriteHealthAsync(
                accessReady ? (initial.Ready ? "ready" : "starting") : "blocked",
                accessReady ? "headless.runtime-starting" : "headless.access-not-ready",
                initial,
                CancellationToken.None).ConfigureAwait(false);

            if (!accessReady) return AccessNotReadyExitCode;

            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(SupervisionInterval, shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }

                var health = host.ReadHealth();
                var fatal = health.LeaseLost
                    || !health.AgentRunning
                    || (DateTimeOffset.UtcNow - startedAt > StartupHeartbeatGrace && !health.HeartbeatFresh);
                await WriteHealthAsync(
                    fatal ? "failed" : health.Ready ? "ready" : "degraded",
                    fatal ? "headless.runtime-unhealthy" : health.Ready ? "headless.ready" : "headless.readiness-degraded",
                    health,
                    CancellationToken.None).ConfigureAwait(false);

                if (fatal) return RuntimeUnhealthyExitCode;
            }

            await TryWriteHealthAsync("stopping", "headless.shutdown-requested", host.ReadHealth()).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Headless runtime failed.");
            await TryWriteHealthAsync("failed", "headless.fatal", null).ConfigureAwait(false);
            return FatalExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            AppDomain.CurrentDomain.ProcessExit -= processExit;
            Log.CloseAndFlush();
        }
    }

    private static async Task TryWriteHealthAsync(string state, string code, TradingRuntimeHealthV1? runtime)
    {
        try { await WriteHealthAsync(state, code, runtime, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warning(ex, "Headless health projection could not be persisted."); }
    }

    private static async Task WriteHealthAsync(string state, string code, TradingRuntimeHealthV1? runtime, CancellationToken ct)
    {
        var payload = new HeadlessProcessHealthV1(
            HeadlessProcessHealthV1.CurrentSchema,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            state,
            code,
            runtime);
        var path = AppDataPaths.RuntimeFile("headless-health-v1.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(payload, HealthJson), ct).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

public sealed record HeadlessProcessHealthV1(
    string Schema,
    int ProcessId,
    DateTimeOffset ObservedAtUtc,
    string State,
    string Code,
    TradingRuntimeHealthV1? Runtime)
{
    public const string CurrentSchema = "wpe.headless-process-health/1.0";
}

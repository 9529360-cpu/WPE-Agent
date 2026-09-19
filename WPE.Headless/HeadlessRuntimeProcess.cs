using System.Text.Json;
using Serilog;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.Headless;

public static class HeadlessRuntimeProcess
{
    public const int UnsupportedPlatformExitCode = 40;
    public const int SetupIncompleteExitCode = 42;
    public const int AccessNotReadyExitCode = 43;
    public const int RuntimeUnhealthyExitCode = 44;
    public const int ServiceDataRootRequiredExitCode = 45;
    public const int ServiceDataRootInvalidExitCode = 46;
    public const int FatalExitCode = 50;

    private static readonly TimeSpan SupervisionInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupHeartbeatGrace = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions HealthJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(CancellationToken shutdownToken)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatformExitCode;

        RuntimeProcessBootstrap.Initialize("headless.log");

        try
        {
            var settingsStore = new AgentSettingsStore();
            var settings = settingsStore.Load();
            if (settingsStore.LastLoadDiagnostic is not null || !settings.SetupCompleted || string.IsNullOrWhiteSpace(settings.ActiveUser))
            {
                var code = settingsStore.LastLoadDiagnostic is not null
                    ? "headless.settings-invalid"
                    : !settings.SetupCompleted
                        ? "headless.setup-incomplete"
                        : "headless.active-user-missing";
                await TryWriteHealthAsync("blocked", code, null).ConfigureAwait(false);
                return SetupIncompleteExitCode;
            }

            await using var host = new TradingRuntimeHost(settings.ActiveUser);
            var startedAt = DateTimeOffset.UtcNow;
            var accessReady = await host.InitializeAsync(startAgentWhenReady: true).ConfigureAwait(false);
            var initial = host.ReadHealth();
            await WriteHealthAsync(
                accessReady ? (initial.Ready ? "ready" : "starting") : "blocked",
                accessReady ? "headless.runtime-starting" : "headless.access-not-ready",
                initial,
                CancellationToken.None).ConfigureAwait(false);

            if (!accessReady) return AccessNotReadyExitCode;

            while (!shutdownToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(SupervisionInterval, shutdownToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
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
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
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

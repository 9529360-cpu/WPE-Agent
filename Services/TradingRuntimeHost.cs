using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services;

public sealed record TradingRuntimeHealthV1(
    string Schema,
    DateTimeOffset ObservedAtUtc,
    bool AccessReady,
    DateTimeOffset? AccessCheckedAtUtc,
    bool AgentRunning,
    string AgentStatus,
    string RunId,
    DateTimeOffset? RuntimeHeartbeatAtUtc,
    string RecoveryStatus,
    long EventSequence,
    bool LeaseLost)
{
    public const string CurrentSchema = "wpe.trading-runtime-health/1.0";
    public static readonly TimeSpan MaximumAccessAge = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumHeartbeatAge = TimeSpan.FromSeconds(15);
    public bool AccessFresh => AccessCheckedAtUtc is { } value && value <= ObservedAtUtc && ObservedAtUtc - value <= MaximumAccessAge;
    public bool HeartbeatFresh => RuntimeHeartbeatAtUtc is { } value && value <= ObservedAtUtc && ObservedAtUtc - value <= MaximumHeartbeatAge;
    public bool Ready => AccessReady && AccessFresh && AgentRunning && HeartbeatFresh && !LeaseLost;
}

/// <summary>
/// UI-neutral owner for the WPE process runtime. Presentation shells may create this host,
/// but trading lifecycle, access readiness and runtime projection do not depend on WPF.
/// </summary>
public sealed class TradingRuntimeHost : IAsyncDisposable
{
    private static readonly TimeSpan SnapshotRefreshInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan AccessRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AgentSettingsStore _settingsStore = new();
    private readonly AccessReadinessService _readiness = new();
    private readonly CancellationTokenSource _snapshotPumpCancellation = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly Task _snapshotPumpTask;
    private string _runtimeJson;
    private bool _publicMarketStarted;
    private DateTimeOffset _nextAccessRefreshAtUtc = DateTimeOffset.MinValue;
    private int _accessReady;
    private int _disposed;

    public TradingRuntimeHost(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new ArgumentException("Runtime user identity is required.", nameof(userName));

        UserName = userName;
        BindIdentity(userName);
        _runtimeJson = SerializeSnapshot(RuntimeSnapshotFactory.Create(ServiceLocator.SystemState, DateTime.UtcNow));
        _snapshotPumpTask = Task.Run(() => RunRuntimeSnapshotPumpAsync(_snapshotPumpCancellation.Token));
    }

    public string UserName { get; }

    /// <summary>
    /// Starts non-UI runtime infrastructure, refreshes access truth and optionally starts
    /// the trading agent only when the fresh access report is ready.
    /// </summary>
    public async Task<bool> InitializeAsync(bool startAgentWhenReady = false)
    {
        ThrowIfDisposed();
        await _initializeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_publicMarketStarted)
                _publicMarketStarted = await StartPublicMarketAsync().ConfigureAwait(false);

            var ready = await RefreshAccessAsync().ConfigureAwait(false);
            if (ready && startAgentWhenReady)
                AutoTradingAgent.StartDefault();
            return ready;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<bool> RefreshAccessAsync()
    {
        ThrowIfDisposed();
        var settings = _settingsStore.Load();
        var report = await _readiness.CheckAsync(settings).ConfigureAwait(false);
        PublishAccess(report, settings);
        settings.LastAccessCheckAtUtc = report.CheckedAtUtc;
        _settingsStore.Save(settings);
        Volatile.Write(ref _accessReady, report.Ready ? 1 : 0);
        _nextAccessRefreshAtUtc = DateTimeOffset.UtcNow + AccessRefreshInterval;
        return report.Ready;
    }

    public async Task<bool> StartAgentAsync()
    {
        ThrowIfDisposed();
        if (!await RefreshAccessAsync().ConfigureAwait(false)) return false;
        AutoTradingAgent.StartDefault();
        return true;
    }

    public string BuildRuntimeJson()
    {
        ThrowIfDisposed();
        return Volatile.Read(ref _runtimeJson);
    }

    public TradingRuntimeHealthV1 ReadHealth()
    {
        ThrowIfDisposed();
        var state = ServiceLocator.SystemState;
        DateTimeOffset? heartbeat = state.RuntimeHeartbeatAtUtc is DateTime value
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : null;
        var recovery = string.IsNullOrWhiteSpace(state.RuntimeRecoveryStatus) ? "UNKNOWN" : state.RuntimeRecoveryStatus;
        DateTimeOffset? accessChecked = state.LastAccessCheckAtUtc is DateTime access
            ? new DateTimeOffset(DateTime.SpecifyKind(access, DateTimeKind.Utc))
            : null;
        return new(
            TradingRuntimeHealthV1.CurrentSchema,
            DateTimeOffset.UtcNow,
            Volatile.Read(ref _accessReady) != 0,
            accessChecked,
            AutoTradingAgent.IsRunning,
            state.Status.ToString(),
            state.RuntimeRunId,
            heartbeat,
            recovery,
            state.RuntimeEventSequence,
            string.Equals(recovery, "LEASE_LOST", StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            await AutoTradingAgent.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _snapshotPumpCancellation.Cancel();
            try
            {
                await _snapshotPumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_snapshotPumpCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                _snapshotPumpCancellation.Dispose();
                _initializeGate.Dispose();
            }

            await ServiceLocator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void BindIdentity(string userName)
    {
        var settings = _settingsStore.Load();
        settings.ActiveUser = userName;
        _settingsStore.Save(settings);

        var runtimeMode = RuntimeModePolicy.Resolve(settings);
        var state = ServiceLocator.SystemState;
        state.BrainMode = runtimeMode.RequestedMode;
        state.BrainEffectiveMode = runtimeMode.EffectiveMode;
        state.BrainRemoteAllowed = runtimeMode.AllowRemoteBrain;
        state.BrainFallbackReason = runtimeMode.FallbackReason;
        state.ActiveBrainProvider = runtimeMode.ProviderName;
        state.ActiveBrainModel = runtimeMode.ModelName;
        state.BrainName = runtimeMode.EffectiveMode == AiRuntimeMode.LocalOnly ? "WPE Local Brain" : runtimeMode.ProviderName;
        state.LoggedInUser = userName;
        state.LastUpdated = DateTime.UtcNow;
    }

    private async Task<bool> StartPublicMarketAsync()
    {
        try
        {
            await ServiceLocator.PublicMarket.StartAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Public market runtime failed to start and remains unavailable.");
            return false;
        }
    }

    private async Task RunRuntimeSnapshotPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshRuntimeSnapshotAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WPE runtime snapshot refresh failed.");
            }

            try
            {
                await Task.Delay(SnapshotRefreshInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RefreshRuntimeSnapshotAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now >= _nextAccessRefreshAtUtc)
        {
            _nextAccessRefreshAtUtc = now + AccessRefreshInterval;
            try
            {
                await RefreshAccessAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Periodic access readiness refresh failed.");
            }
        }

        await ServiceLocator.RuntimeAudit.RefreshAsync(ct);
        await ServiceLocator.RuntimeEquity.RefreshAsync(ct);
        await ServiceLocator.RuntimeSkillCalls.RefreshAsync(ct);
        await ServiceLocator.RuntimeAgentOperations.RefreshAsync(ct);
        await ServiceLocator.RuntimeNotifications.RefreshAsync(ct);
        await ServiceLocator.RuntimeAuthorization.RefreshAsync(ct);
        await ServiceLocator.RuntimeHistoricalCollections.RefreshAsync(ct);

        var snapshot = RuntimeSnapshotFactory.Create(
            ServiceLocator.SystemState,
            DateTime.UtcNow,
            ServiceLocator.RuntimeMarkets.Read(),
            ServiceLocator.RuntimeTrading.Read(),
            ServiceLocator.RuntimeEquity.Read(),
            ServiceLocator.RuntimeConnection.Read(),
            ServiceLocator.RuntimeSkillCalls.Read(),
            ServiceLocator.RuntimeAgentOperations.Read(),
            ServiceLocator.RuntimeBacktests.Read(),
            ServiceLocator.RuntimeAudit.Read(),
            LlmRequestGovernor.Shared.GetTodaySnapshot(),
            LlmRequestGovernor.Shared.GetTodayBreakdown(),
            ServiceLocator.PluginRegistry.List(),
            ServiceLocator.RuntimeNotifications.Read(),
            ServiceLocator.RuntimeAuthorization.Read(),
            ServiceLocator.RuntimeEquityMarkets.Read(),
            ServiceLocator.RuntimeHistoricalCollections.Read(),
            ServiceLocator.RuntimeCrossAssetResearch.Read(),
            ServiceLocator.RuntimeDistribution.Read(),
            ServiceLocator.PublicMarket.Read(),
            ServiceLocator.SecurityStorage.Read());

        Volatile.Write(ref _runtimeJson, SerializeSnapshot(snapshot));
    }

    private static string SerializeSnapshot(object snapshot) => JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);

    private static void PublishAccess(AccessReadinessReport report, AgentSettings settings)
    {
        ServiceLocator.RuntimeConnection.Publish(report, settings);
        var state = ServiceLocator.SystemState;
        var runtimeMode = RuntimeModePolicy.Resolve(settings);
        state.ExchangeConnected = report.Checks.Any(x => x.Key == "exchange" && x.Passed);
        state.BrainConnected = report.Checks.Any(x => x.Key == "brain" && x.Passed);
        state.ApiTradePermission = report.Checks.Any(x => x.Key == "trade_permission" && x.Passed);
        state.RiskReady = report.Checks.Any(x => x.Key == "risk" && x.Passed);
        state.BrainMode = runtimeMode.RequestedMode;
        state.BrainEffectiveMode = runtimeMode.EffectiveMode;
        state.BrainRemoteAllowed = runtimeMode.AllowRemoteBrain;
        state.BrainFallbackReason = runtimeMode.FallbackReason;
        state.ActiveBrainProvider = runtimeMode.ProviderName;
        state.ActiveBrainModel = runtimeMode.ModelName;
        state.BrainName = runtimeMode.EffectiveMode == AiRuntimeMode.LocalOnly ? "WPE Local Brain" : runtimeMode.ProviderName;
        state.LastAccessCheckAtUtc = report.CheckedAtUtc;
        state.LoggedInUser = settings.ActiveUser;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(TradingRuntimeHost));
    }
}

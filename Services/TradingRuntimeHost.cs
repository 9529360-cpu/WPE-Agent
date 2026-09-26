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
    private readonly HeadlessDesktopObserver _headlessObserver = new();
    private int _headlessAuthorityDetected;
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
    public bool HeadlessAuthorityDetected => Volatile.Read(ref _headlessAuthorityDetected) != 0;
    public bool IsObservingHeadless => _headlessObserver.Active;

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

            var headless = _headlessObserver.ReadHeadless();
            if (headless.Ready)
            {
                Interlocked.Exchange(ref _headlessAuthorityDetected, 1);
                var ready = await RefreshAccessProjectionAsync(persistSettings: false).ConfigureAwait(false);
                try
                {
                    await _headlessObserver.TryStartAsync(_snapshotPumpCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var state = ServiceLocator.SystemState;
                    state.Status = AgentStatus.Degraded;
                    state.AgentControlAllowed = false;
                    state.LastMessage = "Headless runtime owns trading authority, but the desktop read-only observer could not attach.";
                    state.LastUpdated = DateTime.UtcNow;
                    Log.Warning(ex, "Desktop read-only observer failed to attach to the active Headless runtime.");
                }
                return ready;
            }

            var localReady = await RefreshAccessAsync().ConfigureAwait(false);
            if (localReady && startAgentWhenReady)
                AutoTradingAgent.StartDefault();
            return localReady;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public Task<bool> RefreshAccessAsync()
    {
        ThrowIfDisposed();
        return RefreshAccessProjectionAsync(persistSettings: !HeadlessAuthorityDetected);
    }

    private async Task<bool> RefreshAccessProjectionAsync(bool persistSettings)
    {
        var settings = _settingsStore.Load();
        var report = await _readiness.CheckAsync(settings).ConfigureAwait(false);
        PublishAccess(report, settings);
        if (persistSettings)
        {
            settings.LastAccessCheckAtUtc = report.CheckedAtUtc;
            _settingsStore.Save(settings);
        }
        Volatile.Write(ref _accessReady, report.Ready ? 1 : 0);
        _nextAccessRefreshAtUtc = DateTimeOffset.UtcNow + AccessRefreshInterval;
        return report.Ready;
    }

    public async Task<bool> StartAgentAsync()
    {
        ThrowIfDisposed();
        if (!await RefreshAccessAsync().ConfigureAwait(false)) return false;
        if (HeadlessAuthorityDetected || _headlessObserver.ReadHeadless().Ready)
        {
            Interlocked.Exchange(ref _headlessAuthorityDetected, 1);
            var state = ServiceLocator.SystemState;
            state.AgentControlAllowed = false;
            state.LastMessage = "Headless runtime already owns trading authority; desktop remains observation-only.";
            state.LastUpdated = DateTime.UtcNow;
            return false;
        }
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
        var externalRunning = HeadlessAuthorityDetected &&
                              state.Status == AgentStatus.Running &&
                              heartbeat is { } externalHeartbeat &&
                              DateTimeOffset.UtcNow - externalHeartbeat <= TradingRuntimeHealthV1.MaximumHeartbeatAge;
        return new(
            TradingRuntimeHealthV1.CurrentSchema,
            DateTimeOffset.UtcNow,
            Volatile.Read(ref _accessReady) != 0,
            accessChecked,
            AutoTradingAgent.IsRunning || externalRunning,
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
            await _headlessObserver.DisposeAsync().ConfigureAwait(false);
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
        if (!string.Equals(settings.ActiveUser, userName, StringComparison.Ordinal))
        {
            settings.ActiveUser = userName;
            _settingsStore.Save(settings);
        }

        var state = ServiceLocator.SystemState;
        ApplyLocalBrainState(state);
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
        state.ExchangeConnected = report.Checks.Any(x => x.Key == "exchange" && x.Passed);
        state.BrainConnected = report.Checks.Any(x => x.Key == "brain" && x.Passed);
        state.ApiTradePermission = report.Checks.Any(x => x.Key == "trade_permission" && x.Passed);
        state.RiskReady = report.Checks.Any(x => x.Key == "risk" && x.Passed);
        ApplyLocalBrainState(state);
        state.LastAccessCheckAtUtc = report.CheckedAtUtc;
        state.LoggedInUser = settings.ActiveUser;
    }

    private static void ApplyLocalBrainState(SystemState state)
    {
        state.ActiveBrainProvider="WPE Local Brain";
        state.ActiveBrainModel="local-deterministic";
        state.BrainName="WPE Local Brain";
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(TradingRuntimeHost));
    }
}

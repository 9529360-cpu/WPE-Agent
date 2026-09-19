using System.Text.Json;
using System.Text.Json.Serialization;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services;

public sealed class DesktopRuntimeHost : IAsyncDisposable
{
    private static readonly TimeSpan SnapshotRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AgentSettingsStore _settingsStore = new();
    private readonly AccessReadinessService _readiness = new();
    private readonly CancellationTokenSource _snapshotPumpCancellation = new();
    private readonly Task _snapshotPumpTask;
    private string _runtimeJson;
    private int _disposed;

    public DesktopRuntimeHost(string userName)
    {
        UserName = userName;
        ServiceLocator.SystemState.LoggedInUser = userName;
        _runtimeJson = SerializeSnapshot(RuntimeSnapshotFactory.Create(ServiceLocator.SystemState, DateTime.UtcNow));
        _snapshotPumpTask = Task.Run(() => RunRuntimeSnapshotPumpAsync(_snapshotPumpCancellation.Token));
    }

    public string UserName { get; }

    public async Task<bool> RefreshAccessAsync()
    {
        var settings = _settingsStore.Load();
        var report = await _readiness.CheckAsync(settings);
        PublishAccess(report, settings);
        settings.LastAccessCheckAtUtc = report.CheckedAtUtc;
        _settingsStore.Save(settings);
        return report.Ready;
    }

    public async Task<bool> StartAgentAsync()
    {
        if (!await RefreshAccessAsync()) return false;
        AutoTradingAgent.StartDefault();
        return true;
    }

    public string BuildRuntimeJson() => Volatile.Read(ref _runtimeJson);

    public async Task<string> BuildHistoricalPageResponseJsonAsync(string requestId,HistoricalCollectionKindV1 kind,string cursor)
    {
        object page;
        try
        {
            page=await ServiceLocator.RuntimeHistoricalCollections.ReadPageAsync(kind,cursor,CancellationToken.None);
        }
        catch(Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WPE historical page read failed: {ex.GetType().Name}");
            page=new
            {
                contractVersion=HistoricalCollectionPageV1<HistoricalOrderV1>.CurrentContractVersion,
                kind,
                state=RuntimeCollectionState.Error,
                items=Array.Empty<object>(),
                nextCursor=(string?)null,
                sourceUpdatedAtUtc=(DateTimeOffset?)null,
                source="local-agent-sqlite",
                message="Historical page read failed."
            };
        }
        return JsonSerializer.Serialize(new{contractVersion="1.0",requestId,kind,page},SnapshotJsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
        }
    }

    private async Task RunRuntimeSnapshotPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshRuntimeSnapshotAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WPE runtime snapshot refresh failed: {ex.GetType().Name}");
            }

            try
            {
                await Task.Delay(SnapshotRefreshInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RefreshRuntimeSnapshotAsync(CancellationToken ct)
    {
        await ServiceLocator.RuntimeAudit.RefreshAsync(ct);
        await ServiceLocator.RuntimeEquity.RefreshAsync(ct);
        await ServiceLocator.RuntimeStrategyRegistry.RefreshAsync(ct);
        await ServiceLocator.RuntimeSkillCalls.RefreshAsync(ct);
        await ServiceLocator.RuntimeMemory.RefreshAsync(ct);
        await ServiceLocator.RuntimeAgentOperations.RefreshAsync(ct);
        await ServiceLocator.RuntimeTeacher.RefreshAsync(ct);
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
            ServiceLocator.RuntimeStrategyRegistry.Read(),
            ServiceLocator.RuntimeSkillCalls.Read(),
            ServiceLocator.RuntimeMemory.Read(),
            ServiceLocator.RuntimeAgentOperations.Read(),
            ServiceLocator.RuntimeTeacher.Read(),
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
            ServiceLocator.SecurityStorage.Read(),
            brokerState: ServiceLocator.RuntimeEquityBroker.Read());

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
}

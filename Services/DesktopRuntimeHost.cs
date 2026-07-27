using System.Text.Json;
using System.Text.Json.Serialization;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services;

public sealed class DesktopRuntimeHost
{
    private readonly AgentSettingsStore _settingsStore = new();
    private readonly AccessReadinessService _readiness = new();

    public DesktopRuntimeHost(string userName)
    {
        UserName = userName;
        ServiceLocator.SystemState.LoggedInUser = userName;
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

    public string BuildRuntimeJson()
    {
        ServiceLocator.RuntimeAudit.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeEquity.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeStrategyRegistry.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeSkillCalls.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeMemory.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeAgentOperations.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeTeacher.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeNotifications.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeAuthorization.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        ServiceLocator.RuntimeHistoricalCollections.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
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
            ServiceLocator.SecurityStorage.Read());
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });
    }

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

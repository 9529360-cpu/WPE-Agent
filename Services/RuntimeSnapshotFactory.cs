using WpeAgent.RuntimeContracts;
using WpeAgent.Plugins;
using WpeAgent.Equities;
using 币安量化机器人.Services.Security;
using 币安量化机器人.Core.MarketData;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Core.Models;

namespace WpeAgent.RuntimeServices;

public static class RuntimeSnapshotFactory
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    public static RuntimeSnapshotV1 Create(
        SystemState state,
        DateTime generatedAtUtc,
        RuntimeMarketState? marketState = null,
        RuntimeTradingState? tradingState = null,
        RuntimeEquityState? equityState = null,
        RuntimeConnectionState? connectionState = null,
        RuntimeStrategyRegistryState? strategyRegistryState = null,
        RuntimeSkillCallState? skillCallState = null,
        RuntimeMemoryState? memoryState = null,
        RuntimeAgentOperationsState? agentOperationsState = null,
        RuntimeTeacherState? teacherState = null,
        RuntimeBacktestState? backtestState = null,
        RuntimeAuditState? auditState = null,
        LlmUsageSnapshot? llmUsage = null,
        LlmUsageBreakdown? llmBreakdown = null,
        PluginRegistrySnapshot? pluginRegistry = null,
        RuntimeNotificationState? notificationState = null,
        RuntimeAuthorizationState? authorizationState = null,
        EquityMarketDataProjection? equityMarkets = null,
        RuntimeHistoricalCollectionsSnapshot? historicalCollections = null,
        RuntimeCrossAssetResearchState? crossAssetResearchState = null,
        RuntimeDistributionState? distributionState = null,
        PublicMarketState? publicMarketState = null,
        SecurityStorageRuntimeStatus? securityStorageState = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        generatedAtUtc = generatedAtUtc.ToUniversalTime();
        var source = state.LastUpdated.ToUniversalTime();
        var sourceIsFuture = source > generatedAtUtc;
        var age = Math.Max(0, (generatedAtUtc - source).TotalSeconds);
        var fresh = !sourceIsFuture && age <= StaleAfter.TotalSeconds;
        var collectionState = fresh ? RuntimeCollectionState.Available : RuntimeCollectionState.Stale;
        var message = fresh ? null : sourceIsFuture
            ? "Runtime source timestamp is later than the snapshot timestamp."
            : "Runtime source is older than the freshness threshold.";
        var runtimeMarkets = marketState ?? RuntimeMarketState.Unsupported("Provider capability probe has not run.");
        var marketAge = runtimeMarkets.UpdatedAt is null ? double.PositiveInfinity : Math.Max(0, (generatedAtUtc - runtimeMarkets.UpdatedAt.Value.UtcDateTime).TotalSeconds);
        var marketStale = runtimeMarkets.UpdatedAt is not null && marketAge > RuntimeMarketStateStore.StaleAfter.TotalSeconds;
        var marketCollectionState = marketStale ? RuntimeCollectionState.Stale : runtimeMarkets.State;
        var marketMessage = marketStale ? "Market capability source is older than the freshness threshold." : runtimeMarkets.Message;
        var runtimeTrading = tradingState ?? RuntimeTradingState.Unsupported("Trading runtime has not started.");
        var tradingAge = runtimeTrading.UpdatedAt is null ? double.PositiveInfinity : Math.Max(0, (generatedAtUtc - runtimeTrading.UpdatedAt.Value.UtcDateTime).TotalSeconds);
        var tradingStale = runtimeTrading.State == RuntimeCollectionState.Available && tradingAge > RuntimeTradingStateStore.StaleAfter.TotalSeconds;
        var tradingCollectionState = tradingStale ? RuntimeCollectionState.Stale : runtimeTrading.State;
        var tradingMessage = tradingStale ? "Trading source is older than the freshness threshold." : runtimeTrading.Message;
        var runtimePositions = tradingCollectionState == RuntimeCollectionState.Available ? runtimeTrading.Positions : Array.Empty<RuntimePositionV1>();
        var runtimeOrders = tradingCollectionState == RuntimeCollectionState.Available ? runtimeTrading.Orders : Array.Empty<RuntimeOrderV1>();
        var runtimeEquity=equityState??RuntimeEquityState.Unsupported("Equity persistence is not connected.");
        var equityAge=runtimeEquity.UpdatedAt is null?double.PositiveInfinity:Math.Max(0,(generatedAtUtc-runtimeEquity.UpdatedAt.Value.UtcDateTime).TotalSeconds);
        var equityStale=runtimeEquity.State==RuntimeCollectionState.Available&&runtimeEquity.Items.Count>0&&equityAge>RuntimeEquityStateStore.StaleAfter.TotalSeconds;
        var equityCollectionState=equityStale?RuntimeCollectionState.Stale:runtimeEquity.State;
        var equityMessage=equityStale?"Latest persisted equity observation is older than the freshness threshold.":runtimeEquity.Message;
        var runtimeConnection=connectionState??RuntimeConnectionState.Unsupported("Access readiness has not been checked.");
        var connectionAge=runtimeConnection.UpdatedAt is null?double.PositiveInfinity:Math.Max(0,(generatedAtUtc-runtimeConnection.UpdatedAt.Value.UtcDateTime).TotalSeconds);
        var connectionStale=runtimeConnection.State==RuntimeCollectionState.Available&&connectionAge>RuntimeConnectionStateStore.StaleAfter.TotalSeconds;
        var connectionCollectionState=connectionStale?RuntimeCollectionState.Stale:runtimeConnection.State;
        var connectionMessage=connectionStale?"The latest access readiness check is older than 30 minutes.":runtimeConnection.Message;
        var runtimeStrategyRegistry=strategyRegistryState??RuntimeStrategyRegistryState.Unsupported("Strategy registry is not connected.");
        var strategyRegistryAge=runtimeStrategyRegistry.UpdatedAt is null?double.PositiveInfinity:Math.Max(0,(generatedAtUtc-runtimeStrategyRegistry.UpdatedAt.Value.UtcDateTime).TotalSeconds);
        var strategyRegistryStale=runtimeStrategyRegistry.State==RuntimeCollectionState.Available&&strategyRegistryAge>RuntimeStrategyRegistryStateStore.StaleAfter.TotalSeconds;
        var strategyRegistryCollectionState=strategyRegistryStale?RuntimeCollectionState.Stale:runtimeStrategyRegistry.State;
        var strategyRegistryMessage=strategyRegistryStale?"Strategy registry projection is stale.":runtimeStrategyRegistry.Message;
        var runtimeSkills=skillCallState??RuntimeSkillCallState.Unsupported("Skill-call persistence is not connected.");var skillsAge=runtimeSkills.UpdatedAt is null?double.PositiveInfinity:Math.Max(0,(generatedAtUtc-runtimeSkills.UpdatedAt.Value.UtcDateTime).TotalSeconds);var skillsStale=runtimeSkills.State==RuntimeCollectionState.Available&&skillsAge>RuntimeSkillCallStateStore.StaleAfter.TotalSeconds;var skillsState=skillsStale?RuntimeCollectionState.Stale:runtimeSkills.State;var skillsMessage=skillsStale?"Skill-call projection is stale.":runtimeSkills.Message;
        var runtimeMemory=memoryState??RuntimeMemoryState.Unsupported("Memory persistence is not connected.");var memoryAge=runtimeMemory.UpdatedAt is null?double.PositiveInfinity:Math.Max(0,(generatedAtUtc-runtimeMemory.UpdatedAt.Value.UtcDateTime).TotalSeconds);var memoryStale=runtimeMemory.State==RuntimeCollectionState.Available&&memoryAge>RuntimeMemoryStateStore.StaleAfter.TotalSeconds;var memoryCollectionState=memoryStale?RuntimeCollectionState.Stale:runtimeMemory.State;var memoryMessage=memoryStale?"Memory projection is stale.":runtimeMemory.Message;
        var runtimeAgentOperations=agentOperationsState??RuntimeAgentOperationsState.Unsupported("Agent operations persistence is not connected.");var agentOperationsAge=runtimeAgentOperations.UpdatedAt is null?double.PositiveInfinity:Math.Max(0,(generatedAtUtc-runtimeAgentOperations.UpdatedAt.Value.UtcDateTime).TotalSeconds);var agentOperationsStale=runtimeAgentOperations.State==RuntimeCollectionState.Available&&runtimeAgentOperations.Operations.Any(x=>x.LastActivityAtUtc is not null)&&agentOperationsAge>RuntimeAgentOperationsStateStore.StaleAfter.TotalSeconds;var agentOperationsCollectionState=agentOperationsStale?RuntimeCollectionState.Stale:runtimeAgentOperations.State;var agentOperationsMessage=agentOperationsStale?"Agent operations projection is stale.":runtimeAgentOperations.Message;
        var runtimeTeacher=teacherState??RuntimeTeacherState.Unsupported("Teacher persistence is not connected.");var teacherAge=runtimeTeacher.UpdatedAt is null?double.PositiveInfinity:Math.Max(0,(generatedAtUtc-runtimeTeacher.UpdatedAt.Value.UtcDateTime).TotalSeconds);var teacherStale=runtimeTeacher.State==RuntimeCollectionState.Available&&teacherAge>RuntimeTeacherStateStore.StaleAfter.TotalSeconds;var teacherCollectionState=teacherStale?RuntimeCollectionState.Stale:runtimeTeacher.State;var teacherMessage=teacherStale?"Teacher projection is stale.":runtimeTeacher.Message;
        var runtimePlugins = pluginRegistry ?? PluginRegistrySnapshot.Unsupported("Plugin registry is not connected.");
        var runtimeBacktests = backtestState ?? RuntimeBacktestState.Unsupported("Backtest persistence is not connected.");
        var backtestAge = runtimeBacktests.UpdatedAt is null ? double.PositiveInfinity : Math.Max(0,(generatedAtUtc-runtimeBacktests.UpdatedAt.Value.UtcDateTime).TotalSeconds);
        var backtestStale = runtimeBacktests.State==RuntimeCollectionState.Available && backtestAge>RuntimeBacktestStateStore.StaleAfter.TotalSeconds;
        var backtestCollectionState = backtestStale ? RuntimeCollectionState.Stale : runtimeBacktests.State;
        var backtestMessage = backtestStale ? "Latest persisted backtest is older than the freshness threshold." : runtimeBacktests.Message;
        var runtimeAudit = auditState ?? RuntimeAuditState.Unsupported("Audit persistence is not connected.");
        var auditAge = runtimeAudit.UpdatedAt is null ? double.PositiveInfinity : Math.Max(0,(generatedAtUtc-runtimeAudit.UpdatedAt.Value.UtcDateTime).TotalSeconds);
        var auditStale = runtimeAudit.State==RuntimeCollectionState.Available && auditAge>RuntimeAuditStateStore.StaleAfter.TotalSeconds;
        var auditCollectionState = auditStale ? RuntimeCollectionState.Stale : runtimeAudit.State;
        var auditMessage = auditStale ? "Audit projection is older than the freshness threshold." : runtimeAudit.Message;
        var pluginItems = runtimePlugins.State == RuntimeCollectionState.Available
            ? runtimePlugins.Items.Select(plugin=>ToRuntimePlugin(plugin,runtimeConnection,connectionCollectionState)).ToArray()
            : Array.Empty<RuntimePluginV1>();
        var runtimeNotifications=notificationState??RuntimeNotificationState.Unsupported("Notification state is not connected.");
        var notificationAge=runtimeNotifications.UpdatedAt is null?double.PositiveInfinity:Math.Max(0,(generatedAtUtc-runtimeNotifications.UpdatedAt.Value.UtcDateTime).TotalSeconds);
        var notificationCollectionState=runtimeNotifications.State==RuntimeCollectionState.Available&&notificationAge>RuntimeNotificationStateStore.StaleAfter.TotalSeconds?RuntimeCollectionState.Stale:runtimeNotifications.State;
        var notificationMessage=notificationCollectionState==RuntimeCollectionState.Stale?"Notification state is stale.":runtimeNotifications.Message;
        var runtimeAuthorization=authorizationState??RuntimeAuthorizationState.Unsupported("Trading authorization state is not connected.");
        var authorizationAge=runtimeAuthorization.UpdatedAt is null?double.PositiveInfinity:Math.Max(0,(generatedAtUtc-runtimeAuthorization.UpdatedAt.Value.UtcDateTime).TotalSeconds);
        var authorizationCollectionState=runtimeAuthorization.State==RuntimeCollectionState.Available&&authorizationAge>RuntimeAuthorizationStateStore.StaleAfter.TotalSeconds?RuntimeCollectionState.Stale:runtimeAuthorization.State;
        var authorizationMessage=authorizationCollectionState==RuntimeCollectionState.Stale?"Trading authorization projection is stale.":runtimeAuthorization.Message;
        var equityMarket=equityMarkets??EquityMarketDataProjection.Unsupported("No authorized equity market-data source is connected.");
        var equityMarketState=MapEquityState(equityMarket.Status);
        var equityMarketItems=equityMarketState==RuntimeCollectionState.Available
            ? equityMarket.Quotes.Select(quote=>new RuntimeEquityMarketV1(
                quote.InstrumentId,quote.VenueId,quote.Currency,quote.Last,quote.Bid,quote.Ask,
                equityMarket.Sessions.FirstOrDefault(session=>string.Equals(session.VenueId,quote.VenueId,StringComparison.Ordinal))?.Phase.ToString()??"Unknown",
                equityMarket.Halts.FirstOrDefault(halt=>string.Equals(halt.InstrumentId,quote.InstrumentId,StringComparison.Ordinal))?.Status.ToString()??"Unknown",
                quote.AsOfUtc,quote.SourceId)).ToArray()
            : Array.Empty<RuntimeEquityMarketV1>();
        var history=historicalCollections??RuntimeHistoricalCollectionsSnapshot.Unsupported();
        var runtimeResearch = crossAssetResearchState ?? RuntimeCrossAssetResearchState.Unsupported("Cross-asset research has not been published.");
        var runtimeDistribution = distributionState ?? RuntimeDistributionState.DefaultDenied(generatedAtUtc);
        var publicMarketItems = publicMarketState?.Tickers.Select(ticker => new RuntimePublicMarketTickerV1(
            ticker.Symbol, ticker.Price, ticker.ChangePercent, ticker.Volume, ticker.Source,
            ticker.UpdatedAt, ticker.Stale, ticker.State.ToString())).ToArray() ?? [];
        var publicMarketCollectionState = publicMarketState is null
            ? RuntimeCollectionState.Unsupported
            : publicMarketState.Tickers.Any(ticker => ticker.State == PublicMarketDataState.Available)
                ? RuntimeCollectionState.Available
                : publicMarketState.Tickers.Any(ticker => ticker.State == PublicMarketDataState.Stale)
                    ? RuntimeCollectionState.Stale
                    : publicMarketState.Health.State == PublicMarketHealthState.Degraded
                        ? RuntimeCollectionState.Error
                        : RuntimeCollectionState.Unsupported;
        var publicMarketMessage = publicMarketCollectionState switch
        {
            RuntimeCollectionState.Unsupported => "Public market prices are not available yet.",
            RuntimeCollectionState.Stale => "Public market prices are stale.",
            RuntimeCollectionState.Error => "Public market feed is unavailable.",
            _ when publicMarketItems.Any(item => item.State != nameof(PublicMarketDataState.Available)) => "Some public market prices are unknown or stale.",
            _ => null
        };
        var publicKlineItems = publicMarketState?.Klines.Select(kline => new RuntimePublicMarketKlineV1(
            kline.Symbol, kline.Interval, kline.Price, kline.Volume, kline.Source,
            kline.UpdatedAt, kline.Stale, kline.State.ToString())).ToArray() ?? [];
        var publicKlineCollectionState = publicMarketState is null || publicMarketState.Klines.Count == 0
            ? RuntimeCollectionState.Unsupported
            : publicMarketState.Klines.Any(kline => kline.State == PublicMarketDataState.Available)
                ? RuntimeCollectionState.Available
                : publicMarketState.Klines.Any(kline => kline.State == PublicMarketDataState.Stale)
                    ? RuntimeCollectionState.Stale
                    : publicMarketState.Health.State == PublicMarketHealthState.Degraded
                        ? RuntimeCollectionState.Error
                        : RuntimeCollectionState.Unsupported;
        var publicKlineMessage = publicKlineCollectionState switch
        {
            RuntimeCollectionState.Unsupported => "Public market klines are not available yet.",
            RuntimeCollectionState.Stale => "Public market klines are stale.",
            RuntimeCollectionState.Error => "Public market kline feed is unavailable.",
            _ when publicKlineItems.Any(item => item.State != nameof(PublicMarketDataState.Available)) => "Some public market klines are unknown or stale.",
            _ => null
        };
        var diagnostic=string.IsNullOrWhiteSpace(state.LastError)?null:UiDiagnostic.FromText(state.LastError,"Runtime operation failed.",state.LastUpdated);
        return new RuntimeSnapshotV1
        {
            GeneratedAtUtc = generatedAtUtc, SourceUpdatedAtUtc = source,
            Freshness = new RuntimeFreshnessV1(fresh, age, StaleAfter.TotalSeconds),
            EventSequence = state.RuntimeEventSequence, Environment = state.Mode.ToString(),
            Account = new(collectionState, new RuntimeAccountV1(state.WalletBalance, state.AvailableBalance), message),
            Positions = new(tradingCollectionState, runtimePositions, SafeMessage(tradingCollectionState,tradingMessage,"Trading observation failed.",generatedAtUtc)),
            Orders = new(tradingCollectionState, runtimeOrders, SafeMessage(tradingCollectionState,tradingMessage,"Order observation failed.",generatedAtUtc)),
            Risk = new(collectionState, new RuntimeRiskV1(state.RiskReady, state.CircuitBreakerActive,
                state.RiskApprovalStatus, state.RiskLoad, state.DailyPnl, state.MaxDrawdown,
                state.PortfolioVaR99, state.PortfolioCVaR99, state.PortfolioConcentration,
                state.PortfolioCorrelation, UiDiagnostic.SafeText(state.RiskSummary)), message),
            Strategies = new(collectionState, new RuntimeStrategyV1(UiDiagnostic.SafeText(state.StrategyStatus), UiDiagnostic.SafeText(state.StrategySummary), state.StrategyCandidates), message),
            Backtests = new(backtestCollectionState,backtestCollectionState==RuntimeCollectionState.Available?runtimeBacktests.Items:Array.Empty<RuntimeBacktestV1>(),SafeMessage(backtestCollectionState,backtestMessage,"Backtest read failed.",generatedAtUtc)),
            CrossAssetResearch = new(runtimeResearch.State,runtimeResearch.State==RuntimeCollectionState.Available?runtimeResearch.Items:Array.Empty<RuntimeCrossAssetResearchV1>(),runtimeResearch.Message),
            Distribution = new(runtimeDistribution.State,runtimeDistribution.Value,runtimeDistribution.Message),
            EquityHistory = new(equityCollectionState,equityCollectionState==RuntimeCollectionState.Available?runtimeEquity.Items:Array.Empty<RuntimeEquityPointV1>(),SafeMessage(equityCollectionState,equityMessage,"Equity history read failed.",generatedAtUtc)),
            EquityMarkets = new(equityMarketState,equityMarketItems,SafeMessage(equityMarketState,equityMarket.Message,"Equity market-data read failed.",generatedAtUtc)),
            HistoricalOrders = Historical(history.Orders),
            HistoricalEquity = Historical(history.Equity),
            HistoricalBacktests = Historical(history.Backtests),
            HistoricalSkillCalls = Historical(history.SkillCalls),
            HistoricalAuditEvents = Historical(history.AuditEvents),
            HistoricalExecutionReality = Historical(history.ExecutionReality),
            ConnectionStatus = new(connectionCollectionState,connectionCollectionState==RuntimeCollectionState.Available?runtimeConnection.Value:null,SafeMessage(connectionCollectionState,connectionMessage,"Connection readiness check failed.",generatedAtUtc)),
            StrategyRegistry = new(strategyRegistryCollectionState,strategyRegistryCollectionState==RuntimeCollectionState.Available?runtimeStrategyRegistry.Profiles.Select(x=>x with{LastReason=UiDiagnostic.SafeText(x.LastReason)}).ToArray():Array.Empty<RuntimeStrategyProfileV1>(),SafeMessage(strategyRegistryCollectionState,strategyRegistryMessage,"Strategy registry read failed.",generatedAtUtc)),
            StrategyLifecycleEvents = new(strategyRegistryCollectionState,strategyRegistryCollectionState==RuntimeCollectionState.Available?runtimeStrategyRegistry.Events.Select(x=>x with{Reason=UiDiagnostic.SafeText(x.Reason)}).ToArray():Array.Empty<RuntimeStrategyLifecycleEventV1>(),SafeMessage(strategyRegistryCollectionState,strategyRegistryMessage,"Strategy lifecycle read failed.",generatedAtUtc)),
            SkillCalls = new(skillsState,skillsState==RuntimeCollectionState.Available?runtimeSkills.Items:Array.Empty<RuntimeSkillCallV1>(),SafeMessage(skillsState,skillsMessage,"Skill-call history read failed.",generatedAtUtc)),
            Diagnostic = new(RuntimeCollectionState.Available,diagnostic is null?null:new RuntimeDiagnosticV1(diagnostic.Code,diagnostic.TimeUtc,diagnostic.Summary)),
            MemoryStatus = new(memoryCollectionState,memoryCollectionState==RuntimeCollectionState.Available?runtimeMemory.Status:null,SafeMessage(memoryCollectionState,memoryMessage,"Memory projection failed.",generatedAtUtc)),
            RecentMemoryRetrievals = new(memoryCollectionState,memoryCollectionState==RuntimeCollectionState.Available?runtimeMemory.Retrievals:Array.Empty<RuntimeMemoryRetrievalV1>(),SafeMessage(memoryCollectionState,memoryMessage,"Memory retrieval projection failed.",generatedAtUtc)),
            AgentOperations = new(agentOperationsCollectionState,agentOperationsCollectionState==RuntimeCollectionState.Available?runtimeAgentOperations.Operations:Array.Empty<RuntimeAgentOperationV1>(),SafeMessage(agentOperationsCollectionState,agentOperationsMessage,"Agent operations projection failed.",generatedAtUtc)),
            AgentHandoffs = new(agentOperationsCollectionState,agentOperationsCollectionState==RuntimeCollectionState.Available?runtimeAgentOperations.Handoffs:Array.Empty<RuntimeAgentHandoffV1>(),SafeMessage(agentOperationsCollectionState,agentOperationsMessage,"Agent handoff projection failed.",generatedAtUtc)),
            TeacherLessons = new(teacherCollectionState,teacherCollectionState==RuntimeCollectionState.Available?runtimeTeacher.Lessons:Array.Empty<RuntimeTeacherLessonV1>(),SafeMessage(teacherCollectionState,teacherMessage,"Teacher lesson projection failed.",generatedAtUtc)),
            TeacherRecommendations = new(teacherCollectionState,teacherCollectionState==RuntimeCollectionState.Available?runtimeTeacher.Recommendations:Array.Empty<RuntimeTeacherRecommendationV1>(),SafeMessage(teacherCollectionState,teacherMessage,"Teacher recommendation projection failed.",generatedAtUtc)),
            TeacherCorrections = new(teacherCollectionState,teacherCollectionState==RuntimeCollectionState.Available?runtimeTeacher.Corrections:Array.Empty<RuntimeTeacherCorrectionV1>(),SafeMessage(teacherCollectionState,teacherMessage,"Teacher correction projection failed.",generatedAtUtc)),
            TeacherOutcomes = new(teacherCollectionState,teacherCollectionState==RuntimeCollectionState.Available?runtimeTeacher.Outcomes:Array.Empty<RuntimeTeacherOutcomeV1>(),SafeMessage(teacherCollectionState,teacherMessage,"Teacher outcome projection failed.",generatedAtUtc)),
            AuditEvents = new(auditCollectionState,auditCollectionState==RuntimeCollectionState.Available?runtimeAudit.Items:Array.Empty<RuntimeAuditEventV1>(),SafeMessage(auditCollectionState,auditMessage,"Audit history read failed.",generatedAtUtc)),
            Telemetry = new(collectionState, new RuntimeTelemetryV1(state.RuntimeRunId, state.RuntimeHeartbeatAtUtc,
                state.RuntimeRecoveryStatus, state.RealtimeStatus, state.WorkflowNode, state.ThinkingProgress), message),
            LlmGovernance = llmUsage is null
                ? new(RuntimeCollectionState.Unsupported, null, "LLM governance metrics are not connected.")
                : new(collectionState, new RuntimeLlmGovernanceV1(
                    state.BrainEffectiveMode.ToString(), state.BrainRemoteAllowed,
                    llmUsage.Calls, llmUsage.Tokens, llmUsage.CostUsd, llmUsage.CacheHits,
                    llmUsage.BudgetBlocks, llmUsage.Fallbacks, llmUsage.PrivacyBlocks, 0,
                    llmUsage.TopProvider, llmUsage.TopPurpose,
                    llmBreakdown?.TopAgent ?? "core", llmBreakdown?.TopTool ?? "assistant",
                    llmUsage.LastCallAtUtc), message),
            Markets = new(marketCollectionState, marketStale ? Array.Empty<RuntimeMarketV1>() : runtimeMarkets.Markets, SafeMessage(marketCollectionState,marketMessage,"Market capability check failed.",generatedAtUtc)),
            PublicMarkets = new(publicMarketCollectionState, publicMarketItems, publicMarketMessage),
            PublicKlines = new(publicKlineCollectionState, publicKlineItems, publicKlineMessage),
            Capabilities = new(marketCollectionState, marketStale ? Array.Empty<RuntimeCapabilityV1>() : runtimeMarkets.Capabilities, SafeMessage(marketCollectionState,marketMessage,"Provider capability check failed.",generatedAtUtc)),
            Plugins = new(runtimePlugins.State, pluginItems, SafeMessage(runtimePlugins.State,runtimePlugins.Message,"Plugin registry read failed.",generatedAtUtc)),
            NotificationStatus = new(notificationCollectionState,notificationCollectionState==RuntimeCollectionState.Available?runtimeNotifications.Status:null,SafeMessage(notificationCollectionState,notificationMessage,"Notification state read failed.",generatedAtUtc)),
            NotificationOutbox = new(notificationCollectionState,notificationCollectionState==RuntimeCollectionState.Available?runtimeNotifications.Recent:Array.Empty<RuntimeNotificationOutboxRowV1>(),SafeMessage(notificationCollectionState,notificationMessage,"Notification outbox read failed.",generatedAtUtc)),
            TelegramSubscribers = new(notificationCollectionState,notificationCollectionState==RuntimeCollectionState.Available?runtimeNotifications.Subscribers:Array.Empty<RuntimeTelegramSubscriberV1>(),SafeMessage(notificationCollectionState,notificationMessage,"Telegram subscriber registry read failed.",generatedAtUtc)),
            AuthorizationMode = new(authorizationCollectionState,authorizationCollectionState==RuntimeCollectionState.Available&&runtimeAuthorization.Mode is not null?new RuntimeAuthorizationModeV1(runtimeAuthorization.Mode):null,SafeMessage(authorizationCollectionState,authorizationMessage,"Trading authorization read failed.",generatedAtUtc)),
            SecurityStorage = securityStorageState is null
                ? new(RuntimeCollectionState.Unsupported,null,"Security storage runtime is not connected.")
                : new(RuntimeCollectionState.Available,new RuntimeSecurityStorageV1(securityStorageState.State.ToString(),securityStorageState.ReasonCode,securityStorageState.EnvelopeVersion,securityStorageState.RecordCount,securityStorageState.EvidenceSha256)),
            AutomaticExecutions = new(authorizationCollectionState,authorizationCollectionState==RuntimeCollectionState.Available?runtimeAuthorization.Automatic:Array.Empty<RuntimeAutomaticExecutionSummaryV1>(),SafeMessage(authorizationCollectionState,authorizationMessage,"Automatic execution queue read failed.",generatedAtUtc)),
            PendingApprovals = new(authorizationCollectionState,authorizationCollectionState==RuntimeCollectionState.Available?runtimeAuthorization.Pending:Array.Empty<RuntimeApprovalSummaryV1>(),SafeMessage(authorizationCollectionState,authorizationMessage,"Trading approval queue read failed.",generatedAtUtc)),
            LegacyFields = new(StringComparer.Ordinal)
            {
                ["btcPrice"] = (double)state.BtcPrice, ["ethPrice"] = (double)state.EthPrice,
                ["walletBalance"] = (double)state.WalletBalance, ["availableBalance"] = (double)state.AvailableBalance,
                ["positionQuantity"] = (double)state.PositionQuantity, ["status"] = state.Status.ToString(),
                ["agentIsRunning"] = AutoTradingAgent.IsRunning,
                ["nextCycleAtUtc"] = state.NextCycleAtUtc,
                ["environment"] = state.Mode.ToString(), ["runtimeFresh"] = fresh, ["runtimeAgeSeconds"] = age,
                ["workflowNode"] = state.WorkflowNode, ["thinkingProgress"] = state.ThinkingProgress,
                ["riskLoad"] = state.RiskLoad, ["riskSummary"] = UiDiagnostic.SafeText(state.RiskSummary),
                ["strategyStatus"] = UiDiagnostic.SafeText(state.StrategyStatus), ["strategySummary"] = UiDiagnostic.SafeText(state.StrategySummary),
                ["strategyCandidates"] = state.StrategyCandidates, ["lastUpdated"] = state.LastUpdated,
                ["lastDecision"] = UiDiagnostic.SafeText(state.LastDecision), ["lastReason"] = UiDiagnostic.SafeText(state.LastReason),
                ["dailyPnl"] = state.DailyPnl, ["maxDrawdown"] = state.MaxDrawdown,
                ["runtimeEventSequence"] = state.RuntimeEventSequence, ["runtimeHeartbeatAtUtc"] = state.RuntimeHeartbeatAtUtc,
                ["exchangeConnected"] = state.ExchangeConnected, ["brainConnected"] = state.BrainConnected,
                ["aiRuntimeMode"] = state.BrainMode.ToString(), ["aiRuntimeEffectiveMode"] = state.BrainEffectiveMode.ToString(),
                ["brainRemoteAllowed"] = state.BrainRemoteAllowed, ["brainFallbackReason"] = string.IsNullOrWhiteSpace(state.BrainFallbackReason)?string.Empty:"Remote Brain unavailable; local deterministic fallback active.",
                ["activeBrainProvider"] = state.ActiveBrainProvider, ["activeBrainModel"] = state.ActiveBrainModel,
                ["apiTradePermission"] = state.ApiTradePermission, ["riskReady"] = state.RiskReady,
                ["realtimeStatus"] = state.RealtimeStatus, ["positionsSummary"] = state.PositionsSummary,
                ["ordersSummary"] = state.OrdersSummary, ["marketSummary"] = state.MarketSummary,
                ["newsSummary"] = UiDiagnostic.SafeText(state.NewsSummary), ["decisionDiagnostics"] = UiDiagnostic.SafeText(state.DecisionDiagnostics),
                ["runtimeProviderId"] = connectionCollectionState == RuntimeCollectionState.Available ? runtimeConnection.Value?.ProviderId : null
            }
        };
    }

    private static RuntimeCollectionState MapEquityState(EquityMarketDataStatus state)=>state switch
    {
        EquityMarketDataStatus.Available=>RuntimeCollectionState.Available,
        EquityMarketDataStatus.Stale=>RuntimeCollectionState.Stale,
        EquityMarketDataStatus.Error=>RuntimeCollectionState.Error,
        _=>RuntimeCollectionState.Unsupported
    };

    private static RuntimeHistoricalCollectionV1<T> Historical<T>(HistoricalCollectionPageV1<T> page)
    {
        var available=page.State==RuntimeCollectionState.Available;
        return new(page.State,available?page.Items:Array.Empty<T>(),available?page.NextCursor:null,page.SourceUpdatedAtUtc,page.Source,page.Message);
    }


    private static RuntimeCollectionV1<T> Unsupported<T>(string message) =>
        new(RuntimeCollectionState.Unsupported, Array.Empty<T>(), message);
    private static string? SafeMessage(RuntimeCollectionState state,string? message,string summary,DateTime time)=>
        state==RuntimeCollectionState.Error?UiDiagnostic.FormatText(message,summary,time):message;

    private static RuntimePluginV1 ToRuntimePlugin(RegisteredPlugin plugin,RuntimeConnectionState connection,RuntimeCollectionState connectionState)
    {
        var providerId=plugin.Manifest.Id.StartsWith("wpe.exchange.",StringComparison.OrdinalIgnoreCase)
            ? plugin.Manifest.Id["wpe.exchange.".Length..]
            : null;
        var selected=providerId is not null&&connection.Value is not null&&string.Equals(providerId,connection.Value.ProviderId,StringComparison.OrdinalIgnoreCase);
        var active=selected&&connectionState==RuntimeCollectionState.Available&&connection.Value!.ExchangeConnected&&string.Equals(connection.Value.AdapterStatus,"ready",StringComparison.OrdinalIgnoreCase);
        var runtimeStatus=active?"running":selected&&connectionState==RuntimeCollectionState.Stale?"stale":selected?"unavailable":"not-configured";
        return new(
        plugin.Manifest.Id,
        plugin.Manifest.Name,
        plugin.Manifest.Type,
        plugin.Manifest.Version,
        plugin.Manifest.Publisher.Id,
        plugin.Manifest.Publisher.Name,
        plugin.Manifest.Entry.Kind,
        plugin.Manifest.Permissions.ToArray(),
        plugin.Enabled,
        plugin.Manifest.Lifecycle.DefaultEnabled,
        active,
        runtimeStatus,
        plugin.Manifest.Lifecycle.TestnetOnly,
        plugin.CompatibilityStatus.ToString().ToLowerInvariant(),
        "metadataPresent",
        plugin.RiskLevel.ToString().ToLowerInvariant(),
        UiDiagnostic.SafeText(plugin.StatusMessage));
    }
}

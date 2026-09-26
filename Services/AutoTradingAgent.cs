using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Localization;
using System.Diagnostics;
using System.Net.Http;
using 币安量化机器人.Services.Exchange;
using 币安量化机器人.Core.Runtime;
using 币安量化机器人.Infrastructure.Runtime;

using 币安量化机器人.Core.Strategy;
using WpeAgent.Notifications;
using WpeAgent.TradingAuthorization;
using WpeAgent.RuntimeServices;
using System.Globalization;
using 币安量化机器人.Services.Access;

namespace 币安量化机器人.Services;

internal static class ProductionAutomaticCapabilityGate
{
    internal static bool IsAvailable(DurableExecutionArtifactV2 artifact,IReadOnlyDictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability> capabilities,DateTimeOffset now)
        =>artifact.Intents.Count>0&&artifact.Intents.All(intent=>capabilities.TryGetValue(intent.Symbol,out var value)&&
            value.Status==WpeAgent.RuntimeContracts.CapabilityStatus.Available&&value.CanTrade&&value.TestnetAvailable&&Fresh(value.CheckedAt,now));

    internal static async Task<bool> EnsureAvailableAsync(DurableExecutionArtifactV2 artifact,IReadOnlyDictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability> capabilities,Func<CancellationToken,Task>? refresh,Func<DateTimeOffset> utcNow,CancellationToken ct)
    {
        if(IsAvailable(artifact,capabilities,utcNow()))return true;
        if(refresh is null)return false;
        try{await refresh(ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return false;}
        return IsAvailable(artifact,capabilities,utcNow());
    }

    private static bool Fresh(DateTimeOffset checkedAt,DateTimeOffset now)
    {
        if(checkedAt==default)return false;
        var age=now.ToUniversalTime()-checkedAt.ToUniversalTime();
        return age<=WpeAgent.RuntimeContracts.ProviderCapabilityPrecondition.MaximumAge&&age>=-WpeAgent.RuntimeContracts.ProviderCapabilityPrecondition.MaximumAge;
    }
}

internal sealed class ProductionAutomaticExecutionValidator : IAutomaticExecutionReadOnlyValidator
{
    private static readonly TimeSpan MaximumMarketAge=TimeSpan.FromSeconds(30);
    private readonly AgentSettingsStore _settings;private readonly ExchangeConnectionProfile _profile;private readonly IExchangeAdapter _exchange;private readonly IReadOnlyDictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability> _capabilities;private readonly Func<bool> _runtimeReady;private readonly Func<CancellationToken,Task>? _capabilityRefresh;
    public ProductionAutomaticExecutionValidator(AgentSettingsStore settings,ExchangeConnectionProfile profile,IExchangeAdapter exchange,IReadOnlyDictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability> capabilities,Func<bool> runtimeReady,Func<CancellationToken,Task>? capabilityRefresh=null)
    {_settings=settings;_profile=profile;_exchange=exchange;_capabilities=capabilities;_runtimeReady=runtimeReady;_capabilityRefresh=capabilityRefresh;}
    public async Task<AutomaticExecutionRuntimeFacts> ValidateAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var now=DateTimeOffset.UtcNow;var current=_settings.Load();
        var testnet=current.Environment==ExchangeEnvironment.Testnet&&current.AuthorizationMode==TradingAuthorizationMode.Auto&&_profile.IsTestnet&&_exchange.Environment==ExchangeEnvironment.Testnet&&artifact.Environment=="Testnet";
        var runtime=_settings.LastLoadDiagnostic is null&&_runtimeReady()&&current.SetupCompleted&&current.LastAccessCheckAtUtc is not null&&now-new DateTimeOffset(DateTime.SpecifyKind(current.LastAccessCheckAtUtc.Value,DateTimeKind.Utc))<=TimeSpan.FromMinutes(30);
        var capability=await ProductionAutomaticCapabilityGate.EnsureAvailableAsync(artifact,_capabilities,_capabilityRefresh,()=>DateTimeOffset.UtcNow,ct);
        now=DateTimeOffset.UtcNow;
        var market=artifact.MarketCollectedAtUtc<=now&&now-artifact.MarketCollectedAtUtc<=MaximumMarketAge;
        return new AutomaticExecutionRuntimeFacts(testnet?AutomaticFactState.True:AutomaticFactState.False,runtime?AutomaticFactState.True:AutomaticFactState.False,capability?AutomaticFactState.True:AutomaticFactState.False,market?AutomaticFactState.True:AutomaticFactState.False,"automatic.production-facts");
    }
}

public static class AutoTradingAgent
{
    private static CancellationTokenSource? _cts;private static Task? _run;private static readonly AgentSettingsStore SettingsStore=new();private static readonly AgentSqliteStore Db=new();private static readonly ExchangeProviderCatalog Providers=new();private static readonly SkillExecutionGuard SkillGuard=new();private static readonly SemaphoreSlim ExecutionGate=new(1,1);private static IExchangeAdapter? _activeExchange;private static TradingExecutionGateway? _activeExecutionGateway;private static Func<CancellationToken,Task>? _activeCapabilityRefresh;private static string? _activeRuntimeSessionId;private static decimal _dayHigh;private static bool _paused;
    public static bool IsRunning=>_run is{IsCompleted:false};public static bool CanEmergencyClose=>_activeExchange is not null&&_activeExecutionGateway is not null&&_activeCapabilityRefresh is not null&&!string.IsNullOrWhiteSpace(_activeRuntimeSessionId);public static event Action? StateChanged;

    public static void StartDefault(){if(IsRunning)return;var settings=SettingsStore.Load();if(string.IsNullOrWhiteSpace(settings.ActiveUser)||!settings.SetupCompleted)throw new InvalidOperationException(L("Access.StartBlocked"));var profile=SettingsStore.GetActiveExchange(settings);if(!profile.IsTestnet)throw new InvalidOperationException(L("Agent.MainnetConfirmationRequired"));if(settings.LastAccessCheckAtUtc is null||DateTime.UtcNow-settings.LastAccessCheckAtUtc>TimeSpan.FromMinutes(30))throw new InvalidOperationException(L("Access.StartBlocked"));ServiceLocator.RuntimeTrading.MarkUnsupported("Waiting for the first provider position/order observation.");AgentRoleRuntimeRegistry.Shared.StopAll("Agent runtime is starting; worker health is not established yet.");_paused=false;var runCts=new CancellationTokenSource();_cts=runCts;_run=Task.Run(async()=>{try{await Run(runCts.Token);}catch(OperationCanceledException)when(runCts.IsCancellationRequested){}catch(Exception ex){var state=ServiceLocator.SystemState;state.ExchangeConnected=false;state.BrainConnected=false;state.ApiTradePermission=false;AgentRoleRuntimeRegistry.Shared.DegradeAll("Agent runtime failed; inspect the sanitized runtime diagnostic.");await Db.RecordErrorAsync("AGENT_LOOP",ex,CancellationToken.None);await NotifyAgentDegradedAsync("agent-loop","AGENT_LOOP",ex);state.LastError=ex.Message;Set(AgentStatus.Degraded,L("Agent.CycleDegraded",ex.Message));}finally{_activeRuntimeSessionId=null;_activeCapabilityRefresh=null;_activeExecutionGateway=null;_activeExchange=null;}});}
    public static void Pause(){_paused=true;Set(AgentStatus.Paused,L("Agent.Paused"));}
    public static void Resume(){_paused=false;Set(AgentStatus.Running,L("Agent.Resumed"));}
    public static async Task StopAsync(){var runCts=_cts;if(runCts is null){ServiceLocator.RuntimeTrading.MarkUnsupported("Trading runtime is stopped.");AgentRoleRuntimeRegistry.Shared.StopAll("Agent runtime is stopped.");return;}runCts.Cancel();try{if(_run is not null)await _run;}catch(OperationCanceledException){}runCts.Dispose();if(ReferenceEquals(_cts,runCts)){_cts=null;_run=null;}ServiceLocator.RuntimeTrading.MarkUnsupported("Trading runtime is stopped.");AgentRoleRuntimeRegistry.Shared.StopAll("Agent runtime is stopped.");Set(AgentStatus.Stopped,L("Agent.Stopped"));}

    public static ManualEmergencyConfirmation CreateEmergencyCloseConfirmation()
    {
        if(!CanEmergencyClose)throw new InvalidOperationException(L("Agent.EmergencyUnavailable"));
        var settings=SettingsStore.Load();var session=_activeRuntimeSessionId??throw new InvalidOperationException(L("Agent.EmergencyUnavailable"));
        return new(Guid.NewGuid().ToString("N"),"EMERGENCY-"+Guid.NewGuid().ToString("N"),settings.ActiveUser,DeviceLicenseService.GetCurrentDeviceCode(),session,DateTimeOffset.UtcNow);
    }

    public static async Task<string> EmergencyCloseAllAsync(ManualEmergencyConfirmation confirmation)
    {
        _paused=true;Set(AgentStatus.Paused,L("Agent.EmergencyRequested"));await ExecutionGate.WaitAsync();try
        {
            var exchange=_activeExchange??throw new InvalidOperationException(L("Agent.EmergencyUnavailable"));var executionGateway=_activeExecutionGateway??throw new InvalidOperationException(L("Agent.EmergencyUnavailable"));var refresh=_activeCapabilityRefresh??throw new InvalidOperationException(L("Agent.EmergencyUnavailable"));using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));var ct=timeout.Token;await refresh(ct);var positions=await ReadPositionsAsync(exchange,ct);
            if(positions.Count==0){Set(AgentStatus.Paused,L("Agent.NoPositions"));return L("Agent.NoPositions");}
            var closed=new List<string>();
            foreach(var position in positions)
            {
                var rule=await exchange.GetRulesAsync(position.Symbol,ct);var quantity=rule.RoundQuantity(position.Quantity);var raw=$"WPE-EMG-{DateTime.UtcNow:yyMMddHHmmss}-{Guid.NewGuid():N}";var id=raw[..Math.Min(36,raw.Length)];var action=position.Side==PositionSide.Long?DecisionAction.CloseLong:DecisionAction.CloseShort;
                var intent=new ExecutionIntent(position.Symbol,position.Side,quantity,true,0,0,id,L("Agent.EmergencyIntent"),action,ExpectedPrice:position.MarkPrice,ReasonCode:PositionExitReasonCodes.ManualEmergencyClose);var command=new EmergencyReductionCommand(confirmation,position,intent,Math.Max(1,(int)position.Leverage),position.Isolated);var execution=await executionGateway.ExecuteEmergencyReductionAsync(command,ct);if(!execution.Executed)throw new InvalidOperationException(execution.Code);closed.Add($"{position.Symbol} {position.Side} {quantity}");
            }
            var (account,remaining,orders)=await ReadAccountStateAsync(exchange,ct);UpdateAccount(account,remaining,orders);var result=L("Agent.EmergencyCompleted",string.Join("; ",closed));Set(AgentStatus.Paused,result);return result;
        }
        catch(Exception ex){await Db.RecordErrorAsync("EMERGENCY_CLOSE",ex,CancellationToken.None);ServiceLocator.SystemState.LastError=ex.Message;Set(AgentStatus.Degraded,L("Agent.EmergencyIncomplete",ex.Message));throw;}
        finally{ExecutionGate.Release();}
    }

    private static async Task Run(CancellationToken ct)
    {
        var settings=SettingsStore.Load();
        var notifications=NotificationRuntimeFactory.Create();_=notifications.RunDispatcher(ct);
        var exchangeProfile=SettingsStore.GetActiveExchange(settings);if(!exchangeProfile.IsTestnet)throw new InvalidOperationException(L("Agent.MainnetConfirmationRequired"));if(!exchangeProfile.ExecutionEnabled)throw new InvalidOperationException(L("Agent.ApiMissing"));var credentials=SettingsStore.GetExchangeCredentials(exchangeProfile);if(credentials.Values.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException(L("Agent.ApiMissing"));
        await using var eventBus=new InProcessAgentEventBus();await using var runtime=new AgentRuntimeSupervisor(Db,eventBus);runtime.HealthChanged+=ApplyRuntimeHealth;await runtime.StartAsync(ct);
        await using IExchangeProvider exchange=Providers.Create(exchangeProfile,credentials);_activeExchange=exchange;
        if(exchange.Environment!=ExchangeEnvironment.Testnet)throw new InvalidOperationException(L("Agent.MainnetConfirmationRequired"));
        var legacyIsolation=await RunLegacyIntentIsolationAsync(exchange,ct);
        ServiceLocator.SystemState.RuntimeRecoveryStatus=$"{legacyIsolation.Code}; examined={legacyIsolation.Examined}; quarantined={legacyIsolation.Quarantined}; retained={legacyIsolation.Retained}; unknown={legacyIsolation.Unknown}";
        var capabilitySnapshot=new System.Collections.Concurrent.ConcurrentDictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability>(StringComparer.OrdinalIgnoreCase);
        async Task RefreshCapabilitySnapshot(CancellationToken token)=>await RefreshCapabilitiesAsync(exchange,settings.Symbols,capabilitySnapshot,token);
        await RefreshCapabilitySnapshot(ct);_activeCapabilityRefresh=RefreshCapabilitySnapshot;
        await using var realtime=exchange.CreateRealtimeFeed(settings.Symbols,Db)??new PollingRealtimeFeed();await realtime.StartAsync(ct);
        var roles=AgentRoleRuntimeRegistry.Shared;roles.Publish("market","monitoring","Public market and Testnet streams are being monitored.");roles.Publish("decision","monitoring","Direct local candle structure is the trading decision authority.");roles.Publish("risk","monitoring","Risk limits and execution authority are being monitored.");roles.Publish("execution","waiting","Execution queue is ready; no approved order is pending.");roles.Publish("recovery","monitoring","Order state and reconciliation queue are being monitored.");roles.Publish("audit","monitoring","Append-only runtime and decision audit is active.");
        var newsResearch=new NewsResearchService();var collector=new EvidenceCollector(exchange,settings.Symbols,realtime,newsResearch);var marketStateStore=new DurableMarketStateStoreV1(Db);
        var executor=new ReliableOrderExecutor(exchange,Db,settings.Risk,SystemOrderPollScheduler.Instance,capabilitySnapshot,true,capabilityRefresh:async(symbol,token)=>{var refreshed=await new ProviderCapabilityProbe().ProbeAsync(exchange,[symbol],true,token);if(refreshed.TryGetValue(symbol,out var value)){capabilitySnapshot[symbol]=value;return value;}return null;});
        var recoveryServices=await ProductionRecoveryComposition.CreateAsync(exchange,executor,Db,ProductionRecoveryComposition.DefaultKeyPath(),ct:ct);var executionGateway=recoveryServices.Gateway;var planner=new RiskAndPositionPlanner();var governance=new DecisionGovernanceSkill();var deterministic=new DeterministicPlanSkill();var independentRisk=new IndependentRiskManagerSkill();var portfolioRiskSkill=new PortfolioRiskSkill();var historicalData=new HistoricalDataService(exchange,Db);var positionManager=new PositionManagementSkill();
        _activeExecutionGateway=executionGateway;_activeRuntimeSessionId=runtime.RunId;
        var automaticGateway=new TradingAutomaticExecutionGateway(executionGateway,exchange,Db);
        var automaticValidator=new ProductionAutomaticExecutionValidator(SettingsStore,exchangeProfile,exchange,capabilitySnapshot,()=>ReferenceEquals(_activeExchange,exchange)&&string.Equals(_activeRuntimeSessionId,runtime.RunId,StringComparison.Ordinal),RefreshCapabilitySnapshot);
        var automaticWorker=new AutomaticExecutionWorker(new AutomaticExecutionProcessor(Db,automaticValidator,automaticGateway));Task? automaticWorkerTask=null;Task? tradingObservationTask=null;string? automaticMaintenanceOwnedMessage=null;
        var interruptedWorkflows=await Db.GetInterruptedWorkflowsAsync(ct);var startupWorkflowRecoveryPending=interruptedWorkflows.Count>0;var startupWorkflowMetadataOnly=interruptedWorkflows.All(x=>x.LastNode is not WorkflowNode.Execution and not WorkflowNode.SafetyExecution);var startupRecovery=executionGateway.AssessUnverifiedAutomaticMutation(AutomaticMutationPath.StartupRecovery,interruptedWorkflows.Count);if(!startupRecovery.SafeToIncreaseRisk)await Db.SetStateAsync("authorization.startup-recovery",startupRecovery.Code,ct);
        var localBrain=AssistantProviderFactory.CreateLocal();IAssistantProvider brain=localBrain;
        ServiceLocator.SystemState.Mode=TradingMode.Testnet;var runtimeState=ServiceLocator.SystemState;ApplyLocalBrainState(runtimeState,brain);runtimeState.ExchangeConnected=true;runtimeState.ApiTradePermission=exchangeProfile.TradePermission;Set(AgentStatus.Running,L("Agent.Started",exchangeProfile.DisplayName,brain.Name));var startupHealth=await brain.HealthCheckAsync(ct);runtimeState.BrainConnected=startupHealth.Healthy;if(!startupHealth.Healthy)throw new InvalidOperationException(L("Agent.BrainFailure",startupHealth.Message));Stage("Stage.HistoricalSync","OBSERVATION",12);
        try{var sync=await SkillAsync("HistoricalData",string.Join(',',settings.Symbols),token=>historicalData.SyncAsync(settings.Symbols,"1h",3,token),x=>string.Join(" | ",x.Select(s=>$"{s.Symbol}:{s.Stored}/{s.CoverageDays}d")),ct);ServiceLocator.SystemState.HistoricalCoverageDays=sync.Count==0?0:sync.Min(x=>x.CoverageDays);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){await Db.RecordErrorAsync("HistoricalData",ex,CancellationToken.None);ServiceLocator.SystemState.HistoricalCoverageDays=0;ServiceLocator.SystemState.LastMessage=L("Agent.CycleDegraded",ex.Message);}
        try
        {
        automaticWorkerTask=automaticWorker.RunAsync("automatic-"+runtime.RunId,TimeSpan.FromSeconds(1),ct);
        tradingObservationTask=ObserveTradingRuntimeAsync(exchange,TimeSpan.FromSeconds(10),ct);
        EvidencePack? previousEvidence=null;
        while(!ct.IsCancellationRequested)
        {
            if(_paused){await Task.Delay(1000,ct);continue;}var cycle=Guid.NewGuid().ToString("N");var workflowStarted=false;
            try
            {
                roles.Publish("market","running","Collecting fresh market evidence for the direct local decision cycle.");
                await RefreshCapabilitySnapshot(ct);
                Stage("Stage.AccountSync","OBSERVATION",18);var (account,positions,orders)=await ReadAccountStateAsync(exchange,ct);var positionSnapshotAt=DateTimeOffset.UtcNow;await Db.SaveSnapshotAsync(account,positions,orders,ct);await Db.SaveEquitySnapshotAsync(new(account.Timestamp,account.Equity,account.AvailableBalance,exchange.Environment.ToString(),exchange.ProviderId),ct);await ServiceLocator.RuntimeEquity.RefreshAsync(ct);_dayHigh=await Db.GetOrUpdateDailyHighAsync(DateOnly.FromDateTime(DateTime.UtcNow),account.Equity,ct);UpdateAccount(account,positions,orders);
                Stage("Stage.Recovery","OBSERVATION",25);var recoverableBefore=await Db.GetRecoverableIntentsAsync(ct);var recovered=recoverableBefore.Count>0?await recoveryServices.Recovery.RecoverPendingAsync(ct):new RecoveryResult(true,Array.Empty<string>());if(recoverableBefore.Count>0){(account,positions,orders)=await ReadAccountStateAsync(exchange,ct);positionSnapshotAt=DateTimeOffset.UtcNow;await Db.SaveSnapshotAsync(account,positions,orders,ct);UpdateAccount(account,positions,orders);}var recoverable=await Db.GetRecoverableIntentsAsync(ct);var pendingAssessment=executionGateway.AssessUnverifiedAutomaticMutation(AutomaticMutationPath.StartupRecovery,recoverable.Count);var pendingRecovery=new RecoveryResult(recovered.SafeToIncreaseRisk&&pendingAssessment.SafeToIncreaseRisk,recovered.Messages.Append(pendingAssessment.Code).ToArray());orders=await ReadOrdersAsync(exchange,null,ct);ServiceLocator.RuntimeTrading.Publish(positions,orders);var reconciliationNow=DateTimeOffset.UtcNow;var protectionReconciliation=ProtectionReconciliationServiceV1.Reconcile(positions,orders,reconciliationNow,reconciliationNow);await Db.SaveProtectionReconciliationAsync(protectionReconciliation,ct);var protectionFillReconciliation=await ProtectionFillReconciliationServiceV1.ReconcileAsync(exchange,Db,positions,ct);await Db.SetStateAsync("protection-fill-reconciliation:last",System.Text.Json.JsonSerializer.Serialize(protectionFillReconciliation),ct);var observedAt=account.Timestamp.Kind==DateTimeKind.Utc?new DateTimeOffset(account.Timestamp):new DateTimeOffset(DateTime.SpecifyKind(account.Timestamp,DateTimeKind.Utc));var ownershipObservationFresh=observedAt<=reconciliationNow&&reconciliationNow-observedAt<=PositionReconciliationServiceV1.MaximumAge;var executionLedger=await Db.GetExecutionPositionLedgerAsync(ct);var ownershipLedger=await Db.GetExecutionPositionLedgerAsync(positionSnapshotAt,ct);if(ownershipObservationFresh){await QuarantineConflictingManagedPositionOwnershipAsync(Db,ownershipLedger,positions,positionSnapshotAt,reconciliationNow,ct);await RevokeMissingManagedPositionOwnershipAsync(Db,ownershipLedger,positions,positionSnapshotAt,reconciliationNow,ct);}var ownershipUntrusted=await HasUntrustedManagedPositionOwnershipAsync(Db,positions,ct);var positionReconciliation=PositionReconciliationServiceV1.Reconcile(executionLedger,positions,observedAt,reconciliationNow);await Db.SavePositionReconciliationAsync(positionReconciliation,ct);var externalPositionIsolation=ExternalPositionIsolationServiceV1.Evaluate(executionLedger,positions,observedAt,reconciliationNow);await Db.SaveExternalPositionIsolationAsync(externalPositionIsolation,ct);var safeToIncreaseRisk=pendingRecovery.SafeToIncreaseRisk&&protectionReconciliation.AllowsRiskIncrease&&positionReconciliation.AllowsRiskIncrease&&externalPositionIsolation.AllowsRiskIncrease&&!ownershipUntrusted;var protectionMessage=protectionReconciliation.AllowsRiskIncrease?"protection.reconciliation-confirmed":string.Join(',',protectionReconciliation.ReasonCodes);var positionMessage=positionReconciliation.AllowsRiskIncrease?"position.reconciliation-confirmed":string.Join(',',positionReconciliation.ReasonCodes);var isolationMessage=externalPositionIsolation.AllowsRiskIncrease?"external-position.isolation-clear":string.Join(',',externalPositionIsolation.ReasonCodes);var ownershipMessage=ownershipUntrusted?"position.ownership-untrusted":"position.ownership-clear";var safetyMessage=string.Join("；",pendingRecovery.Messages.Append(protectionMessage).Append(positionMessage).Append(isolationMessage).Append(ownershipMessage));await Db.SetStateAsync("authorization.automatic-maintenance",safetyMessage,ct);
                if(safeToIncreaseRisk&&startupWorkflowRecoveryPending&&startupWorkflowMetadataOnly)
                {
                    var recoveredWorkflowCount=await runtime.RecoverNonExecutionInterruptedAsync(safetyMessage,ct);
                    startupWorkflowRecoveryPending=false;
                    if(recoveredWorkflowCount>0)await Db.SetStateAsync("authorization.startup-recovery","authorization.no-mutation-required",ct);
                }
                var maintenanceUi=ResolveAutomaticMaintenanceMessage(safeToIncreaseRisk,runtimeState.LastMessage,safetyMessage,automaticMaintenanceOwnedMessage,L("Agent.Started",exchangeProfile.DisplayName,brain.Name));runtimeState.LastMessage=maintenanceUi.LastMessage;automaticMaintenanceOwnedMessage=maintenanceUi.OwnedMessage;

                Stage("Stage.Evidence","OBSERVATION",32);var evidence=await SkillAsync("EvidenceCollector",string.Join(',',settings.Symbols),token=>collector.CollectAsync(token),x=>$"completeness={x.Completeness} markets={x.Markets.Count} missing={x.MissingSources.Count}",ct);var evidenceDelta=await SkillAsync("EvidenceDelta",$"baseline={(previousEvidence is null?"none":"present")} markets={evidence.Markets.Count}",_=>Task.FromResult(EvidenceDeltaEngineV1.Compare(evidence,previousEvidence)),x=>$"baseline={x.HasBaseline} changes={x.Signals.Count} critical={x.CriticalCount} source_degradations={x.SourceDegradationCount}",ct);await Db.SetStateAsync("market.evidence-delta:last",System.Text.Json.JsonSerializer.Serialize(evidenceDelta),ct);var marketStates=await SkillAsync("MarketState",$"markets={evidence.Markets.Count}",token=>marketStateStore.ObserveAsync(evidence.Markets,token),x=>$"states={x.Count} confirmed={x.Values.Count(s=>s.Lifecycle==MarketStateLifecycleV1.Confirmed)} transitions={x.Values.Count(s=>s.TransitionKind!=MarketStateTransitionKindV1.Stable)}",ct);evidence=MarketStateEvidenceOverlayV1.Attach(evidence,marketStates);previousEvidence=evidence;await Db.SaveNewsAsync(evidence.News,ct);var marketDegraded=evidenceDelta.SourceDegradationCount>0||marketStates.Values.Any(x=>!x.Available);roles.Publish("market",marketDegraded?"degraded":"monitoring",evidenceDelta.SourceDegradationCount>0?$"Fresh evidence collected; {evidenceDelta.SourceDegradationCount} source degradation(s) detected.":marketStates.Values.Any(x=>!x.Available)?"Fresh evidence collected; one or more persistent market states are unavailable.":"Fresh market evidence and persistent market state were updated.");await Db.StartCycleAsync(cycle,evidence,brain.Name,ct);await runtime.BeginCycleAsync(cycle,new{evidence.Completeness,Markets=evidence.Markets.Keys,MarketStates=marketStates.Values.Select(x=>new{x.Symbol,x.Lifecycle,x.Bias,x.Phase,x.Scenario,x.TransitionKind,x.ObservationCount}),brain=brain.Name,decisionPath=DirectMarketStructureDecisionSkill.DecisionContextKind},ct);workflowStarted=true;
                Stage("Stage.Research","OBSERVATION",36);await runtime.TransitionAsync(cycle,WorkflowNode.Research,new{evidence.Completeness,MarketCount=evidence.Markets.Count,ExecutionAuthority="direct-market-structure"},ct);var histories=new Dictionary<string,IReadOnlyList<CandleEvidence>>(StringComparer.OrdinalIgnoreCase);foreach(var market in evidence.Markets.Values)histories[market.Symbol]=await Db.LoadHistoricalCandlesAsync(market.Symbol,"1h",30000,ct);
                var positionTradingRules=await ReadPositionManagementRulesAsync(exchange,positions,ct);
                Stage("Stage.PositionManagement","RISK",39);await runtime.TransitionAsync(cycle,WorkflowNode.PositionManagement,new{Positions=positions.Count},ct);var management=await SkillAsync("PositionManagement",$"positions={positions.Count}",token=>positionManager.EvaluateAsync(positions,evidence.Markets,Db,token,executionLedger,positionTradingRules,evidence.MarketStates),x=>$"intents={x.Intents.Count} adjustments={x.ProtectionAdjustments.Count}",ct);var managementActivity=false;
                if(management.Intents.Count>0||management.ProtectionAdjustments.Count>0)
                {
                    await runtime.TransitionAsync(cycle,WorkflowNode.SafetyExecution,new{RiskReducingIntents=management.Intents.Count,ProtectionAdjustments=management.ProtectionAdjustments.Count},ct);
                    var reductionCount=await ExecutePositionManagementRecoveryAsync(recoveryServices.Recovery,cycle,management.Intents,positions,positionTradingRules,ct);
                    var protectionCount=await ExecutePositionProtectionRecoveryAsync(recoveryServices.Recovery,cycle,management.ProtectionAdjustments,positions,ct);
                    managementActivity=reductionCount>0||protectionCount>0;
                    (safeToIncreaseRisk,safetyMessage)=ApplyPositionMutationInvalidation(safeToIncreaseRisk,safetyMessage,reductionCount>0);
                    if(reductionCount>0)await Db.SetStateAsync("authorization.position-reconciliation","position.reconciliation-invalidated-by-recovery",ct);
                    if(protectionCount>0){safeToIncreaseRisk=false;safetyMessage="position.protection-adjusted-awaiting-reconciliation";await Db.SetStateAsync("authorization.position-management",safetyMessage,ct);}
                }

                Stage("Stage.Aggregation","OBSERVATION",43);await runtime.TransitionAsync(cycle,WorkflowNode.Aggregation,new{ManagementIntents=management.Intents.Count,managementActivity,DecisionPath=DirectMarketStructureDecisionSkill.DecisionContextKind},ct);UpdateEvidence(evidence);
                Stage("Stage.Planner","PLANNER",52);await runtime.TransitionAsync(cycle,WorkflowNode.Planner,new{DecisionPath=DirectMarketStructureDecisionSkill.DecisionContextKind,Markets=evidence.Markets.Count},ct);DecisionPlan proposed;string? brainRequest=null,brainResponse=null;
                try{var brainContext=new AgentContext(brain.Name);var brainResult=await SkillAsync("BrainPlanner",$"markets={evidence.Markets.Count} direct=true",token=>brain.DecideAsync(evidence,brainContext,token),x=>$"{x.Decision.Action} {x.Decision.Instrument} direct={DirectMarketStructureDecisionSkill.IsDirect(x.Decision)}",ct);proposed=brainResult.Decision;brainRequest=brainResult.Request;brainResponse=brainResult.Response;}
                catch(BrainCallException ex){brainRequest=ex.Request;brainResponse=ex.Response;await Db.RecordErrorAsync("Brain",ex,ct);proposed=new(){Action=DecisionAction.Hold,Reason=L("Agent.BrainFailure",ex.Message)};}
                catch(Exception ex){await Db.RecordErrorAsync("Brain",ex,ct);proposed=new(){Action=DecisionAction.Hold,Reason=L("Agent.BrainFailureShort")};}

                evidence.Markets.TryGetValue(proposed.Instrument,out var proposedMarket);proposed=await SkillAsync("DeterministicPlan",$"{proposed.Action} {proposed.Instrument}",_=>Task.FromResult(deterministic.Complete(proposed,proposedMarket,settings.Risk)),x=>$"entry={x.EntryPrice} stop={x.StopLossPrice} take={x.TakeProfitPrice} RR={x.RiskRewardRatio:F2}",ct);
                Stage("Stage.Review","CRITIC",65);await runtime.TransitionAsync(cycle,WorkflowNode.Critic,new{proposed.Action,proposed.Instrument,DecisionPath=proposed.DecisionContextKind},ct);var review=await SkillAsync("DecisionReviewer",$"{proposed.Action} {proposed.Instrument}",_=>Task.FromResult(governance.Review(proposed,evidence,settings.Decision)),x=>$"accepted={x.Accepted} blocks={x.BlockingReasons.Count}",ct);var decision=review.Decision;UpdateDecisionUi(decision,review);

                Stage("Stage.Risk","RISK",78);await runtime.TransitionAsync(cycle,WorkflowNode.Risk,new{decision.Action,decision.Instrument,ReviewAccepted=review.Accepted},ct);string result;IReadOnlyList<ExecutionIntent> intents=Array.Empty<ExecutionIntent>();TradingRule? tradingRule=null;
                var riskHistory=await Db.GetRiskHistoryAsync(ct);var sessionRisk=SessionRiskGate.Evaluate(riskHistory,evidence.Account.Equity,_dayHigh,settings.Risk);var planningSafeToIncreaseRisk=safeToIncreaseRisk&&sessionRisk.AllowsRiskIncrease;var planningSafetyMessage=sessionRisk.AllowsRiskIncrease?safetyMessage:string.Join("；",new[]{safetyMessage}.Concat(sessionRisk.Reasons).Where(x=>!string.IsNullOrWhiteSpace(x)));
                if(!settings.Symbols.Contains(decision.Instrument,StringComparer.OrdinalIgnoreCase)){result=L("Agent.InvalidInstrument");}
                else{tradingRule=await exchange.GetRulesAsync(decision.Instrument,ct);var lockedSide=await Db.GetLockedSideAsync(decision.Instrument,ct);var planned=await SkillAsync("RiskAndPositionPlanner",$"{decision.Action} {decision.Instrument} direct=true",_=>Task.FromResult(planner.Plan(decision,evidence,tradingRule,settings.Risk,planningSafeToIncreaseRisk,planningSafetyMessage,lockedSide)),x=>$"intents={x.Intents.Count} result={x.Result}",ct);intents=planned.Intents;result=planned.Result;}
                var portfolioRisk=await SkillAsync("PortfolioRisk",$"positions={evidence.Positions.Count} intents={intents.Count}",_=>Task.FromResult(portfolioRiskSkill.Evaluate(evidence,histories,intents,settings.Risk)),x=>x.Summary,ct);await Db.SavePortfolioRiskAsync(cycle,portfolioRisk,ct);UpdatePortfolioRiskUi(portfolioRisk,realtime);
                var riskReview=await SkillAsync("IndependentRiskManager",$"{decision.Action} intents={intents.Count}",_=>Task.FromResult(independentRisk.Review(decision,evidence,intents,settings.Risk,riskHistory,portfolioRisk)),x=>$"approved={x.Approved} level={x.RiskLevel} blocks={x.BlockingReasons.Count}",ct);
                if(DeterministicPlanSkill.IsRiskIncreasing(decision.Action)&&intents.Count==0)riskReview=new(){Approved=false,RiskLevel="BLOCKED",BlockingReasons=[result],Summary=result};
                var state=ServiceLocator.SystemState;state.ReviewerStatus=review.Accepted?"APPROVED":"REJECTED";state.RiskApprovalStatus=riskReview.Approved?"APPROVED":"BLOCKED";state.PlannedQuantity=riskReview.PlannedQuantity;state.CircuitBreakerActive=!planningSafeToIncreaseRisk;state.RiskSummary=riskReview.Summary;state.DecisionAuditSummary=$"Reviewer: {review.Verdict} · Risk: {state.RiskSummary}";
                if(!riskReview.Approved){intents=Array.Empty<ExecutionIntent>();result=riskReview.Summary;state.ExecutionApprovalStatus="BLOCKED";}
                else if(intents.Count==0)state.ExecutionApprovalStatus="NO_ORDER";
                else state.ExecutionApprovalStatus="READY";

                if(intents.Count>0&&tradingRule is not null)
                {
                    var leverage=Math.Min(settings.Risk.Leverage,tradingRule.MaxLeverage);
                    if(settings.AuthorizationMode==TradingAuthorizationMode.Auto)
                    {
                        var riskIncreasingDecision=DeterministicPlanSkill.IsRiskIncreasing(decision.Action);
                        var directDriven=DirectMarketStructureDecisionSkill.IsDirect(decision);
                        var decisionContextId=directDriven?decision.DecisionContextId:null;
                        var decisionContextVersion=directDriven?decision.StrategyVersion:null;
                        var decisionContextValid=directDriven&&proposedMarket is not null&&DirectMarketStructureDecisionSkill.ContextMatches(decision,proposedMarket);
                        if(!decisionContextValid||string.IsNullOrWhiteSpace(decisionContextId)||string.IsNullOrWhiteSpace(decisionContextVersion)||!settings.Risk.Isolated){result=riskIncreasingDecision?"automatic.decision-context-invalid":"automatic.artifact-context-invalid";state.ExecutionApprovalStatus="BLOCKED";}
                        else if(riskIncreasingDecision&&await Db.HasAutomaticExecutionBlockingRepeatAsync(decisionContextId!,ct))
                        {
                            result="automatic.decision-repeat-blocked";state.ExecutionApprovalStatus="BLOCKED";
                        }
                        else
                        {
                            var created=DateTimeOffset.UtcNow;var expires=created.AddMinutes(2);var durableIntents=intents.Select((value,index)=>new DurableExecutionIntentSnapshotV1(index,value.Symbol,value.Side.ToString(),value.Quantity,value.ReduceOnly,value.StopLoss,value.TakeProfit,value.ClientOrderId,ExecutionReasonCode.ResolveForDurableIntent(value,"automatic.risk-approved"),value.Action.ToString(),value.OrderType.ToString(),value.LimitPrice,value.ExpectedPrice)).ToArray();var marketCollected=new DateTimeOffset(DateTime.SpecifyKind(proposedMarket!.CollectedAt,DateTimeKind.Utc));var artifact=new DurableExecutionArtifactV2(DurableExecutionArtifactV2.Version,cycle,durableIntents,leverage,true,exchange.ProviderId,"Testnet",decisionContextId,decisionContextVersion,marketCollected,"provider-market-v1",created,expires);var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);var saved=await Db.SaveAutomaticExecutionAsync(cycle,artifact,ct);if(!saved.Succeeded){result=saved.Code;state.ExecutionApprovalStatus="BLOCKED";}else{var receipt=new DeterministicRiskReceipt("risk-"+cycle,cycle,hashes.IntentHash,true,created,expires,null,hashes.ArtifactHash);var approved=await Db.RecordAutomaticRiskDecisionAsync(cycle,receipt,ct);result=approved.Succeeded?"automatic.risk-approved":approved.Code;state.ExecutionApprovalStatus=approved.Succeeded?"RISK_APPROVED":"BLOCKED";}
                        }
                    }
                    else if(settings.AuthorizationMode==TradingAuthorizationMode.Research){result="authorization.research-read-only";state.ExecutionApprovalStatus="RESEARCH_ONLY";}
                    else if(settings.AuthorizationMode==TradingAuthorizationMode.Signal){result="authorization.signal-no-mutation";state.ExecutionApprovalStatus="SIGNAL_ONLY";}
                    else{result=settings.AuthorizationMode==TradingAuthorizationMode.Review?"authorization.legacy-review-blocked":"authorization.mode-blocked";state.ExecutionApprovalStatus="BLOCKED";await Db.SetStateAsync("authorization.auto-main-plan",result,ct);}
                }
                await Db.CompleteCycleAsync(cycle,decision,result,brainRequest,brainResponse,ct);await Db.RecordDecisionAuditAsync(cycle,review,ct);await Db.RecordMaturityAuditAsync(cycle,decision,review,riskReview,result,ct);await runtime.CompleteCycleAsync(cycle,new{decision.Action,decision.Instrument,result,riskReview.Approved},ct);workflowStarted=false;
                Set(_paused?AgentStatus.Paused:safeToIncreaseRisk?AgentStatus.Running:AgentStatus.Degraded,L("Agent.CycleResult",decision.Action,result,evidence.Completeness));Stage(_paused?"Stage.Paused":"Stage.Completed",_paused?"SYSTEM":"AUDIT",_paused?5:100);roles.Publish("decision","waiting","Latest direct decision cycle completed; waiting for fresh market evidence.");roles.Publish("risk","monitoring",riskReview.Approved?"Latest risk evaluation allowed the intent.":"Latest risk evaluation withheld execution authority.");roles.Publish("audit","monitoring","Latest cycle facts and decisions were appended to audit storage.");
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){var state=ServiceLocator.SystemState;state.ExchangeConnected=false;state.ApiTradePermission=false;roles.Publish("decision","degraded","Direct decision cycle failed; retry remains scheduled.");if(workflowStarted)await runtime.FailCycleAsync(cycle,ex,CancellationToken.None);await Db.FailCycleAsync(cycle,state.SkillStage,ex,ct);await Db.RecordErrorAsync(state.SkillStage,ex,ct);await NotifyAgentDegradedAsync(cycle,state.SkillStage,ex);state.LastError=ex.Message;Set(AgentStatus.Degraded,L("Agent.CycleDegraded",ex.Message));}
            roles.Publish("market",realtime.Healthy?"monitoring":"degraded",realtime.Healthy?"Realtime market and account streams are healthy.":"Realtime stream is reconnecting or incomplete.");ServiceLocator.SystemState.NextCycleAtUtc=DateTime.UtcNow.AddMinutes(15);ServiceLocator.SystemState.RealtimeStatus=realtime.Status;StateChanged?.Invoke();_ = await realtime.WaitForTriggerAsync(TimeSpan.FromMinutes(15),ct);
        }
        }
        finally
        {
            if(automaticWorkerTask is not null)try{await automaticWorkerTask;}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
            if(tradingObservationTask is not null)try{await tradingObservationTask;}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        }
    }

    private static void UpdateAccount(AccountSnapshot account,IReadOnlyList<ManagedPosition> positions,IReadOnlyList<ExchangeOrder> orders)
    {var state=ServiceLocator.SystemState;state.WalletBalance=account.WalletBalance;state.AvailableBalance=account.AvailableBalance;state.PositionQuantity=positions.Sum(x=>x.Side==PositionSide.Long?x.Quantity:-x.Quantity);state.EntryPrice=positions.FirstOrDefault()?.EntryPrice??0;state.PositionsSummary=positions.Count==0?L("Agent.NoPositions"):string.Join("\n",positions.Select(x=>$"{x.Symbol} {x.Side} · {x.Quantity} · {x.EntryPrice:F2} · {x.UnrealizedPnl:F2}"));state.OrdersSummary=orders.Count==0?L("Agent.NoOrders"):string.Join("\n",orders.Take(8).Select(x=>$"{x.Symbol} {x.Type} · {x.Status} · {x.PositionSide}"));}

    private static async Task RefreshCapabilitiesAsync(
        IExchangeProvider exchange,
        IReadOnlyList<string> symbols,
        System.Collections.Concurrent.ConcurrentDictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability> target,
        CancellationToken ct)
    {
        try
        {
            var refreshed=await new ProviderCapabilityProbe().ProbeAsync(exchange,symbols,true,ct);
            if(refreshed.Count!=symbols.Count||refreshed.Values.Any(x=>x.Status!=WpeAgent.RuntimeContracts.CapabilityStatus.Available||!x.CanRead||!x.CanTrade||!x.TestnetAvailable))
                throw new InvalidOperationException("Agent capability probe failed closed: one or more configured instruments are unavailable or untradeable.");
            target.Clear();foreach(var item in refreshed)target[item.Key]=item.Value;
            ServiceLocator.RuntimeMarkets.Publish(refreshed);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch
        {
            ServiceLocator.RuntimeMarkets.PublishError("Provider capability refresh failed.");
            throw;
        }
    }

    private static async Task<(AccountSnapshot Account,IReadOnlyList<ManagedPosition> Positions,IReadOnlyList<ExchangeOrder> Orders)> ReadAccountStateAsync(IExchangeAdapter exchange,CancellationToken ct)
    {
        try
        {
            var account=await exchange.GetAccountAsync(ct);
            var positions=await exchange.GetPositionsAsync(ct);
            try{await Db.SavePositionMarkObservationsAsync(positions,DateTimeOffset.UtcNow,ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){try{await Db.RecordErrorAsync("POSITION_MARK_OBSERVATION",ex,CancellationToken.None);}catch{}}
            var orders=await exchange.GetOpenOrdersAsync(null,ct);
            ServiceLocator.RuntimeTrading.Publish(positions,orders);
            return(account,positions,orders);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{ServiceLocator.RuntimeTrading.PublishError("Provider position/order observation failed.");throw;}
    }

    private static async Task ObserveTradingRuntimeAsync(IExchangeAdapter exchange,TimeSpan interval,CancellationToken ct)
    {
        using var timer=new PeriodicTimer(interval);
        while(await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var(account,positions,orders)=await ReadAccountStateAsync(exchange,ct);
                UpdateAccount(account,positions,orders);
                ServiceLocator.SystemState.ExchangeConnected=true;
                StateChanged?.Invoke();
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch
            {
                ServiceLocator.SystemState.ExchangeConnected=false;
                StateChanged?.Invoke();
            }
        }
    }

    internal static async Task<int> QuarantineConflictingManagedPositionOwnershipAsync(
        AgentSqliteStore db,IReadOnlyList<ExecutionPositionLegV1> managedLegs,IReadOnlyList<ManagedPosition> positions,
        DateTimeOffset observedAtUtc,DateTimeOffset evaluatedAtUtc,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);ArgumentNullException.ThrowIfNull(managedLegs);ArgumentNullException.ThrowIfNull(positions);
        observedAtUtc=observedAtUtc.ToUniversalTime();evaluatedAtUtc=evaluatedAtUtc.ToUniversalTime();
        if(observedAtUtc>evaluatedAtUtc||evaluatedAtUtc-observedAtUtc>PositionReconciliationServiceV1.MaximumAge)return 0;
        var quarantined=0;
        foreach(var leg in managedLegs.Where(x=>x.Quantity>0))
        {
            var matches=positions.Where(x=>string.Equals(x.Symbol,leg.Symbol,StringComparison.OrdinalIgnoreCase)&&x.Side==leg.Side&&x.Quantity>0).ToArray();
            if(matches.Length!=1)continue;
            var actual=matches[0].Quantity;
            var tolerance=Math.Max(.00000001m,Math.Max(leg.Quantity,actual)*.000001m);
            if(Math.Abs(leg.Quantity-actual)<=tolerance)continue;

            var opening=await db.GetLatestOpeningIntentAsync(leg.Symbol,leg.Side,observedAtUtc,ct);
            if(opening is null)continue;
            var revocationKey=PositionManagementDurableState.OwnershipRevocationKey(opening.ClientOrderId);
            var candidateKey=PositionManagementDurableState.OwnershipMissingCandidateKey(opening.ClientOrderId);
            if(await db.HasStateAsync(revocationKey,ct)||await db.HasStateAsync(candidateKey,ct))continue;

            var latestExecutionAtUtc=await db.GetLatestExecutionEventObservedAtAsync(leg.Symbol,leg.Side,observedAtUtc,ct);
            if(latestExecutionAtUtc is not null&&observedAtUtc-latestExecutionAtUtc.Value<PositionReconciliationServiceV1.MaximumAge)continue;

            await db.SetStateAsync(candidateKey,observedAtUtc.ToString("O",CultureInfo.InvariantCulture),ct);
            quarantined++;
        }
        return quarantined;
    }

    internal static async Task<int> RevokeMissingManagedPositionOwnershipAsync(
        AgentSqliteStore db,IReadOnlyList<ExecutionPositionLegV1> managedLegs,IReadOnlyList<ManagedPosition> positions,
        DateTimeOffset observedAtUtc,DateTimeOffset evaluatedAtUtc,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);ArgumentNullException.ThrowIfNull(managedLegs);ArgumentNullException.ThrowIfNull(positions);
        observedAtUtc=observedAtUtc.ToUniversalTime();evaluatedAtUtc=evaluatedAtUtc.ToUniversalTime();
        if(observedAtUtc>evaluatedAtUtc||evaluatedAtUtc-observedAtUtc>PositionReconciliationServiceV1.MaximumAge)return 0;
        var revoked=0;
        var confirmationAge=TimeSpan.FromSeconds(5);
        foreach(var leg in managedLegs.Where(x=>x.Quantity>0))
        {
            var opening=await db.GetLatestOpeningIntentAsync(leg.Symbol,leg.Side,observedAtUtc,ct);
            if(opening is null)continue;
            var openingObservedAtUtc=await db.GetExecutionEventObservedAtAsync(opening.ClientOrderId,ct);
            if(openingObservedAtUtc is null||observedAtUtc-openingObservedAtUtc.Value<PositionReconciliationServiceV1.MaximumAge)continue;
            var revocationKey=PositionManagementDurableState.OwnershipRevocationKey(opening.ClientOrderId);
            var revocation=await db.GetStateAsync(revocationKey,ct);
            if(revocation is not null)
            {
                const string prefix="position.missing-on-exchange:";
                if(revocation.StartsWith(prefix,StringComparison.Ordinal)&&
                   DateTimeOffset.TryParseExact(revocation[prefix.Length..],"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var revokedAtUtc))
                    await db.RetireExecutionPositionLedgerAsync(leg.Symbol,leg.Side,revokedAtUtc,ct);
                continue;
            }
            var candidateKey=PositionManagementDurableState.OwnershipMissingCandidateKey(opening.ClientOrderId);
            var positionPresent=positions.Any(x=>string.Equals(x.Symbol,leg.Symbol,StringComparison.OrdinalIgnoreCase)&&x.Side==leg.Side&&x.Quantity>0);
            if(positionPresent)continue;

            var firstMissing=await db.GetStateAsync(candidateKey,ct);
            if(firstMissing is null||
               !DateTimeOffset.TryParseExact(firstMissing,"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var firstObservedAtUtc)||
               firstObservedAtUtc>observedAtUtc)
            {
                await db.SetStateAsync(candidateKey,observedAtUtc.ToString("O",CultureInfo.InvariantCulture),ct);
                continue;
            }
            if(observedAtUtc-firstObservedAtUtc<confirmationAge)continue;
            await db.SetStateAsync(revocationKey,$"position.missing-on-exchange:{observedAtUtc:O}",ct);
            await db.RetireExecutionPositionLedgerAsync(leg.Symbol,leg.Side,observedAtUtc,ct);
            revoked++;
        }
        return revoked;
    }

    internal static async Task<bool> HasUntrustedManagedPositionOwnershipAsync(
        AgentSqliteStore db,IReadOnlyList<ManagedPosition> positions,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);ArgumentNullException.ThrowIfNull(positions);
        foreach(var position in positions.Where(x=>x.Quantity>0))
        {
            var opening=await db.GetLatestOpeningIntentAsync(position.Symbol,position.Side,ct);
            if(opening is null)continue;
            if(await db.HasStateAsync(PositionManagementDurableState.OwnershipRevocationKey(opening.ClientOrderId),ct)||
               await db.HasStateAsync(PositionManagementDurableState.OwnershipMissingCandidateKey(opening.ClientOrderId),ct))
                return true;
        }
        return false;
    }

    internal static async Task<int> ExecutePositionManagementRecoveryAsync(
        ProductionRecoveryService recovery,string correlationId,IReadOnlyList<ExecutionIntent> intents,
        IReadOnlyList<ManagedPosition> positions,IReadOnlyDictionary<string,TradingRule> tradingRules,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recovery);ArgumentNullException.ThrowIfNull(intents);ArgumentNullException.ThrowIfNull(positions);ArgumentNullException.ThrowIfNull(tradingRules);
        await ExecutionGate.WaitAsync(ct);try
        {
            var completed=0;
            foreach(var intent in intents)
            {
                var matches=positions.Where(x=>string.Equals(x.Symbol,intent.Symbol,StringComparison.Ordinal)&&x.Side==intent.Side).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("recovery.position-context-invalid");
                var position=matches[0];
                if(intent.Action is DecisionAction.ReduceLong or DecisionAction.ReduceShort)
                {
                    if(!tradingRules.TryGetValue(intent.Symbol,out var rule))throw new InvalidOperationException("recovery.reduce-rule-missing");
                    if(rule.StepSize<=0||rule.MinQuantity<=0||intent.Quantity<rule.MinQuantity||
                       intent.Quantity<=0||intent.Quantity>=position.Quantity||rule.RoundQuantity(intent.Quantity)!=intent.Quantity)
                        throw new InvalidOperationException("recovery.reduce-quantity-invalid");
                }
                var result=await recovery.ExecuteAsync(correlationId,intent,Math.Max(1,(int)position.Leverage),position.Isolated,ct);
                if(!result.Executed)throw new InvalidOperationException(result.Code);
                completed++;
            }
            return completed;
        }
        finally{ExecutionGate.Release();}
    }

    internal static async Task<int> ExecutePositionProtectionRecoveryAsync(
        ProductionRecoveryService recovery,string correlationId,IReadOnlyList<ProtectionAdjustment> adjustments,
        IReadOnlyList<ManagedPosition> positions,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recovery);ArgumentNullException.ThrowIfNull(adjustments);ArgumentNullException.ThrowIfNull(positions);
        await ExecutionGate.WaitAsync(ct);try
        {
            var completed=0;
            foreach(var adjustment in adjustments)
            {
                var matches=positions.Where(x=>string.Equals(x.Symbol,adjustment.Symbol,StringComparison.Ordinal)&&x.Side==adjustment.Side).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("recovery.position-context-invalid");
                var result=await recovery.ReplaceProtectionAsync(correlationId,adjustment,matches[0],ct);
                if(!result.Executed)throw new InvalidOperationException(result.Code);
                completed++;
            }
            return completed;
        }
        finally{ExecutionGate.Release();}
    }

    internal static (bool SafeToIncreaseRisk,string SafetyMessage) ApplyPositionMutationInvalidation(bool safeToIncreaseRisk,string safetyMessage,bool positionChanged)=>
        positionChanged?(false,"position.reconciliation-invalidated-by-recovery"):(safeToIncreaseRisk,safetyMessage);

    internal static (string LastMessage,string? OwnedMessage) ResolveAutomaticMaintenanceMessage(
        bool safeToIncreaseRisk,string currentLastMessage,string safetyMessage,string? ownedMessage,string healthyMessage)
    {
        if(!safeToIncreaseRisk)
        {
            var visible=string.IsNullOrWhiteSpace(safetyMessage)?currentLastMessage:safetyMessage;
            return(visible,string.IsNullOrWhiteSpace(safetyMessage)?null:safetyMessage);
        }
        return !string.IsNullOrWhiteSpace(ownedMessage)&&string.Equals(currentLastMessage,ownedMessage,StringComparison.Ordinal)
            ?(healthyMessage,null)
            :(currentLastMessage,null);
    }

    private static async Task<IReadOnlyDictionary<string,TradingRule>> ReadPositionManagementRulesAsync(
        IExchangeAdapter exchange,IReadOnlyList<ManagedPosition> positions,CancellationToken ct)
    {
        var rules=new Dictionary<string,TradingRule>(StringComparer.OrdinalIgnoreCase);
        foreach(var symbol in positions.Where(x=>x.Quantity>0).Select(x=>x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var rule=await exchange.GetRulesAsync(symbol,ct);
                if(rule.StepSize>0&&rule.MinQuantity>0)rules[symbol]=rule;
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch{ServiceLocator.RuntimeTrading.PublishError($"Provider trading rules unavailable for {symbol}; partial position reduction is disabled.");}
        }
        return rules;
    }

    private static async Task<IReadOnlyList<ManagedPosition>> ReadPositionsAsync(IExchangeAdapter exchange,CancellationToken ct)
    {
        try{return await exchange.GetPositionsAsync(ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{ServiceLocator.RuntimeTrading.PublishError("Provider position observation failed.");throw;}
    }

    private static async Task<IReadOnlyList<ExchangeOrder>> ReadOrdersAsync(IExchangeAdapter exchange,string? symbol,CancellationToken ct)
    {
        try{return await exchange.GetOpenOrdersAsync(symbol,ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{ServiceLocator.RuntimeTrading.PublishError("Provider order observation failed.");throw;}
    }

    private static void UpdateEvidence(EvidencePack evidence)
    {
        var state=ServiceLocator.SystemState;state.EvidenceCompleteness=evidence.Completeness;var markets=evidence.Markets.Values.OrderBy(x=>x.Symbol).ToArray();state.MarketSummary=markets.Length==0?L("Agent.NoMarketEvidence"):string.Join("\n",markets.Select(x=>$"{x.Symbol} {x.Price:F2} · spread {x.Quality.SpreadBps:F2}bp · ATR {x.Quality.AtrPercent:P2} · OI {x.Derivatives.OpenInterest:F0}"));state.NewsFullTextDocuments=evidence.News.Count(x=>x.BodySummary.Length>=160);state.NewsCorroboratingSources=evidence.News.Select(x=>x.CorroboratingSources).DefaultIfEmpty().Max();state.NewsSummary=evidence.News.Count==0?L("Agent.NoNews"):string.Join("\n",evidence.News.Take(6).Select(x=>$"[{x.Source}] {x.Title} · {x.EventType} · {x.Confidence:P0} · {x.CorroboratingSources} sources"));
        var btc=markets.FirstOrDefault(x=>x.Symbol=="BTCUSDT");var eth=markets.FirstOrDefault(x=>x.Symbol=="ETHUSDT");if(btc is not null){state.BtcPrice=btc.Price;state.BtcTrend=btc.Trend15m;state.BtcRsi=btc.Rsi;}if(eth is not null){state.EthPrice=eth.Price;state.EthTrend=eth.Trend15m;state.EthRsi=eth.Rsi;}
        var market=markets.FirstOrDefault();if(market is not null){state.Symbol=market.Symbol;state.Timeframe="15m / 1h / 4h";state.CurrentPrice=market.Price;state.Support=market.Support;state.Resistance=market.Resistance;state.DataQualityScore=market.Quality.QualityScore;state.VolatilityPercent=market.Quality.AtrPercent*100;state.LiquidityScore=market.Quality.LiquidityScore*100;}
        state.DecisionDiagnostics="Direct candle-structure decision path active.";state.MissingConditions=string.Join("; ",evidence.MissingSources.Distinct());
    }

    private static void UpdateDecisionUi(DecisionPlan decision,DecisionReview review)
    {
        var state=ServiceLocator.SystemState;state.LastDecision=decision.Action.ToString();state.LastReason=review.Explanation;state.DecisionDiagnostics=review.Explanation;state.PlannedEntry=decision.EntryPrice;state.PlannedStop=decision.StopLossPrice;state.PlannedTakeProfit=decision.TakeProfitPrice;state.RiskRewardRatio=decision.RiskRewardRatio;state.ReviewerStatus=review.Accepted?"APPROVED":"REJECTED";state.MarketRegime=string.IsNullOrWhiteSpace(decision.Regime)?MarketRegime.Unknown.ToString().ToUpperInvariant():decision.Regime.ToUpperInvariant();state.MissingConditions=string.Join("; ",review.BlockingReasons.Concat(decision.MissingConditions).Distinct());
    }

private static void UpdatePortfolioRiskUi(PortfolioRiskAssessment risk,IRealtimeMarketFeed realtime){var state=ServiceLocator.SystemState;state.RealtimeStatus=realtime.Status;state.PortfolioVaR99=risk.VaR99*100;state.PortfolioCVaR99=risk.CVaR99*100;state.PortfolioConcentration=risk.LargestPositionShare*100;state.PortfolioCorrelation=risk.MaximumPairCorrelation;state.PortfolioRiskSummary=risk.Summary;}

    private static void ApplyRuntimeHealth(RuntimeHealth health){var state=ServiceLocator.SystemState;state.RuntimeRunId=health.RunId;state.RuntimeHeartbeatAtUtc=health.HeartbeatAtUtc;state.RuntimeRecoveryStatus=health.RecoveryStatus;state.RuntimeEventSequence=health.EventSequence;state.LastUpdated=health.HeartbeatAtUtc;AgentRoleRuntimeRegistry.Shared.Publish("audit",health.RecoveryStatus=="LEASE_LOST"?"degraded":"monitoring",health.RecoveryStatus=="LEASE_LOST"?"Runtime audit lease was lost; persisted health is no longer authoritative.":"Runtime lease and append-only audit heartbeat are active.",health.HeartbeatAtUtc);StateChanged?.Invoke();}

    private static void Stage(string key,string node,int progress){var state=ServiceLocator.SystemState;state.SkillStageKey=key;state.SkillStage=L(key);state.WorkflowNode=node;state.ThinkingProgress=progress;state.LastUpdated=DateTime.UtcNow;StateChanged?.Invoke();}
    private static void ApplyLocalBrainState(SystemState state,IAssistantProvider brain)
    {
        state.BrainName=brain.Name;
        state.ActiveBrainProvider=brain.Name;
        state.ActiveBrainModel="local-deterministic";
    }
    private static async Task<T> SkillAsync<T>(string name,string input,Func<CancellationToken,Task<T>> call,Func<T,string> summarize,CancellationToken ct)
    {
        var descriptor=SkillGuard.Authorize(name,ServiceLocator.SystemState.Mode);const string mode="LocalOnly";using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(descriptor.TimeoutSeconds));var stopwatch=Stopwatch.StartNew();try{var value=await call(timeout.Token);await Db.RecordSkillCallAsync(name,"SUCCESS",stopwatch.ElapsedMilliseconds,input,Trim(summarize(value)),null,ct,mode,false,null,null,null,null,null,null,null,null);return value;}catch(Exception ex){await Db.RecordSkillCallAsync(name,"FAILED",stopwatch.ElapsedMilliseconds,input,string.Empty,Trim(ex.Message),CancellationToken.None,mode,false,null,null,null,null,null,null,null,null);throw;}
    }
    private static async Task NotifyAgentDegradedAsync(string correlation,string stage,Exception ex)
    {
        try
        {
            var provider=_activeExchange is IExchangeProvider value?value.ProviderId:"unknown";
            var environment=_activeExchange?.Environment.ToString()??"Testnet";
            var notification=ConfirmedNotificationTruth.System(
                ConfirmedNotificationTruth.EventKey(correlation,stage,NotificationEventKind.AgentDegraded),
                NotificationEventKind.AgentDegraded,provider,environment,DateTime.UtcNow,
                UiDiagnostic.FromText(ex.ToString(),"Agent degraded").Code);
            await NotificationRuntimeFactory.CurrentObserver.ObserveAsync(notification,CancellationToken.None);
        }
        catch{/* Notification failure must not affect the Agent failure path. */}
    }
    private static string Trim(string? value)=>SensitiveDataRedactor.ForLog(value,500);
    private static string Safe(Exception exception)=>SensitiveDataRedactor.ForLog(exception.Message,240);
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
    private static void Set(AgentStatus status,string message){var state=ServiceLocator.SystemState;state.Status=status;state.LastMessage=SensitiveDataRedactor.ForLog(message,500);state.LastUpdated=DateTime.UtcNow;StateChanged?.Invoke();}

    internal static Task<LegacyIntentIsolationRunResult> RunLegacyIntentIsolationAsync(IExchangeAdapter exchange,CancellationToken ct)=>
        new LegacyIntentIsolationService(Db,new TestnetLegacyIntentReadOnlyFacts(exchange)).RunAsync(ct);

    internal static async Task<LegacyIntentIsolationRunResult> RunConfiguredLegacyIntentIsolationAsync(CancellationToken ct)
    {
        var settings=SettingsStore.Load();var profile=SettingsStore.GetActiveExchange(settings);
        if(!profile.IsTestnet)throw new InvalidOperationException("Legacy intent isolation is Testnet-only.");
        var credentials=SettingsStore.GetExchangeCredentials(profile);
        if(credentials.Values.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException("Configured Testnet credentials are incomplete.");
        await using var exchange=Providers.Create(profile,credentials);
        return await RunLegacyIntentIsolationAsync(exchange,ct);
    }
}

internal sealed class TestnetLegacyIntentReadOnlyFacts:ILegacyIntentReadOnlyFacts
{
    private readonly IExchangeAdapter _exchange;
    public TestnetLegacyIntentReadOnlyFacts(IExchangeAdapter exchange)
    {
        _exchange=exchange??throw new ArgumentNullException(nameof(exchange));
        if(exchange.Environment!=ExchangeEnvironment.Testnet)throw new InvalidOperationException("Legacy intent isolation is Testnet-only.");
    }

    public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)=>_exchange.FindOrderAsync(symbol,clientOrderId,ct);
    public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>_exchange.GetPositionsAsync(ct);
    public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string symbol,CancellationToken ct)=>_exchange.GetOpenOrdersAsync(symbol,ct);
}

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
using WpeAgent.FinancialEvidence;
using WpeAgent.ModelOff;
using WpeAgent.RuntimeServices;
using System.Security.Cryptography;
using 币安量化机器人.Services.Access;

namespace 币安量化机器人.Services;

internal sealed class ProductionAutomaticExecutionValidator : IAutomaticExecutionReadOnlyValidator
{
    private static readonly TimeSpan MaximumMarketAge=TimeSpan.FromSeconds(30);
    private readonly AgentSettingsStore _settings;private readonly ExchangeConnectionProfile _profile;private readonly IExchangeAdapter _exchange;private readonly IReadOnlyDictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability> _capabilities;private readonly Func<bool> _runtimeReady;
    public ProductionAutomaticExecutionValidator(AgentSettingsStore settings,ExchangeConnectionProfile profile,IExchangeAdapter exchange,IReadOnlyDictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability> capabilities,Func<bool> runtimeReady)
    {_settings=settings;_profile=profile;_exchange=exchange;_capabilities=capabilities;_runtimeReady=runtimeReady;}
    public Task<AutomaticExecutionRuntimeFacts> ValidateAsync(DurableExecutionArtifactV2 artifact,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var now=DateTimeOffset.UtcNow;var current=_settings.Load();
        var testnet=current.Environment==ExchangeEnvironment.Testnet&&current.AuthorizationMode==TradingAuthorizationMode.Auto&&_profile.IsTestnet&&_exchange.Environment==ExchangeEnvironment.Testnet&&artifact.Environment=="Testnet";
        var runtime=_settings.LastLoadDiagnostic is null&&_runtimeReady()&&current.SetupCompleted&&current.LastAccessCheckAtUtc is not null&&now-new DateTimeOffset(DateTime.SpecifyKind(current.LastAccessCheckAtUtc.Value,DateTimeKind.Utc))<=TimeSpan.FromMinutes(30);
        var capability=artifact.Intents.Count>0&&artifact.Intents.All(intent=>_capabilities.TryGetValue(intent.Symbol,out var value)&&value.Status==WpeAgent.RuntimeContracts.CapabilityStatus.Available&&value.CanTrade&&value.TestnetAvailable&&now-value.CheckedAt<=WpeAgent.RuntimeContracts.ProviderCapabilityPrecondition.MaximumAge);
        var market=artifact.MarketCollectedAtUtc<=now&&now-artifact.MarketCollectedAtUtc<=MaximumMarketAge;
        return Task.FromResult(new AutomaticExecutionRuntimeFacts(testnet?AutomaticFactState.True:AutomaticFactState.False,runtime?AutomaticFactState.True:AutomaticFactState.False,capability?AutomaticFactState.True:AutomaticFactState.False,market?AutomaticFactState.True:AutomaticFactState.False,"automatic.production-facts"));
    }
}

public static class AutoTradingAgent
{
    private static CancellationTokenSource? _cts;private static Task? _run;private static readonly AgentSettingsStore SettingsStore=new();private static readonly AgentSqliteStore Db=new();private static readonly ExchangeProviderCatalog Providers=new();private static readonly SkillExecutionGuard SkillGuard=new();private static readonly SemaphoreSlim ExecutionGate=new(1,1);private static IExchangeAdapter? _activeExchange;private static TradingExecutionGateway? _activeExecutionGateway;private static Func<CancellationToken,Task>? _activeCapabilityRefresh;private static string? _activeRuntimeSessionId;private static decimal _dayHigh;private static bool _paused;
    public static bool IsRunning=>_run is{IsCompleted:false};public static bool CanEmergencyClose=>_activeExchange is not null&&_activeExecutionGateway is not null&&_activeCapabilityRefresh is not null&&!string.IsNullOrWhiteSpace(_activeRuntimeSessionId);public static event Action? StateChanged;

    internal sealed record ModelOffFixtureCycleRequestV1(
        string CycleId,DateTimeOffset EvaluationTimeUtc,
        IReadOnlyList<FinancialEvidenceRecordV1>? Fixtures,
        FinancialEvidenceRetrievalRequestV1 Retrieval,
        bool MainnetRequested=false);

    internal sealed record ModelOffAutonomousCycleResultV1(
        IReadOnlyDictionary<ModelOffAgentV1,ModelOffAgentOutputV1> Outputs,
        IReadOnlyDictionary<ModelOffAgentV1,ModelOffCanonicalDocumentV1> Documents,
        IReadOnlyDictionary<ModelOffAgentV1,string> Reports,
        IReadOnlyDictionary<ModelOffAgentV1,string> Alerts,
        IReadOnlyList<ModelOffCanonicalDocumentV1> Handoffs,
        IReadOnlyDictionary<ModelOffAgentV1,string> AuditCoverage,
        bool EligibleForRiskIncrease,string Code);

    internal static async Task<ModelOffAutonomousCycleResultV1> RunModelOffFixtureCycleAsync(
        ModelOffFixtureCycleRequestV1 request,AgentSqliteStore auditStore,
        ModelExplanationAttachmentV1? explanation,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);ArgumentNullException.ThrowIfNull(auditStore);
        if(string.IsNullOrWhiteSpace(request.CycleId))throw new ArgumentException("Cycle id is required.",nameof(request));
        if(request.EvaluationTimeUtc==default||request.EvaluationTimeUtc.Offset!=TimeSpan.Zero)throw new ArgumentException("An injected UTC evaluation time is required.",nameof(request));
        _=explanation; // Attachments are deliberately outside canonical composition and eligibility.

        AuthorizedLocalCorpusResultV1 corpus;
        try{corpus=new AuthorizedLocalCorpusV1().Collect(request.Fixtures,request.Retrieval);}
        catch(Exception ex) when(ex is ArgumentException or FinancialEvidenceValidationException)
        {corpus=new(false,[],["evidence.fixture-invalid"]);}
        var corpusReasons=corpus.ReasonCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var marketSources=corpus.Accepted
            ? corpus.Records.Select(record=>new ModelOffSourceV1(record.RecordId,ModelOffSourceKindV1.Market,record.ObservedAt,record.Draft.RecordedAt,ModelOffSourceStatusV1.Available,NormalizeHash(record.RecordHash))).ToArray()
            : [BlockedSource("market-fixture-gate",request.EvaluationTimeUtc,corpusReasons)];
        var market=Output(ModelOffAgentV1.Market,request,"market",marketSources,corpus.Accepted,"record_market",corpusReasons,
            new{fixture_count=corpus.Records.Count,source="authorized_local_fixture"});
        var marketDocument=ModelOffCanonicalSerializerV1.Serialize(market);

        var researchSource=DownstreamSource("market:BTCUSDT",ModelOffSourceKindV1.Market,request.EvaluationTimeUtc,marketDocument,ModelOffEligibilityV1.IsEligibleForDownstream(market));
        var marketEvidenceSha256=researchSource.ArtifactHash!["sha256:".Length..];
        var research=DeterministicResearchCapabilityProducerV1.Produce(new(
            ModelOffResearchCapabilityV1.Technical,Id(request,"research"),request.CycleId,request.EvaluationTimeUtc,
            DeterministicResearchCapabilityProducerV1.InputSchema,"wpe.technical-method",DeterministicResearchCapabilityProducerV1.MethodVersion,
            [researchSource],System.Text.Json.JsonSerializer.SerializeToElement(new{schema="wpe.technical-assessment/1.0",symbol="BTCUSDT",marketSymbol="BTCUSDT",observedAtUtc=request.EvaluationTimeUtc,marketEvidenceSha256,rsi=50d,trend15m=0d,trend1h=0d,trend4h=0d}),[]));
        var researchDocument=ModelOffCanonicalSerializerV1.Serialize(research);

        var researchReady=ModelOffEligibilityV1.IsEligibleForDownstream(research);
        var strategyReasons=researchReady?Array.Empty<string>():["strategy.research-ineligible"];
        var strategy=Output(ModelOffAgentV1.Strategy,request,"strategy",
            [DownstreamSource("research-output",ModelOffSourceKindV1.Strategy,request.EvaluationTimeUtc,researchDocument,researchReady)],
            researchReady,"hold",strategyReasons,new{action="hold",deterministic_tie_break=false});
        var strategyDocument=ModelOffCanonicalSerializerV1.Serialize(strategy);

        var riskInputState=ModelOffEligibilityV1.IsEligibleForDownstream(strategy)?ModelOffInputStateV1.Available:ModelOffInputStateV1.Unknown;
        var riskContract=new RiskAndPositionPlanner().EvaluateModelOffIntent(new(
            DecisionAction.Hold,"BTCUSDT",request.EvaluationTimeUtc,TimeSpan.FromMinutes(5),request.MainnetRequested,
            [new("strategy",riskInputState,"sha256:"+strategyDocument.Sha256,request.EvaluationTimeUtc)]));
        var riskReasons=riskContract.Rules.Where(rule=>!rule.Passed).Select(rule=>rule.ReasonCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var riskReady=researchReady&&riskContract.EligibleForRiskIncrease&&!request.MainnetRequested;
        var risk=Output(ModelOffAgentV1.Risk,request,"risk",
            [DownstreamSource("strategy-output",ModelOffSourceKindV1.Strategy,request.EvaluationTimeUtc,strategyDocument,riskReady)],
            riskReady,riskReady?"risk_approved":"block",riskReasons,
            new{riskContract.IntentSha256,riskContract.LedgerSha256,eligible=riskReady,mainnet=request.MainnetRequested},
            riskContract.Rules.Select(rule=>System.Text.Json.JsonSerializer.SerializeToElement(rule)).ToArray());
        var riskDocument=ModelOffCanonicalSerializerV1.Serialize(risk);

        var executionReady=ModelOffEligibilityV1.IsEligibleForDownstream(risk)&&!request.MainnetRequested;
        var executionReasons=executionReady?Array.Empty<string>():[request.MainnetRequested?"execution.mainnet-disabled":"execution.risk-blocked"];
        var execution=Output(ModelOffAgentV1.Execution,request,"execution",
            [DownstreamSource("risk-output",ModelOffSourceKindV1.Audit,request.EvaluationTimeUtc,riskDocument,executionReady)],
            executionReady,"no_mutation",executionReasons,new{mutation_attempted=false,gateway_required=true,executor="ReliableOrderExecutor"});
        var executionDocument=ModelOffCanonicalSerializerV1.Serialize(execution);

        var recoveryReady=ModelOffEligibilityV1.IsEligibleForDownstream(execution);
        var recoveryReasons=recoveryReady?Array.Empty<string>():["recovery.execution-blocked"];
        var recovery=Output(ModelOffAgentV1.Recovery,request,"recovery",
            [DownstreamSource("execution-output",ModelOffSourceKindV1.Audit,request.EvaluationTimeUtc,executionDocument,recoveryReady)],
            recoveryReady,"no_recovery_required",recoveryReasons,new{quarantined=!recoveryReady,resubmit_allowed=false});
        var recoveryDocument=ModelOffCanonicalSerializerV1.Serialize(recovery);

        var upstream=new[]{marketDocument,researchDocument,strategyDocument,riskDocument,executionDocument,recoveryDocument};
        var upstreamOutputs=new[]{market,research,strategy,risk,execution,recovery};
        var auditReady=upstreamOutputs.All(ModelOffEligibilityV1.IsEligibleForDownstream);
        var auditReasons=auditReady?Array.Empty<string>():["audit.upstream-ineligible"];
        var auditSources=upstreamOutputs.Zip(upstream,(output,document)=>DownstreamSource(output.OutputId,ModelOffSourceKindV1.Audit,request.EvaluationTimeUtc,document,ModelOffEligibilityV1.IsEligibleForDownstream(output))).ToArray();
        var audit=Output(ModelOffAgentV1.Audit,request,"audit",auditSources,auditReady,auditReady?"record_audit":"block",auditReasons,
            new{expected_output_count=7,upstream_hashes=upstream.Select(document=>document.Sha256).ToArray(),self=Id(request,"audit")});
        var auditDocument=ModelOffCanonicalSerializerV1.Serialize(audit);

        var outputArray=upstreamOutputs.Append(audit).ToArray();
        var documentArray=upstream.Append(auditDocument).ToArray();
        var outputs=outputArray.ToDictionary(output=>output.Agent);
        var documents=outputArray.Zip(documentArray).ToDictionary(pair=>pair.First.Agent,pair=>pair.Second);
        var persisted=true;string persistenceCode="audit.persisted";
        try
        {
            foreach(var pair in outputArray.Zip(documentArray))
            {var saved=await auditStore.SaveModelOffCanonicalAuditAsync(pair.First,pair.Second,ct);if(!saved.Succeeded){persisted=false;persistenceCode=saved.Code;break;}}
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{persisted=false;persistenceCode="audit.persistence-failed";}

        var reports=outputs.ToDictionary(pair=>pair.Key,pair=>ModelOffFixedTemplatesV1.RenderReport(pair.Value));
        var alerts=outputs.ToDictionary(pair=>pair.Key,pair=>ModelOffFixedTemplatesV1.RenderAlert(pair.Value,new(
            ModelOffEligibilityV1.IsEligibleForDownstream(pair.Value)?ModelOffAlertSeverityV1.Info:ModelOffAlertSeverityV1.High,
            pair.Value.Decision.ReasonCodes.FirstOrDefault()??"model-off.ready",pair.Key.ToString().ToLowerInvariant(),
            ModelOffEligibilityV1.IsEligibleForDownstream(pair.Value)?"continue_deterministically":"block_risk_increase",null)));
        var handoffs=new List<ModelOffCanonicalDocumentV1>();
        for(var index=0;index<outputArray.Length-1;index++)
        {
            var ready=ModelOffEligibilityV1.IsEligibleForDownstream(outputArray[index]);
            handoffs.Add(ModelOffCanonicalSerializerV1.SerializeHandoff(new(
                $"{request.CycleId}-handoff-{index+1}",request.CycleId,outputArray[index].Agent,outputArray[index+1].Agent,
                outputArray[index].OutputId,documentArray[index].Sha256,ready?ModelOffHandoffStatusV1.Ready:ModelOffHandoffStatusV1.Blocked,
                ready?["continue"]:[],["model_override","direct_mutation"],ready?[]:outputArray[index].Decision.ReasonCodes,
                request.EvaluationTimeUtc,request.EvaluationTimeUtc.AddMinutes(5))));
        }
        var coverage=documents.ToDictionary(pair=>pair.Key,pair=>pair.Value.Sha256);
        var eligible=auditReady&&ModelOffEligibilityV1.IsEligibleForDownstream(audit)&&persisted;
        return new(outputs,documents,reports,alerts,handoffs,coverage,eligible,eligible?"model-off.cycle-ready":persistenceCode=="audit.persisted"?"model-off.cycle-blocked":persistenceCode);
    }

    private static ModelOffAgentOutputV1 Output(
        ModelOffAgentV1 agent,ModelOffFixtureCycleRequestV1 request,string suffix,IReadOnlyList<ModelOffSourceV1> sources,
        bool succeeded,string action,IReadOnlyList<string> reasons,object facts,IReadOnlyList<System.Text.Json.JsonElement>? calculations=null)
        =>new(agent,Id(request,suffix),request.CycleId,request.EvaluationTimeUtc,request.EvaluationTimeUtc,"wpe.model-off-cycle-input/1.0",
            new($"wpe.{suffix}-composition","1.0"),sources,succeeded?ModelOffOutputStatusV1.Succeeded:ModelOffOutputStatusV1.Blocked,
            new(succeeded?ModelOffUncertaintyLevelV1.None:ModelOffUncertaintyLevelV1.Unknown,reasons,succeeded?[]:["downstream_eligibility"]),
            System.Text.Json.JsonSerializer.SerializeToElement(facts),calculations??[],new(action,succeeded,reasons),[],ModelOffFixedTemplatesV1.SummaryVersion);
    private static string Id(ModelOffFixtureCycleRequestV1 request,string suffix)=>$"{request.CycleId}-{suffix}";
    private static ModelOffSourceV1 DownstreamSource(string id,ModelOffSourceKindV1 kind,DateTimeOffset at,ModelOffCanonicalDocumentV1 document,bool ready)=>
        new(id,kind,at,at,ready?ModelOffSourceStatusV1.Available:ModelOffSourceStatusV1.Unknown,"sha256:"+document.Sha256);
    private static ModelOffSourceV1 BlockedSource(string id,DateTimeOffset at,IReadOnlyList<string> reasons)=>
        new(id,ModelOffSourceKindV1.Config,at,at,ModelOffSourceStatusV1.Unknown,Hash(string.Join("|",reasons.DefaultIfEmpty("evidence.rejected"))));
    private static string NormalizeHash(string hash)=>hash.StartsWith("sha256:",StringComparison.Ordinal)?hash:"sha256:"+hash;
    private static string Hash(string value)=>"sha256:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
                var intent=new ExecutionIntent(position.Symbol,position.Side,quantity,true,0,0,id,L("Agent.EmergencyIntent"),action,ExpectedPrice:position.MarkPrice);var command=new EmergencyReductionCommand(confirmation,position,intent,Math.Max(1,(int)position.Leverage),position.Isolated);var execution=await executionGateway.ExecuteEmergencyReductionAsync(command,ct);if(!execution.Executed)throw new InvalidOperationException(execution.Code);closed.Add($"{position.Symbol} {position.Side} {quantity}");
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
        var roles=AgentRoleRuntimeRegistry.Shared;roles.Publish("market","monitoring","Public market and Testnet streams are being monitored.");roles.Publish("research","waiting","Waiting for the next market-driven research cycle.");roles.Publish("strategy","monitoring","Active strategies and the candidate research schedule are being monitored.");roles.Publish("risk","monitoring","Risk limits and execution authority are being monitored.");roles.Publish("execution","waiting","Execution queue is ready; no approved order is pending.");roles.Publish("recovery","monitoring","Order state and reconciliation queue are being monitored.");roles.Publish("audit","monitoring","Append-only runtime and decision audit is active.");
        var newsResearch=new NewsResearchService();var collector=new EvidenceCollector(exchange,settings.Symbols,realtime,newsResearch);
        var executor=new ReliableOrderExecutor(exchange,Db,settings.Risk,SystemOrderPollScheduler.Instance,capabilitySnapshot,true,capabilityRefresh:async(symbol,token)=>{var refreshed=await new ProviderCapabilityProbe().ProbeAsync(exchange,[symbol],true,token);if(refreshed.TryGetValue(symbol,out var value)){capabilitySnapshot[symbol]=value;return value;}return null;});
        var recoveryServices=await ProductionRecoveryComposition.CreateAsync(exchange,executor,Db,ProductionRecoveryComposition.DefaultKeyPath(),ct:ct);var executionGateway=recoveryServices.Gateway;var planner=new RiskAndPositionPlanner();var aggregator=new SignalAggregationSkill();var governance=new DecisionGovernanceSkill();var deterministic=new DeterministicPlanSkill();var independentRisk=new IndependentRiskManagerSkill();var longResearch=new LongHorizonResearchSkill();var portfolioRiskSkill=new PortfolioRiskSkill();var historicalData=new HistoricalDataService(exchange,Db);var positionManager=new PositionManagementSkill();var strategyResearch=new StrategyResearchAgent(Db,runtimeBacktests:ServiceLocator.RuntimeBacktests);var strategyScheduler=new StrategyResearchScheduler(strategyResearch,Db);var strategySchedulerTask=strategyScheduler.StartAsync(settings.Symbols,settings.Risk,ct);bool TeacherNotificationAllowed(NotificationEventKind kind){var current=SettingsStore.Load();return current.Notification.Enabled&&current.Notification.EventKinds.Contains(kind.ToString(),StringComparer.OrdinalIgnoreCase);}using var teacherCryptoScheduler=new TeacherCryptoEvidenceSchedulerV2(Db,notifications.Observer,TeacherNotificationAllowed);var teacherCryptoSchedulerTask=teacherCryptoScheduler.StartAsync(settings.Symbols,ct);var teacherLessonPublisher=new TeacherLessonNotificationPublisherV2(Db,notifications.Observer,TeacherNotificationAllowed);var macroHttp=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};macroHttp.DefaultRequestHeaders.UserAgent.ParseAdd("WPE-Agent/3.6 (local macro research)");var macroScheduler=new WpeAgent.AgentServices.MacroResearchScheduler(new(new WpeAgent.AgentServices.HttpBlsMacroDataTransport(macroHttp)),Db,calendar:new(new WpeAgent.AgentServices.HttpBlsReleaseCalendarTransport(macroHttp)));var macroSchedulerTask=macroScheduler.StartAsync(ct);
        _activeExecutionGateway=executionGateway;_activeRuntimeSessionId=runtime.RunId;
        var automaticGateway=new TradingAutomaticExecutionGateway(executionGateway,exchange,Db);
        var automaticValidator=new ProductionAutomaticExecutionValidator(SettingsStore,exchangeProfile,exchange,capabilitySnapshot,()=>ReferenceEquals(_activeExchange,exchange)&&string.Equals(_activeRuntimeSessionId,runtime.RunId,StringComparison.Ordinal));
        var automaticWorker=new AutomaticExecutionWorker(new AutomaticExecutionProcessor(Db,automaticValidator,automaticGateway));Task? automaticWorkerTask=null;Task? tradingObservationTask=null;
        var interruptedWorkflows=await Db.GetInterruptedWorkflowsAsync(ct);var startupRecovery=executionGateway.AssessUnverifiedAutomaticMutation(AutomaticMutationPath.StartupRecovery,interruptedWorkflows.Count);if(!startupRecovery.SafeToIncreaseRisk)await Db.SetStateAsync("authorization.startup-recovery",startupRecovery.Code,ct);
        var runtimeMode=RuntimeModePolicy.Resolve(settings);var selectedBrain=RuntimeModePolicy.GetActiveBrain(settings);var localBrain=AssistantProviderFactory.CreateLocal();IAssistantProvider brain=CreateConfiguredBrain(runtimeMode,selectedBrain)??localBrain;
        ServiceLocator.SystemState.Mode=TradingMode.Testnet;var runtimeState=ServiceLocator.SystemState;ApplyRuntimeModeState(runtimeState,runtimeMode,brain);runtimeState.ExchangeConnected=true;runtimeState.ApiTradePermission=exchangeProfile.TradePermission;Set(AgentStatus.Running,L("Agent.Started",exchangeProfile.DisplayName,brain.Name));var startupHealth=await brain.HealthCheckAsync(ct);if(!startupHealth.Healthy&&runtimeMode.AllowRemoteBrain&&!brain.IsLocal){runtimeState.BrainFallbackReason=string.IsNullOrWhiteSpace(runtimeState.BrainFallbackReason)?startupHealth.Message:$"{runtimeState.BrainFallbackReason} | {startupHealth.Message}";brain=localBrain;startupHealth=await brain.HealthCheckAsync(ct);runtimeState.BrainEffectiveMode=AiRuntimeMode.LocalOnly;runtimeState.BrainRemoteAllowed=false;runtimeState.BrainName=brain.Name;runtimeState.ActiveBrainProvider=brain.Name;runtimeState.ActiveBrainModel="local-deterministic";}runtimeState.BrainConnected=startupHealth.Healthy;if(!startupHealth.Healthy)throw new InvalidOperationException(L("Agent.BrainFailure",startupHealth.Message));Stage("Stage.HistoricalSync","OBSERVATION",12);
        try{var sync=await SkillAsync("HistoricalData",string.Join(',',settings.Symbols),token=>historicalData.SyncAsync(settings.Symbols,"1h",3,token),x=>string.Join(" | ",x.Select(s=>$"{s.Symbol}:{s.Stored}/{s.CoverageDays}d")),ct);ServiceLocator.SystemState.HistoricalCoverageDays=sync.Count==0?0:sync.Min(x=>x.CoverageDays);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){await Db.RecordErrorAsync("HistoricalData",ex,CancellationToken.None);ServiceLocator.SystemState.HistoricalCoverageDays=0;ServiceLocator.SystemState.LastMessage=L("Agent.CycleDegraded",ex.Message);}
        try
        {
        automaticWorkerTask=automaticWorker.RunAsync("automatic-"+runtime.RunId,TimeSpan.FromSeconds(1),ct);
        tradingObservationTask=ObserveTradingRuntimeAsync(exchange,TimeSpan.FromSeconds(10),ct);
        while(!ct.IsCancellationRequested)
        {
            if(_paused){await Task.Delay(1000,ct);continue;}var cycle=Guid.NewGuid().ToString("N");var workflowStarted=false;
            try
            {
                roles.Publish("research","running","Collecting evidence and evaluating the current market cycle.");
                await RefreshCapabilitySnapshot(ct);
                Stage("Stage.AccountSync","OBSERVATION",18);var (account,positions,orders)=await ReadAccountStateAsync(exchange,ct);await Db.SaveSnapshotAsync(account,positions,orders,ct);await Db.SaveEquitySnapshotAsync(new(account.Timestamp,account.Equity,account.AvailableBalance,exchange.Environment.ToString(),exchange.ProviderId),ct);await ServiceLocator.RuntimeEquity.RefreshAsync(ct);_dayHigh=await Db.GetOrUpdateDailyHighAsync(DateOnly.FromDateTime(DateTime.UtcNow),account.Equity,ct);UpdateAccount(account,positions,orders);
                Stage("Stage.Recovery","OBSERVATION",25);var recoverable=await Db.GetRecoverableIntentsAsync(ct);var pendingAssessment=executionGateway.AssessUnverifiedAutomaticMutation(AutomaticMutationPath.StartupRecovery,recoverable.Count);var pendingRecovery=new RecoveryResult(pendingAssessment.SafeToIncreaseRisk,[pendingAssessment.Code]);orders=await ReadOrdersAsync(exchange,null,ct);ServiceLocator.RuntimeTrading.Publish(positions,orders);var reconciliationNow=DateTimeOffset.UtcNow;var protectionReconciliation=ProtectionReconciliationServiceV1.Reconcile(positions,orders,reconciliationNow,reconciliationNow);await Db.SaveProtectionReconciliationAsync(protectionReconciliation,ct);var observedAt=account.Timestamp.Kind==DateTimeKind.Utc?new DateTimeOffset(account.Timestamp):new DateTimeOffset(DateTime.SpecifyKind(account.Timestamp,DateTimeKind.Utc));var executionLedger=await Db.GetExecutionPositionLedgerAsync(ct);var positionReconciliation=PositionReconciliationServiceV1.Reconcile(executionLedger,positions,observedAt,reconciliationNow);await Db.SavePositionReconciliationAsync(positionReconciliation,ct);var externalPositionIsolation=ExternalPositionIsolationServiceV1.Evaluate(executionLedger,positions,observedAt,reconciliationNow);await Db.SaveExternalPositionIsolationAsync(externalPositionIsolation,ct);var safeToIncreaseRisk=pendingRecovery.SafeToIncreaseRisk&&protectionReconciliation.AllowsRiskIncrease&&positionReconciliation.AllowsRiskIncrease&&externalPositionIsolation.AllowsRiskIncrease;var protectionMessage=protectionReconciliation.AllowsRiskIncrease?"protection.reconciliation-confirmed":string.Join(',',protectionReconciliation.ReasonCodes);var positionMessage=positionReconciliation.AllowsRiskIncrease?"position.reconciliation-confirmed":string.Join(',',positionReconciliation.ReasonCodes);var isolationMessage=externalPositionIsolation.AllowsRiskIncrease?"external-position.isolation-clear":string.Join(',',externalPositionIsolation.ReasonCodes);var safetyMessage=string.Join("；",pendingRecovery.Messages.Append(protectionMessage).Append(positionMessage).Append(isolationMessage));if(!safeToIncreaseRisk)await Db.SetStateAsync("authorization.automatic-maintenance",safetyMessage,ct);
                if(!safeToIncreaseRisk)ServiceLocator.SystemState.LastMessage=string.IsNullOrWhiteSpace(safetyMessage)?L("Agent.CycleDegraded",string.Empty):safetyMessage;

                Stage("Stage.Evidence","OBSERVATION",32);var evidence=await SkillAsync("EvidenceCollector",string.Join(',',settings.Symbols),token=>collector.CollectAsync(token),x=>$"completeness={x.Completeness} markets={x.Markets.Count} missing={x.MissingSources.Count}",ct);await Db.SaveNewsAsync(evidence.News,ct);roles.Publish("strategy","running","Evaluating active signals and shadow candidates against fresh market evidence.");await strategyResearch.ObserveAsync(evidence,ct);var strategyProfiles=await Db.GetStrategiesAsync(ct);var strategySelections=await AdaptiveStrategySelector.SelectAsync(strategyProfiles,evidence,strategyResearch,ct);var localSignals=strategySelections.ToDictionary(x=>x.Key,x=>x.Value.Signal,StringComparer.OrdinalIgnoreCase);var strategySnapshot=await Db.GetStrategySnapshotAsync(ct);ServiceLocator.SystemState.StrategyStatus=strategySnapshot.Status;ServiceLocator.SystemState.StrategySummary=strategySnapshot.ActiveStrategy;ServiceLocator.SystemState.StrategyCandidates=strategySnapshot.Candidates;roles.Publish("strategy","monitoring",$"Active and shadow strategies were evaluated; {strategySnapshot.Candidates} candidates remain under lifecycle monitoring.");await Db.StartCycleAsync(cycle,evidence,brain.Name,ct);await runtime.BeginCycleAsync(cycle,new{evidence.Completeness,Markets=evidence.Markets.Keys,brain=brain.Name,strategies=strategySnapshot.ActiveStrategy},ct);workflowStarted=true;
                Stage("Stage.Research","OBSERVATION",36);await runtime.TransitionAsync(cycle,WorkflowNode.Research,new{evidence.Completeness,MarketCount=evidence.Markets.Count},ct);var research=new Dictionary<string,ResearchValidationResult>(StringComparer.OrdinalIgnoreCase);var histories=new Dictionary<string,IReadOnlyList<CandleEvidence>>(StringComparer.OrdinalIgnoreCase);foreach(var market in evidence.Markets.Values){var series=await Db.LoadHistoricalCandlesAsync(market.Symbol,"1h",30000,ct);histories[market.Symbol]=series;var validation=await SkillAsync("StrategyResearch",$"{market.Symbol} candles={series.Count}",_=>Task.FromResult(longResearch.Evaluate(market.Symbol,series,settings.Risk)),x=>$"promoted={x.Promoted} score={x.QualityScore:F2} trades={x.Trades} coverage={x.CoverageDays}d",ct);research[market.Symbol]=validation;await Db.SaveResearchAsync(validation,ct);}
                Stage("Stage.PositionManagement","RISK",39);await runtime.TransitionAsync(cycle,WorkflowNode.PositionManagement,new{Positions=positions.Count,Research=research.Count},ct);var management=await SkillAsync("PositionManagement",$"positions={positions.Count}",token=>positionManager.EvaluateAsync(positions,evidence.Markets,Db,token),x=>$"intents={x.Intents.Count} adjustments={x.ProtectionAdjustments.Count}",ct);var managementActivity=false;
                if(management.Intents.Count>0||management.ProtectionAdjustments.Count>0)
                {
                    await runtime.TransitionAsync(cycle,WorkflowNode.SafetyExecution,new{RiskReducingIntents=management.Intents.Count,ProtectionAdjustments=management.ProtectionAdjustments.Count},ct);
                    managementActivity=await ExecutePositionManagementRecoveryAsync(recoveryServices.Recovery,cycle,management.Intents,positions,ct)>0;
                    (safeToIncreaseRisk,safetyMessage)=ApplyPositionMutationInvalidation(safeToIncreaseRisk,safetyMessage,managementActivity);
                    if(managementActivity)await Db.SetStateAsync("authorization.position-reconciliation","position.reconciliation-invalidated-by-recovery",ct);
                    if(management.ProtectionAdjustments.Count>0){var blocked=executionGateway.AssessUnverifiedAutomaticMutation(AutomaticMutationPath.PositionManagement,management.ProtectionAdjustments.Count);safeToIncreaseRisk=false;safetyMessage=blocked.Code;await Db.SetStateAsync("authorization.position-management",blocked.Code,ct);}
                }

                Stage("Stage.Aggregation","OBSERVATION",43);await runtime.TransitionAsync(cycle,WorkflowNode.Aggregation,new{ManagementIntents=management.Intents.Count,managementActivity},ct);var assessments=await SkillAsync("SignalAggregation",$"markets={evidence.Markets.Count}",_=>Task.FromResult(aggregator.Analyze(evidence,settings.Decision,localSignals)),x=>$"assessments={x.Count} ready={x.Count(a=>a.EntryReady)}",ct);UpdateEvidence(evidence,assessments,research);
                Stage("Stage.Planner","PLANNER",52);await runtime.TransitionAsync(cycle,WorkflowNode.Planner,new{Assessments=assessments.Count,EntryReady=assessments.Count(x=>x.EntryReady)},ct);DecisionPlan proposed;string? brainRequest=null,brainResponse=null;
                try{var activeSymbols=positions.Select(x=>x.Symbol).Distinct().ToArray();var outcomes=await Db.RecentOutcomeMemoriesAsync(ct);var holdCount=await Db.ConsecutiveHoldCountAsync(ct);var memorySymbol=activeSymbols.Length==1?activeSymbols[0]:assessments.Where(x=>x.Fresh).OrderByDescending(x=>x.EntryReady).ThenByDescending(x=>x.Confidence).Select(x=>x.Symbol).FirstOrDefault();var relevantMemories=await Db.RetrievePlannerMemoriesAsync(memorySymbol,ct);var brainContext=new AgentContext(brain.Name,_dayHigh>0&&(_dayHigh-account.Equity)/_dayHigh>=settings.Risk.DailyDrawdownLimit,activeSymbols.Length==1?activeSymbols[0]:null,outcomes,assessments,holdCount,relevantMemories);var useRemotePlanner=runtimeMode.AllowRemoteBrain&&!brain.IsLocal&&ShouldUseRemotePlanner(assessments,positions,holdCount);var plannerBrain=useRemotePlanner?brain:localBrain;var brainResult=await SkillAsync("BrainPlanner",$"markets={evidence.Markets.Count} holds={holdCount} remote={useRemotePlanner}",token=>plannerBrain.DecideAsync(evidence,brainContext,token),x=>$"{x.Decision.Action} {x.Decision.Instrument} confidence={x.Decision.Confidence:F2}",ct,useRemotePlanner);proposed=brainResult.Decision;brainRequest=brainResult.Request;brainResponse=brainResult.Response;}
                catch(BrainCallException ex) when(runtimeMode.AllowRemoteBrain&&!brain.IsLocal){brainRequest=ex.Request;brainResponse=ex.Response;await Db.RecordErrorAsync("Brain",ex,ct);runtimeState.BrainFallbackReason=string.IsNullOrWhiteSpace(runtimeState.BrainFallbackReason)?ex.Message:$"{runtimeState.BrainFallbackReason} | {ex.Message}";brain=localBrain;runtimeState.BrainEffectiveMode=AiRuntimeMode.LocalOnly;runtimeState.BrainRemoteAllowed=false;runtimeState.BrainName=brain.Name;runtimeState.ActiveBrainProvider=brain.Name;runtimeState.ActiveBrainModel="local-deterministic";runtimeState.BrainConnected=true;var activeSymbols=positions.Select(x=>x.Symbol).Distinct().ToArray();var outcomes=await Db.RecentOutcomeMemoriesAsync(ct);var holdCount=await Db.ConsecutiveHoldCountAsync(ct);var memorySymbol=activeSymbols.Length==1?activeSymbols[0]:assessments.Where(x=>x.Fresh).OrderByDescending(x=>x.EntryReady).ThenByDescending(x=>x.Confidence).Select(x=>x.Symbol).FirstOrDefault();var relevantMemories=await Db.RetrievePlannerMemoriesAsync(memorySymbol,ct);var fallbackResult=await SkillAsync("BrainPlanner",$"fallback markets={evidence.Markets.Count} holds={holdCount}",token=>brain.DecideAsync(evidence,new(brain.Name,_dayHigh>0&&(_dayHigh-account.Equity)/_dayHigh>=settings.Risk.DailyDrawdownLimit,activeSymbols.Length==1?activeSymbols[0]:null,outcomes,assessments,holdCount,relevantMemories),token),x=>$"{x.Decision.Action} {x.Decision.Instrument} confidence={x.Decision.Confidence:F2}",ct,false);proposed=fallbackResult.Decision;brainRequest=fallbackResult.Request;brainResponse=fallbackResult.Response;}
                catch(BrainCallException ex){brainRequest=ex.Request;brainResponse=ex.Response;await Db.RecordErrorAsync("Brain",ex,ct);proposed=new(){Action=DecisionAction.Hold,Reason=L("Agent.BrainFailure",ex.Message)};}
                catch(Exception ex) when(runtimeMode.AllowRemoteBrain&&!brain.IsLocal){await Db.RecordErrorAsync("Brain",ex,ct);runtimeState.BrainFallbackReason=string.IsNullOrWhiteSpace(runtimeState.BrainFallbackReason)?ex.Message:$"{runtimeState.BrainFallbackReason} | {ex.Message}";brain=localBrain;runtimeState.BrainEffectiveMode=AiRuntimeMode.LocalOnly;runtimeState.BrainRemoteAllowed=false;runtimeState.BrainName=brain.Name;runtimeState.ActiveBrainProvider=brain.Name;runtimeState.ActiveBrainModel="local-deterministic";runtimeState.BrainConnected=true;var activeSymbols=positions.Select(x=>x.Symbol).Distinct().ToArray();var outcomes=await Db.RecentOutcomeMemoriesAsync(ct);var holdCount=await Db.ConsecutiveHoldCountAsync(ct);var memorySymbol=activeSymbols.Length==1?activeSymbols[0]:assessments.Where(x=>x.Fresh).OrderByDescending(x=>x.EntryReady).ThenByDescending(x=>x.Confidence).Select(x=>x.Symbol).FirstOrDefault();var relevantMemories=await Db.RetrievePlannerMemoriesAsync(memorySymbol,ct);var fallbackResult=await SkillAsync("BrainPlanner",$"fallback markets={evidence.Markets.Count} holds={holdCount}",token=>brain.DecideAsync(evidence,new(brain.Name,_dayHigh>0&&(_dayHigh-account.Equity)/_dayHigh>=settings.Risk.DailyDrawdownLimit,activeSymbols.Length==1?activeSymbols[0]:null,outcomes,assessments,holdCount,relevantMemories),token),x=>$"{x.Decision.Action} {x.Decision.Instrument} confidence={x.Decision.Confidence:F2}",ct,false);proposed=fallbackResult.Decision;brainRequest=fallbackResult.Request;brainResponse=fallbackResult.Response;}
                catch(Exception ex){await Db.RecordErrorAsync("Brain",ex,ct);proposed=new(){Action=DecisionAction.Hold,Reason=L("Agent.BrainFailureShort")};}

                var proposedAssessment=assessments.FirstOrDefault(x=>x.Symbol.Equals(proposed.Instrument,StringComparison.OrdinalIgnoreCase));evidence.Markets.TryGetValue(proposed.Instrument,out var proposedMarket);proposed=await SkillAsync("DeterministicPlan",$"{proposed.Action} {proposed.Instrument}",_=>Task.FromResult(deterministic.Complete(proposed,proposedMarket,proposedAssessment,settings.Risk)),x=>$"entry={x.EntryPrice} stop={x.StopLossPrice} take={x.TakeProfitPrice} RR={x.RiskRewardRatio:F2}",ct);
                Stage("Stage.Review","CRITIC",65);await runtime.TransitionAsync(cycle,WorkflowNode.Critic,new{proposed.Action,proposed.Instrument,proposed.Confidence},ct);var review=await SkillAsync("DecisionReviewer",$"{proposed.Action} {proposed.Instrument}",_=>Task.FromResult(governance.Review(proposed,assessments,evidence,settings.Decision)),x=>$"accepted={x.Accepted} blocks={x.BlockingReasons.Count}",ct);var decision=review.Decision;var selected=assessments.FirstOrDefault(x=>x.Symbol==decision.Instrument)??assessments.FirstOrDefault();var selectedResearch=research.GetValueOrDefault(decision.Instrument);UpdateDecisionUi(decision,review,selected,selectedResearch);

                Stage("Stage.Risk","RISK",78);await runtime.TransitionAsync(cycle,WorkflowNode.Risk,new{decision.Action,decision.Instrument,ReviewAccepted=review.Accepted},ct);string result;IReadOnlyList<ExecutionIntent> intents=Array.Empty<ExecutionIntent>();TradingRule? tradingRule=null;
                if(!settings.Symbols.Contains(decision.Instrument,StringComparer.OrdinalIgnoreCase)){result=L("Agent.InvalidInstrument");}
                else{tradingRule=await exchange.GetRulesAsync(decision.Instrument,ct);var lockedSide=await Db.GetLockedSideAsync(decision.Instrument,ct);var planned=await SkillAsync("RiskAndPositionPlanner",$"{decision.Action} {decision.Instrument}",_=>Task.FromResult(planner.Plan(decision,evidence,tradingRule,settings.Risk,_dayHigh,safeToIncreaseRisk,safetyMessage,lockedSide)),x=>$"intents={x.Intents.Count} result={x.Result}",ct);intents=planned.Intents;result=planned.Result;}
                var portfolioRisk=await SkillAsync("PortfolioRisk",$"positions={evidence.Positions.Count} intents={intents.Count}",_=>Task.FromResult(portfolioRiskSkill.Evaluate(evidence,histories,intents,settings.Risk)),x=>x.Summary,ct);await Db.SavePortfolioRiskAsync(cycle,portfolioRisk,ct);UpdatePortfolioRiskUi(portfolioRisk,realtime,selectedResearch);
                var riskHistory=await Db.GetRiskHistoryAsync(ct);var riskReview=await SkillAsync("IndependentRiskManager",$"{decision.Action} intents={intents.Count}",_=>Task.FromResult(independentRisk.Review(decision,evidence,selected,intents,settings.Risk,riskHistory,selectedResearch,portfolioRisk)),x=>$"approved={x.Approved} level={x.RiskLevel} blocks={x.BlockingReasons.Count}",ct);
                if(DeterministicPlanSkill.IsRiskIncreasing(decision.Action)&&intents.Count==0)riskReview=new(){Approved=false,RiskLevel="BLOCKED",BlockingReasons=[result],Summary=result};
                var state=ServiceLocator.SystemState;state.ReviewerStatus=review.Accepted?"APPROVED":"REJECTED";state.RiskApprovalStatus=riskReview.Approved?"APPROVED":"BLOCKED";state.PlannedQuantity=riskReview.PlannedQuantity;state.CircuitBreakerActive=!riskReview.Approved&&DeterministicPlanSkill.IsRiskIncreasing(decision.Action);state.RiskSummary=riskReview.Summary;state.DecisionAuditSummary=$"Reviewer: {review.Verdict} · Risk: {riskReview.Summary}";
                if(!riskReview.Approved){intents=Array.Empty<ExecutionIntent>();result=riskReview.Summary;state.ExecutionApprovalStatus="BLOCKED";}
                else if(intents.Count==0)state.ExecutionApprovalStatus="NO_ORDER";
                else state.ExecutionApprovalStatus="READY";

                var modelOffCycle=await RunModelOffProductionShadowAsync(Db,cycle,DateTimeOffset.UtcNow,evidence,research,assessments,review,riskReview,
                    positionReconciliation,protectionReconciliation,externalPositionIsolation,ct);
                if(settings.Notification.Enabled&&settings.Notification.EventKinds.Contains(WpeAgent.Notifications.NotificationEventKind.MarketBrief.ToString(),StringComparer.OrdinalIgnoreCase)&&modelOffCycle is not null&&modelOffCycle.Outputs.TryGetValue(WpeAgent.ModelOff.ModelOffAgentV1.Research,out var canonicalResearch)&&WpeAgent.ModelOff.ModelOffEligibilityV1.IsEligibleForDownstream(canonicalResearch))
                    try{await MarketTeacherBriefPublisherV1.PublishDailyAsync(Db,notifications.Observer,canonicalResearch,exchange.ProviderId,exchange.Environment.ToString(),ct,LocalizationService.Current.CurrentCode);}catch(Exception ex){await Db.RecordErrorAsync("MARKET_TEACHER_BRIEF",ex,CancellationToken.None);}
                if(modelOffCycle is not null)
                    try{var teacherLesson=await MarketTeacherRuntimeV2.GenerateDueLocalLessonAsync(Db,cycle,DateTimeOffset.UtcNow,ct,LocalizationService.Current.CurrentCode);if(teacherLesson is not null)await teacherLessonPublisher.PublishIfAllowedAsync(teacherLesson,exchange.ProviderId,exchange.Environment.ToString(),ct);}catch(Exception ex){await Db.RecordErrorAsync("MARKET_TEACHER_V2",ex,CancellationToken.None);}
                var gatedIntents=ApplyModelOffProductionRiskIncreaseGate(intents,modelOffCycle);
                if(gatedIntents.Count!=intents.Count)
                {
                    intents=gatedIntents;result="model-off.production-gate-blocked";
                    state.RiskApprovalStatus="BLOCKED";state.ExecutionApprovalStatus=intents.Count>0?"RISK_REDUCING_ONLY":"BLOCKED";
                    state.CircuitBreakerActive=true;state.RiskSummary=result;
                }

                if(intents.Count>0&&tradingRule is not null)
                {
                    var leverage=Math.Min(settings.Risk.Leverage,tradingRule.MaxLeverage);
                    if(settings.AuthorizationMode==TradingAuthorizationMode.Auto)
                    {
                        var riskIncreasingDecision=DeterministicPlanSkill.IsRiskIncreasing(decision.Action);
                        strategySelections.TryGetValue(decision.Instrument,out var selectedStrategy);
                        var activeStrategy=selectedStrategy?.Profile??(!riskIncreasingDecision?strategyProfiles.FirstOrDefault(value=>value.Lifecycle==StrategyLifecycle.Active&&value.Symbol.Equals(decision.Instrument,StringComparison.OrdinalIgnoreCase)):null);
                        var strategyContextValid=activeStrategy is not null&&(!riskIncreasingDecision||(selectedStrategy is not null&&AdaptiveStrategySelector.DirectionMatches(selectedStrategy,decision.Action)));
                        if(!strategyContextValid||proposedMarket is null||!settings.Risk.Isolated){result=riskIncreasingDecision?"automatic.strategy-signal-context-invalid":"automatic.artifact-context-invalid";state.ExecutionApprovalStatus="BLOCKED";}
                        else
                        {
                            var created=DateTimeOffset.UtcNow;var expires=created.AddMinutes(2);var durableIntents=intents.Select((value,index)=>new DurableExecutionIntentSnapshotV1(index,value.Symbol,value.Side.ToString(),value.Quantity,value.ReduceOnly,value.StopLoss,value.TakeProfit,value.ClientOrderId,"automatic.risk-approved",value.Action.ToString(),value.OrderType.ToString(),value.LimitPrice,value.ExpectedPrice)).ToArray();var marketCollected=new DateTimeOffset(DateTime.SpecifyKind(proposedMarket.CollectedAt,DateTimeKind.Utc));var artifact=new DurableExecutionArtifactV2(DurableExecutionArtifactV2.Version,cycle,durableIntents,leverage,true,exchange.ProviderId,"Testnet",activeStrategy.Id,activeStrategy.Version,marketCollected,"provider-market-v1",created,expires);var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);var saved=await Db.SaveAutomaticExecutionAsync(cycle,artifact,ct);if(!saved.Succeeded){result=saved.Code;state.ExecutionApprovalStatus="BLOCKED";}else{var receipt=new DeterministicRiskReceipt("risk-"+cycle,cycle,hashes.IntentHash,true,created,expires,null,hashes.ArtifactHash);var approved=await Db.RecordAutomaticRiskDecisionAsync(cycle,receipt,ct);result=approved.Succeeded?"automatic.risk-approved":approved.Code;state.ExecutionApprovalStatus=approved.Succeeded?"RISK_APPROVED":"BLOCKED";}
                        }
                    }
                    else if(settings.AuthorizationMode==TradingAuthorizationMode.Research){result="authorization.research-read-only";state.ExecutionApprovalStatus="RESEARCH_ONLY";}
                    else if(settings.AuthorizationMode==TradingAuthorizationMode.Signal){result="authorization.signal-no-mutation";state.ExecutionApprovalStatus="SIGNAL_ONLY";}
                    else{result=settings.AuthorizationMode==TradingAuthorizationMode.Review?"authorization.legacy-review-blocked":"authorization.mode-blocked";state.ExecutionApprovalStatus="BLOCKED";await Db.SetStateAsync("authorization.auto-main-plan",result,ct);}
                }
                await Db.CompleteCycleAsync(cycle,decision,result,brainRequest,brainResponse,ct);await Db.RecordDecisionAuditAsync(cycle,assessments,review,ct);await Db.RecordMaturityAuditAsync(cycle,decision,review,riskReview,selectedResearch,result,ct);await runtime.CompleteCycleAsync(cycle,new{decision.Action,decision.Instrument,result,riskReview.Approved},ct);workflowStarted=false;
                Set(_paused?AgentStatus.Paused:safeToIncreaseRisk?AgentStatus.Running:AgentStatus.Degraded,L("Agent.CycleResult",decision.Action,result,evidence.Completeness));Stage(_paused?"Stage.Paused":"Stage.Reflection",_paused?"SYSTEM":"REFLECTION",_paused?5:100);roles.Publish("research","waiting","Latest research cycle completed; waiting for fresh evidence.");roles.Publish("risk","monitoring",riskReview.Approved?"Latest risk evaluation allowed the intent.":"Latest risk evaluation withheld execution authority.");roles.Publish("audit","monitoring","Latest cycle facts and decisions were appended to audit storage.");
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){var state=ServiceLocator.SystemState;state.ExchangeConnected=false;state.ApiTradePermission=false;roles.Publish("research","degraded","Research cycle failed; retry remains scheduled.");if(workflowStarted)await runtime.FailCycleAsync(cycle,ex,CancellationToken.None);await Db.FailCycleAsync(cycle,state.SkillStage,ex,ct);await Db.RecordErrorAsync(state.SkillStage,ex,ct);await NotifyAgentDegradedAsync(cycle,state.SkillStage,ex);state.LastError=ex.Message;Set(AgentStatus.Degraded,L("Agent.CycleDegraded",ex.Message));}
            roles.Publish("market",realtime.Healthy?"monitoring":"degraded",realtime.Healthy?"Realtime market and account streams are healthy.":"Realtime stream is reconnecting or incomplete.");ServiceLocator.SystemState.NextCycleAtUtc=DateTime.UtcNow.AddMinutes(15);ServiceLocator.SystemState.RealtimeStatus=realtime.Status;StateChanged?.Invoke();_ = await realtime.WaitForTriggerAsync(TimeSpan.FromMinutes(15),ct);
        }
        }
        finally
        {
            if(automaticWorkerTask is not null)try{await automaticWorkerTask;}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
            if(tradingObservationTask is not null)try{await tradingObservationTask;}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
            try{await strategySchedulerTask;}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
            try{await teacherCryptoSchedulerTask;}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
            try{await macroSchedulerTask;}catch(OperationCanceledException)when(ct.IsCancellationRequested){}finally{macroHttp.Dispose();}
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

    internal static async Task<int> ExecutePositionManagementRecoveryAsync(
        ProductionRecoveryService recovery,string correlationId,IReadOnlyList<ExecutionIntent> intents,
        IReadOnlyList<ManagedPosition> positions,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recovery);ArgumentNullException.ThrowIfNull(intents);ArgumentNullException.ThrowIfNull(positions);
        await ExecutionGate.WaitAsync(ct);try
        {
            var completed=0;
            foreach(var intent in intents)
            {
                var matches=positions.Where(x=>string.Equals(x.Symbol,intent.Symbol,StringComparison.Ordinal)).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("recovery.position-context-invalid");
                var position=matches[0];
                var result=await recovery.ExecuteAsync(correlationId,intent,Math.Max(1,(int)position.Leverage),position.Isolated,ct);
                if(!result.Executed)throw new InvalidOperationException(result.Code);
                completed++;
            }
            return completed;
        }
        finally{ExecutionGate.Release();}
    }

    internal static (bool SafeToIncreaseRisk,string SafetyMessage) ApplyPositionMutationInvalidation(bool safeToIncreaseRisk,string safetyMessage,bool positionChanged)=>
        positionChanged?(false,"position.reconciliation-invalidated-by-recovery"):(safeToIncreaseRisk,safetyMessage);

    internal static async Task<ModelOffProductionCycleResultV1?> RunModelOffProductionShadowAsync(
        AgentSqliteStore auditStore,string cycleId,DateTimeOffset evaluationTimeUtc,EvidencePack evidence,
        IReadOnlyDictionary<string,ResearchValidationResult> research,IReadOnlyList<MarketDecisionAssessment> assessments,
        DecisionReview decisionReview,IndependentRiskReview riskReview,PositionReconciliationReportV1 positionReconciliation,
        ProtectionReconciliationReportV1 protectionReconciliation,ExternalPositionIsolationReportV1 externalPositionIsolation,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auditStore);
        try
        {
            var macroObservations=await auditStore.GetLatestMacroObservationsAsync(8,ct);
            var inputs=ModelOffLiveCycleInputComposerV1.Compose(new(
                cycleId,evaluationTimeUtc,evidence,research,assessments,decisionReview,riskReview,macroObservations,
                positionReconciliation,protectionReconciliation,externalPositionIsolation));
            return await new ModelOffProductionCycleOrchestratorV1(auditStore).RunAsync(
                new(cycleId,evaluationTimeUtc,inputs),ct);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)
        {
            try{await auditStore.RecordErrorAsync("ModelOffProductionShadow",ex,CancellationToken.None);}catch{/* Shadow audit must not alter execution. */}
            return null;
        }
    }

    internal static bool ModelOffProductionGateAllowsRiskIncrease(ModelOffProductionCycleResultV1? result) =>
        result is {EligibleForRiskIncrease:true,Code:"model-off.production-cycle-ready"}
        &&result.Outputs.Count==7&&result.Documents.Count==7&&result.AuditCoverage.Count==7&&result.Handoffs.Count==6
        &&Enum.GetValues<ModelOffAgentV1>().All(role=>CanonicalRoleMatches(result,role))
        &&result.Handoffs.All(CanonicalDocumentHashMatches);

    internal static IReadOnlyList<ExecutionIntent> ApplyModelOffProductionRiskIncreaseGate(
        IReadOnlyList<ExecutionIntent> intents,ModelOffProductionCycleResultV1? result)
    {
        ArgumentNullException.ThrowIfNull(intents);
        return intents.Any(x=>!x.ReduceOnly)&&!ModelOffProductionGateAllowsRiskIncrease(result)
            ?intents.Where(x=>x.ReduceOnly).ToArray():intents;
    }

    private static bool CanonicalRoleMatches(ModelOffProductionCycleResultV1 result,ModelOffAgentV1 role)
    {
        if(!result.Outputs.TryGetValue(role,out var output)||!result.Documents.TryGetValue(role,out var document)
           ||!result.AuditCoverage.TryGetValue(role,out var coverageHash)
           ||!string.Equals(coverageHash,document.Sha256,StringComparison.Ordinal))return false;
        try
        {
            var canonical=ModelOffCanonicalSerializerV1.Serialize(output);
            return ModelOffEligibilityV1.IsEligibleForDownstream(output)
                &&string.Equals(canonical.Sha256,document.Sha256,StringComparison.Ordinal)
                &&CryptographicOperations.FixedTimeEquals(canonical.Utf8Bytes,document.Utf8Bytes);
        }
        catch(InvalidOperationException){return false;}
    }

    private static bool CanonicalDocumentHashMatches(ModelOffCanonicalDocumentV1 document)
    {
        if(document.Utf8Bytes is not {Length:>0}||string.IsNullOrWhiteSpace(document.Sha256))return false;
        var hash=Convert.ToHexString(SHA256.HashData(document.Utf8Bytes)).ToLowerInvariant();
        return string.Equals(hash,document.Sha256,StringComparison.Ordinal);
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

    private static void UpdateEvidence(EvidencePack evidence,IReadOnlyList<MarketDecisionAssessment> assessments,IReadOnlyDictionary<string,ResearchValidationResult> research)
    {
        var state=ServiceLocator.SystemState;state.EvidenceCompleteness=evidence.Completeness;var markets=evidence.Markets.Values.OrderBy(x=>x.Symbol).ToArray();state.MarketSummary=markets.Length==0?L("Agent.NoMarketEvidence"):string.Join("\n",markets.Select(x=>$"{x.Symbol} {x.Price:F2} · Q {x.Quality.QualityScore} · spread {x.Quality.SpreadBps:F2}bp · ATR {x.Quality.AtrPercent:P2} · OI {x.Derivatives.OpenInterest:F0}"));state.NewsFullTextDocuments=evidence.News.Count(x=>x.BodySummary.Length>=160);state.NewsCorroboratingSources=evidence.News.Select(x=>x.CorroboratingSources).DefaultIfEmpty().Max();state.NewsSummary=evidence.News.Count==0?L("Agent.NoNews"):string.Join("\n",evidence.News.Take(6).Select(x=>$"[{x.Source}] {x.Title} · {x.EventType} · {x.Confidence:P0} · {x.CorroboratingSources} sources"));
        var btc=markets.FirstOrDefault(x=>x.Symbol=="BTCUSDT");var eth=markets.FirstOrDefault(x=>x.Symbol=="ETHUSDT");if(btc is not null){state.BtcPrice=btc.Price;state.BtcTrend=btc.Trend15m;state.BtcRsi=btc.Rsi;}if(eth is not null){state.EthPrice=eth.Price;state.EthTrend=eth.Trend15m;state.EthRsi=eth.Rsi;}
        var primary=assessments.FirstOrDefault();var market=primary is null?markets.FirstOrDefault():markets.FirstOrDefault(x=>x.Symbol==primary.Symbol);if(market is not null){state.Symbol=market.Symbol;state.Timeframe="15m / 1h / 4h";state.CurrentPrice=market.Price;state.Support=market.Support;state.Resistance=market.Resistance;state.DataQualityScore=market.Quality.QualityScore;state.VolatilityPercent=market.Quality.AtrPercent*100;state.LiquidityScore=market.Quality.LiquidityScore*100;state.ResearchScore=(research.GetValueOrDefault(market.Symbol)?.QualityScore??0)*100;}
        state.DecisionDiagnostics=assessments.Count==0?L("Agent.NoSignals"):string.Join("\n",assessments.Select(x=>x.Summary));state.MissingConditions=string.Join("; ",evidence.MissingSources.Concat(assessments.SelectMany(x=>x.MissingConditions)).Distinct());
    }

    private static void UpdateDecisionUi(DecisionPlan decision,DecisionReview review,MarketDecisionAssessment? selected,ResearchValidationResult? research)
    {
        var state=ServiceLocator.SystemState;state.LastDecision=decision.Action.ToString();state.LastReason=review.Explanation;state.DecisionDiagnostics=review.Explanation;state.BrainConfidence=decision.Confidence*100;state.PlannedEntry=decision.EntryPrice;state.PlannedStop=decision.StopLossPrice;state.PlannedTakeProfit=decision.TakeProfitPrice;state.RiskRewardRatio=decision.RiskRewardRatio;state.ReviewerStatus=review.Accepted?"APPROVED":"REJECTED";state.ResearchScore=(research?.QualityScore??0)*100;
        if(selected is null)return;state.DecisionScore=selected.NetScore*100;state.ConflictRate=selected.ConflictRatio*100;state.RiskLoad=Math.Clamp(selected.ConflictRatio*.60+(1-selected.Confidence)*.40,0,1)*100;state.MarketRegime=selected.Regime.ToString().ToUpperInvariant();state.SignalContributions=selected.Signals.ToDictionary(x=>x.Name,x=>x.WeightedScore);state.MissingConditions=string.Join("; ",review.BlockingReasons.Concat(selected.MissingConditions).Distinct());
    }

private static void UpdatePortfolioRiskUi(PortfolioRiskAssessment risk,IRealtimeMarketFeed realtime,ResearchValidationResult? research){var state=ServiceLocator.SystemState;state.RealtimeStatus=realtime.Status;state.HistoricalCoverageDays=research?.CoverageDays??state.HistoricalCoverageDays;state.PortfolioVaR99=risk.VaR99*100;state.PortfolioCVaR99=risk.CVaR99*100;state.PortfolioConcentration=risk.LargestPositionShare*100;state.PortfolioCorrelation=risk.MaximumPairCorrelation;state.PortfolioRiskSummary=risk.Summary;}

    private static void ApplyRuntimeHealth(RuntimeHealth health){var state=ServiceLocator.SystemState;state.RuntimeRunId=health.RunId;state.RuntimeHeartbeatAtUtc=health.HeartbeatAtUtc;state.RuntimeRecoveryStatus=health.RecoveryStatus;state.RuntimeEventSequence=health.EventSequence;state.LastUpdated=health.HeartbeatAtUtc;AgentRoleRuntimeRegistry.Shared.Publish("audit",health.RecoveryStatus=="LEASE_LOST"?"degraded":"monitoring",health.RecoveryStatus=="LEASE_LOST"?"Runtime audit lease was lost; persisted health is no longer authoritative.":"Runtime lease and append-only audit heartbeat are active.",health.HeartbeatAtUtc);StateChanged?.Invoke();}

    private static void Stage(string key,string node,int progress){var state=ServiceLocator.SystemState;state.SkillStageKey=key;state.SkillStage=L(key);state.WorkflowNode=node;state.ThinkingProgress=progress;if(node=="REFLECTION")state.ReflectionStatus="EXPERIENCE COMMITTED";state.LastUpdated=DateTime.UtcNow;StateChanged?.Invoke();}
    private static bool ShouldUseRemotePlanner(IReadOnlyList<MarketDecisionAssessment> assessments,IReadOnlyList<ManagedPosition> positions,int consecutiveHolds)
    {
        if(positions.Count>0)return true;
        if(consecutiveHolds>=3)return true;
        return assessments.Any(x=>x.EntryReady);
    }
    private static IAssistantProvider? CreateConfiguredBrain(RuntimeModeResolution runtimeMode,BrainSlot? selectedBrain)
    {
        if(!runtimeMode.AllowRemoteBrain||selectedBrain is null)return null;
        var secret=selectedBrain.IsLocal||selectedBrain.Provider.Equals("WPE Local Brain",StringComparison.OrdinalIgnoreCase)?null:selectedBrain.Provider.Contains("Ollama",StringComparison.OrdinalIgnoreCase)&&string.IsNullOrWhiteSpace(selectedBrain.EncryptedKey)?"ollama-local":SecretVaultService.Decrypt(selectedBrain.EncryptedKey);
        return AssistantProviderFactory.Create(selectedBrain,secret,runtimeMode.EffectiveMode);
    }
    private static void ApplyRuntimeModeState(SystemState state,RuntimeModeResolution runtimeMode,IAssistantProvider brain)
    {
        state.BrainMode=runtimeMode.RequestedMode;state.BrainEffectiveMode=runtimeMode.EffectiveMode;state.BrainRemoteAllowed=runtimeMode.AllowRemoteBrain;state.BrainFallbackReason=runtimeMode.FallbackReason;state.BrainName=brain.Name;state.ActiveBrainProvider=runtimeMode.ProviderName;state.ActiveBrainModel=string.IsNullOrWhiteSpace(runtimeMode.ModelName)&&brain.IsLocal?"local-deterministic":runtimeMode.ModelName;
    }
    private static async Task<T> SkillAsync<T>(string name,string input,Func<CancellationToken,Task<T>> call,Func<T,string> summarize,CancellationToken ct,bool? remoteLlmUsed=null)
    {
        var descriptor=SkillGuard.Authorize(name,ServiceLocator.SystemState.Mode);var mode=ServiceLocator.SystemState.BrainEffectiveMode.ToString();var remote=remoteLlmUsed??(name=="BrainPlanner"&&ServiceLocator.SystemState.BrainRemoteAllowed);if(remote)LlmRequestGovernor.ClearCurrentCallUsage();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(descriptor.TimeoutSeconds));var stopwatch=Stopwatch.StartNew();try{var value=await call(timeout.Token);var llmUsage=remote?LlmRequestGovernor.ConsumeCurrentCallUsage():null;await Db.RecordSkillCallAsync(name,"SUCCESS",stopwatch.ElapsedMilliseconds,input,Trim(summarize(value)),null,ct,mode,remote,llmUsage?.LoggedTokens,llmUsage?.LoggedCostUsd,llmUsage?.ContextCharacters,llmUsage?.InputTokens,llmUsage?.OutputTokens,llmUsage?.CacheHit,llmUsage?.Outcome,llmUsage?.TokenSource);return value;}catch(Exception ex){var llmUsage=remote?LlmRequestGovernor.ConsumeCurrentCallUsage():null;await Db.RecordSkillCallAsync(name,"FAILED",stopwatch.ElapsedMilliseconds,input,string.Empty,Trim(ex.Message),CancellationToken.None,mode,remote,llmUsage?.LoggedTokens,llmUsage?.LoggedCostUsd,llmUsage?.ContextCharacters,llmUsage?.InputTokens,llmUsage?.OutputTokens,llmUsage?.CacheHit,llmUsage?.Outcome,llmUsage?.TokenSource);throw;}
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

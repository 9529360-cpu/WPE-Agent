using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Localization;
using System.Diagnostics;

namespace 币安量化机器人.Services;

public static class AutoTradingAgent
{
    private static CancellationTokenSource? _cts;private static Task? _run;private static readonly AgentSettingsStore SettingsStore=new();private static readonly AgentSqliteStore Db=new();private static readonly SemaphoreSlim ExecutionGate=new(1,1);private static IExchangeAdapter? _activeExchange;private static decimal _dayHigh;private static bool _paused;
    public static bool IsRunning=>_run is{IsCompleted:false};public static bool CanEmergencyClose=>_activeExchange is not null;public static event Action? StateChanged;

    public static void StartDefault(){if(IsRunning)return;_paused=false;_cts=new();_run=Task.Run(async()=>{try{await Run(_cts.Token);}catch(OperationCanceledException)when(_cts?.IsCancellationRequested==true){}catch(Exception ex){await Db.RecordErrorAsync("AGENT_LOOP",ex,CancellationToken.None);ServiceLocator.SystemState.LastError=ex.Message;Set(AgentStatus.Degraded,L("Agent.CycleDegraded",ex.Message));}finally{_activeExchange=null;}});}
    public static void Pause(){_paused=true;Set(AgentStatus.Paused,L("Agent.Paused"));}
    public static void Resume(){_paused=false;Set(AgentStatus.Running,L("Agent.Resumed"));}
    public static async Task StopAsync(){if(_cts is null)return;_cts.Cancel();try{if(_run is not null)await _run;}catch(OperationCanceledException){} _cts.Dispose();_cts=null;_run=null;Set(AgentStatus.Stopped,L("Agent.Stopped"));}

    public static async Task<string> EmergencyCloseAllAsync()
    {
        _paused=true;Set(AgentStatus.Paused,L("Agent.EmergencyRequested"));await ExecutionGate.WaitAsync();try
        {
            var exchange=_activeExchange??throw new InvalidOperationException(L("Agent.EmergencyUnavailable"));using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));var ct=timeout.Token;var settings=SettingsStore.Load();var executor=new ReliableOrderExecutor(exchange,Db,settings.Risk);var positions=await exchange.GetPositionsAsync(ct);
            if(positions.Count==0){Set(AgentStatus.Paused,L("Agent.NoPositions"));return L("Agent.NoPositions");}
            var cycle="EMERGENCY-"+Guid.NewGuid().ToString("N");var closed=new List<string>();
            foreach(var position in positions)
            {
                var rule=await exchange.GetRulesAsync(position.Symbol,ct);var quantity=rule.RoundQuantity(position.Quantity);var raw=$"WPE-EMG-{DateTime.UtcNow:yyMMddHHmmss}-{Guid.NewGuid():N}";var id=raw[..Math.Min(36,raw.Length)];var action=position.Side==PositionSide.Long?DecisionAction.CloseLong:DecisionAction.CloseShort;
                var intent=new ExecutionIntent(position.Symbol,position.Side,quantity,true,0,0,id,L("Agent.EmergencyIntent"),action,ExpectedPrice:position.MarkPrice);await executor.ExecuteAsync(cycle,intent,Math.Max(1,(int)position.Leverage),true,ct);closed.Add($"{position.Symbol} {position.Side} {quantity}");
            }
            var account=await exchange.GetAccountAsync(ct);var remaining=await exchange.GetPositionsAsync(ct);var orders=await exchange.GetOpenOrdersAsync(null,ct);UpdateAccount(account,remaining,orders);var result=L("Agent.EmergencyCompleted",string.Join("; ",closed));Set(AgentStatus.Paused,result);return result;
        }
        catch(Exception ex){await Db.RecordErrorAsync("EMERGENCY_CLOSE",ex,CancellationToken.None);ServiceLocator.SystemState.LastError=ex.Message;Set(AgentStatus.Degraded,L("Agent.EmergencyIncomplete",ex.Message));throw;}
        finally{ExecutionGate.Release();}
    }

    private static async Task Run(CancellationToken ct)
    {
        var settings=SettingsStore.Load();SettingsStore.ImportDesktopTestnetIfEmpty(settings);SettingsStore.ImportDesktopDeepSeekIfEmpty(settings);
        if(settings.Environment==ExchangeEnvironment.Mainnet)throw new InvalidOperationException(L("Agent.MainnetConfirmationRequired"));
        var (key,secret)=SettingsStore.GetCredentials(settings);if(string.IsNullOrWhiteSpace(key)||string.IsNullOrWhiteSpace(secret))throw new InvalidOperationException(L("Agent.ApiMissing"));
        await using IExchangeAdapter exchange=new BinanceFuturesAdapter(ExchangeEnvironment.Testnet,key,secret);_activeExchange=exchange;
        var collector=new EvidenceCollector(exchange,settings.Symbols);var executor=new ReliableOrderExecutor(exchange,Db,settings.Risk);var planner=new RiskAndPositionPlanner();var aggregator=new SignalAggregationSkill();var governance=new DecisionGovernanceSkill();var deterministic=new DeterministicPlanSkill();var independentRisk=new IndependentRiskManagerSkill();var researchSkill=new StrategyResearchSkill();var positionManager=new PositionManagementSkill();
        if(!settings.Brains.TryGetValue(settings.ActiveBrain,out var brainSlot))throw new InvalidOperationException(L("Agent.BrainMissing"));var brainKey=SecretVaultService.Decrypt(brainSlot.EncryptedKey);IBrainProvider brain=new HttpBrainProvider(brainSlot,brainKey);
        ServiceLocator.SystemState.Mode=TradingMode.Testnet;ServiceLocator.SystemState.BrainName=brain.Name;Set(AgentStatus.Running,L("Agent.Started",ExchangeEnvironment.Testnet,brain.Name));var startupHealth=await brain.HealthCheckAsync(ct);if(!startupHealth.Healthy)Set(AgentStatus.Degraded,L("Agent.BrainFailure",startupHealth.Message));
        while(!ct.IsCancellationRequested)
        {
            if(_paused){await Task.Delay(1000,ct);continue;}var cycle=Guid.NewGuid().ToString("N");
            try
            {
                Stage("Stage.AccountSync","OBSERVATION",18);var account=await exchange.GetAccountAsync(ct);var positions=await exchange.GetPositionsAsync(ct);var orders=await exchange.GetOpenOrdersAsync(null,ct);await Db.SaveSnapshotAsync(account,positions,orders,ct);_dayHigh=await Db.GetOrUpdateDailyHighAsync(DateOnly.FromDateTime(DateTime.UtcNow),account.Equity,ct);UpdateAccount(account,positions,orders);
                Stage("Stage.Recovery","OBSERVATION",25);var pendingRecovery=await SkillAsync("ProtectionRecovery","pending intents",()=>executor.RecoverPendingAsync(ct),x=>$"safe={x.SafeToIncreaseRisk} messages={x.Messages.Count}",ct);orders=await exchange.GetOpenOrdersAsync(null,ct);var protection=await SkillAsync("ProtectionAudit",$"positions={positions.Count} orders={orders.Count}",()=>executor.AuditAndRepairProtectionAsync(positions,orders,ct),x=>$"safe={x.SafeToIncreaseRisk} messages={x.Messages.Count}",ct);var safeToIncreaseRisk=pendingRecovery.SafeToIncreaseRisk&&protection.SafeToIncreaseRisk;var safetyMessage=string.Join("；",pendingRecovery.Messages.Concat(protection.Messages));
                if(!safeToIncreaseRisk)ServiceLocator.SystemState.LastMessage=string.IsNullOrWhiteSpace(safetyMessage)?L("Agent.CycleDegraded",string.Empty):safetyMessage;

                Stage("Stage.Evidence","OBSERVATION",32);var evidence=await SkillAsync("EvidenceCollector",string.Join(',',settings.Symbols),()=>collector.CollectAsync(ct),x=>$"completeness={x.Completeness} markets={x.Markets.Count} missing={x.MissingSources.Count}",ct);await Db.SaveNewsAsync(evidence.News,ct);await Db.StartCycleAsync(cycle,evidence,brain.Name,ct);
                Stage("Stage.Research","OBSERVATION",36);var research=new Dictionary<string,ResearchValidationResult>(StringComparer.OrdinalIgnoreCase);foreach(var market in evidence.Markets.Values){var validation=await SkillAsync("StrategyResearch",$"{market.Symbol} candles={market.Candles.Count}",()=>Task.FromResult(researchSkill.Evaluate(market)),x=>$"approved={x.Approved} score={x.QualityScore:F2} trades={x.Trades}",ct);research[market.Symbol]=validation;await Db.SaveResearchAsync(validation,ct);}
                Stage("Stage.PositionManagement","RISK",39);var management=await SkillAsync("PositionManagement",$"positions={positions.Count}",()=>positionManager.EvaluateAsync(positions,evidence.Markets,Db,ct),x=>$"intents={x.Intents.Count} adjustments={x.ProtectionAdjustments.Count}",ct);var managementActivity=false;
                if(management.Intents.Count>0||management.ProtectionAdjustments.Count>0)
                {
                    await ExecutionGate.WaitAsync(ct);try
                    {
                        foreach(var rawIntent in management.Intents){var rule=await exchange.GetRulesAsync(rawIntent.Symbol,ct);var intent=rawIntent with{Quantity=rule.RoundQuantity(rawIntent.Quantity)};if(intent.Quantity>=rule.MinQuantity&&intent.Quantity*intent.ExpectedPrice>=rule.MinNotional){await SkillAsync("ReliableOrderExecutor",$"{intent.Symbol} {intent.Action} qty={intent.Quantity}",()=>executor.ExecuteAsync(cycle,intent,Math.Min(settings.Risk.Leverage,rule.MaxLeverage),settings.Risk.Isolated,ct),x=>x,ct);managementActivity=true;}}
                        foreach(var adjustment in management.ProtectionAdjustments){await SkillAsync("ProtectionRecovery",$"{adjustment.Symbol} {adjustment.Side}",()=>executor.ReplaceProtectionAsync(cycle,adjustment,ct),x=>x,ct);managementActivity=true;}
                        foreach(var note in management.Notes)await Db.SetStateAsync(note,"completed",ct);
                    }finally{ExecutionGate.Release();}
                    if(managementActivity){safeToIncreaseRisk=false;safetyMessage=L("Position.ManagementCycle");account=await exchange.GetAccountAsync(ct);positions=await exchange.GetPositionsAsync(ct);orders=await exchange.GetOpenOrdersAsync(null,ct);UpdateAccount(account,positions,orders);}
                }

                Stage("Stage.Aggregation","OBSERVATION",43);var assessments=await SkillAsync("SignalAggregation",$"markets={evidence.Markets.Count}",()=>Task.FromResult(aggregator.Analyze(evidence,settings.Decision)),x=>$"assessments={x.Count} ready={x.Count(a=>a.EntryReady)}",ct);UpdateEvidence(evidence,assessments,research);
                Stage("Stage.Planner","PLANNER",52);DecisionPlan proposed;string? brainRequest=null,brainResponse=null;
                try{var activeSymbols=positions.Select(x=>x.Symbol).Distinct().ToArray();var outcomes=await Db.RecentOutcomesAsync(ct);var holdCount=await Db.ConsecutiveHoldCountAsync(ct);var brainResult=await SkillAsync("BrainPlanner",$"markets={evidence.Markets.Count} holds={holdCount}",()=>brain.DecideAsync(evidence,new(brain.Name,_dayHigh>0&&(_dayHigh-account.Equity)/_dayHigh>=settings.Risk.DailyDrawdownLimit,activeSymbols.Length==1?activeSymbols[0]:null,outcomes,assessments,holdCount),ct),x=>$"{x.Decision.Action} {x.Decision.Instrument} confidence={x.Decision.Confidence:F2}",ct);proposed=brainResult.Decision;brainRequest=brainResult.Request;brainResponse=brainResult.Response;}
                catch(BrainCallException ex){brainRequest=ex.Request;brainResponse=ex.Response;await Db.RecordErrorAsync("Brain",ex,ct);proposed=new(){Action=DecisionAction.Hold,Reason=L("Agent.BrainFailure",ex.Message)};}
                catch(Exception ex){await Db.RecordErrorAsync("Brain",ex,ct);proposed=new(){Action=DecisionAction.Hold,Reason=L("Agent.BrainFailureShort")};}

                var proposedAssessment=assessments.FirstOrDefault(x=>x.Symbol.Equals(proposed.Instrument,StringComparison.OrdinalIgnoreCase));evidence.Markets.TryGetValue(proposed.Instrument,out var proposedMarket);proposed=await SkillAsync("DeterministicPlan",$"{proposed.Action} {proposed.Instrument}",()=>Task.FromResult(deterministic.Complete(proposed,proposedMarket,proposedAssessment,settings.Risk)),x=>$"entry={x.EntryPrice} stop={x.StopLossPrice} take={x.TakeProfitPrice} RR={x.RiskRewardRatio:F2}",ct);
                Stage("Stage.Review","CRITIC",65);var review=await SkillAsync("DecisionReviewer",$"{proposed.Action} {proposed.Instrument}",()=>Task.FromResult(governance.Review(proposed,assessments,evidence,settings.Decision)),x=>$"accepted={x.Accepted} blocks={x.BlockingReasons.Count}",ct);var decision=review.Decision;var selected=assessments.FirstOrDefault(x=>x.Symbol==decision.Instrument)??assessments.FirstOrDefault();var selectedResearch=research.GetValueOrDefault(decision.Instrument);UpdateDecisionUi(decision,review,selected,selectedResearch);

                Stage("Stage.Risk","RISK",78);string result;IReadOnlyList<ExecutionIntent> intents=Array.Empty<ExecutionIntent>();TradingRule? tradingRule=null;
                if(!settings.Symbols.Contains(decision.Instrument,StringComparer.OrdinalIgnoreCase)){result=L("Agent.InvalidInstrument");}
                else{tradingRule=await exchange.GetRulesAsync(decision.Instrument,ct);var lockedSide=await Db.GetLockedSideAsync(decision.Instrument,ct);var planned=await SkillAsync("RiskAndPositionPlanner",$"{decision.Action} {decision.Instrument}",()=>Task.FromResult(planner.Plan(decision,evidence,tradingRule,settings.Risk,_dayHigh,safeToIncreaseRisk,safetyMessage,lockedSide)),x=>$"intents={x.Intents.Count} result={x.Result}",ct);intents=planned.Intents;result=planned.Result;}
                var history=await Db.GetRiskHistoryAsync(ct);var riskReview=await SkillAsync("IndependentRiskManager",$"{decision.Action} intents={intents.Count}",()=>Task.FromResult(independentRisk.Review(decision,evidence,selected,intents,settings.Risk,history,selectedResearch)),x=>$"approved={x.Approved} level={x.RiskLevel} blocks={x.BlockingReasons.Count}",ct);
                if(DeterministicPlanSkill.IsRiskIncreasing(decision.Action)&&intents.Count==0)riskReview=new(){Approved=false,RiskLevel="BLOCKED",BlockingReasons=[result],Summary=result};
                var state=ServiceLocator.SystemState;state.ReviewerStatus=review.Accepted?"APPROVED":"REJECTED";state.RiskApprovalStatus=riskReview.Approved?"APPROVED":"BLOCKED";state.PlannedQuantity=riskReview.PlannedQuantity;state.CircuitBreakerActive=!riskReview.Approved&&DeterministicPlanSkill.IsRiskIncreasing(decision.Action);state.RiskSummary=riskReview.Summary;state.DecisionAuditSummary=$"Reviewer: {review.Verdict} · Risk: {riskReview.Summary}";
                if(!riskReview.Approved){intents=Array.Empty<ExecutionIntent>();result=riskReview.Summary;state.ExecutionApprovalStatus="BLOCKED";}
                else if(intents.Count==0)state.ExecutionApprovalStatus="NO_ORDER";
                else state.ExecutionApprovalStatus="READY";

                if(intents.Count>0&&tradingRule is not null)
                {
                    if(_paused)result=L("Agent.PausedBeforeExecution");else{await ExecutionGate.WaitAsync(ct);try{if(_paused)result=L("Agent.PausedBeforeExecution");else{Stage("Stage.Execution","EXECUTION",91);result=await SkillAsync("ReliableOrderExecutor",$"intents={intents.Count}",()=>executor.ExecutePlanAsync(cycle,intents,Math.Min(settings.Risk.Leverage,tradingRule.MaxLeverage),settings.Risk.Isolated,ct),x=>x,ct);state.ExecutionApprovalStatus="EXECUTED";}}finally{ExecutionGate.Release();}}
                }
                await Db.CompleteCycleAsync(cycle,decision,result,brainRequest,brainResponse,ct);await Db.RecordDecisionAuditAsync(cycle,assessments,review,ct);await Db.RecordMaturityAuditAsync(cycle,decision,review,riskReview,selectedResearch,result,ct);
                Set(_paused?AgentStatus.Paused:safeToIncreaseRisk?AgentStatus.Running:AgentStatus.Degraded,L("Agent.CycleResult",decision.Action,result,evidence.Completeness));Stage(_paused?"Stage.Paused":"Stage.Reflection",_paused?"SYSTEM":"REFLECTION",_paused?5:100);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){await Db.FailCycleAsync(cycle,ServiceLocator.SystemState.SkillStage,ex,ct);await Db.RecordErrorAsync(ServiceLocator.SystemState.SkillStage,ex,ct);ServiceLocator.SystemState.LastError=ex.Message;Set(AgentStatus.Degraded,L("Agent.CycleDegraded",ex.Message));}
            ServiceLocator.SystemState.NextCycleAtUtc=DateTime.UtcNow.AddMinutes(15);StateChanged?.Invoke();await Task.Delay(TimeSpan.FromMinutes(15),ct);
        }
    }

    private static void UpdateAccount(AccountSnapshot account,IReadOnlyList<ManagedPosition> positions,IReadOnlyList<ExchangeOrder> orders)
    {var state=ServiceLocator.SystemState;state.WalletBalance=account.WalletBalance;state.AvailableBalance=account.AvailableBalance;state.PositionQuantity=positions.Sum(x=>x.Side==PositionSide.Long?x.Quantity:-x.Quantity);state.EntryPrice=positions.FirstOrDefault()?.EntryPrice??0;state.PositionsSummary=positions.Count==0?L("Agent.NoPositions"):string.Join("\n",positions.Select(x=>$"{x.Symbol} {x.Side} · {x.Quantity} · {x.EntryPrice:F2} · {x.UnrealizedPnl:F2}"));state.OrdersSummary=orders.Count==0?L("Agent.NoOrders"):string.Join("\n",orders.Take(8).Select(x=>$"{x.Symbol} {x.Type} · {x.Status} · {x.PositionSide}"));}

    private static void UpdateEvidence(EvidencePack evidence,IReadOnlyList<MarketDecisionAssessment> assessments,IReadOnlyDictionary<string,ResearchValidationResult> research)
    {
        var state=ServiceLocator.SystemState;state.EvidenceCompleteness=evidence.Completeness;var markets=evidence.Markets.Values.OrderBy(x=>x.Symbol).ToArray();state.MarketSummary=markets.Length==0?L("Agent.NoMarketEvidence"):string.Join("\n",markets.Select(x=>$"{x.Symbol} {x.Price:F2} · Q {x.Quality.QualityScore} · spread {x.Quality.SpreadBps:F2}bp · ATR {x.Quality.AtrPercent:P2} · OI {x.Derivatives.OpenInterest:F0}"));state.NewsSummary=evidence.News.Count==0?L("Agent.NoNews"):string.Join("\n",evidence.News.Take(6).Select(x=>$"[{x.Source}] {x.Title}"));
        var btc=markets.FirstOrDefault(x=>x.Symbol=="BTCUSDT");var eth=markets.FirstOrDefault(x=>x.Symbol=="ETHUSDT");if(btc is not null){state.BtcPrice=btc.Price;state.BtcTrend=btc.Trend15m;state.BtcRsi=btc.Rsi;}if(eth is not null){state.EthPrice=eth.Price;state.EthTrend=eth.Trend15m;state.EthRsi=eth.Rsi;}
        var primary=assessments.FirstOrDefault();var market=primary is null?markets.FirstOrDefault():markets.FirstOrDefault(x=>x.Symbol==primary.Symbol);if(market is not null){state.Symbol=market.Symbol;state.Timeframe="15m / 1h / 4h";state.CurrentPrice=market.Price;state.Support=market.Support;state.Resistance=market.Resistance;state.DataQualityScore=market.Quality.QualityScore;state.VolatilityPercent=market.Quality.AtrPercent*100;state.LiquidityScore=market.Quality.LiquidityScore*100;state.ResearchScore=(research.GetValueOrDefault(market.Symbol)?.QualityScore??0)*100;}
        state.DecisionDiagnostics=assessments.Count==0?L("Agent.NoSignals"):string.Join("\n",assessments.Select(x=>x.Summary));state.MissingConditions=string.Join("; ",evidence.MissingSources.Concat(assessments.SelectMany(x=>x.MissingConditions)).Distinct());
    }

    private static void UpdateDecisionUi(DecisionPlan decision,DecisionReview review,MarketDecisionAssessment? selected,ResearchValidationResult? research)
    {
        var state=ServiceLocator.SystemState;state.LastDecision=decision.Action.ToString();state.LastReason=review.Explanation;state.DecisionDiagnostics=review.Explanation;state.BrainConfidence=decision.Confidence*100;state.PlannedEntry=decision.EntryPrice;state.PlannedStop=decision.StopLossPrice;state.PlannedTakeProfit=decision.TakeProfitPrice;state.RiskRewardRatio=decision.RiskRewardRatio;state.ReviewerStatus=review.Accepted?"APPROVED":"REJECTED";state.ResearchScore=(research?.QualityScore??0)*100;
        if(selected is null)return;state.DecisionScore=selected.NetScore*100;state.ConflictRate=selected.ConflictRatio*100;state.RiskLoad=Math.Clamp(selected.ConflictRatio*.60+(1-selected.Confidence)*.40,0,1)*100;state.MarketRegime=selected.Regime.ToString().ToUpperInvariant();state.SignalContributions=selected.Signals.ToDictionary(x=>x.Name,x=>x.WeightedScore);state.MissingConditions=string.Join("; ",review.BlockingReasons.Concat(selected.MissingConditions).Distinct());
    }

    private static void Stage(string key,string node,int progress){var state=ServiceLocator.SystemState;state.SkillStageKey=key;state.SkillStage=L(key);state.WorkflowNode=node;state.ThinkingProgress=progress;if(node=="REFLECTION")state.ReflectionStatus="EXPERIENCE COMMITTED";state.LastUpdated=DateTime.UtcNow;StateChanged?.Invoke();}
    private static async Task<T> SkillAsync<T>(string name,string input,Func<Task<T>> call,Func<T,string> summarize,CancellationToken ct)
    {
        var stopwatch=Stopwatch.StartNew();try{var value=await call();await Db.RecordSkillCallAsync(name,"SUCCESS",stopwatch.ElapsedMilliseconds,input,Trim(summarize(value)),null,ct);return value;}catch(Exception ex){await Db.RecordSkillCallAsync(name,"FAILED",stopwatch.ElapsedMilliseconds,input,string.Empty,Trim(ex.Message),CancellationToken.None);throw;}
    }
    private static string Trim(string? value){value??=string.Empty;return value.Length<=500?value:value[..500];}
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
    private static void Set(AgentStatus status,string message){var state=ServiceLocator.SystemState;state.Status=status;state.LastMessage=message;state.LastUpdated=DateTime.UtcNow;StateChanged?.Invoke();}
}

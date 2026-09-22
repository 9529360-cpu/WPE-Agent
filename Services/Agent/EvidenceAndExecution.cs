using 币安量化机器人.Services.Localization;
using 币安量化机器人.Services.Exchange;
using 币安量化机器人.Core.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WpeAgent.RuntimeContracts;
using WpeAgent.Notifications;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services;

namespace 币安量化机器人.Services.Agent;

public enum DurableReviewReconciliationState { Succeeded,NotSubmitted,Unknown,Failed }
public sealed record DurableReviewReconciliationResult(DurableReviewReconciliationState State,string Code);
public interface IDurableReviewExecutionReconciler
{
    Task<DurableReviewReconciliationResult> ReconcileAsync(DurableReviewExecutionArtifactV1 artifact,CancellationToken ct);
}

public sealed class EvidenceCollector
{
    private readonly IExchangeAdapter _exchange;private readonly IReadOnlyList<string> _symbols;private readonly IRealtimeMarketFeed? _realtime;private readonly NewsResearchService _news;
    public EvidenceCollector(IExchangeAdapter exchange,IEnumerable<string>? symbols=null,IRealtimeMarketFeed? realtime=null,NewsResearchService? news=null){_exchange=exchange;_symbols=(symbols??["BTCUSDT","ETHUSDT"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();_realtime=realtime;_news=news??new();}
    public async Task<EvidencePack> CollectAsync(CancellationToken ct)
    {
        var missing=new List<string>();var markets=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase);
        var account=await _exchange.GetAccountAsync(ct);var positions=await _exchange.GetPositionsAsync(ct);
        foreach(var symbol in _symbols)try{var market=await _exchange.GetMarketAsync(symbol,ct);market=_realtime?.Enrich(market)??market;var provider=_exchange is IExchangeProvider p?p.ProviderId:"local";market=market with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,provider,_exchange.Environment.ToString())};markets[symbol]=market;}catch{missing.Add(symbol+":market_derivatives");}
        if(_realtime is not null&&!_realtime.Healthy)missing.Add("realtime_stream_unhealthy:"+_realtime.Status);
        var fundamentals=new Dictionary<string,CryptoInstrumentFundamentalV1>(StringComparer.OrdinalIgnoreCase);
        if(_exchange is ICryptoInstrumentFundamentalReader fundamentalReader)try{foreach(var fact in await fundamentalReader.GetInstrumentFundamentalsAsync(_symbols,ct))fundamentals[fact.Symbol]=fact;foreach(var symbol in _symbols.Where(x=>!fundamentals.ContainsKey(x)))missing.Add(symbol+":fundamental_unavailable");}catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{foreach(var symbol in _symbols)missing.Add(symbol+":fundamental_error");}else foreach(var symbol in _symbols)missing.Add(symbol+":fundamental_unsupported");
        var newsResult=await _news.CollectAsync(_symbols,ct);missing.AddRange(newsResult.MissingSources);var news=newsResult.Items;
        foreach(var market in markets.Values)foreach(var anomaly in market.Quality.Anomalies)missing.Add($"{market.Symbol}:{anomaly}");
        var marketScore=_symbols.Count==0?0:(int)Math.Round(markets.Count/(double)_symbols.Count*35);var qualityScore=markets.Count==0?0:(int)Math.Round(markets.Values.Average(x=>x.Quality.QualityScore)*.25);
        var derivScore=markets.Count==0?0:(int)Math.Round(markets.Count(x=>x.Value.Derivatives.OpenInterest>0||x.Value.Derivatives.FundingRate!=0)/(double)markets.Count*15);var newsScore=Math.Min(12,newsResult.SuccessfulSources+(newsResult.FullTextDocuments>0?3:0));var realtimeScore=_realtime is null?4:_realtime.Healthy?8:0;
        return new EvidencePack{Account=account,Positions=positions,Markets=markets,News=news,Fundamentals=fundamentals,MissingSources=missing.Distinct().ToArray(),Completeness=Math.Min(100,10+marketScore+qualityScore+derivScore+newsScore+realtimeScore)};
    }
}

public sealed class ReliableOrderExecutor:ITradingMutationExecutor,IDurableReviewExecutionReconciler
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> IntentLocks=new(StringComparer.Ordinal);
    private readonly IExchangeAdapter _ex;private readonly AgentSqliteStore _db;private readonly RiskLimits _limits;private readonly IOrderPollScheduler _poll;
    private readonly IProviderCapabilityPrecondition? _capabilityPrecondition;
    private readonly Func<ExecutionIntent,(Instrument Instrument,ExchangeCapability? Capability)>? _capabilityResolver;
    private readonly Func<string,CancellationToken,Task<ExchangeCapability?>>? _capabilityRefresh;
    private readonly bool _testnet;
    private IConfirmedNotificationObserver _notifications=NullConfirmedNotificationObserver.Instance;
    public ReliableOrderExecutor(IExchangeAdapter ex,AgentSqliteStore db,RiskLimits? limits=null,IOrderPollScheduler? poll=null):this(ex,db,limits,poll,false){}
    private ReliableOrderExecutor(IExchangeAdapter ex,AgentSqliteStore db,RiskLimits? limits,IOrderPollScheduler? poll,bool providerCapabilityRequired)
    {
        if(!providerCapabilityRequired&&ex is IExchangeProvider)throw new InvalidOperationException("Real exchange providers require a capability-aware ReliableOrderExecutor.");
        _ex=ex;_db=db;_limits=limits??new();_poll=poll??SystemOrderPollScheduler.Instance;
    }
    /// <summary>Production execution boundary. Capability context is checked before any provider mutation.</summary>
    public ReliableOrderExecutor(IExchangeAdapter ex,AgentSqliteStore db,RiskLimits limits,IOrderPollScheduler poll,IProviderCapabilityPrecondition capabilityPrecondition,Func<ExecutionIntent,(Instrument Instrument,ExchangeCapability? Capability)> capabilityResolver,bool testnet=true,IConfirmedNotificationObserver? notifications=null)
        :this(ex,db,limits,poll,true){_capabilityPrecondition=capabilityPrecondition??throw new ArgumentNullException(nameof(capabilityPrecondition));_capabilityResolver=capabilityResolver??throw new ArgumentNullException(nameof(capabilityResolver));_testnet=testnet;_notifications=notifications??NotificationRuntimeFactory.CurrentObserver;}
    public ReliableOrderExecutor(IExchangeAdapter ex,AgentSqliteStore db,IOrderPollScheduler poll):this(ex,db,null,poll){}
    public ReliableOrderExecutor(IExchangeAdapter ex,AgentSqliteStore db,RiskLimits limits,IOrderPollScheduler poll,IReadOnlyDictionary<string,ExchangeCapability> capabilities,bool testnet=true,IConfirmedNotificationObserver? notifications=null,Func<string,CancellationToken,Task<ExchangeCapability?>>? capabilityRefresh=null)
        :this(ex,db,limits,poll,new ProviderCapabilityPrecondition(),intent =>
        {
            var symbol = intent.Symbol;
            var capability = capabilities.TryGetValue(symbol, out var value) ? value : null;
            var instrument = capability is null
                ? new Instrument(symbol, symbol, string.Empty, string.Empty)
                : new Instrument(capability.CanonicalSymbol, capability.NativeSymbol, capability.ExchangeId, capability.ProviderId, capability.MarketType);
            return (instrument, capability);
        },testnet,notifications)
    {
        _capabilityRefresh=capabilityRefresh??(ex is IExchangeProvider provider
            ?async(symbol,ct)=>{var refreshed=await new ProviderCapabilityProbe().ProbeAsync(provider,[symbol],testnet,ct);return refreshed.GetValueOrDefault(symbol);}
            :null);
    }
    public bool IsTestnet=>_ex.Environment==ExchangeEnvironment.Testnet;
    public string ProviderId=>_ex is IExchangeProvider provider?provider.ProviderId:"local";
    public string AccountId=>_ex is IExchangeProvider provider?provider.ConnectionId:"local";
    public async Task<string> ExecutePlanAsync(string cycle,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct)
    {var results=new List<string>();foreach(var intent in intents){results.Add(await ExecuteAsync(cycle,intent,leverage,isolated,ct));var observed=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);if(observed is not null&&observed.ExecutedQuantity>0&&observed.ExecutedQuantity<intent.Quantity)break;}return string.Join("; ",results);}

    public async Task<string> ExecuteReduceOnlyRecoveryAsync(string cycle,ExecutionIntent intent,CancellationToken ct)
    {
        if(!intent.ReduceOnly||intent.OrderType!=ExecutionOrderType.Market)
            throw new InvalidOperationException("Recovery execution requires a reduce-only market intent.");
        EnsureTestnet();
        await EnsureFreshCapabilityAsync(intent,ct);
        var gate=IntentLocks.GetOrAdd(intent.ClientOrderId,_=>new SemaphoreSlim(1,1));
        await gate.WaitAsync(ct);
        try
        {
            var status=await _db.GetOrderIntentStatusAsync(intent.ClientOrderId,ct);
            if(status is not null||await _db.HasExecutionSubmissionJournalAsync(intent.ClientOrderId,ct))
                throw new InvalidOperationException("Recovery intent already has durable state and must be reconciled.");
            await _db.SaveIntentAsync(cycle,intent,"INTENT",null,ct);
            var order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);
            if(order is null)try
            {
                order=await _ex.PlaceMarketAsync(
                    intent.Symbol,intent.Side,intent.Quantity,intent.ClientOrderId,true,ct);
            }
            catch
            {
                order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);
                if(order is null)
                {
                    await _db.SaveIntentAsync(cycle,intent,"UNKNOWN",null,ct);
                    throw;
                }
            }
            await _db.SaveIntentAsync(cycle,intent,order.Status,order.OrderId,ct);
            if(order.ExecutedQuantity<=0)
            {
                await _db.SaveIntentAsync(cycle,intent,"UNKNOWN",order.OrderId,ct);
                throw new InvalidOperationException(L("Execution.Unconfirmed",order.Status));
            }
            var partial=order.ExecutedQuantity<intent.Quantity;
            await CaptureFeeEvidenceAsync(order,ct);await CaptureFundingEvidenceAsync(intent.Symbol,intent.Side,order.ExecutedQuantity,ct);await _db.RecordExecutionAsync(cycle,intent with{Quantity=order.ExecutedQuantity},
                partial?order with{Status="PARTIALLY_FILLED"}:order,"wpe-core-v2",ct);
            await _db.SaveIntentAsync(cycle,intent,partial?"COMPLETED_PARTIAL":"COMPLETED",order.OrderId,ct);
            await NotifyExecutionAsync(cycle,intent,order,ct);
            if(partial)throw new InvalidOperationException(L("Execution.PartialClose",order.ExecutedQuantity,intent.Quantity));
            return L("Execution.CloseConfirmed");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DurableReviewReconciliationResult> ReconcileAsync(DurableReviewExecutionArtifactV1 artifact,CancellationToken ct)
    {
        if(artifact is null||!DurableReviewArtifactCanonicalizer.Validate(artifact).Valid)return new(DurableReviewReconciliationState.Failed,"review.reconcile-artifact-invalid");var found=0;var missing=0;
        foreach(var intent in artifact.Intents)
        {
            var order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);var status=await _db.GetOrderIntentStatusAsync(intent.ClientOrderId,ct);
            if(order is null){missing++;continue;}found++;
            if(order.ExecutedQuantity<=0&&order.Status is "CANCELED" or "EXPIRED" or "REJECTED")return new(DurableReviewReconciliationState.Failed,"review.reconcile-order-terminal");
            var completed=intent.ReduceOnly?status is "COMPLETED" or "EMERGENCY_CLOSED":status=="PROTECTED";
            if(!completed)return new(DurableReviewReconciliationState.Unknown,"review.reconcile-manual-required");
        }
        if(found==0&&missing==artifact.Intents.Count)return new(DurableReviewReconciliationState.NotSubmitted,"review.reconcile-not-submitted");
        if(missing>0)return new(DurableReviewReconciliationState.Unknown,"review.reconcile-manual-required");
        return new(DurableReviewReconciliationState.Succeeded,"review.reconcile-succeeded");
    }

    public async Task<string> ExecuteAsync(string cycle,ExecutionIntent intent,int leverage,bool isolated,CancellationToken ct)
    {
        var gate=IntentLocks.GetOrAdd(intent.ClientOrderId,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(ct);try{return await ExecuteCoreAsync(cycle,intent,leverage,isolated,ct);}finally{gate.Release();}
    }

    private async Task<string> ExecuteCoreAsync(string cycle,ExecutionIntent intent,int leverage,bool isolated,CancellationToken ct)
    {
        EnsureMutationAllowed(intent);
        EnsureCapability(intent);
        if(!intent.ReduceOnly)try{await PreflightAsync(intent,ct);}catch(Exception ex){await _db.SaveIntentAsync(cycle,intent,"PREFLIGHT_BLOCKED",null,ct);var blocked=ConfirmedNotificationTruth.System(ConfirmedNotificationTruth.EventKey(cycle,intent.ClientOrderId,NotificationEventKind.RiskBlocked),NotificationEventKind.RiskBlocked,ProviderName(),_ex.Environment.ToString(),DateTime.UtcNow,UiDiagnostic.FromText(ex.ToString(),"Risk blocked").Code,intent.Symbol,intent.Side.ToString());await ObserveSafelyAsync(blocked);throw;}
        await _db.SaveIntentAsync(cycle,intent,"INTENT",null,ct);
        if(!intent.ReduceOnly)
        {
            try{await _ex.SetHedgeModeAsync(true,ct);await _ex.SetMarginModeAsync(intent.Symbol,isolated,ct);await _ex.SetLeverageAsync(intent.Symbol,leverage,ct);}
            catch(BinanceHttpException ex){await _db.SaveIntentAsync(cycle,intent,ex.Outcome==BinanceHttpOutcome.Rejected?"REJECTED":"UNKNOWN",null,ct);throw;}
            catch{await _db.SaveIntentAsync(cycle,intent,"UNKNOWN",null,ct);throw;}
        }
        ExchangeOrder? order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);
        if(order is null)try
        {
            order=intent.OrderType==ExecutionOrderType.Limit&&intent.LimitPrice>0
                ?await _ex.PlaceLimitAsync(intent.Symbol,intent.Side,intent.Quantity,intent.LimitPrice,intent.ClientOrderId,intent.ReduceOnly,ct)
                :await _ex.PlaceMarketAsync(intent.Symbol,intent.Side,intent.Quantity,intent.ClientOrderId,intent.ReduceOnly,ct);
        }catch(BinanceHttpException ex) when(ex.Outcome==BinanceHttpOutcome.Rejected){await _db.SaveIntentAsync(cycle,intent,"REJECTED",null,ct);throw;}
        catch{order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);if(order is null){await _db.SaveIntentAsync(cycle,intent,"UNKNOWN",null,ct);throw;}}
        await _db.SaveIntentAsync(cycle,intent,order.Status,order.OrderId,ct);
        order=await WaitAndCancelOnTimeoutAsync(intent.Symbol,intent.ClientOrderId,order,ct);
        await _db.SaveIntentAsync(cycle,intent,order.Status,order.OrderId,ct);
        if(order.ExecutedQuantity<=0)
        {
            if(order.Status is "CANCELED" or "EXPIRED" or "REJECTED"){await _db.SaveIntentAsync(cycle,intent,order.Status,order.OrderId,ct);throw new InvalidOperationException(L("Execution.Unconfirmed",order.Status));}
            await _db.SaveIntentAsync(cycle,intent,"UNKNOWN",order.OrderId,ct);
            throw new InvalidOperationException(L("Execution.Unconfirmed",order.Status));
        }
        var filledIntent=intent with{Quantity=order.ExecutedQuantity};var recordedOrder=order.ExecutedQuantity>0&&order.Status!="FILLED"?order with{Status="PARTIALLY_FILLED"}:order;await CaptureFeeEvidenceAsync(recordedOrder,ct);if(intent.ReduceOnly&&recordedOrder.Status=="FILLED")await CaptureFundingEvidenceAsync(intent.Symbol,intent.Side,order.ExecutedQuantity,ct);await _db.RecordExecutionAsync(cycle,filledIntent,recordedOrder,"wpe-core-v2",ct);
        if(intent.ReduceOnly)
        {
            if(IsFullClose(intent.Action)){await CancelProtectionOrdersAsync(intent.Symbol,intent.Side,ct);await _db.ClearLockedSideIfMatchesAsync(intent.Symbol,intent.Side,ct);}
            if(intent.Action==DecisionAction.Unlock)await _db.ClearLockedSideAsync(intent.Symbol,ct);
            var partial=order.ExecutedQuantity<intent.Quantity;await _db.SaveIntentAsync(cycle,intent,partial?"COMPLETED_PARTIAL":"COMPLETED",order.OrderId,ct);if(partial)throw new InvalidOperationException(L("Execution.PartialClose",order.ExecutedQuantity,intent.Quantity));await NotifyExecutionAsync(cycle,intent,order,ct);return L("Execution.CloseConfirmed");
        }
        var partiallyFilled=order.ExecutedQuantity<intent.Quantity;
        try
        {
            await _ex.PlaceProtectionAsync(intent.Symbol,intent.Side,intent.StopLoss,intent.TakeProfit,intent.ClientOrderId,ct);
            if(intent.Action==DecisionAction.Lock)await _db.SetLockedSideAsync(intent.Symbol,intent.Side,ct);
            var status=partiallyFilled?"PROTECTED_PARTIAL":"PROTECTED";await _db.SaveIntentAsync(cycle,intent,status,order.OrderId,ct);
            await NotifyExecutionAsync(cycle,intent,order,ct);
            await NotifyProtectionAsync(cycle,intent,NotificationEventKind.ProtectionPlaced,"protection confirmed",ct);
        }
        catch(Exception protectionError)
        {
            await _db.SaveIntentAsync(cycle,intent,"PROTECTION_FAILED",order.OrderId,ct);
            await NotifyProtectionAsync(cycle,intent,NotificationEventKind.ProtectionFailed,protectionError.ToString(),ct);
            var emergency=filledIntent with{ReduceOnly=true,OrderType=ExecutionOrderType.Market,ClientOrderId=EmergencyId(intent.ClientOrderId)};
            var close=await SubmitIdempotentlyAsync(emergency,ct);await _db.SaveIntentAsync(cycle,intent,"EMERGENCY_SUBMITTED",close.OrderId,ct);close=await WaitAndCancelOnTimeoutAsync(intent.Symbol,emergency.ClientOrderId,close,ct);
            var fullyClosed=close.Status=="FILLED"&&close.ExecutedQuantity>=order.ExecutedQuantity;await _db.SaveIntentAsync(cycle,intent,fullyClosed?"EMERGENCY_CLOSED":"EMERGENCY_UNKNOWN",close.OrderId,ct);
            if(fullyClosed){await CaptureFeeEvidenceAsync(close,ct);await CaptureFundingEvidenceAsync(emergency.Symbol,emergency.Side,close.ExecutedQuantity,ct);await _db.RecordExecutionAsync(cycle,emergency with{Quantity=close.ExecutedQuantity},close,"wpe-core-v2",ct);}
            var safety=fullyClosed?L("Execution.ProtectionEmergencyClosed"):L("Execution.ProtectionEmergencyUnknown",close.Status);
            throw new InvalidOperationException($"{safety} · {SensitiveDataRedactor.ForLog(protectionError.Message,180)}",protectionError);
        }
        if(partiallyFilled)throw new InvalidOperationException(L("Execution.PartialProtected",order.ExecutedQuantity,intent.Quantity));return L("Execution.Protected");
    }

    private async Task CaptureFeeEvidenceAsync(ExchangeOrder order,CancellationToken ct)
    {
        if(_ex is not IExchangeOrderFeeEvidenceReader reader)return;
        try{var evidence=await reader.ReadOrderFeeEvidenceAsync(order,ct);await _db.SaveExchangeOrderFeeEvidenceAsync(evidence,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{ /* Fee evidence is observational and never changes execution truth. */ }
    }

    private async Task CaptureFundingEvidenceAsync(string symbol,PositionSide side,decimal closingQuantity,CancellationToken ct)
    {
        if(_ex is not IExchangeFundingIncomeReader reader)return;
        try
        {
            var start=await _db.GetUnambiguousFundingObservationStartAsync(symbol,side,closingQuantity,ct);if(start is null)return;
            var result=await reader.ReadFundingIncomeAsync(symbol,start.Value,DateTimeOffset.UtcNow,ct);await _db.SaveFundingObservationAsync(result,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{ /* Funding evidence is observational and never changes execution truth. */ }
    }

    private void EnsureCapability(ExecutionIntent intent)
    {
        if (_capabilityPrecondition is null || _capabilityResolver is null) return;
        var (instrument, capability) = _capabilityResolver(intent);
        var result = _capabilityPrecondition.Check(instrument, capability, _testnet);
        if (!result.Allowed)
            throw new InvalidOperationException($"Execution.CapabilityBlocked: {result.Reason}");
    }

    private void EnsureCapability(string symbol)
    {
        EnsureCapability(new ExecutionIntent(symbol,PositionSide.Long,0,true,0,0,"CAPABILITY-CHECK","Provider mutation capability check"));
    }
    private async Task EnsureFreshCapabilityAsync(ExecutionIntent intent,CancellationToken ct)
    {
        if(_capabilityPrecondition is null||_capabilityResolver is null)return;
        var (cachedInstrument,cachedCapability)=_capabilityResolver(intent);
        var cachedResult=_capabilityPrecondition.Check(cachedInstrument,cachedCapability,_testnet);
        if(cachedResult.Allowed)return;
        if(_capabilityRefresh is null)
            throw new InvalidOperationException($"Execution.CapabilityBlocked: {cachedResult.Reason}");
        var capability=await _capabilityRefresh(intent.Symbol,ct);
        var instrument=capability is null
            ?new Instrument(intent.Symbol,intent.Symbol,string.Empty,string.Empty)
            :new Instrument(capability.CanonicalSymbol,capability.NativeSymbol,capability.ExchangeId,capability.ProviderId,capability.MarketType);
        var result=_capabilityPrecondition.Check(instrument,capability,_testnet);
        if(!result.Allowed)
            throw new InvalidOperationException($"Execution.CapabilityBlocked: {result.Reason}");
    }

    public async Task<string> ReplaceProtectionAsync(string cycle,ProtectionAdjustment adjustment,CancellationToken ct)
    {
        EnsureTestnet();ArgumentNullException.ThrowIfNull(adjustment);
        var group=string.IsNullOrWhiteSpace(adjustment.AdjustmentId)
            ?EmergencyId($"WPE-PROT-{DateTime.UtcNow:yyMMddHHmmss}")
            :adjustment.AdjustmentId;
        var stateKey=string.IsNullOrWhiteSpace(adjustment.AdjustmentId)
            ?null
            :PositionManagementDurableState.ProtectionAdjustmentKey(adjustment.AdjustmentId);
        var durableGate=stateKey is null?null:IntentLocks.GetOrAdd("protection:"+adjustment.AdjustmentId!,_=>new SemaphoreSlim(1,1));
        if(durableGate is not null)await durableGate.WaitAsync(ct);
        try
        {
            if(stateKey is not null&&await _db.GetStateAsync(stateKey,ct) is not null)
                throw new InvalidOperationException("Protection adjustment already has durable state and must be reconciled.");
            var capabilityIntent=new ExecutionIntent(adjustment.Symbol,adjustment.Side,0,true,0,0,group,"protection adjustment capability refresh");
            await EnsureFreshCapabilityAsync(capabilityIntent,ct);
            if(stateKey is not null)await _db.SetStateAsync(stateKey,"PENDING",ct);
        }
        finally
        {
            durableGate?.Release();
        }
        if(_ex is not IInPlaceProtectionUpdateAdapter)
        {
            try
            {
                await CancelProtectionOrdersAsync(adjustment.Symbol,adjustment.Side,ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested)
            {
                if(stateKey is not null)await _db.SetStateAsync(stateKey,"CANCELED",CancellationToken.None);
                throw;
            }
            catch(Exception ex)
            {
                if(stateKey is not null)await _db.SetStateAsync(stateKey,"FAILED",CancellationToken.None);
                await EmergencyCloseAfterProtectionFailureAsync(cycle,adjustment,group,ex,ct);
                throw;
            }
        }

        try
        {
            await _ex.PlaceProtectionAsync(adjustment.Symbol,adjustment.Side,adjustment.StopLoss,adjustment.TakeProfit,group,ct);
            if(stateKey is not null)await _db.SetStateAsync(stateKey,"COMPLETED",ct);
            var value=ConfirmedNotificationTruth.Protection(
                ConfirmedNotificationTruth.EventKey(cycle,group,NotificationEventKind.ProtectionUpdated),
                NotificationEventKind.ProtectionUpdated,ProviderName(),_ex.Environment.ToString(),
                adjustment.Symbol,adjustment.Side,adjustment.StopLoss,adjustment.TakeProfit,
                DateTime.UtcNow,UiDiagnostic.FromText("protection updated","Protection updated").Code);
            await ObserveSafelyAsync(value);
            return L("Execution.ProtectionAdjusted",adjustment.StopLoss,adjustment.TakeProfit);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            if(stateKey is not null)await _db.SetStateAsync(stateKey,"CANCELED",CancellationToken.None);
            throw;
        }
        catch(Exception ex)
        {
            if(stateKey is not null)await _db.SetStateAsync(stateKey,"FAILED",CancellationToken.None);
            var value=ConfirmedNotificationTruth.Protection(
                ConfirmedNotificationTruth.EventKey(cycle,group,NotificationEventKind.ProtectionFailed),
                NotificationEventKind.ProtectionFailed,ProviderName(),_ex.Environment.ToString(),
                adjustment.Symbol,adjustment.Side,adjustment.StopLoss,adjustment.TakeProfit,
                DateTime.UtcNow,UiDiagnostic.FromText(ex.ToString(),"Protection update failed").Code);
            await ObserveSafelyAsync(value);
            await EmergencyCloseAfterProtectionFailureAsync(cycle,adjustment,group,ex,ct);
            throw;
        }
    }

    private async Task EmergencyCloseAfterProtectionFailureAsync(
        string cycle,ProtectionAdjustment adjustment,string group,Exception failure,CancellationToken ct)
    {
        var position=(await _ex.GetPositionsAsync(ct)).FirstOrDefault(x=>x.Symbol==adjustment.Symbol&&x.Side==adjustment.Side);
        if(position is null||position.Quantity<=0)return;
        var intent=new ExecutionIntent(
            position.Symbol,position.Side,position.Quantity,true,0,0,EmergencyId(group),
            L("Execution.ProtectionReplaceFailed",SensitiveDataRedactor.ForLog(failure.Message,180)),
            position.Side==PositionSide.Long?DecisionAction.CloseLong:DecisionAction.CloseShort,
            ExpectedPrice:position.MarkPrice,
            ReasonCode:PositionExitReasonCodes.ProtectionReplaceFailed);
        await ExecuteAsync(cycle,intent,Math.Max(1,(int)position.Leverage),true,ct);
    }

    private async Task NotifyExecutionAsync(
        string cycle,ExecutionIntent intent,ExchangeOrder order,CancellationToken ct)
    {
        var kind=intent.ReduceOnly?NotificationEventKind.PositionClosed:NotificationEventKind.PositionOpened;
        var value=ConfirmedNotificationTruth.FromExecution(
            ConfirmedNotificationTruth.EventKey(cycle,intent.ClientOrderId,kind),
            ProviderName(),_ex.Environment.ToString(),intent,order,DateTime.UtcNow,
            UiDiagnostic.FromText($"{order.Status}:{order.ExecutedQuantity}","Execution confirmed").Code);
        if(value is not null)await ObserveSafelyAsync(value);
    }

    private async Task NotifyProtectionAsync(
        string cycle,ExecutionIntent intent,NotificationEventKind kind,string diagnostic,CancellationToken ct)
    {
        var value=ConfirmedNotificationTruth.Protection(
            ConfirmedNotificationTruth.EventKey(cycle,intent.ClientOrderId,kind),kind,
            ProviderName(),_ex.Environment.ToString(),intent.Symbol,intent.Side,
            intent.StopLoss>0?intent.StopLoss:null,intent.TakeProfit>0?intent.TakeProfit:null,
            DateTime.UtcNow,UiDiagnostic.FromText(diagnostic,"Protection status").Code);
        await ObserveSafelyAsync(value);
    }

    private string ProviderName()=>_ex is IExchangeProvider provider?provider.ProviderId:"local";
    private async Task ObserveSafelyAsync(ConfirmedNotificationEvent value)
    {
        try{await _notifications.ObserveAsync(value,CancellationToken.None);}
        catch{/* Notification observers must never alter execution outcomes. */}
    }

    public async Task<RecoveryResult> RecoverPendingAsync(CancellationToken ct)
    {
        var safe=true;var messages=new List<string>();foreach(var saved in await _db.GetRecoverableIntentsAsync(ct))
        {
            var intent=saved.Intent;
            if(saved.Status is "PROTECTION_FAILED" or "EMERGENCY_SUBMITTED" or "EMERGENCY_UNKNOWN")
            {
                EnsureTestnet();var emergencyId=EmergencyId(intent.ClientOrderId);var emergency=await _ex.FindOrderAsync(intent.Symbol,emergencyId,ct);var original=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);if(emergency is null||original is null){await _db.SaveIntentAsync(saved.CycleId,intent,"EMERGENCY_UNKNOWN",saved.ExchangeOrderId,ct);safe=false;messages.Add(L("Execution.ExchangeUnknown",emergencyId));continue;}var fullyClosed=emergency.Status=="FILLED"&&original.ExecutedQuantity>0&&emergency.ExecutedQuantity>=original.ExecutedQuantity;if(fullyClosed){await _db.SaveIntentAsync(saved.CycleId,intent,"EMERGENCY_CLOSED",emergency.OrderId,ct);messages.Add(L("Execution.ProtectionEmergencyClosed"));}else{await _db.SaveIntentAsync(saved.CycleId,intent,"EMERGENCY_UNKNOWN",emergency.OrderId,ct);safe=false;messages.Add(L("Execution.ProtectionEmergencyUnknown",emergency.Status));}continue;
            }
            var order=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);if(order is null){await _db.SaveIntentAsync(saved.CycleId,intent,"UNKNOWN",saved.ExchangeOrderId,ct);safe=false;messages.Add(L("Execution.ExchangeUnknown",intent.ClientOrderId));continue;}
            if(order.Status is "NEW" or "PARTIALLY_FILLED"){EnsureTestnet();order=await WaitAndCancelOnTimeoutAsync(intent.Symbol,intent.ClientOrderId,order,ct);}
            await _db.SaveIntentAsync(saved.CycleId,intent,order.Status,order.OrderId,ct);
            if(order.ExecutedQuantity>0&&!intent.ReduceOnly)try{EnsureTestnet();EnsureCapability(intent);await _ex.PlaceProtectionAsync(intent.Symbol,intent.Side,intent.StopLoss,intent.TakeProfit,intent.ClientOrderId,ct);await _db.SaveIntentAsync(saved.CycleId,intent,order.Status=="FILLED"?"PROTECTED":"PROTECTED_PARTIAL",order.OrderId,ct);messages.Add(L("Execution.ProtectionRecovered",intent.Symbol,intent.Side));}catch(Exception ex){await _db.SaveIntentAsync(saved.CycleId,intent,"PROTECTION_FAILED",order.OrderId,ct);safe=false;messages.Add(L("Execution.ProtectionRecoveryFailed",intent.Symbol,intent.Side,SensitiveDataRedactor.ForLog(ex.Message,180)));}
            else if(order.Status=="FILLED"){await _db.SaveIntentAsync(saved.CycleId,intent,"COMPLETED",order.OrderId,ct);}
            else if(order.ExecutedQuantity>0){await _db.SaveIntentAsync(saved.CycleId,intent,"COMPLETED_PARTIAL",order.OrderId,ct);safe=false;messages.Add(L("Execution.Pending",intent.ClientOrderId,order.Status));}
            else if(!IsTerminal(order.Status)){await _db.SaveIntentAsync(saved.CycleId,intent,"UNKNOWN",order.OrderId,ct);safe=false;messages.Add(L("Execution.Pending",intent.ClientOrderId,order.Status));}
        }return new(safe,messages);
    }

    public async Task<RecoveryResult> AuditAndRepairProtectionAsync(IReadOnlyList<ManagedPosition> positions,IReadOnlyList<ExchangeOrder> orders,CancellationToken ct)
    {
        EnsureTestnet();
        var managedLegs=await _db.GetExecutionPositionLedgerAsync(ct);
        var safe=true;var messages=new List<string>();foreach(var position in positions)
        {
            if(!HasExactLocalPositionOwnership(position,positions,managedLegs))
            {
                safe=false;messages.Add($"recovery.position-ownership-conflict:{position.Symbol}:{position.Side}");
                continue;
            }
            var leg=orders.Where(o=>o.Symbol==position.Symbol&&o.PositionSide==position.Side&&o.IsProtection).ToArray();var combined=leg.Any(o=>o.Type.Contains("OCO",StringComparison.OrdinalIgnoreCase)||o.Type.Contains("POSITION_TPSL",StringComparison.OrdinalIgnoreCase));var hasSl=combined||leg.Any(o=>o.Type.Contains("STOP",StringComparison.OrdinalIgnoreCase)||o.Type.Contains("LOSS",StringComparison.OrdinalIgnoreCase));var hasTp=combined||leg.Any(o=>o.Type.Contains("TAKE",StringComparison.OrdinalIgnoreCase)||o.Type.Contains("PROFIT",StringComparison.OrdinalIgnoreCase));if(hasSl&&hasTp)continue;
            var intent=await _db.GetLatestOpeningIntentAsync(position.Symbol,position.Side,ct);if(intent is null){safe=false;messages.Add(L("Execution.ProtectionMissing",position.Symbol,position.Side,!hasSl,!hasTp));continue;}
            if(await _db.HasStateAsync(PositionManagementDurableState.OwnershipRevocationKey(intent.ClientOrderId),ct))
            {
                safe=false;messages.Add($"recovery.position-ownership-revoked:{position.Symbol}:{position.Side}");continue;
            }
            if(await _db.HasStateAsync(PositionManagementDurableState.OwnershipMissingCandidateKey(intent.ClientOrderId),ct))
            {
                safe=false;messages.Add($"recovery.position-ownership-uncertain:{position.Symbol}:{position.Side}");continue;
            }
            try{EnsureCapability(intent);await _ex.PlaceProtectionAsync(position.Symbol,position.Side,intent.StopLoss,intent.TakeProfit,intent.ClientOrderId,ct);messages.Add(L("Execution.ProtectionRepaired",position.Symbol,position.Side));}catch(Exception ex){safe=false;messages.Add(L("Execution.ProtectionRepairFailed",position.Symbol,position.Side,SensitiveDataRedactor.ForLog(ex.Message,180)));}
        }return new(safe,messages);
    }

    private static bool HasExactLocalPositionOwnership(
        ManagedPosition position,IReadOnlyList<ManagedPosition> positions,IReadOnlyList<ExecutionPositionLegV1> managedLegs)
    {
        var exchangeMatches=positions.Count(x=>string.Equals(x.Symbol,position.Symbol,StringComparison.OrdinalIgnoreCase)&&x.Side==position.Side);
        if(exchangeMatches!=1)return false;
        var localMatches=managedLegs.Where(x=>string.Equals(x.Symbol,position.Symbol,StringComparison.OrdinalIgnoreCase)&&x.Side==position.Side).ToArray();
        if(localMatches.Length!=1||localMatches[0].Quantity<=0||position.Quantity<=0)return false;
        var tolerance=Math.Max(.00000001m,Math.Max(localMatches[0].Quantity,position.Quantity)*.000001m);
        return Math.Abs(localMatches[0].Quantity-position.Quantity)<=tolerance;
    }

    private async Task PreflightAsync(ExecutionIntent intent,CancellationToken ct)
    {
        var market=await _ex.GetMarketAsync(intent.Symbol,ct);var quality=market.Quality;
        if(quality.QualityScore<65||quality.LiquidityScore<_limits.MinimumLiquidityScore||quality.SpreadBps>_limits.MaximumSpreadBps||quality.AtrPercent>_limits.MaxAtrPercent)throw new InvalidOperationException(L("Execution.PreflightBlocked",quality.QualityScore,quality.LiquidityScore,quality.SpreadBps,quality.AtrPercent));
        if(intent.ExpectedPrice>0){var slippage=Math.Abs((double)((market.Price-intent.ExpectedPrice)/intent.ExpectedPrice))*10000;if(slippage>_limits.MaximumSlippageBps)throw new InvalidOperationException(L("Execution.SlippageBlocked",slippage,_limits.MaximumSlippageBps));}
    }
    private async Task<ExchangeOrder> SubmitIdempotentlyAsync(ExecutionIntent intent,CancellationToken ct)
    {
        var existing=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);if(existing is not null)return existing;try{return intent.OrderType==ExecutionOrderType.Limit&&intent.LimitPrice>0?await _ex.PlaceLimitAsync(intent.Symbol,intent.Side,intent.Quantity,intent.LimitPrice,intent.ClientOrderId,intent.ReduceOnly,ct):await _ex.PlaceMarketAsync(intent.Symbol,intent.Side,intent.Quantity,intent.ClientOrderId,intent.ReduceOnly,ct);}catch{existing=await _ex.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);if(existing is not null)return existing;throw;}
    }
    private async Task<ExchangeOrder> WaitAndCancelOnTimeoutAsync(string symbol,string clientOrderId,ExchangeOrder order,CancellationToken ct)
    {
        var deadline=_poll.UtcNow+_poll.OrderTimeout;while(!IsTerminal(order.Status)&&_poll.UtcNow<deadline){await _poll.DelayAsync(ct);order=await _ex.FindOrderAsync(symbol,clientOrderId,ct)??order;}if(IsTerminal(order.Status))return order;
        await EnsureFreshCapabilityAsync(new ExecutionIntent(symbol,order.PositionSide??PositionSide.Long,0,true,0,0,clientOrderId,"timeout cancellation capability refresh"),ct);var cancelFailed=false;try{await _ex.CancelOrderAsync(symbol,order.OrderId,ct);}catch{cancelFailed=true;}
        var observed=await _ex.FindOrderAsync(symbol,clientOrderId,ct)??order with{Status="UNKNOWN"};var postCancelDeadline=_poll.UtcNow+_poll.PostCancelWindow;while(_poll.UtcNow<postCancelDeadline){await _poll.DelayAsync(ct);var latest=await _ex.FindOrderAsync(symbol,clientOrderId,ct);if(latest is not null)observed=latest;}
        if(!IsTerminal(observed.Status)&&observed.Status!="FILLED")observed=observed with{Status=cancelFailed?"UNKNOWN":observed.Status};return observed;
    }
    private void EnsureMutationAllowed(ExecutionIntent intent)
    {
        EnsureTestnet();var status=global::币安量化机器人.Services.ServiceLocator.SystemState.Status;if(!intent.ReduceOnly&&status is AgentStatus.Degraded or AgentStatus.Stopped)throw new InvalidOperationException($"Risk increase is blocked while Agent status is {status}.");
    }
    private void EnsureTestnet(){if(_ex.Environment!=ExchangeEnvironment.Testnet)throw new InvalidOperationException("Exchange mutation is restricted to Testnet.");}
    private static bool IsTerminal(string status)=>status is "FILLED" or "CANCELED" or "REJECTED" or "EXPIRED";
    private async Task CancelProtectionOrdersAsync(string symbol,PositionSide side,CancellationToken ct){EnsureCapability(symbol);var orders=await _ex.GetOpenOrdersAsync(symbol,ct);foreach(var order in orders.Where(x=>x.IsProtection&&x.PositionSide==side))await _ex.CancelOrderAsync(symbol,order.OrderId,ct);}
    private static bool IsFullClose(DecisionAction action)=>action is DecisionAction.CloseLong or DecisionAction.CloseShort or DecisionAction.Unlock or DecisionAction.ReverseToLong or DecisionAction.ReverseToShort;
    private static string EmergencyId(string id){const string suffix="-E";var stem=id.Length>36-suffix.Length?id[..(36-suffix.Length)]:id;return stem+suffix;}
    private static string L(string key,params object?[] args)=>LocalizationService.Current.T(key,args);
}

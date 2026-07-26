using WpeAgent.RuntimeContracts;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed class ActiveTradingReviewContextProvider : ITrustedTradingReviewContextProvider
{
    private readonly Func<TrustedTradingReviewContext?> _read;
    public ActiveTradingReviewContextProvider(Func<TrustedTradingReviewContext?> read)=>_read=read??throw new ArgumentNullException(nameof(read));
    public ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();return ValueTask.FromResult(_read());}
}

public sealed class ActiveTradingReviewRuntimeValidator : ITradingReviewRuntimeValidator
{
    private static readonly TimeSpan MaximumMarketAge=TimeSpan.FromMinutes(5);
    private readonly AgentSettingsStore _settings;
    private readonly AgentSqliteStore _store;
    private readonly ITrustedTradingReviewContextProvider _context;
    private readonly IReadOnlyDictionary<string,ExchangeCapability> _capabilities;
    private readonly ProviderCapabilityPrecondition _capabilityGate=new();
    private readonly Func<DateTimeOffset> _utcNow;

    public ActiveTradingReviewRuntimeValidator(AgentSettingsStore settings,AgentSqliteStore store,ITrustedTradingReviewContextProvider context,IReadOnlyDictionary<string,ExchangeCapability> capabilities,Func<DateTimeOffset>? utcNow=null)
    {
        _settings=settings??throw new ArgumentNullException(nameof(settings));_store=store??throw new ArgumentNullException(nameof(store));_context=context??throw new ArgumentNullException(nameof(context));_capabilities=capabilities??throw new ArgumentNullException(nameof(capabilities));_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }

    public async Task<TradingReviewRuntimeValidation> ValidateAsync(TradingApprovalRequest request,DurableReviewExecutionArtifactV1 artifact,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings=_settings.Load();var context=await _context.ReadAsync(ct);var now=_utcNow().ToUniversalTime();
        if(settings.AuthorizationMode!=TradingAuthorizationMode.Review||context?.AuthorizationMode!=TradingAuthorizationMode.Review)return Deny("review.mode-not-review");
        if(context is null||!context.IsTestnet||!string.Equals(artifact.Environment,"Testnet",StringComparison.Ordinal))return Deny("review.mainnet-blocked");
        if(!Matches(request.UserId,context.UserId)||!Matches(request.DeviceId,context.DeviceId)||!Matches(request.SessionId,context.SessionId))return Deny("review.context-mismatch");
        if(!Matches(artifact.ProviderId,context.ProviderId))return Deny("review.provider-mismatch");
        if(now-artifact.MarketCollectedAtUtc>MaximumMarketAge||artifact.MarketCollectedAtUtc>now)return Deny("review.market-stale");
        var strategies=await _store.GetStrategiesAsync(ct);if(!strategies.Any(value=>value.Lifecycle==StrategyLifecycle.Active&&Matches(value.Id,artifact.StrategyId)&&Matches(value.Version,artifact.StrategyVersion)))return Deny("review.strategy-invalid");
        foreach(var intent in artifact.Intents)
        {
            if(!_capabilities.TryGetValue(intent.Symbol,out var capability))return Deny("review.capability-stale");
            var instrument=new Instrument(capability.CanonicalSymbol,capability.NativeSymbol,capability.ExchangeId,capability.ProviderId,capability.MarketType);
            if(!_capabilityGate.Check(instrument,capability,true).Allowed||!Matches(capability.ProviderId,context.ProviderId))return Deny("review.capability-stale");
        }
        return new(true,"review.runtime-valid");
    }

    private static bool Matches(string? left,string? right)=>!string.IsNullOrWhiteSpace(left)&&string.Equals(left,right,StringComparison.OrdinalIgnoreCase);
    private static TradingReviewRuntimeValidation Deny(string code)=>new(false,code);
}

public sealed class ActiveTradingReviewRiskValidator : ITradingReviewRiskValidator
{
    private readonly AgentSettingsStore _settings;
    private readonly AgentSqliteStore _store;
    private readonly EvidenceCollector _collector;
    private readonly IndependentRiskManagerSkill _risk=new();
    private readonly LongHorizonResearchSkill _research=new();
    private readonly PortfolioRiskSkill _portfolio=new();
    private readonly Func<DateTimeOffset> _utcNow;

    public ActiveTradingReviewRiskValidator(AgentSettingsStore settings,AgentSqliteStore store,EvidenceCollector collector,Func<DateTimeOffset>? utcNow=null)
    {
        _settings=settings??throw new ArgumentNullException(nameof(settings));_store=store??throw new ArgumentNullException(nameof(store));_collector=collector??throw new ArgumentNullException(nameof(collector));_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }

    public async Task<TradingReviewRiskValidation> ValidateAsync(TradingApprovalRequest request,DurableReviewExecutionArtifactV1 artifact,DurableReviewArtifactHashes hashes,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var settings=_settings.Load();
        if(settings.AuthorizationMode!=TradingAuthorizationMode.Review||artifact.Intents.Count==0)return Deny("review.risk-context-invalid");
        if(artifact.Intents.Select(value=>value.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1)return Deny("review.risk-context-invalid");
        var intents=artifact.Intents.Select(ToIntent).ToArray();var first=intents[0];
        if(intents.Any(value=>value.Action!=first.Action))return Deny("review.risk-context-invalid");
        var evidence=await _collector.CollectAsync(ct);if(!evidence.Markets.TryGetValue(first.Symbol,out var market))return Deny("review.risk-market-unavailable");
        var histories=new Dictionary<string,IReadOnlyList<CandleEvidence>>(StringComparer.OrdinalIgnoreCase);
        foreach(var symbol in evidence.Markets.Keys)histories[symbol]=await _store.LoadHistoricalCandlesAsync(symbol,"1h",30000,ct);
        var research=_research.Evaluate(first.Symbol,histories.GetValueOrDefault(first.Symbol)??[],settings.Risk);
        var portfolio=_portfolio.Evaluate(evidence,histories,intents,settings.Risk);var history=await _store.GetRiskHistoryAsync(ct);
        var decision=new DecisionPlan{Action=first.Action,Instrument=first.Symbol,EntryPrice=first.ExpectedPrice,StopLossPrice=first.StopLoss,TakeProfitPrice=first.TakeProfit,OrderType=first.OrderType,StrategyVersion=artifact.StrategyVersion,RiskRewardRatio=RiskReward(first)};
        var assessment=new MarketDecisionAssessment{Symbol=first.Symbol,Fresh=DateTime.UtcNow-market.CollectedAt<=TimeSpan.FromMinutes(5),EntryReady=true,RecommendedAction=first.Action};
        var review=_risk.Review(decision,evidence,assessment,intents,settings.Risk,history,research,portfolio);if(!review.Approved)return Deny("review.risk-blocked");
        var now=_utcNow().ToUniversalTime();var receipt=new DeterministicRiskReceipt("review-risk-"+Guid.NewGuid().ToString("N"),request.CorrelationId,hashes.IntentHash,true,now,now.AddMinutes(1),null,hashes.ArtifactHash);
        return new(true,"review.risk-valid",receipt);
    }

    private static ExecutionIntent ToIntent(DurableExecutionIntentSnapshotV1 value)=>new(value.Symbol,Enum.Parse<PositionSide>(value.Side),value.Quantity,value.ReduceOnly,value.StopLoss,value.TakeProfit,value.ClientOrderId,value.ReasonCode,Enum.Parse<DecisionAction>(value.Action),Enum.Parse<ExecutionOrderType>(value.OrderType),value.LimitPrice,value.ExpectedPrice);
    private static double RiskReward(ExecutionIntent value){var risk=Math.Abs(value.ExpectedPrice-value.StopLoss);return risk<=0?0:(double)(Math.Abs(value.TakeProfit-value.ExpectedPrice)/risk);}
    private static TradingReviewRiskValidation Deny(string code)=>new(false,code,null);
}

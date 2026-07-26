using WpeAgent.TradingAuthorization;

namespace 币安量化机器人.Services.Agent;

public sealed record TradingReviewRuntimeValidation(bool Allowed,string Code);
/// <summary>Must revalidate current Review mode, Testnet/provider, capability freshness, strategy lifecycle/version, and market/data freshness.</summary>
public interface ITradingReviewRuntimeValidator
{
    Task<TradingReviewRuntimeValidation> ValidateAsync(TradingApprovalRequest request,DurableReviewExecutionArtifactV1 artifact,CancellationToken ct);
}

public sealed record TradingReviewRiskValidation(bool Allowed,string Code,DeterministicRiskReceipt? Receipt);
/// <summary>Must run the current account/portfolio risk gate and return its short-lived receipt bound to both durable hashes.</summary>
public interface ITradingReviewRiskValidator
{
    Task<TradingReviewRiskValidation> ValidateAsync(TradingApprovalRequest request,DurableReviewExecutionArtifactV1 artifact,DurableReviewArtifactHashes hashes,CancellationToken ct);
}

public sealed record TradingReviewProcessorResult(bool Handled,string Code);

public sealed class TradingReviewExecutionProcessor
{
    private static readonly TimeSpan LeaseLifetime=TimeSpan.FromSeconds(30);
    private readonly AgentSqliteStore _store;
    private readonly TradingExecutionGateway _gateway;
    private readonly ITradingReviewRuntimeValidator _runtimeValidator;
    private readonly ITradingReviewRiskValidator _riskValidator;
    private readonly IDurableReviewExecutionReconciler _reconciler;
    private readonly Func<DateTimeOffset> _utcNow;

    public TradingReviewExecutionProcessor(AgentSqliteStore store,TradingExecutionGateway gateway,ITradingReviewRuntimeValidator runtimeValidator,ITradingReviewRiskValidator riskValidator,IDurableReviewExecutionReconciler reconciler)
        :this(store,gateway,runtimeValidator,riskValidator,reconciler,null){}
    internal TradingReviewExecutionProcessor(AgentSqliteStore store,TradingExecutionGateway gateway,ITradingReviewRuntimeValidator runtimeValidator,ITradingReviewRiskValidator riskValidator,IDurableReviewExecutionReconciler reconciler,Func<DateTimeOffset>? utcNow)
    {
        _store=store??throw new ArgumentNullException(nameof(store));_gateway=gateway??throw new ArgumentNullException(nameof(gateway));_runtimeValidator=runtimeValidator??throw new ArgumentNullException(nameof(runtimeValidator));_riskValidator=riskValidator??throw new ArgumentNullException(nameof(riskValidator));_reconciler=reconciler??throw new ArgumentNullException(nameof(reconciler));_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }

    public async Task<TradingReviewProcessorResult> ProcessNextApprovedAsync(string workerId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var candidates=await _store.GetTradingReviewQueueAsync(TradingReviewQueueStatus.Approved,1,0,ct);if(candidates.Count==0)candidates=await _store.GetTradingReviewQueueAsync(TradingReviewQueueStatus.Claimed,100,0,ct);if(candidates.Count==0)return Result(false,"review.no-approved-work");PersistedTradingReviewQueueItem? candidate=null;TradingReviewQueueClaimResult? claim=null;
        foreach(var value in candidates){var attempted=await _store.TryClaimTradingReviewAsync(value.RequestId,workerId,LeaseLifetime,ct);if(attempted.Claimed){candidate=value;claim=attempted;break;}if(attempted.Code=="review.artifact-invalid")await _store.TryTransitionTradingReviewAsync(value.RequestId,value.Status,TradingReviewQueueStatus.ArtifactInvalid,value.Status==TradingReviewQueueStatus.Claimed?value.LeaseOwner:null,"review.artifact-invalid",ct);}
        if(candidate is null||claim is null)return Result(false,"review.claim-unavailable");var item=await _store.GetTradingReviewQueueItemAsync(candidate.RequestId,ct);
        if(item is null||!item.ArtifactValid||item.Artifact is null)return await BlockClaimed(candidate.RequestId,workerId,TradingReviewQueueStatus.ArtifactInvalid,"review.artifact-invalid",ct);
        var artifact=item.Artifact;var now=_utcNow().ToUniversalTime();if(artifact.ExpiresAtUtc<=now){var expired=await _store.TryExpireTradingReviewAsync(candidate.RequestId,TradingReviewQueueStatus.Claimed,ct);return Result(expired.Succeeded,"review.expired");}
        var request=await _store.GetTradingApprovalRequestAsync(candidate.RequestId,ct);var persistedReceipt=await _store.GetTradingApprovalReceiptForRequestAsync(candidate.RequestId,ct);if(request is null||persistedReceipt is null)return await BlockClaimed(candidate.RequestId,workerId,TradingReviewQueueStatus.PolicyBlocked,"review.approval-chain-missing",ct);
        TradingReviewRuntimeValidation runtime;
        try{runtime=await _runtimeValidator.ValidateAsync(request,artifact,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{return await BlockClaimed(candidate.RequestId,workerId,TradingReviewQueueStatus.PolicyBlocked,"review.runtime-validation-failed",ct);}
        if(!runtime.Allowed){var (status,code)=RuntimeFailure(runtime.Code);return await BlockClaimed(candidate.RequestId,workerId,status,code,ct);}
        var hashes=DurableReviewArtifactCanonicalizer.ComputeHashes(artifact);TradingReviewRiskValidation risk;
        try{risk=await _riskValidator.ValidateAsync(request,artifact,hashes,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{return await BlockClaimed(candidate.RequestId,workerId,TradingReviewQueueStatus.PolicyBlocked,"review.risk-validation-failed",ct);}
        if(!risk.Allowed||!ValidRiskReceipt(risk.Receipt,request,hashes,now))return await BlockClaimed(candidate.RequestId,workerId,TradingReviewQueueStatus.PolicyBlocked,risk.Allowed?"review.risk-receipt-invalid":"review.risk-blocked",ct);
        var executing=await _store.TryTransitionTradingReviewAsync(candidate.RequestId,TradingReviewQueueStatus.Claimed,TradingReviewQueueStatus.Executing,workerId,"review.executing",ct);if(!executing.Succeeded)return Result(false,executing.Code);
        TradingExecutionGatewayResult execution;
        try{execution=await _gateway.ExecuteDurableReviewAsync(new(request,persistedReceipt.Receipt,risk.Receipt!,artifact),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{return Result(true,"review.execution-ambiguous");}
        if(!execution.Executed)
        {
            var blocked=await _store.TryTransitionTradingReviewAsync(candidate.RequestId,TradingReviewQueueStatus.Executing,TradingReviewQueueStatus.PolicyBlocked,workerId,"review.gateway-blocked",ct);return Result(blocked.Succeeded,"review.gateway-blocked");
        }
        var succeeded=await _store.TryTransitionTradingReviewAsync(candidate.RequestId,TradingReviewQueueStatus.Executing,TradingReviewQueueStatus.Succeeded,workerId,"review.succeeded",ct);return Result(succeeded.Succeeded,succeeded.Succeeded?"review.succeeded":"review.execution-ambiguous");
    }

    public async Task<TradingReviewProcessorResult> ReconcileNextAsync(string workerId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var candidates=await _store.GetTradingReviewQueueAsync(TradingReviewQueueStatus.Executing,100,0,ct);
        foreach(var candidate in candidates)
        {
            var claim=await _store.TryClaimTradingReviewReconciliationAsync(candidate.RequestId,workerId,LeaseLifetime,ct);if(!claim.Claimed)continue;var item=await _store.GetTradingReviewQueueItemAsync(candidate.RequestId,ct);
            if(item is null||!item.ArtifactValid||item.Artifact is null){await _store.TryTransitionTradingReviewAsync(candidate.RequestId,TradingReviewQueueStatus.Reconciling,TradingReviewQueueStatus.ArtifactInvalid,workerId,"review.artifact-invalid",ct);return Result(true,"review.artifact-invalid");}
            DurableReviewReconciliationResult reconciliation;try{reconciliation=await _reconciler.ReconcileAsync(item.Artifact,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{reconciliation=new(DurableReviewReconciliationState.Unknown,"review.reconcile-manual-required");}
            var (status,code)=reconciliation.State switch
            {
                DurableReviewReconciliationState.Succeeded=>(TradingReviewQueueStatus.Succeeded,"review.reconcile-succeeded"),
                DurableReviewReconciliationState.NotSubmitted=>(TradingReviewQueueStatus.FailedTerminal,"review.reconcile-not-submitted"),
                DurableReviewReconciliationState.Failed=>(TradingReviewQueueStatus.FailedTerminal,"review.reconcile-failed"),
                _=>(TradingReviewQueueStatus.PolicyBlocked,"review.reconcile-manual-required")
            };
            var transitioned=await _store.TryTransitionTradingReviewAsync(candidate.RequestId,TradingReviewQueueStatus.Reconciling,status,workerId,code,ct);return Result(transitioned.Succeeded,code);
        }
        return Result(false,"review.no-reconciliation-work");
    }

    private async Task<TradingReviewProcessorResult> BlockClaimed(string requestId,string workerId,TradingReviewQueueStatus status,string code,CancellationToken ct)
    {
        var transition=await _store.TryTransitionTradingReviewAsync(requestId,TradingReviewQueueStatus.Claimed,status,workerId,code,ct);return Result(transition.Succeeded,code);
    }
    private static (TradingReviewQueueStatus Status,string Code) RuntimeFailure(string code)=>code switch
    {
        "review.artifact-invalid"=>(TradingReviewQueueStatus.ArtifactInvalid,"review.artifact-invalid"),
        "review.strategy-invalid"=>(TradingReviewQueueStatus.StrategyInvalid,"review.strategy-invalid"),
        "review.market-stale"=>(TradingReviewQueueStatus.MarketStale,"review.market-stale"),
        _=>(TradingReviewQueueStatus.PolicyBlocked,"review.policy-blocked")
    };
    private static bool ValidRiskReceipt(DeterministicRiskReceipt? receipt,TradingApprovalRequest request,DurableReviewArtifactHashes hashes,DateTimeOffset now)
        =>receipt is not null&&receipt.Approved&&receipt.RevokedAtUtc is null&&!string.IsNullOrWhiteSpace(receipt.ReceiptId)&&receipt.IssuedAtUtc<=now&&receipt.ExpiresAtUtc>now&&receipt.ExpiresAtUtc>receipt.IssuedAtUtc&&receipt.ExpiresAtUtc-receipt.IssuedAtUtc<=TimeSpan.FromMinutes(2)&&string.Equals(receipt.CorrelationId,request.CorrelationId,StringComparison.Ordinal)&&string.Equals(receipt.IntentHash,hashes.IntentHash,StringComparison.Ordinal)&&string.Equals(receipt.ArtifactHash,hashes.ArtifactHash,StringComparison.Ordinal);
    private static TradingReviewProcessorResult Result(bool handled,string code)=>new(handled,code);
}

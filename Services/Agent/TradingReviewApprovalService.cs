using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services;

namespace 币安量化机器人.Services.Agent;

public enum TradingReviewApprovalAction
{
    Approve,
    Reject,
    Revoke
}

/// <summary>The desktop command intentionally has no execution-plan or order fields.</summary>
public sealed record TradingReviewApprovalCommand(string RequestId,TradingReviewApprovalAction Action,string Reason);

public sealed record TrustedTradingReviewContext(
    string UserId,
    string DeviceId,
    string SessionId,
    TradingAuthorizationMode AuthorizationMode,
    bool IsTestnet,
    string ProviderId);

public interface ITrustedTradingReviewContextProvider
{
    ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct);
}

public sealed record TradingReviewApprovalServiceResult(bool Applied,string Code);

/// <summary>
/// Local desktop authorization boundary. It never receives order fields and never invokes a provider or executor.
/// </summary>
public sealed class TradingReviewApprovalService
{
    private readonly AgentSqliteStore _store;
    private readonly ITrustedTradingReviewContextProvider _context;

    public TradingReviewApprovalService(AgentSqliteStore store,ITrustedTradingReviewContextProvider context)
    {
        _store=store??throw new ArgumentNullException(nameof(store));_context=context??throw new ArgumentNullException(nameof(context));
    }

    public async Task<TradingReviewApprovalServiceResult> HandleAsync(TradingReviewApprovalCommand? command,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(command is null||string.IsNullOrWhiteSpace(command.RequestId)||!Enum.IsDefined(command.Action))return Deny("review.command-invalid");
        var reason=SensitiveDataRedactor.ForLog(command.Reason,240);if(string.IsNullOrWhiteSpace(reason))return Deny("review.reason-required");
        TrustedTradingReviewContext? context;
        try{context=await _context.ReadAsync(ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return Deny("review.context-unavailable");}
        if(context is null||string.IsNullOrWhiteSpace(context.UserId)||string.IsNullOrWhiteSpace(context.DeviceId)||string.IsNullOrWhiteSpace(context.SessionId)||string.IsNullOrWhiteSpace(context.ProviderId))return Deny("review.context-invalid");
        if(context.AuthorizationMode!=TradingAuthorizationMode.Review)return Deny("review.mode-not-review");
        if(!context.IsTestnet)return Deny("review.testnet-required");

        var queue=await _store.GetTradingReviewQueueItemAsync(command.RequestId,ct);if(queue is null)return Deny("review.not-found");
        if(!queue.ArtifactValid||queue.Artifact is null)return Deny("review.artifact-invalid");
        if(!string.Equals(queue.Artifact.Environment,"Testnet",StringComparison.Ordinal))return Deny("review.testnet-required");
        if(!string.Equals(queue.Artifact.ProviderId,context.ProviderId,StringComparison.OrdinalIgnoreCase))return Deny("review.provider-mismatch");
        var request=await _store.GetTradingApprovalRequestAsync(command.RequestId,ct);if(request is null)return Deny("review.not-found");
        if(!string.Equals(request.UserId,context.UserId,StringComparison.Ordinal)||!string.Equals(request.DeviceId,context.DeviceId,StringComparison.Ordinal)||!string.Equals(request.SessionId,context.SessionId,StringComparison.Ordinal))return Deny("review.approval-context-mismatch");

        TradingReviewQueueMutationResult result;
        switch(command.Action)
        {
            case TradingReviewApprovalAction.Approve:
                result=await _store.TryApproveTradingReviewWithReceiptAsync(command.RequestId,context.UserId,context.DeviceId,context.SessionId,context.ProviderId,reason,ct);
                break;
            case TradingReviewApprovalAction.Reject:
                if(queue.Status!=TradingReviewQueueStatus.Pending)return Deny("review.not-rejectable");
                result=await _store.TryRejectTradingReviewAsync(command.RequestId,reason,ct);
                break;
            case TradingReviewApprovalAction.Revoke:
                if(queue.Status is not (TradingReviewQueueStatus.Pending or TradingReviewQueueStatus.Approved or TradingReviewQueueStatus.Claimed))return Deny("review.not-revocable");
                result=await _store.TryRevokeTradingReviewAsync(command.RequestId,queue.Status,reason,ct);
                break;
            default:return Deny("review.command-invalid");
        }
        return new(result.Succeeded,result.Code);
    }

    private static TradingReviewApprovalServiceResult Deny(string code)=>new(false,code);
}

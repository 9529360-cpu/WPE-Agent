using 币安量化机器人.Services.Agent;

namespace WpeAgent.TradingAuthorization;

public sealed record TradingReviewWorkerResult(
    bool Eligible,
    bool Handled,
    string Code,
    TradingReviewProcessorResult? Processing = null,
    TradingReviewProcessorResult? Reconciliation = null);

/// <summary>
/// An unhosted, single-step execution worker. Production startup wiring is intentionally owned by a later batch.
/// </summary>
public sealed class TradingReviewExecutionWorker
{
    private static readonly TimeSpan MinimumInterval=TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumInterval=TimeSpan.FromSeconds(5);
    private readonly ITrustedTradingReviewContextProvider _context;
    private readonly Func<string,CancellationToken,Task<TradingReviewProcessorResult>> _processNext;
    private readonly Func<string,CancellationToken,Task<TradingReviewProcessorResult>> _reconcileNext;

    public TradingReviewExecutionWorker(ITrustedTradingReviewContextProvider context,TradingReviewExecutionProcessor processor)
    {
        _context=context??throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(processor);
        _processNext=processor.ProcessNextApprovedAsync;
        _reconcileNext=processor.ReconcileNextAsync;
    }

    internal TradingReviewExecutionWorker(
        ITrustedTradingReviewContextProvider context,
        Func<string,CancellationToken,Task<TradingReviewProcessorResult>> processNext,
        Func<string,CancellationToken,Task<TradingReviewProcessorResult>> reconcileNext)
    {
        _context=context??throw new ArgumentNullException(nameof(context));
        _processNext=processNext??throw new ArgumentNullException(nameof(processNext));
        _reconcileNext=reconcileNext??throw new ArgumentNullException(nameof(reconcileNext));
    }

    public async Task<TradingReviewWorkerResult> RunOnceAsync(string workerId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(string.IsNullOrWhiteSpace(workerId))return Result(false,false,"review.worker-id-invalid");

        TrustedTradingReviewContext? context;
        try{context=await _context.ReadAsync(ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return Result(false,false,"review.worker-context-unavailable");}

        if(context is null||string.IsNullOrWhiteSpace(context.UserId)||string.IsNullOrWhiteSpace(context.DeviceId)||string.IsNullOrWhiteSpace(context.SessionId)||string.IsNullOrWhiteSpace(context.ProviderId))
            return Result(false,false,"review.worker-context-invalid");
        if(context.AuthorizationMode!=TradingAuthorizationMode.Review)
            return Result(false,false,"review.worker-mode-not-review");
        if(!context.IsTestnet)
            return Result(false,false,"review.worker-testnet-required");

        TradingReviewProcessorResult processing;
        try{processing=await _processNext(workerId,ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return await ReconcileAfterFailureAsync(workerId,null,ct);}

        if(processing.Code=="review.no-approved-work")
            return new(true,false,"review.worker-idle",processing);
        if(processing.Code!="review.execution-ambiguous")
            return new(true,processing.Handled,processing.Code,processing);

        return await ReconcileAfterFailureAsync(workerId,processing,ct);
    }

    public async Task RunAsync(string workerId,TimeSpan interval,CancellationToken ct)
    {
        if(interval<MinimumInterval||interval>MaximumInterval)throw new ArgumentOutOfRangeException(nameof(interval));
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            _=await RunOnceAsync(workerId,ct);
            await Task.Delay(interval,ct);
        }
    }

    private async Task<TradingReviewWorkerResult> ReconcileAfterFailureAsync(string workerId,TradingReviewProcessorResult? processing,CancellationToken ct)
    {
        try
        {
            var reconciliation=await _reconcileNext(workerId,ct);
            return new(true,processing?.Handled==true||reconciliation.Handled,reconciliation.Handled?reconciliation.Code:"review.worker-processing-failed",processing,reconciliation);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return new(true,processing?.Handled==true,"review.worker-reconciliation-failed",processing);}
    }

    private static TradingReviewWorkerResult Result(bool eligible,bool handled,string code)=>new(eligible,handled,code);
}

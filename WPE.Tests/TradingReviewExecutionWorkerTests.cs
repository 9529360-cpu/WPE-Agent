using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingReviewExecutionWorkerTests
{
    [Fact]
    public async Task NonReviewContext_IsIneligibleAndDoesNotInvokeProcessor()
    {
        var setup=Setup(Context(TradingAuthorizationMode.Signal,true));

        var result=await setup.Worker.RunOnceAsync("worker",CancellationToken.None);

        Assert.False(result.Eligible);Assert.Equal("review.worker-mode-not-review",result.Code);Assert.Equal(0,setup.ProcessCalls+setup.ReconcileCalls);
    }

    [Fact]
    public async Task NonTestnetContext_IsIneligibleAndDoesNotInvokeProcessor()
    {
        var setup=Setup(Context(TradingAuthorizationMode.Review,false));

        var result=await setup.Worker.RunOnceAsync("worker",CancellationToken.None);

        Assert.False(result.Eligible);Assert.Equal("review.worker-testnet-required",result.Code);Assert.Equal(0,setup.ProcessCalls+setup.ReconcileCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOrUnavailableContext_FailsClosed(bool throws)
    {
        var setup=Setup(throws?Context():null);setup.Context.Throw=throws;

        var result=await setup.Worker.RunOnceAsync("worker",CancellationToken.None);

        Assert.False(result.Eligible);Assert.Equal(throws?"review.worker-context-unavailable":"review.worker-context-invalid",result.Code);Assert.Equal(0,setup.ProcessCalls+setup.ReconcileCalls);
    }

    [Fact]
    public async Task Cancellation_PropagatesBeforeAnyProcessorCall()
    {
        var setup=Setup(Context());using var cancellation=new CancellationTokenSource();cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(()=>setup.Worker.RunOnceAsync("worker",cancellation.Token));

        Assert.Equal(0,setup.ProcessCalls+setup.ReconcileCalls);
    }

    [Fact]
    public async Task EmptyQueue_ReturnsIdleWithoutReconciliation()
    {
        var setup=Setup(Context());setup.ProcessResult=new(false,"review.no-approved-work");

        var result=await setup.Worker.RunOnceAsync("worker",CancellationToken.None);

        Assert.True(result.Eligible);Assert.False(result.Handled);Assert.Equal("review.worker-idle",result.Code);Assert.Equal(1,setup.ProcessCalls);Assert.Equal(0,setup.ReconcileCalls);
    }

    [Fact]
    public async Task SuccessfulProcessing_StopsAfterOneProcessorCall()
    {
        var setup=Setup(Context());setup.ProcessResult=new(true,"review.succeeded");

        var result=await setup.Worker.RunOnceAsync("worker",CancellationToken.None);

        Assert.True(result.Handled);Assert.Equal("review.succeeded",result.Code);Assert.Equal(1,setup.ProcessCalls);Assert.Equal(0,setup.ReconcileCalls);
    }

    [Fact]
    public async Task ProcessingFailure_PerformsOneBoundedReconciliationWithoutRetry()
    {
        var setup=Setup(Context());setup.ProcessException=new InvalidOperationException("failed");setup.ReconcileResult=new(true,"review.reconcile-succeeded");

        var result=await setup.Worker.RunOnceAsync("worker",CancellationToken.None);

        Assert.True(result.Handled);Assert.Equal("review.reconcile-succeeded",result.Code);Assert.Equal(1,setup.ProcessCalls);Assert.Equal(1,setup.ReconcileCalls);Assert.NotNull(result.Reconciliation);
    }

    [Fact]
    public async Task AmbiguousProcessing_PerformsOneBoundedReconciliationWithoutDuplicateExecution()
    {
        var setup=Setup(Context());setup.ProcessResult=new(true,"review.execution-ambiguous");setup.ReconcileResult=new(false,"review.no-reconciliation-work");

        var result=await setup.Worker.RunOnceAsync("worker",CancellationToken.None);

        Assert.True(result.Handled);Assert.Equal("review.worker-processing-failed",result.Code);Assert.Equal(1,setup.ProcessCalls);Assert.Equal(1,setup.ReconcileCalls);
    }

    private static SetupState Setup(TrustedTradingReviewContext? context)
    {
        var state=new SetupState{Context=new ContextProvider{Current=context}};
        state.Worker=new(state.Context,state.ProcessAsync,state.ReconcileAsync);return state;
    }

    private static TrustedTradingReviewContext Context(TradingAuthorizationMode mode=TradingAuthorizationMode.Review,bool testnet=true)
        =>new("user","device","session",mode,testnet,"binance");

    private sealed class SetupState
    {
        public required ContextProvider Context{get;init;}
        public TradingReviewExecutionWorker Worker{get;set;}=null!;
        public TradingReviewProcessorResult ProcessResult{get;set;}=new(false,"review.no-approved-work");
        public TradingReviewProcessorResult ReconcileResult{get;set;}=new(false,"review.no-reconciliation-work");
        public Exception? ProcessException{get;set;}
        public int ProcessCalls{get;private set;}
        public int ReconcileCalls{get;private set;}
        public Task<TradingReviewProcessorResult> ProcessAsync(string workerId,CancellationToken ct){ProcessCalls++;ct.ThrowIfCancellationRequested();return ProcessException is null?Task.FromResult(ProcessResult):Task.FromException<TradingReviewProcessorResult>(ProcessException);}
        public Task<TradingReviewProcessorResult> ReconcileAsync(string workerId,CancellationToken ct){ReconcileCalls++;ct.ThrowIfCancellationRequested();return Task.FromResult(ReconcileResult);}
    }

    private sealed class ContextProvider:ITrustedTradingReviewContextProvider
    {
        public TrustedTradingReviewContext? Current{get;set;}
        public bool Throw{get;set;}
        public ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();if(Throw)throw new InvalidOperationException("unavailable");return ValueTask.FromResult(Current);}
    }
}

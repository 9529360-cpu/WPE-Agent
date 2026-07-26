using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingReviewExecutionProcessorTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,21,8,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-review-processor-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task ApprovedArtifact_ExecutesOnceThroughDurableGatewayAndSucceeds()
    {
        var setup=await Setup("success");await Approve(setup);var result=await setup.Processor.ProcessNextApprovedAsync("worker-1",CancellationToken.None);var item=await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None);var request=await setup.Store.GetTradingApprovalRequestAsync(setup.RequestId,CancellationToken.None);var receipt=await setup.Store.GetTradingApprovalReceiptForRequestAsync(setup.RequestId,CancellationToken.None);

        Assert.True(result.Handled);Assert.Equal("review.succeeded",result.Code);Assert.Equal(TradingReviewQueueStatus.Succeeded,item!.Status);Assert.Equal(1,setup.Executor.MutationCount);Assert.Equal(new[]{"WPE-PROC-1","WPE-PROC-2"},setup.Executor.ClientOrderIds);Assert.NotNull(request!.ConsumedAtUtc);Assert.NotNull(receipt!.Receipt.ConsumedAtUtc);
    }

    [Fact]
    public async Task ConcurrentProcessors_ClaimApprovedArtifactExactlyOnce()
    {
        var setup=await Setup("concurrent");await Approve(setup);var results=await Task.WhenAll(Enumerable.Range(0,24).Select(index=>setup.Processor.ProcessNextApprovedAsync($"worker-{index}",CancellationToken.None)));

        Assert.Equal(1,results.Count(value=>value.Code=="review.succeeded"));Assert.Equal(1,setup.Executor.MutationCount);Assert.Equal(TradingReviewQueueStatus.Succeeded,(await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None))!.Status);
    }

    [Theory]
    [InlineData("review.strategy-invalid",TradingReviewQueueStatus.StrategyInvalid)]
    [InlineData("review.market-stale",TradingReviewQueueStatus.MarketStale)]
    [InlineData("review.mainnet-blocked",TradingReviewQueueStatus.PolicyBlocked)]
    [InlineData("review.provider-mismatch",TradingReviewQueueStatus.PolicyBlocked)]
    [InlineData("review.capability-stale",TradingReviewQueueStatus.PolicyBlocked)]
    public async Task RuntimeValidationFailure_IsTerminalWithoutMutation(string validationCode,TradingReviewQueueStatus expected)
    {
        var setup=await Setup("runtime-"+expected);await Approve(setup);setup.Runtime.Result=new(false,validationCode);

        var result=await setup.Processor.ProcessNextApprovedAsync("worker",CancellationToken.None);

        Assert.True(result.Handled);Assert.Equal(expected,(await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None))!.Status);Assert.Equal(0,setup.Executor.MutationCount);Assert.Equal(0,setup.Risk.CallCount);
    }

    [Fact]
    public async Task InvalidRiskReceipt_BlocksBeforeGatewayMutation()
    {
        var setup=await Setup("risk");await Approve(setup);setup.Risk.InvalidReceipt=true;

        var result=await setup.Processor.ProcessNextApprovedAsync("worker",CancellationToken.None);

        Assert.True(result.Handled);Assert.Equal("review.risk-receipt-invalid",result.Code);Assert.Equal(TradingReviewQueueStatus.PolicyBlocked,(await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None))!.Status);Assert.Equal(0,setup.Executor.MutationCount);
    }

    [Fact]
    public async Task PendingRejectedRevokedExpiredAndTamperedQueues_NeverMutate()
    {
        var pending=await Setup("pending");Assert.Equal("review.no-approved-work",(await pending.Processor.ProcessNextApprovedAsync("worker",CancellationToken.None)).Code);
        var rejected=await Setup("rejected");Assert.True((await rejected.Approvals.HandleAsync(new(rejected.RequestId,TradingReviewApprovalAction.Reject,"reject"),CancellationToken.None)).Applied);Assert.Equal("review.no-approved-work",(await rejected.Processor.ProcessNextApprovedAsync("worker",CancellationToken.None)).Code);
        var revoked=await Setup("revoked");await Approve(revoked);Assert.True((await revoked.Approvals.HandleAsync(new(revoked.RequestId,TradingReviewApprovalAction.Revoke,"revoke"),CancellationToken.None)).Applied);Assert.Equal("review.no-approved-work",(await revoked.Processor.ProcessNextApprovedAsync("worker",CancellationToken.None)).Code);
        var expired=await Setup("expired",Artifact(Now.AddMinutes(-10),Now.AddMinutes(-5)));Assert.Equal("review.no-approved-work",(await expired.Processor.ProcessNextApprovedAsync("worker",CancellationToken.None)).Code);
        var tampered=await Setup("tampered");await Approve(tampered);await TamperHash(tampered.RequestId);var tamperedResult=await tampered.Processor.ProcessNextApprovedAsync("worker",CancellationToken.None);Assert.False(tamperedResult.Handled);Assert.Equal("review.claim-unavailable",tamperedResult.Code);Assert.Equal(TradingReviewQueueStatus.ArtifactInvalid,(await tampered.Store.GetTradingReviewQueueItemAsync(tampered.RequestId,CancellationToken.None))!.Status);
        Assert.Equal(0,pending.Executor.MutationCount+rejected.Executor.MutationCount+revoked.Executor.MutationCount+expired.Executor.MutationCount+tampered.Executor.MutationCount);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("approval")]
    [InlineData("risk")]
    public async Task GatewayRejectsAnyArtifactHashChainMismatchBeforeMutation(string target)
    {
        var setup=await Setup("hash-"+target);await Approve(setup);Assert.True((await setup.Store.TryClaimTradingReviewAsync(setup.RequestId,"worker",TimeSpan.FromMinutes(1),CancellationToken.None)).Claimed);Assert.True((await setup.Store.TryTransitionTradingReviewAsync(setup.RequestId,TradingReviewQueueStatus.Claimed,TradingReviewQueueStatus.Executing,"worker","review.executing",CancellationToken.None)).Succeeded);
        var request=(await setup.Store.GetTradingApprovalRequestAsync(setup.RequestId,CancellationToken.None))!;var approval=(await setup.Store.GetTradingApprovalReceiptForRequestAsync(setup.RequestId,CancellationToken.None))!.Receipt;var hashes=DurableReviewArtifactCanonicalizer.ComputeHashes(setup.Artifact);var risk=setup.Risk.Create(request,hashes);
        if(target=="request")request=request with{ArtifactHash="0".PadLeft(64,'0')};else if(target=="approval")approval=approval with{ArtifactHash="0".PadLeft(64,'0')};else risk=risk with{ArtifactHash="0".PadLeft(64,'0')};

        var result=await setup.Gateway.ExecuteDurableReviewAsync(new(request,approval,risk,setup.Artifact),CancellationToken.None);

        Assert.False(result.Executed);Assert.Equal(0,setup.Executor.MutationCount);Assert.Null((await setup.Store.GetTradingApprovalRequestAsync(setup.RequestId,CancellationToken.None))!.ConsumedAtUtc);
    }

    [Theory]
    [InlineData(DurableReviewReconciliationState.Succeeded,TradingReviewQueueStatus.Succeeded)]
    [InlineData(DurableReviewReconciliationState.NotSubmitted,TradingReviewQueueStatus.FailedTerminal)]
    [InlineData(DurableReviewReconciliationState.Unknown,TradingReviewQueueStatus.PolicyBlocked)]
    [InlineData(DurableReviewReconciliationState.Failed,TradingReviewQueueStatus.FailedTerminal)]
    public async Task ExpiredExecutingLease_ReconcilesWithoutResubmission(DurableReviewReconciliationState state,TradingReviewQueueStatus expected)
    {
        var setup=await Setup("reconcile-"+state);await PrepareExecuting(setup);setup.Reconciler.Result=new(state,"untrusted-code");

        var result=await setup.Processor.ReconcileNextAsync("recovery-worker",CancellationToken.None);

        Assert.True(result.Handled);Assert.Equal(expected,(await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None))!.Status);Assert.Equal(1,setup.Reconciler.CallCount);Assert.Equal(new[]{"WPE-PROC-1","WPE-PROC-2"},setup.Reconciler.ClientOrderIds);Assert.Equal(0,setup.Executor.MutationCount);
    }

    [Fact]
    public async Task ExpiredClaimedLease_IsReclaimedBeforeAnySubmission()
    {
        var setup=await Setup("reclaim");await Approve(setup);Assert.True((await setup.Store.TryClaimTradingReviewAsync(setup.RequestId,"crashed-worker",TimeSpan.FromSeconds(30),CancellationToken.None)).Claimed);await ExpireLease(setup.RequestId);

        var result=await setup.Processor.ProcessNextApprovedAsync("replacement-worker",CancellationToken.None);

        Assert.True(result.Handled);Assert.Equal("review.succeeded",result.Code);Assert.Equal(1,setup.Executor.MutationCount);Assert.Equal(2,(await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None))!.AttemptCount);
    }

    [Fact]
    public void ProcessorCannotMintRiskOrResubmitDuringReconciliation()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));var processor=File.ReadAllText(Path.Combine(root,"Services","Agent","TradingReviewExecutionProcessor.cs"));var executor=File.ReadAllText(Path.Combine(root,"Services","Agent","EvidenceAndExecution.cs"));var reconcileStart=executor.IndexOf("public async Task<DurableReviewReconciliationResult> ReconcileAsync",StringComparison.Ordinal);var reconcileEnd=executor.IndexOf("public async Task<string> ExecuteAsync",reconcileStart,StringComparison.Ordinal);var reconcile=executor[reconcileStart..reconcileEnd];
        Assert.DoesNotContain("new DeterministicRiskReceipt",processor,StringComparison.Ordinal);Assert.DoesNotContain("ExecuteDurableReviewAsync",processor[processor.IndexOf("public async Task<TradingReviewProcessorResult> ReconcileNextAsync",StringComparison.Ordinal)..],StringComparison.Ordinal);foreach(var forbidden in new[]{"PlaceMarketAsync","PlaceLimitAsync","PlaceProtectionAsync","CancelOrderAsync","SetLeverageAsync","SetMarginModeAsync"})Assert.DoesNotContain(forbidden,reconcile,StringComparison.Ordinal);Assert.Contains("FindOrderAsync",reconcile,StringComparison.Ordinal);
    }

    private async Task<SetupResult> Setup(string suffix,DurableReviewExecutionArtifactV1? artifact=null)
    {
        artifact??=Artifact();var store=new AgentSqliteStore(DatabasePath,()=>Now);var hashes=DurableReviewArtifactCanonicalizer.ComputeHashes(artifact);var requestId="request-"+suffix;var request=new TradingApprovalRequest(requestId,TradingAuthorizationMode.Review,"cycle-"+suffix,hashes.IntentHash,"user","device","session",artifact.CreatedAtUtc,artifact.ExpiresAtUtc,ArtifactHash:hashes.ArtifactHash);Assert.True((await store.SaveTradingReviewQueueAsync(request,artifact,CancellationToken.None)).Succeeded);
        var context=new ContextProvider();var approvals=new TradingReviewApprovalService(store,context);var executor=new RecordingExecutor();var gateway=new TradingExecutionGateway(executor,store,()=>Now);var runtime=new RuntimeValidator();var risk=new RiskValidator();var reconciler=new Reconciler();var processor=new TradingReviewExecutionProcessor(store,gateway,runtime,risk,reconciler,()=>Now);return new(requestId,artifact,store,approvals,executor,gateway,runtime,risk,reconciler,processor);
    }
    private static async Task Approve(SetupResult setup){var result=await setup.Approvals.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve,"manual approval"),CancellationToken.None);Assert.True(result.Applied);}
    private async Task PrepareExecuting(SetupResult setup){await Approve(setup);Assert.True((await setup.Store.TryClaimTradingReviewAsync(setup.RequestId,"crashed-worker",TimeSpan.FromSeconds(30),CancellationToken.None)).Claimed);Assert.True((await setup.Store.TryTransitionTradingReviewAsync(setup.RequestId,TradingReviewQueueStatus.Claimed,TradingReviewQueueStatus.Executing,"crashed-worker","review.executing",CancellationToken.None)).Succeeded);await ExpireLease(setup.RequestId);}
    private async Task ExpireLease(string requestId){await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="UPDATE trading_review_execution_queue SET lease_expires_at=$expired WHERE request_id=$request";q.Parameters.AddWithValue("$expired",Now.AddSeconds(-1).ToString("O"));q.Parameters.AddWithValue("$request",requestId);await q.ExecuteNonQueryAsync();}
    private async Task TamperHash(string requestId){await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="UPDATE trading_review_execution_queue SET artifact_hash='bad' WHERE request_id=$request";q.Parameters.AddWithValue("$request",requestId);await q.ExecuteNonQueryAsync();}
    private static DurableReviewExecutionArtifactV1 Artifact(DateTimeOffset? created=null,DateTimeOffset? expires=null){var start=created??Now.AddMinutes(-1);return new(DurableReviewExecutionArtifactV1.Version,[new(0,"BTCUSDT","Long",.01m,false,49_000m,53_000m,"WPE-PROC-1","strategy.entry","OpenLong","Limit",50_000m,50_100m),new(1,"ETHUSDT","Short",.2m,false,2_650m,2_400m,"WPE-PROC-2","strategy.hedge","OpenShort","Market",0m,2_500m)],5,true,"binance","Testnet","strategy-alpha","v1",start.AddSeconds(-10),"book-v4",start,expires??Now.AddMinutes(5));}

    private sealed class ContextProvider:ITrustedTradingReviewContextProvider{public ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct)=>ValueTask.FromResult<TrustedTradingReviewContext?>(new("user","device","session",TradingAuthorizationMode.Review,true,"binance"));}
    private sealed class RuntimeValidator:ITradingReviewRuntimeValidator{public TradingReviewRuntimeValidation Result{get;set;}=new(true,"review.runtime-valid");public int CallCount{get;private set;}public Task<TradingReviewRuntimeValidation> ValidateAsync(TradingApprovalRequest request,DurableReviewExecutionArtifactV1 artifact,CancellationToken ct){CallCount++;return Task.FromResult(Result);}}
    private sealed class RiskValidator:ITradingReviewRiskValidator{public int CallCount{get;private set;}public bool InvalidReceipt{get;set;}public Task<TradingReviewRiskValidation> ValidateAsync(TradingApprovalRequest request,DurableReviewExecutionArtifactV1 artifact,DurableReviewArtifactHashes hashes,CancellationToken ct){CallCount++;return Task.FromResult(new TradingReviewRiskValidation(true,"review.risk-valid",Create(request,hashes)));}public DeterministicRiskReceipt Create(TradingApprovalRequest request,DurableReviewArtifactHashes hashes)=>new("risk-"+request.RequestId,request.CorrelationId,hashes.IntentHash,true,Now,Now.AddMinutes(1),null,InvalidReceipt?"0".PadLeft(64,'0'):hashes.ArtifactHash);}
    private sealed class RecordingExecutor:ITradingMutationExecutor{public bool IsTestnet=>true;public int MutationCount{get;private set;}public string[] ClientOrderIds{get;private set;}=[];public Task<string> ExecutePlanAsync(string correlationId,IReadOnlyList<ExecutionIntent> intents,int leverage,bool isolated,CancellationToken ct){MutationCount++;ClientOrderIds=intents.Select(value=>value.ClientOrderId).ToArray();return Task.FromResult("submitted");}}
    private sealed class Reconciler:IDurableReviewExecutionReconciler{public DurableReviewReconciliationResult Result{get;set;}=new(DurableReviewReconciliationState.Unknown,"unknown");public int CallCount{get;private set;}public string[] ClientOrderIds{get;private set;}=[];public Task<DurableReviewReconciliationResult> ReconcileAsync(DurableReviewExecutionArtifactV1 artifact,CancellationToken ct){CallCount++;ClientOrderIds=artifact.Intents.Select(value=>value.ClientOrderId).ToArray();return Task.FromResult(Result);}}
    private sealed record SetupResult(string RequestId,DurableReviewExecutionArtifactV1 Artifact,AgentSqliteStore Store,TradingReviewApprovalService Approvals,RecordingExecutor Executor,TradingExecutionGateway Gateway,RuntimeValidator Runtime,RiskValidator Risk,Reconciler Reconciler,TradingReviewExecutionProcessor Processor);
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

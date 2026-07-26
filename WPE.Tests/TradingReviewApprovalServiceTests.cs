using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingReviewApprovalServiceTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,21,7,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-review-approval-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public void DesktopCommand_HasOnlyRequestActionAndReason_AndServiceHasNoExecutionDependency()
    {
        Assert.Equal(new[]{"Action","Reason","RequestId"},typeof(TradingReviewApprovalCommand).GetProperties().Select(value=>value.Name).Order().ToArray());
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));var source=File.ReadAllText(Path.Combine(root,"Services","Agent","TradingReviewApprovalService.cs"));
        Assert.DoesNotContain("ExecutionIntent",source,StringComparison.Ordinal);Assert.DoesNotContain("TradingExecutionGateway",source,StringComparison.Ordinal);Assert.DoesNotContain("IExchange",source,StringComparison.Ordinal);Assert.DoesNotContain("ExecutePlan",source,StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_AtomicallyCreatesReceiptTransitionsQueueAndRedactsReason()
    {
        const string secret="sk-private-secret-123456789";var setup=await Setup("approve");

        var result=await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve,$"manual approval api_key={secret}"),CancellationToken.None);var saved=await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None);var receipt=await Receipt(setup.RequestId);var events=await setup.Store.GetTradingReviewQueueEventsAsync(setup.RequestId,100,CancellationToken.None);

        Assert.True(result.Applied);Assert.Equal("review.approved",result.Code);Assert.Equal(TradingReviewQueueStatus.Approved,saved!.Status);Assert.NotNull(receipt);Assert.True(receipt!.Value.Approved);Assert.Equal("user",receipt.Value.UserId);Assert.Equal("device",receipt.Value.DeviceId);Assert.Equal("session",receipt.Value.SessionId);Assert.Equal(2,events.Count);Assert.Equal("review.approved",events[^1].EventCode);Assert.Contains("[REDACTED]",events[^1].Reason,StringComparison.Ordinal);Assert.DoesNotContain(secret,events[^1].Reason,StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TradingAuthorizationMode.Research,true,"binance","review.mode-not-review")]
    [InlineData(TradingAuthorizationMode.Signal,true,"binance","review.mode-not-review")]
    [InlineData(TradingAuthorizationMode.Auto,true,"binance","review.mode-not-review")]
    [InlineData(TradingAuthorizationMode.Review,false,"binance","review.testnet-required")]
    [InlineData(TradingAuthorizationMode.Review,true,"other-provider","review.provider-mismatch")]
    public async Task UntrustedModeEnvironmentOrProvider_CannotApprove(TradingAuthorizationMode mode,bool testnet,string provider,string code)
    {
        var setup=await Setup("context");setup.Context.Current=setup.Context.Current! with{AuthorizationMode=mode,IsTestnet=testnet,ProviderId=provider};

        var result=await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve,"manual review"),CancellationToken.None);

        Assert.False(result.Applied);Assert.Equal(code,result.Code);Assert.Equal(TradingReviewQueueStatus.Pending,(await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None))!.Status);Assert.Equal(0,await ReceiptCount(setup.RequestId));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("device")]
    [InlineData("session")]
    public async Task IdentityMismatch_CannotApprove(string field)
    {
        var setup=await Setup("identity");setup.Context.Current=field switch{"user"=>setup.Context.Current! with{UserId="different"},"device"=>setup.Context.Current! with{DeviceId="different"},_=>setup.Context.Current! with{SessionId="different"}};

        var result=await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve,"manual review"),CancellationToken.None);

        Assert.False(result.Applied);Assert.Equal("review.approval-context-mismatch",result.Code);Assert.Equal(0,await ReceiptCount(setup.RequestId));
    }

    [Fact]
    public async Task Reject_TransitionsPendingWithoutCreatingReceipt()
    {
        var setup=await Setup("reject");var result=await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Reject,"risk no longer accepted"),CancellationToken.None);

        Assert.True(result.Applied);Assert.Equal("review.rejected",result.Code);Assert.Equal(TradingReviewQueueStatus.Rejected,(await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None))!.Status);Assert.Equal(0,await ReceiptCount(setup.RequestId));Assert.Equal("risk no longer accepted",(await setup.Store.GetTradingReviewQueueEventsAsync(setup.RequestId,100,CancellationToken.None))[^1].Reason);
        var repeated=await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Reject,"again"),CancellationToken.None);Assert.False(repeated.Applied);Assert.Equal("review.not-rejectable",repeated.Code);
    }

    [Fact]
    public async Task Revoke_RevokesApprovedQueueRequestAndReceipt()
    {
        var setup=await Setup("revoke");Assert.True((await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve,"initial approval"),CancellationToken.None)).Applied);

        var result=await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Revoke,"operator revoked"),CancellationToken.None);var request=await setup.Store.GetTradingApprovalRequestAsync(setup.RequestId,CancellationToken.None);var receipt=await Receipt(setup.RequestId);

        Assert.True(result.Applied);Assert.Equal("review.revoked",result.Code);Assert.Equal(TradingReviewQueueStatus.Revoked,(await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None))!.Status);Assert.NotNull(request!.RevokedAtUtc);Assert.NotNull(receipt!.Value.RevokedAtUtc);
        var repeated=await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Revoke,"again"),CancellationToken.None);Assert.False(repeated.Applied);Assert.Equal("review.not-revocable",repeated.Code);
    }

    [Fact]
    public async Task ExpiredOrTamperedArtifact_CannotApprove()
    {
        var expired=await Setup("expired",Artifact(Now.AddMinutes(-10),Now.AddMinutes(-5)));var expiredResult=await expired.Service.HandleAsync(new(expired.RequestId,TradingReviewApprovalAction.Approve,"late approval"),CancellationToken.None);
        Assert.False(expiredResult.Applied);Assert.Equal("review.expired",expiredResult.Code);Assert.Equal(TradingReviewQueueStatus.Expired,(await expired.Store.GetTradingReviewQueueItemAsync(expired.RequestId,CancellationToken.None))!.Status);Assert.Equal(0,await ReceiptCount(expired.RequestId));

        var tampered=await Setup("tampered");await using(var c=new SqliteConnection($"Data Source={DatabasePath}")){await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="UPDATE trading_review_execution_queue SET artifact_hash='bad' WHERE request_id=$request";q.Parameters.AddWithValue("$request",tampered.RequestId);await q.ExecuteNonQueryAsync();}
        var tamperedResult=await tampered.Service.HandleAsync(new(tampered.RequestId,TradingReviewApprovalAction.Approve,"manual review"),CancellationToken.None);Assert.False(tamperedResult.Applied);Assert.Equal("review.artifact-invalid",tamperedResult.Code);Assert.Equal(0,await ReceiptCount(tampered.RequestId));
    }

    [Fact]
    public async Task ConcurrentApprove_AllowsOneReceiptAndOneApprovedEvent()
    {
        var setup=await Setup("concurrent");var results=await Task.WhenAll(Enumerable.Range(0,24).Select(index=>setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve,$"approval {index}"),CancellationToken.None)));

        Assert.Equal(1,results.Count(value=>value.Applied));Assert.Equal(1,await ReceiptCount(setup.RequestId));var events=await setup.Store.GetTradingReviewQueueEventsAsync(setup.RequestId,100,CancellationToken.None);Assert.Equal(1,events.Count(value=>value.ToStatus==TradingReviewQueueStatus.Approved));Assert.Equal(TradingReviewQueueStatus.Approved,(await setup.Store.GetTradingReviewQueueItemAsync(setup.RequestId,CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task InvalidUnknownUnavailableAndCancelledCommands_FailClosed()
    {
        var setup=await Setup("invalid");Assert.Equal("review.reason-required",(await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve," "),CancellationToken.None)).Code);Assert.Equal("review.command-invalid",(await setup.Service.HandleAsync(new(setup.RequestId,(TradingReviewApprovalAction)99,"reason"),CancellationToken.None)).Code);Assert.Equal("review.not-found",(await setup.Service.HandleAsync(new("missing",TradingReviewApprovalAction.Approve,"reason"),CancellationToken.None)).Code);
        setup.Context.Throw=true;Assert.Equal("review.context-unavailable",(await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve,"reason"),CancellationToken.None)).Code);setup.Context.Throw=false;setup.Context.Current=null;Assert.Equal("review.context-invalid",(await setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve,"reason"),CancellationToken.None)).Code);
        using var cts=new CancellationTokenSource();cts.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>setup.Service.HandleAsync(new(setup.RequestId,TradingReviewApprovalAction.Approve,"reason"),cts.Token));Assert.Equal(0,await ReceiptCount(setup.RequestId));
    }

    private async Task<SetupResult> Setup(string suffix,DurableReviewExecutionArtifactV1? artifact=null)
    {
        artifact??=Artifact();var id="request-"+suffix;var store=new AgentSqliteStore(DatabasePath,()=>Now);var hash=DurableReviewArtifactCanonicalizer.ComputeHashes(artifact).IntentHash;var request=new TradingApprovalRequest(id,TradingAuthorizationMode.Review,"cycle-"+suffix,hash,"user","device","session",artifact.CreatedAtUtc,artifact.ExpiresAtUtc);Assert.True((await store.SaveTradingReviewQueueAsync(request,artifact,CancellationToken.None)).Succeeded);var context=new ContextProvider{Current=new("user","device","session",TradingAuthorizationMode.Review,true,"binance")};return new(id,store,context,new(store,context));
    }
    private static DurableReviewExecutionArtifactV1 Artifact(DateTimeOffset? created=null,DateTimeOffset? expires=null){var start=created??Now.AddMinutes(-1);return new(DurableReviewExecutionArtifactV1.Version,[new(0,"BTCUSDT","Long",.01m,false,49_000m,53_000m,"WPE-APPROVAL-1","strategy.entry","OpenLong","Limit",50_000m,50_100m)],5,true,"binance","Testnet","strategy-alpha","v1",start.AddSeconds(-10),"book-v4",start,expires??Now.AddMinutes(5));}
    private async Task<int> ReceiptCount(string requestId){await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM trading_approval_receipts WHERE request_id=$request";q.Parameters.AddWithValue("$request",requestId);return Convert.ToInt32(await q.ExecuteScalarAsync());}
    private async Task<(bool Approved,string UserId,string DeviceId,string SessionId,DateTimeOffset? RevokedAtUtc)?> Receipt(string requestId){await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="SELECT approved,user_id,device_id,session_id,revoked_at FROM trading_approval_receipts WHERE request_id=$request";q.Parameters.AddWithValue("$request",requestId);await using var r=await q.ExecuteReaderAsync();if(!await r.ReadAsync())return null;return(r.GetInt32(0)==1,r.GetString(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:DateTimeOffset.Parse(r.GetString(4)));}
    private sealed class ContextProvider:ITrustedTradingReviewContextProvider{public TrustedTradingReviewContext? Current{get;set;}public bool Throw{get;set;}public ValueTask<TrustedTradingReviewContext?> ReadAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();if(Throw)throw new InvalidOperationException("context unavailable");return ValueTask.FromResult(Current);}}
    private sealed record SetupResult(string RequestId,AgentSqliteStore Store,ContextProvider Context,TradingReviewApprovalService Service);
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

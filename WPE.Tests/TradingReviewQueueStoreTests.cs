using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TradingReviewQueueStoreTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,21,6,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-review-queue-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public void CanonicalHashes_BindIntentOrderAndRejectNonCanonicalJson()
    {
        var artifact=Artifact();var hashes=DurableReviewArtifactCanonicalizer.ComputeHashes(artifact);var reordered=artifact with{Intents=artifact.Intents.Reverse().Select((value,index)=>value with{Sequence=index}).ToArray()};

        Assert.Equal(64,hashes.ArtifactHash.Length);Assert.Equal(64,hashes.IntentHash.Length);
        Assert.NotEqual(hashes.ArtifactHash,DurableReviewArtifactCanonicalizer.ComputeHashes(reordered).ArtifactHash);
        Assert.NotEqual(hashes.IntentHash,DurableReviewArtifactCanonicalizer.ComputeHashes(reordered).IntentHash);
        var canonical=DurableReviewArtifactCanonicalizer.Serialize(artifact);byte[] nonCanonical=[.. canonical,(byte)' '];
        Assert.False(DurableReviewArtifactCanonicalizer.TryDeserializeCanonical(nonCanonical,out _,out var code));Assert.Equal("review.artifact-invalid",code);
    }

    [Fact]
    public async Task RequestArtifactAndInitialEvent_AreAtomicAndSurviveRestart()
    {
        var artifact=Artifact();var request=Request("request-restart",artifact);var store=Store();

        var result=await store.SaveTradingReviewQueueAsync(request,artifact,CancellationToken.None);SqliteConnection.ClearAllPools();var reopened=Store();var saved=await reopened.GetTradingReviewQueueItemAsync(request.RequestId,CancellationToken.None);var events=await reopened.GetTradingReviewQueueEventsAsync(request.RequestId,100,CancellationToken.None);

        Assert.True(result.Succeeded);Assert.NotNull(saved);Assert.True(saved!.ArtifactValid);Assert.Equal(TradingReviewQueueStatus.Pending,saved.Status);Assert.Equal(DurableReviewArtifactCanonicalizer.Serialize(artifact),saved.CanonicalArtifactBytes);Assert.Equal(DurableReviewArtifactCanonicalizer.ComputeHashes(artifact).ArtifactHash,saved.ArtifactHash);Assert.Equal(DurableReviewArtifactCanonicalizer.ComputeHashes(artifact).IntentHash,saved.IntentHash);
        var initial=Assert.Single(events);Assert.Equal(1,initial.Sequence);Assert.Null(initial.FromStatus);Assert.Equal(TradingReviewQueueStatus.Pending,initial.ToStatus);Assert.Equal("review.pending",initial.EventCode);
    }

    [Fact]
    public async Task InvalidArtifact_DoesNotPartiallyPersistRequest()
    {
        var artifact=Artifact() with{Intents=[Artifact().Intents[0] with{Sequence=4}]};var request=new TradingApprovalRequest("request-invalid",TradingAuthorizationMode.Review,"cycle-invalid","0".PadLeft(64,'0'),"user","device","session",artifact.CreatedAtUtc,artifact.ExpiresAtUtc);var store=Store();

        var result=await store.SaveTradingReviewQueueAsync(request,artifact,CancellationToken.None);

        Assert.False(result.Succeeded);Assert.Equal("review.artifact-invalid",result.Code);Assert.Equal(0,await Count("trading_approval_requests"));Assert.Equal(0,await Count("trading_review_execution_queue"));Assert.Equal(0,await Count("trading_review_queue_events"));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("artifact-hash")]
    [InlineData("intent-hash")]
    [InlineData("metadata")]
    public async Task TamperedArtifact_IsWithheldWithNonSensitiveDiagnostic(string mutation)
    {
        const string secret="private-secret-payload";var artifact=Artifact();var request=Request("request-tamper",artifact);var store=Store();Assert.True((await store.SaveTradingReviewQueueAsync(request,artifact,CancellationToken.None)).Succeeded);
        await using(var c=new SqliteConnection($"Data Source={DatabasePath}")){await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=mutation switch{"bytes"=>"UPDATE trading_review_execution_queue SET artifact_bytes=$value WHERE request_id=$request","artifact-hash"=>"UPDATE trading_review_execution_queue SET artifact_hash=$value WHERE request_id=$request","intent-hash"=>"UPDATE trading_review_execution_queue SET intent_hash=$value WHERE request_id=$request",_=>"UPDATE trading_review_execution_queue SET provider_id=$value WHERE request_id=$request"};q.Parameters.AddWithValue("$value",mutation=="bytes"?System.Text.Encoding.UTF8.GetBytes(secret):(object)secret);q.Parameters.AddWithValue("$request",request.RequestId);await q.ExecuteNonQueryAsync();}

        var loaded=await store.GetTradingReviewQueueItemAsync(request.RequestId,CancellationToken.None);

        Assert.NotNull(loaded);Assert.False(loaded!.ArtifactValid);Assert.Null(loaded.Artifact);Assert.Null(loaded.CanonicalArtifactBytes);Assert.Empty(loaded.ArtifactHash);Assert.Empty(loaded.IntentHash);Assert.Equal("review.artifact-invalid",loaded.DiagnosticCode);Assert.DoesNotContain(secret,loaded.DiagnosticCode,StringComparison.Ordinal);
        var approval=await store.TryApproveTradingReviewAsync(request.RequestId,CancellationToken.None);var claim=await store.TryClaimTradingReviewAsync(request.RequestId,"worker",TimeSpan.FromMinutes(1),CancellationToken.None);Assert.False(approval.Succeeded);Assert.Equal("review.artifact-invalid",approval.Code);Assert.False(claim.Claimed);Assert.Equal("review.artifact-invalid",claim.Code);Assert.Equal(TradingReviewQueueStatus.Pending,(await store.GetTradingReviewQueueItemAsync(request.RequestId,CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ConcurrentApproveReject_AllowsExactlyOneWinnerAndOneTransitionEvent()
    {
        var store=Store();var artifact=Artifact();var request=Request("request-decision",artifact);Assert.True((await store.SaveTradingReviewQueueAsync(request,artifact,CancellationToken.None)).Succeeded);

        Assert.False((await store.TryTransitionTradingReviewAsync(request.RequestId,TradingReviewQueueStatus.Pending,TradingReviewQueueStatus.Approved,null,"review.processor-approve",CancellationToken.None)).Succeeded);

        var results=await Task.WhenAll(Enumerable.Range(0,24).Select(index=>index%2==0?store.TryApproveTradingReviewAsync(request.RequestId,CancellationToken.None):store.TryRejectTradingReviewAsync(request.RequestId,CancellationToken.None)));

        Assert.Equal(1,results.Count(value=>value.Succeeded));var saved=await store.GetTradingReviewQueueItemAsync(request.RequestId,CancellationToken.None);Assert.Contains(saved!.Status,new[]{TradingReviewQueueStatus.Approved,TradingReviewQueueStatus.Rejected});Assert.Equal(2,(await store.GetTradingReviewQueueEventsAsync(request.RequestId,100,CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ClaimLeaseAndProcessorTransitions_AreConditionalAndCountAttempts()
    {
        var store=Store();var artifact=Artifact();var request=Request("request-claim",artifact);Assert.True((await store.SaveTradingReviewQueueAsync(request,artifact,CancellationToken.None)).Succeeded);Assert.True((await store.TryApproveTradingReviewAsync(request.RequestId,CancellationToken.None)).Succeeded);

        var claims=await Task.WhenAll(Enumerable.Range(0,16).Select(index=>store.TryClaimTradingReviewAsync(request.RequestId,$"worker-{index}",TimeSpan.FromMinutes(1),CancellationToken.None)));var winner=Assert.Single(claims,value=>value.Claimed);var saved=await store.GetTradingReviewQueueItemAsync(request.RequestId,CancellationToken.None);

        Assert.Equal(1,winner.AttemptCount);Assert.Equal(TradingReviewQueueStatus.Claimed,saved!.Status);Assert.Equal(1,saved.AttemptCount);Assert.NotNull(saved.LeaseOwner);Assert.False((await store.TryTransitionTradingReviewAsync(request.RequestId,TradingReviewQueueStatus.Claimed,TradingReviewQueueStatus.Executing,"wrong-worker","review.executing",CancellationToken.None)).Succeeded);Assert.True((await store.TryTransitionTradingReviewAsync(request.RequestId,TradingReviewQueueStatus.Claimed,TradingReviewQueueStatus.Executing,saved.LeaseOwner,"review.executing",CancellationToken.None)).Succeeded);Assert.True((await store.TryTransitionTradingReviewAsync(request.RequestId,TradingReviewQueueStatus.Executing,TradingReviewQueueStatus.Reconciling,saved.LeaseOwner,"review.reconciling",CancellationToken.None)).Succeeded);Assert.True((await store.TryTransitionTradingReviewAsync(request.RequestId,TradingReviewQueueStatus.Reconciling,TradingReviewQueueStatus.Succeeded,saved.LeaseOwner,"review.succeeded",CancellationToken.None)).Succeeded);
        Assert.False((await store.TryTransitionTradingReviewAsync(request.RequestId,TradingReviewQueueStatus.Succeeded,TradingReviewQueueStatus.Executing,saved.LeaseOwner,"review.replay",CancellationToken.None)).Succeeded);Assert.Equal(TradingReviewQueueStatus.Succeeded,(await store.GetTradingReviewQueueItemAsync(request.RequestId,CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ExpireAndRevoke_ArePersistedAsTerminalEvents()
    {
        var store=Store();var expiredArtifact=Artifact(Now.AddMinutes(-10),Now.AddMinutes(-5));var expired=Request("request-expired",expiredArtifact);Assert.True((await store.SaveTradingReviewQueueAsync(expired,expiredArtifact,CancellationToken.None)).Succeeded);Assert.True((await store.TryExpireTradingReviewAsync(expired.RequestId,TradingReviewQueueStatus.Pending,CancellationToken.None)).Succeeded);
        var activeArtifact=Artifact();var revoked=Request("request-revoked",activeArtifact);Assert.True((await store.SaveTradingReviewQueueAsync(revoked,activeArtifact,CancellationToken.None)).Succeeded);Assert.True((await store.TryApproveTradingReviewAsync(revoked.RequestId,CancellationToken.None)).Succeeded);Assert.True((await store.TryRevokeTradingReviewAsync(revoked.RequestId,TradingReviewQueueStatus.Approved,CancellationToken.None)).Succeeded);

        Assert.Equal(TradingReviewQueueStatus.Expired,(await store.GetTradingReviewQueueItemAsync(expired.RequestId,CancellationToken.None))!.Status);Assert.Equal(TradingReviewQueueStatus.Revoked,(await store.GetTradingReviewQueueItemAsync(revoked.RequestId,CancellationToken.None))!.Status);Assert.NotNull((await store.GetTradingApprovalRequestAsync(revoked.RequestId,CancellationToken.None))!.RevokedAtUtc);
    }

    [Fact]
    public async Task QueueQuery_IsBoundedAndEventsAreAppendOnly()
    {
        var store=Store();for(var index=0;index<105;index++){var artifact=Artifact().WithTimes(Now.AddMinutes(-1).AddTicks(index),Now.AddMinutes(5).AddTicks(index));Assert.True((await store.SaveTradingReviewQueueAsync(Request($"request-{index:D3}",artifact),artifact,CancellationToken.None)).Succeeded);}

        Assert.Equal(100,(await store.GetTradingReviewQueueAsync(null,500,0,CancellationToken.None)).Count);Assert.Equal(5,(await store.GetTradingReviewQueueAsync(TradingReviewQueueStatus.Pending,5,50,CancellationToken.None)).Count);
        await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var update=c.CreateCommand();update.CommandText="UPDATE trading_review_queue_events SET event_code='changed' WHERE request_id='request-000'";await Assert.ThrowsAsync<SqliteException>(()=>update.ExecuteNonQueryAsync());await using var delete=c.CreateCommand();delete.CommandText="DELETE FROM trading_review_queue_events WHERE request_id='request-000'";await Assert.ThrowsAsync<SqliteException>(()=>delete.ExecuteNonQueryAsync());
    }

    private AgentSqliteStore Store()=>new(DatabasePath,()=>Now);
    private static TradingApprovalRequest Request(string id,DurableReviewExecutionArtifactV1 artifact){var hash=DurableReviewArtifactCanonicalizer.ComputeHashes(artifact).IntentHash;return new(id,TradingAuthorizationMode.Review,"cycle-"+id,hash,"user","device","session",artifact.CreatedAtUtc,artifact.ExpiresAtUtc);}
    private static DurableReviewExecutionArtifactV1 Artifact(DateTimeOffset? created=null,DateTimeOffset? expires=null)
    {
        var createdAt=created??Now.AddMinutes(-1);var expiresAt=expires??Now.AddMinutes(5);
        return new(DurableReviewExecutionArtifactV1.Version,[
            new(0,"BTCUSDT","Long",.01m,false,49_000m,53_000m,"WPE-REVIEW-OPEN-1","strategy.entry","OpenLong","Limit",50_000m,50_100m),
            new(1,"ETHUSDT","Short",.2m,false,2_650m,2_400m,"WPE-REVIEW-OPEN-2","strategy.hedge","OpenShort","Market",0m,2_500m)],
            5,true,"binance","Testnet","strategy-alpha","v1.2.3",createdAt.AddSeconds(-10),"book-v4",createdAt,expiresAt);
    }
    private async Task<int> Count(string table){await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=$"SELECT COUNT(*) FROM {table}";return Convert.ToInt32(await q.ExecuteScalarAsync());}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

file static class DurableReviewArtifactTestExtensions
{
    public static DurableReviewExecutionArtifactV1 WithTimes(this DurableReviewExecutionArtifactV1 value,DateTimeOffset created,DateTimeOffset expires)
        =>value with{MarketCollectedAtUtc=created.AddSeconds(-10),CreatedAtUtc=created,ExpiresAtUtc=expires};
}

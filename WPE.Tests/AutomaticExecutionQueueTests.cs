using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class AutomaticExecutionQueueTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-automatic-"+Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock=new(new(2026,7,22,3,0,0,TimeSpan.Zero));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public void V2CanonicalArtifact_RoundTripsAndRejectsTampering()
    {
        var artifact=Artifact();var bytes=DurableExecutionArtifactCanonicalizerV2.Serialize(artifact);var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        Assert.True(DurableExecutionArtifactCanonicalizerV2.TryDeserializeCanonical(bytes,out var restored,out var code));Assert.Equal("automatic.artifact-valid",code);Assert.NotNull(restored);Assert.Equal(bytes,DurableExecutionArtifactCanonicalizerV2.Serialize(restored!));Assert.Equal(artifact.Intents.ToArray(),restored!.Intents.ToArray());Assert.Equal(64,hashes.ArtifactHash.Length);Assert.Equal(64,hashes.IntentHash.Length);
        Assert.False(DurableExecutionArtifactCanonicalizerV2.TryDeserializeCanonical([..bytes,(byte)' '],out _,out _));
    }

    [Fact]
    public async Task Queue_PersistsRiskDecisionAndOnlyRiskApprovedCanClaim()
    {
        var store=Store();var artifact=Artifact();Assert.True((await store.SaveAutomaticExecutionAsync("execution-approved",artifact,CancellationToken.None)).Succeeded);
        Assert.False((await store.TryClaimAutomaticExecutionAsync("execution-approved","worker",TimeSpan.FromSeconds(30),CancellationToken.None)).Claimed);
        Assert.True((await store.RecordAutomaticRiskDecisionAsync("execution-approved",Receipt(artifact),CancellationToken.None)).Succeeded);
        var claims=await Task.WhenAll(Enumerable.Range(0,16).Select(i=>store.TryClaimAutomaticExecutionAsync("execution-approved","worker-"+i,TimeSpan.FromSeconds(30),CancellationToken.None)));
        Assert.Single(claims,x=>x.Claimed);Assert.Equal(AutomaticExecutionQueueStatus.Claimed,(await store.GetAutomaticExecutionAsync("execution-approved",CancellationToken.None))!.Status);

        var blocked=Artifact("blocked");Assert.True((await store.SaveAutomaticExecutionAsync("execution-blocked",blocked,CancellationToken.None)).Succeeded);Assert.True((await store.RecordAutomaticRiskDecisionAsync("execution-blocked",Receipt(blocked) with{Approved=false},CancellationToken.None)).Succeeded);
        Assert.Equal(AutomaticExecutionQueueStatus.RiskBlocked,(await store.GetAutomaticExecutionAsync("execution-blocked",CancellationToken.None))!.Status);Assert.False((await store.TryClaimAutomaticExecutionAsync("execution-blocked","worker",TimeSpan.FromSeconds(30),CancellationToken.None)).Claimed);
    }

    [Fact]
    public async Task TamperedArtifactOrReceipt_IsNeverClaimedAndEventsAreAppendOnly()
    {
        var store=Store();var artifact=Artifact();await Approve(store,"execution-tamper",artifact);
        await using(var c=new SqliteConnection($"Data Source={DatabasePath}")){await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="UPDATE automatic_execution_queue SET artifact_hash='bad' WHERE execution_id='execution-tamper'";await q.ExecuteNonQueryAsync();}
        Assert.False((await store.TryClaimAutomaticExecutionAsync("execution-tamper","worker",TimeSpan.FromSeconds(30),CancellationToken.None)).Claimed);
        await using(var c=new SqliteConnection($"Data Source={DatabasePath}")){await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="DELETE FROM automatic_execution_events WHERE execution_id='execution-tamper'";await Assert.ThrowsAsync<SqliteException>(()=>q.ExecuteNonQueryAsync());}
    }

    [Fact]
    public void V2Artifact_HasNoHumanDecisionSemantics()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));var contracts=File.ReadAllText(Path.Combine(root,"Core","Contracts","TradingAuthorizationContracts.cs"));var start=contracts.IndexOf("public sealed record DurableExecutionArtifactV2",StringComparison.Ordinal);var end=contracts.IndexOf("public sealed record DurableExecutionArtifactHashesV2",start,StringComparison.Ordinal);var v2=contracts[start..end];
        Assert.DoesNotContain("Approval",v2,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("Review",v2,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("User",v2,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyPendingAndApprovedRows_AreInvisibleToAutomaticClaim()
    {
        var store=Store();var legacyArtifact=new DurableReviewExecutionArtifactV1(1,Artifact().Intents,5,true,"binance","Testnet","strategy-alpha","v1",_clock.Now.AddSeconds(-20),"book-v4",_clock.Now.AddSeconds(-10),_clock.Now.AddMinutes(5));var hashes=DurableReviewArtifactCanonicalizer.ComputeHashes(legacyArtifact);
        var pending=new TradingApprovalRequest("legacy-pending",TradingAuthorizationMode.Review,"legacy-cycle-1",hashes.IntentHash,"user","device","session",legacyArtifact.CreatedAtUtc,legacyArtifact.ExpiresAtUtc);Assert.True((await store.SaveTradingReviewQueueAsync(pending,legacyArtifact,CancellationToken.None)).Succeeded);
        var approved= pending with{RequestId="legacy-approved",CorrelationId="legacy-cycle-2"};Assert.True((await store.SaveTradingReviewQueueAsync(approved,legacyArtifact,CancellationToken.None)).Succeeded);Assert.True((await store.TryApproveTradingReviewAsync(approved.RequestId,CancellationToken.None)).Succeeded);
        Assert.False((await store.TryClaimAutomaticExecutionAsync(pending.RequestId,"automatic-worker",TimeSpan.FromSeconds(30),CancellationToken.None)).Claimed);Assert.False((await store.TryClaimAutomaticExecutionAsync(approved.RequestId,"automatic-worker",TimeSpan.FromSeconds(30),CancellationToken.None)).Claimed);Assert.Empty(await store.GetAutomaticExecutionQueueAsync(AutomaticExecutionQueueStatus.RiskApproved,100,CancellationToken.None));
    }

    [Fact]
    public async Task Initialization_PreservesLegacyRowsAndTables()
    {
        Directory.CreateDirectory(_directory);await using(var c=new SqliteConnection($"Data Source={DatabasePath}")){await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="CREATE TABLE trading_approval_requests(request_id TEXT PRIMARY KEY,mode TEXT NOT NULL,correlation_id TEXT NOT NULL,intent_hash TEXT NOT NULL,user_id TEXT NOT NULL,device_id TEXT NOT NULL,session_id TEXT NOT NULL,issued_at TEXT NOT NULL,expires_at TEXT NOT NULL,revoked_at TEXT,consumed_at TEXT); INSERT INTO trading_approval_requests VALUES('legacy','Review','c','h','u','d','s','2026-01-01Z','2027-01-01Z',NULL,NULL)";await q.ExecuteNonQueryAsync();}
        _=Store();await using var reopened=new SqliteConnection($"Data Source={DatabasePath}");await reopened.OpenAsync();await using var count=reopened.CreateCommand();count.CommandText="SELECT COUNT(*) FROM trading_approval_requests WHERE request_id='legacy'";Assert.Equal(1,Convert.ToInt32(await count.ExecuteScalarAsync()));await using var tables=reopened.CreateCommand();tables.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('trading_review_execution_queue','automatic_execution_queue','automatic_execution_events')";Assert.Equal(3,Convert.ToInt32(await tables.ExecuteScalarAsync()));
    }

    private AgentSqliteStore Store()=>new(DatabasePath,()=>_clock.Now);
    private async Task Approve(AgentSqliteStore store,string id,DurableExecutionArtifactV2 artifact){Assert.True((await store.SaveAutomaticExecutionAsync(id,artifact,CancellationToken.None)).Succeeded);Assert.True((await store.RecordAutomaticRiskDecisionAsync(id,Receipt(artifact),CancellationToken.None)).Succeeded);}
    private DurableExecutionArtifactV2 Artifact(string suffix="base")=>new(2,"cycle-"+suffix,[new(0,"BTCUSDT","Long",.01m,false,49_000m,53_000m,"WPE-AUTO-"+suffix.ToUpperInvariant(),"strategy.entry","OpenLong","Limit",50_000m,50_100m)],5,true,"binance","Testnet","strategy-alpha","v2",_clock.Now.AddSeconds(-20),"book-v5",_clock.Now.AddSeconds(-10),_clock.Now.AddMinutes(5));
    private DeterministicRiskReceipt Receipt(DurableExecutionArtifactV2 artifact){var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);return new("risk-"+artifact.CorrelationId,artifact.CorrelationId,hashes.IntentHash,true,_clock.Now,_clock.Now.AddMinutes(1),null,hashes.ArtifactHash);}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
    private sealed class MutableClock(DateTimeOffset now){public DateTimeOffset Now{get;set;}=now;}
}

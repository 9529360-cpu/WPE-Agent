using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class RuntimeAuthorizationProjectionTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,21,5,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-runtime-authorization-"+Guid.NewGuid().ToString("N"));
    private string SettingsPath=>Path.Combine(_directory,"settings.json");
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task Snapshot_ProjectsAllowlistedApprovalArtifactWithoutSensitiveAuthorizationContext()
    {
        var settings=Settings();var database=Database();var loaded=settings.Load();
        const string correlation="private-correlation",requestId="private-request",user="private-user",device="private-device",session="private-session",hash="private-intent-hash",receiptId="private-receipt",secret="sk-private-secret-token-123456";
        var decision=new DecisionPlan{Action=DecisionAction.OpenLong,Instrument="SOLUSDT",OrderType=ExecutionOrderType.Limit,EntryPrice=145.25m,StopLossPrice=140m,TakeProfitPrice=156m,Reason="api_key="+secret};
        var risk=new IndependentRiskReview{Approved=true,PlannedQuantity=2.5m,Summary="token="+secret};
        await PersistArtifact(database,correlation,decision,risk);
        var request=new TradingApprovalRequest(requestId,TradingAuthorizationMode.Review,correlation,hash,user,device,session,Now.AddMinutes(-1),Now.AddMinutes(4));
        Assert.True((await database.SaveTradingApprovalRequestAsync(request,CancellationToken.None)).Succeeded);
        Assert.True((await database.SaveTradingApprovalReceiptAsync(requestId,new(receiptId,correlation,hash,user,device,session,true,Now.AddSeconds(-30),Now.AddMinutes(3)),CancellationToken.None)).Succeeded);

        var state=new RuntimeAuthorizationStateStore(settings,database,()=>Now).Read();
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,authorizationState:state);
        var item=Assert.Single(snapshot.PendingApprovals.Items);

        Assert.Equal(RuntimeCollectionState.Available,snapshot.AuthorizationMode.State);Assert.Equal("Review",snapshot.AuthorizationMode.Value!.Mode);
        Assert.StartsWith("approval#",item.ApprovalId,StringComparison.Ordinal);Assert.DoesNotContain(requestId,item.ApprovalId,StringComparison.Ordinal);
        Assert.Equal("SOLUSDT",item.Symbol);Assert.Equal("Long",item.Side);Assert.Equal("Limit",item.OrderType);Assert.Equal(2.5m,item.Quantity);Assert.Equal(145.25m,item.EntryPrice);Assert.Equal(140m,item.StopLoss);Assert.Equal(156m,item.TakeProfit);Assert.Equal("Pending",item.Status);Assert.Equal("approval.historical-read-only",item.ReasonCode);

        var options=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase};options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));var json=JsonSerializer.Serialize(snapshot,options);
        foreach(var forbidden in new[]{correlation,requestId,user,device,session,hash,receiptId,secret,"userId","deviceId","sessionId","intentHash","approvalReceipt","encrypted","destination","payload"})Assert.DoesNotContain(forbidden,json,StringComparison.OrdinalIgnoreCase);
        using var document=JsonDocument.Parse(JsonSerializer.Serialize(item,options));var fields=document.RootElement.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.True(fields.SetEquals(["approvalId","symbol","side","orderType","quantity","entryPrice","stopLoss","takeProfit","createdAtUtc","expiresAtUtc","status","reasonCode"]));
    }

    [Fact]
    public async Task ExpiredAndArtifactUnavailableRequestsRemainTruthful()
    {
        var database=Database();var settings=Settings();settings.Save(settings.Load());
        var expired=new TradingApprovalRequest("expired-request",TradingAuthorizationMode.Review,"missing-cycle","hash","user","device","session",Now.AddMinutes(-10),Now.AddMinutes(-1));
        Assert.True((await database.SaveTradingApprovalRequestAsync(expired,CancellationToken.None)).Succeeded);

        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,authorizationState:new RuntimeAuthorizationStateStore(settings,database,()=>Now).Read());
        var item=Assert.Single(snapshot.PendingApprovals.Items);

        Assert.Equal("Expired",item.Status);Assert.Equal("approval.expired",item.ReasonCode);Assert.Null(item.Symbol);Assert.Null(item.Side);Assert.Null(item.OrderType);Assert.Null(item.Quantity);Assert.Null(item.EntryPrice);Assert.Null(item.StopLoss);Assert.Null(item.TakeProfit);
    }

    [Fact]
    public async Task InvalidArtifactContent_IsWithheldInsteadOfProjected()
    {
        const string secret="sk-invalid-symbol-secret-123456";var database=Database();var settings=Settings();settings.Save(settings.Load());
        var decision=new DecisionPlan{Action=DecisionAction.OpenLong,Instrument=secret,OrderType=ExecutionOrderType.Market,EntryPrice=10m,StopLossPrice=9m,TakeProfitPrice=12m};var risk=new IndependentRiskReview{Approved=true,PlannedQuantity=1m};
        await PersistArtifact(database,"bad-cycle",decision,risk);Assert.True((await database.SaveTradingApprovalRequestAsync(new("bad-request",TradingAuthorizationMode.Review,"bad-cycle","hash","user","device","session",Now.AddMinutes(-1),Now.AddMinutes(4)),CancellationToken.None)).Succeeded);

        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,authorizationState:new RuntimeAuthorizationStateStore(settings,database,()=>Now).Read());var item=Assert.Single(snapshot.PendingApprovals.Items);var json=JsonSerializer.Serialize(snapshot);

        Assert.Equal("ArtifactUnavailable",item.Status);Assert.Equal("approval.artifact-unavailable",item.ReasonCode);Assert.Null(item.Symbol);Assert.Null(item.Quantity);Assert.DoesNotContain(secret,json,StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticExecutionProjectionMasksExecutionIdentityBeforeSnapshot()
    {
        var database=Database();var settings=Settings();settings.Save(settings.Load());
        const string rawExecutionId="execution-private-runtime-id";
        var artifact=new DurableExecutionArtifactV2(
            2,
            "cycle-runtime-mask",
            [new(0,"BTCUSDT","Long",.01m,false,49_000m,53_000m,"WPE-AUTO-MASK","strategy.entry","OpenLong","Limit",50_000m,50_100m)],
            5,
            true,
            "binance",
            "Testnet",
            "strategy-alpha",
            "v2",
            Now.AddSeconds(-20),
            "book-v5",
            Now.AddSeconds(-10),
            Now.AddMinutes(5));
        Assert.True((await database.SaveAutomaticExecutionAsync(rawExecutionId,artifact,CancellationToken.None)).Succeeded);

        var state=new RuntimeAuthorizationStateStore(settings,database,()=>Now).Read();
        var item=Assert.Single(state.Automatic);
        Assert.Matches("^execution#[A-F0-9]{12}$",item.ExecutionId);
        Assert.DoesNotContain(rawExecutionId,item.ExecutionId,StringComparison.Ordinal);

        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,authorizationState:state);
        var json=JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(rawExecutionId,json,StringComparison.Ordinal);
        Assert.Contains(item.ExecutionId,json,StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyQueue_IsAvailableAndDoesNotInventApprovals()
    {
        var settings=Settings();settings.Save(settings.Load());var state=new RuntimeAuthorizationStateStore(settings,Database(),()=>Now).Read();
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,authorizationState:state);

        Assert.Equal(RuntimeCollectionState.Available,snapshot.AuthorizationMode.State);Assert.Equal("Review",snapshot.AuthorizationMode.Value!.Mode);Assert.Equal(RuntimeCollectionState.Available,snapshot.PendingApprovals.State);Assert.Empty(snapshot.PendingApprovals.Items);
    }

    [Theory]
    [InlineData(RuntimeCollectionState.Unsupported)]
    [InlineData(RuntimeCollectionState.Error)]
    [InlineData(RuntimeCollectionState.Stale)]
    public void NonAvailableState_FailsClosedWithoutModeOrQueue(RuntimeCollectionState state)
    {
        var item=new RuntimeApprovalSummaryV1("approval#unsafe","BTCUSDT","Long","Market",1m,1m,.9m,1.2m,Now,Now.AddMinutes(5),"Pending","approval.awaiting-user");
        var value=state==RuntimeCollectionState.Stale
            ?new RuntimeAuthorizationState(RuntimeCollectionState.Available,"Auto",[item],Now-RuntimeAuthorizationStateStore.StaleAfter-TimeSpan.FromSeconds(1),null)
            :new RuntimeAuthorizationState(state,"Auto",[item],Now,state.ToString());

        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,authorizationState:value);

        Assert.Equal(state,snapshot.AuthorizationMode.State);Assert.Null(snapshot.AuthorizationMode.Value);Assert.Equal(state,snapshot.PendingApprovals.State);Assert.Empty(snapshot.PendingApprovals.Items);
    }

    [Fact]
    public async Task QueueQuery_IsBoundedAndSupportsPaginationWithoutLoadingTheFullTable()
    {
        var database=Database();await InsertRequests(105);

        var maximum=await database.GetPendingTradingApprovalSummariesAsync(500,0,CancellationToken.None);
        var page=await database.GetPendingTradingApprovalSummariesAsync(10,50,CancellationToken.None);
        var state=new RuntimeAuthorizationStateStore(Settings(),database,()=>Now).Read();

        Assert.Equal(100,maximum.Count);Assert.Equal(10,page.Count);Assert.Equal(RuntimeAuthorizationStateStore.PendingLimit,state.Pending.Count);
        var source=File.ReadAllText(Path.Combine(Root(),"Services","Agent","AgentSqliteStore.cs"));var start=source.IndexOf("GetPendingTradingApprovalSummariesAsync",StringComparison.Ordinal);var end=source.IndexOf("return list;",start,StringComparison.Ordinal);var method=source[start..end];Assert.Contains("LEFT JOIN cycles",method,StringComparison.Ordinal);Assert.Contains("LEFT JOIN maturity_audits",method,StringComparison.Ordinal);Assert.Contains("LIMIT $limit OFFSET $offset",method,StringComparison.Ordinal);Assert.Equal(1,Count(method,"ExecuteReaderAsync"));
    }

    private AgentSettingsStore Settings()=>new(SettingsPath,()=>Now);
    private AgentSqliteStore Database()=>new(DatabasePath,()=>Now);
    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
    private static int Count(string value,string token){int count=0,index=0;while((index=value.IndexOf(token,index,StringComparison.Ordinal))>=0){count++;index+=token.Length;}return count;}
    private static async Task PersistArtifact(AgentSqliteStore database,string correlation,DecisionPlan decision,IndependentRiskReview risk)
    {
        await database.StartCycleAsync(correlation,new EvidencePack(),"local",CancellationToken.None);await database.CompleteCycleAsync(correlation,decision,"authorization.review-waiting",null,null,CancellationToken.None);await database.RecordMaturityAuditAsync(correlation,decision,new DecisionReview{Decision=decision,Accepted=true},risk,null,"authorization.review-waiting",CancellationToken.None);
    }
    private async Task InsertRequests(int count)
    {
        await using var connection=new SqliteConnection($"Data Source={DatabasePath}");await connection.OpenAsync();await using var transaction=await connection.BeginTransactionAsync();
        for(var i=0;i<count;i++){await using var command=connection.CreateCommand();command.Transaction=(SqliteTransaction)transaction;command.CommandText="INSERT INTO trading_approval_requests(request_id,mode,correlation_id,intent_hash,user_id,device_id,session_id,issued_at,expires_at) VALUES($id,'Review',$id,'hash','user','device','session',$issued,$expires)";command.Parameters.AddWithValue("$id",$"request-{i:D3}");command.Parameters.AddWithValue("$issued",Now.AddSeconds(-i).ToString("O"));command.Parameters.AddWithValue("$expires",Now.AddMinutes(5).ToString("O"));await command.ExecuteNonQueryAsync();}
        await transaction.CommitAsync();
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

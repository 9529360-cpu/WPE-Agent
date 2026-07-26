using System.Text.Json;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class RuntimeAuditTimelineTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-audit-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task SolExecution_RoundTripsAfterRestartWithoutSensitiveBodies()
    {
        const string secret="sk-live-super-secret";
        var database=new AgentSqliteStore(DatabasePath);
        var intent=new ExecutionIntent("SOLUSDT",PositionSide.Long,2m,false,120m,180m,"client-sol",secret,DecisionAction.OpenLong,ExpectedPrice:150m);
        var order=new ExchangeOrder("SOLUSDT","order-sol","client-sol","FILLED",2m,150m,"MARKET",PositionSide.Long,false,DateTime.UtcNow);
        await database.RecordExecutionAsync("cycle-sol",intent,order,"v1",default);
        await database.RecordSkillCallAsync("BrainPlanner","SUCCESS",12,secret,"prompt="+secret,secret,default);

        var reopened=new RuntimeAuditStateStore(new AgentSqliteStore(DatabasePath));
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,auditState:reopened.Read());

        Assert.Equal(RuntimeCollectionState.Available,snapshot.AuditEvents.State);
        var execution=Assert.Single(snapshot.AuditEvents.Items,x=>x.Category=="execution");
        Assert.Equal("cycle-sol",execution.CorrelationId);
        Assert.Contains("SOLUSDT",execution.Summary,StringComparison.Ordinal);
        Assert.DoesNotContain(secret,JsonSerializer.Serialize(snapshot),StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyConnectedStore_IsAvailableWithEmptyItems()
    {
        var store=new RuntimeAuditStateStore(new AgentSqliteStore(DatabasePath));
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,auditState:store.Read());
        Assert.Equal(RuntimeCollectionState.Available,snapshot.AuditEvents.State);
        Assert.Empty(snapshot.AuditEvents.Items);
    }

    [Fact]
    public void MissingSource_IsUnsupported()
    {
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow);
        Assert.Equal(RuntimeCollectionState.Unsupported,snapshot.AuditEvents.State);
        Assert.Empty(snapshot.AuditEvents.Items);
    }

    [Fact]
    public void ExpiredProjection_IsStaleAndItemsAreWithheld()
    {
        var item=new RuntimeAuditEventV1("event",DateTime.UtcNow,"system","test",null,"SUCCESS","Safe summary.");
        var state=new RuntimeAuditState(RuntimeCollectionState.Available,[item],DateTimeOffset.UtcNow.Subtract(RuntimeAuditStateStore.StaleAfter).AddSeconds(-1),null);
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,auditState:state);
        Assert.Equal(RuntimeCollectionState.Stale,snapshot.AuditEvents.State);
        Assert.Empty(snapshot.AuditEvents.Items);
    }

    [Fact]
    public void PersistenceError_IsExposedWithoutItems()
    {
        var item=new RuntimeAuditEventV1("old",DateTime.UtcNow,"system","test",null,"SUCCESS","Must not leak.");
        var state=new RuntimeAuditState(RuntimeCollectionState.Error,[item],DateTimeOffset.UtcNow,"database unavailable?token=sk-audit-secret-123456");
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,auditState:state);
        Assert.Equal(RuntimeCollectionState.Error,snapshot.AuditEvents.State);
        Assert.Contains("Audit history read failed.",snapshot.AuditEvents.Message);
        Assert.Contains("WPE-",snapshot.AuditEvents.Message);
        Assert.Contains("UTC",snapshot.AuditEvents.Message);
        Assert.DoesNotContain("sk-audit-secret",snapshot.AuditEvents.Message);
        Assert.Empty(snapshot.AuditEvents.Items);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }
}

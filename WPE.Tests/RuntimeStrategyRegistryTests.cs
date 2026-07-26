using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class RuntimeStrategyRegistryTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-strategies-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");
    [Fact] public async Task RegistryAndLifecycleEvents_RoundTripAfterRestart()
    {
        var db=new AgentSqliteStore(DatabasePath);var changed=DateTime.UtcNow.AddMinutes(-2);
        await db.UpsertStrategyAsync(new StrategyProfile{Id="SOL-trend",Version="v2",Symbol="SOLUSDT",Family=StrategyFamily.TrendBreakout,Lifecycle=StrategyLifecycle.Shadow,Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0),StateChangedAtUtc=changed,LastReason="shadow quality gate",QualityScore=.82},default);
        await using(var c=new SqliteConnection($"Data Source={DatabasePath}")){await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="INSERT INTO strategy_lifecycle_events(strategy_id,from_state,to_state,occurred_at,reason) VALUES('SOL-trend','Backtested','Shadow',$t,'shadow quality gate')";q.Parameters.AddWithValue("$t",changed.ToString("O"));await q.ExecuteNonQueryAsync();}
        var reopened=new RuntimeStrategyRegistryStateStore(new AgentSqliteStore(DatabasePath));
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,strategyRegistryState:reopened.Read());
        var profile=Assert.Single(snapshot.StrategyRegistry.Items);Assert.Equal("SOLUSDT",profile.Symbol);Assert.Equal("Shadow",profile.Lifecycle);Assert.Equal("v2",profile.Version);
        var evt=Assert.Single(snapshot.StrategyLifecycleEvents.Items);Assert.Equal("Backtested",evt.FromState);Assert.Equal("Shadow",evt.ToState);Assert.Equal("shadow quality gate",evt.Reason);
    }
    [Fact] public void EmptyConnectedStore_IsAvailableEmpty()
    {
        var state=new RuntimeStrategyRegistryStateStore(new AgentSqliteStore(DatabasePath)).Read();var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,strategyRegistryState:state);
        Assert.Equal(RuntimeCollectionState.Available,snapshot.StrategyRegistry.State);Assert.Empty(snapshot.StrategyRegistry.Items);Assert.Empty(snapshot.StrategyLifecycleEvents.Items);
    }
    [Fact] public void UnsupportedStaleAndError_WithholdItems()
    {
        var now=DateTime.UtcNow;var missing=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now);Assert.Equal(RuntimeCollectionState.Unsupported,missing.StrategyRegistry.State);
        var profile=new RuntimeStrategyProfileV1("id","v1","SOLUSDT","TrendBreakout","Active","reason",now,.8);var evt=new RuntimeStrategyLifecycleEventV1(1,"id","Shadow","Active",now,"reason");
        var staleState=new RuntimeStrategyRegistryState(RuntimeCollectionState.Available,[profile],[evt],new DateTimeOffset(now-RuntimeStrategyRegistryStateStore.StaleAfter-TimeSpan.FromSeconds(1)),null);
        var stale=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now,strategyRegistryState:staleState);Assert.Equal(RuntimeCollectionState.Stale,stale.StrategyRegistry.State);Assert.Empty(stale.StrategyRegistry.Items);Assert.Empty(stale.StrategyLifecycleEvents.Items);
        var error=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now,strategyRegistryState:RuntimeStrategyRegistryState.Error("db failed"));Assert.Equal(RuntimeCollectionState.Error,error.StrategyRegistry.State);Assert.Empty(error.StrategyRegistry.Items);
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

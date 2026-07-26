using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class RuntimeEquityHistoryTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-equity-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task RealProviderObservation_RoundTripsAfterRestartWithExactPrecision()
    {
        var time=DateTime.UtcNow.AddMinutes(-1);var database=new AgentSqliteStore(DatabasePath);
        await database.SaveEquitySnapshotAsync(new(time,12345.678901234567890123m,9876.543210987654321m,"Testnet","arbitrary-provider"),default);

        var reopened=new RuntimeEquityStateStore(new AgentSqliteStore(DatabasePath));
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,equityState:reopened.Read());

        Assert.Equal(RuntimeCollectionState.Available,snapshot.EquityHistory.State);
        var point=Assert.Single(snapshot.EquityHistory.Items);
        Assert.Equal(12345.678901234567890123m,point.Equity);
        Assert.Equal(9876.543210987654321m,point.AvailableBalance);
        Assert.Equal("Testnet",point.Environment);
        Assert.Equal("arbitrary-provider",point.ProviderId);
    }

    [Fact]
    public async Task IdenticalObservationWithinFiveMinutes_IsDeduplicated()
    {
        var database=new AgentSqliteStore(DatabasePath);var time=DateTime.UtcNow.AddMinutes(-2);
        Assert.True(await database.SaveEquitySnapshotAsync(new(time,1000m,900m,"Testnet","provider-x"),default));
        Assert.False(await database.SaveEquitySnapshotAsync(new(time.AddMinutes(2),1000m,900m,"Testnet","provider-x"),default));
        Assert.True(await database.SaveEquitySnapshotAsync(new(time.AddMinutes(2),1001m,900m,"Testnet","provider-x"),default));
        Assert.Equal(2,(await database.GetRecentEquitySnapshotsAsync(DateTime.UtcNow.AddDays(-1),100,default)).Count);
    }

    [Fact]
    public void EmptyConnectedStore_IsAvailableAndEmpty()
    {
        var state=new RuntimeEquityStateStore(new AgentSqliteStore(DatabasePath)).Read();
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,equityState:state);
        Assert.Equal(RuntimeCollectionState.Available,snapshot.EquityHistory.State);
        Assert.Empty(snapshot.EquityHistory.Items);
    }

    [Fact]
    public void MissingSource_IsUnsupported()
    {
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow);
        Assert.Equal(RuntimeCollectionState.Unsupported,snapshot.EquityHistory.State);
        Assert.Empty(snapshot.EquityHistory.Items);
    }

    [Fact]
    public void StaleAndErrorStates_WithholdOldPoints()
    {
        var point=new RuntimeEquityPointV1(DateTime.UtcNow.AddHours(-1),1000m,900m,"Testnet","provider-x");
        var stale=new RuntimeEquityState(RuntimeCollectionState.Available,[point],DateTimeOffset.UtcNow-RuntimeEquityStateStore.StaleAfter-TimeSpan.FromSeconds(1),null);
        var staleSnapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,equityState:stale);
        Assert.Equal(RuntimeCollectionState.Stale,staleSnapshot.EquityHistory.State);Assert.Empty(staleSnapshot.EquityHistory.Items);

        var error=new RuntimeEquityState(RuntimeCollectionState.Error,[point],DateTimeOffset.UtcNow,"database unavailable");
        var errorSnapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,equityState:error);
        Assert.Equal(RuntimeCollectionState.Error,errorSnapshot.EquityHistory.State);Assert.Empty(errorSnapshot.EquityHistory.Items);
    }

    [Fact]
    public async Task NonFiniteOrMalformedPersistedValues_AreRejectedFromRuntime()
    {
        _=new AgentSqliteStore(DatabasePath);
        await using(var c=new SqliteConnection($"Data Source={DatabasePath}")){await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="INSERT INTO equity_snapshots(observed_at,equity,available_balance,environment,provider_id) VALUES($t,'NaN','Infinity','Testnet','provider-x')";q.Parameters.AddWithValue("$t",DateTime.UtcNow.ToString("O"));await q.ExecuteNonQueryAsync();}
        var state=new RuntimeEquityStateStore(new AgentSqliteStore(DatabasePath)).Read();
        Assert.Equal(RuntimeCollectionState.Available,state.State);Assert.Empty(state.Items);
    }

    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

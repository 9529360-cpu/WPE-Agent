using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class BacktestRuntimeTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-backtests-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task SolBacktest_RoundTripsAfterSqliteRestart()
    {
        var completed=DateTime.UtcNow.AddMinutes(-2);
        var store=new AgentSqliteStore(DatabasePath);
        await store.SaveBacktestRunAsync(new("sol-run","SOLUSDT-TrendBreakout-0","trendbreakout-1","SOLUSDT","PASSED",completed,365,84,.087,.116,1.34),default);

        var reopened=new RuntimeBacktestStateStore(new AgentSqliteStore(DatabasePath));
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,backtestState:reopened.Read());

        Assert.Equal(RuntimeCollectionState.Available,snapshot.Backtests.State);
        var item=Assert.Single(snapshot.Backtests.Items);
        Assert.Equal("SOLUSDT",item.Symbol);
        Assert.Equal("trendbreakout-1",item.StrategyVersion);
        Assert.Equal(84,item.Trades);
        Assert.Equal(.087,item.OutOfSampleReturn,6);
    }

    [Fact]
    public void EmptyConnectedStore_IsAvailableWithEmptyItems()
    {
        var store=new RuntimeBacktestStateStore(new AgentSqliteStore(DatabasePath));
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,backtestState:store.Read());
        Assert.Equal(RuntimeCollectionState.Available,snapshot.Backtests.State);
        Assert.Empty(snapshot.Backtests.Items);
    }

    [Fact]
    public void MissingSource_IsUnsupported()
    {
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow);
        Assert.Equal(RuntimeCollectionState.Unsupported,snapshot.Backtests.State);
        Assert.Empty(snapshot.Backtests.Items);
    }

    [Fact]
    public async Task OldPersistedResult_IsStaleAndItemsAreWithheld()
    {
        var database=new AgentSqliteStore(DatabasePath);
        await database.SaveBacktestRunAsync(new("old","strategy","v1","SOLUSDT","PASSED",DateTime.UtcNow.AddDays(-2),300,40,.02,.1,.8),default);
        var store=new RuntimeBacktestStateStore(new AgentSqliteStore(DatabasePath));
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,backtestState:store.Read());
        Assert.Equal(RuntimeCollectionState.Stale,snapshot.Backtests.State);
        Assert.Empty(snapshot.Backtests.Items);
    }

    [Fact]
    public void PersistenceError_IsExposedWithoutItems()
    {
        var store=new RuntimeBacktestStateStore(new AgentSqliteStore(DatabasePath));
        store.PublishError("database unavailable Authorization: Bearer sk-backtest-secret-123456");
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,backtestState:store.Read());
        Assert.Equal(RuntimeCollectionState.Error,snapshot.Backtests.State);
        Assert.Contains("Backtest read failed.",snapshot.Backtests.Message);
        Assert.Contains("WPE-",snapshot.Backtests.Message);
        Assert.Contains("UTC",snapshot.Backtests.Message);
        Assert.DoesNotContain("sk-backtest-secret",snapshot.Backtests.Message);
        Assert.Empty(snapshot.Backtests.Items);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }
}

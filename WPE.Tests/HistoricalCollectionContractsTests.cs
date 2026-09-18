using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;

namespace WPE.Tests;

public sealed class HistoricalCollectionContractsTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-history-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");
    private static readonly DateTimeOffset Now=new(2026,7,21,12,0,0,TimeSpan.Zero);

    [Fact]
    public async Task OrdersAreBoundedPaginatedAndComeFromSqlite()
    {
        await InitializeAsync();
        for(var i=0;i<3;i++)await ExecuteAsync("INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,avg_price,status,occurred_at) VALUES($c,$o,'BTCUSDT','Long','OPEN',0,'1','100','FILLED',$t)",( "$c",$"cycle-{i}"),("$o",$"order-{i}"),("$t",Now.AddMinutes(-i).ToString("O")));
        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);
        var first=await store.ReadOrdersAsync(new(2));var second=await store.ReadOrdersAsync(new(2,first.NextCursor));
        var wrongCollection=await store.ReadEquityAsync(new(2,first.NextCursor));
        Assert.Equal(RuntimeCollectionState.Available,first.State);Assert.Equal(2,first.Items.Count);Assert.NotNull(first.NextCursor);Assert.Single(second.Items);Assert.Equal("cycle-0",first.Items[0].CorrelationId);Assert.Equal("local-agent-sqlite",first.Source);Assert.Equal(RuntimeCollectionState.Error,wrongCollection.State);Assert.Empty(wrongCollection.Items);
    }

    [Fact]
    public async Task PageSizeIsCappedAtOneHundred()
    {
        await InitializeAsync();
        for(var i=0;i<101;i++)await ExecuteAsync("INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,status,occurred_at) VALUES($c,$o,'BTCUSDT','Long','OPEN',0,'1','FILLED',$t)",( "$c",$"cycle-{i}"),("$o",$"order-{i}"),("$t",Now.ToString("O")));
        var page=await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadOrdersAsync(new(500));
        Assert.Equal(100,page.Items.Count);Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task TamperedExpiredAndCrossCollectionCursorsFailClosed()
    {
        await InitializeAsync();for(var i=0;i<2;i++)await ExecuteAsync("INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,status,occurred_at) VALUES($c,$o,'BTCUSDT','Long','OPEN',0,'1','FILLED',$t)",( "$c",$"cycle-{i}"),("$o",$"order-{i}"),("$t",Now.ToString("O")));
        var clock=Now;var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>clock);var first=await store.ReadOrdersAsync(new(1));var cursor=Assert.IsType<string>(first.NextCursor);
        var tampered=cursor[..^2]+(cursor[^2]=='A'?'B':'A')+cursor[^1];
        var tamperedPage=await store.ReadOrdersAsync(new(1,tampered));var crossCollection=await store.ReadEquityAsync(new(1,cursor));
        clock=Now.Add(HistoricalCollectionRequestV1.CursorLifetime).AddSeconds(1);var expired=await store.ReadOrdersAsync(new(1,cursor));
        Assert.Equal(RuntimeCollectionState.Error,tamperedPage.State);Assert.Empty(tamperedPage.Items);Assert.Null(tamperedPage.NextCursor);
        Assert.Equal(RuntimeCollectionState.Error,crossCollection.State);Assert.Empty(crossCollection.Items);Assert.Null(crossCollection.NextCursor);
        Assert.Equal(RuntimeCollectionState.Error,expired.State);Assert.Empty(expired.Items);Assert.Null(expired.NextCursor);
    }

    [Fact]
    public async Task InvalidCursorAndStaleDataFailClosedWithoutItems()
    {
        await InitializeAsync();await ExecuteAsync("INSERT INTO equity_snapshots(observed_at,equity,available_balance,environment,provider_id) VALUES($t,'10','9','Testnet','binance')",("$t",Now.AddDays(-2).ToString("O")));
        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);
        var invalid=await store.ReadOrdersAsync(new(10,"not-base64"));var stale=await store.ReadEquityAsync(new());
        Assert.Equal(RuntimeCollectionState.Error,invalid.State);Assert.Empty(invalid.Items);Assert.Equal(RuntimeCollectionState.Stale,stale.State);Assert.Empty(stale.Items);
    }

    [Fact]
    public async Task UnsupportedStaleAndReadErrorNeverReturnItemsOrCursor()
    {
        Directory.CreateDirectory(_directory);await ExecuteAsync("CREATE TABLE equity_snapshots(id INTEGER PRIMARY KEY,observed_at TEXT,equity TEXT,available_balance TEXT,environment TEXT,provider_id TEXT); INSERT INTO equity_snapshots VALUES(1,$t,'not-decimal','9','Testnet','binance')",("$t",Now.ToString("O")));
        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);var unsupported=await store.ReadOrdersAsync(new());var error=await store.ReadEquityAsync(new());
        await ExecuteAsync("UPDATE equity_snapshots SET observed_at=$t,equity='10'",("$t",Now.AddDays(-2).ToString("O")));var stale=await store.ReadEquityAsync(new());
        Assert.Equal(RuntimeCollectionState.Unsupported,unsupported.State);Assert.Empty(unsupported.Items);Assert.Null(unsupported.NextCursor);
        Assert.Equal(RuntimeCollectionState.Error,error.State);Assert.Empty(error.Items);Assert.Null(error.NextCursor);
        Assert.Equal(RuntimeCollectionState.Stale,stale.State);Assert.Empty(stale.Items);Assert.Null(stale.NextCursor);
    }

    [Fact]
    public async Task MissingTableIsUnsupportedAndAuditPayloadIsNeverProjected()
    {
        Directory.CreateDirectory(_directory);await ExecuteAsync("CREATE TABLE runtime_events(event_id TEXT,sequence INTEGER,correlation_id TEXT,event_type TEXT,source TEXT,payload_json TEXT,occurred_at TEXT)");
        const string secret="sk-secret-token-123456";await ExecuteAsync("INSERT INTO runtime_events VALUES('e1',1,$c,$type,$source,$p,$t)",( "$c",secret),("$type","token="+secret),("$source","Bearer "+secret),("$p",secret),("$t",Now.ToString("O")));
        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);var audit=await store.ReadAuditEventsAsync(new());var backtests=await store.ReadBacktestsAsync(new());
        var item=Assert.Single(audit.Items);var json=System.Text.Json.JsonSerializer.Serialize(audit);Assert.DoesNotContain(secret,json);Assert.DoesNotContain("payload_json",json,StringComparison.OrdinalIgnoreCase);Assert.Equal("[REDACTED]",item.Category);Assert.Equal("[REDACTED]",item.Source);Assert.Equal("[REDACTED]",item.CorrelationId);Assert.Equal(RuntimeCollectionState.Unsupported,backtests.State);Assert.Empty(backtests.Items);
    }

    [Fact]
    public async Task ExecutionRealityIsReadOnlySafeAndDoesNotProjectOpaqueOrderIdentity()
    {
        await InitializeAsync();
        await ExecuteAsync("""
            INSERT INTO execution_reality_drift(
                canonical_sha256,schema,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,
                symbol,state,terminal,comparable,fee_comparable,total_comparable,fill_ratio,slippage_drift_bps,
                fee_drift_bps,total_execution_drift_bps,observation_latency_ms,observed_at,reason_code)
            VALUES('hash-a','wpe.execution-reality-drift/1.0','SECRET-CORRELATION','SECRET-ORDER',
                'trend-alpha','v7','research-cost-v1','BTCUSDT','Filled',1,1,0,0,'1','12.5','0','0',480,$t,'filled-fee-unavailable')
            """,("$t",Now.ToString("O")));

        var page=await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadExecutionRealityAsync(new());
        Assert.Equal(RuntimeCollectionState.Available,page.State);
        var item=Assert.Single(page.Items);
        Assert.Equal("trend-alpha",item.StrategyId);
        Assert.Equal("v7",item.StrategyVersion);
        Assert.Equal("research-cost-v1",item.CostModelVersion);
        Assert.Equal(12.5m,item.SlippageDriftBps);
        Assert.False(item.FeeComparable);
        Assert.False(item.TotalComparable);
        Assert.Null(item.FeeDriftBps);
        Assert.Null(item.TotalExecutionDriftBps);
        var json=System.Text.Json.JsonSerializer.Serialize(page);
        Assert.DoesNotContain("SECRET-CORRELATION",json,StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-ORDER",json,StringComparison.Ordinal);
        Assert.DoesNotContain("canonical_sha256",json,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutionRealityWithholdsSlippageWhenPriceIsNotComparable()
    {
        await InitializeAsync();
        await ExecuteAsync("""
            INSERT INTO execution_reality_drift(
                canonical_sha256,schema,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,
                symbol,state,terminal,comparable,fee_comparable,total_comparable,fill_ratio,slippage_drift_bps,
                fee_drift_bps,total_execution_drift_bps,observation_latency_ms,observed_at,reason_code)
            VALUES('hash-b','wpe.execution-reality-drift/1.0','cycle-b','order-b',
                'trend-alpha','v7','research-cost-v1','BTCUSDT','NotFilled',1,0,0,0,'0','0','0','0',250,$t,'terminal-no-fill')
            """,("$t",Now.ToString("O")));

        var page=await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadExecutionRealityAsync(new());
        var item=Assert.Single(page.Items);
        Assert.False(item.PriceComparable);
        Assert.Null(item.SlippageDriftBps);
        Assert.Null(item.FeeDriftBps);
        Assert.Null(item.TotalExecutionDriftBps);
    }

    private async Task InitializeAsync(){Directory.CreateDirectory(_directory);await ExecuteAsync("""
        CREATE TABLE execution_events(id INTEGER PRIMARY KEY AUTOINCREMENT,cycle_id TEXT,client_order_id TEXT,symbol TEXT,side TEXT,action TEXT,reduce_only INTEGER,quantity TEXT,avg_price TEXT,status TEXT,occurred_at TEXT);
        CREATE TABLE equity_snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT,observed_at TEXT,equity TEXT,available_balance TEXT,environment TEXT,provider_id TEXT);
        CREATE TABLE runtime_skill_calls(id INTEGER PRIMARY KEY AUTOINCREMENT,occurred_at TEXT,skill TEXT,status TEXT,duration_ms INTEGER,mode TEXT,remote_llm INTEGER,tokens INTEGER,cost_usd TEXT);
        CREATE TABLE backtest_runs(id TEXT PRIMARY KEY,strategy_id TEXT,strategy_version TEXT,symbol TEXT,status TEXT,completed_at TEXT,coverage_days INTEGER,trades INTEGER,out_of_sample_return REAL,max_drawdown REAL,sharpe REAL);
        CREATE TABLE runtime_events(event_id TEXT,sequence INTEGER,correlation_id TEXT,event_type TEXT,source TEXT,payload_json TEXT,occurred_at TEXT);
        CREATE TABLE execution_reality_drift(
            canonical_sha256 TEXT PRIMARY KEY,schema TEXT,correlation_id TEXT,client_order_id TEXT,
            strategy_id TEXT,strategy_version TEXT,cost_model_version TEXT,symbol TEXT,state TEXT,
            terminal INTEGER,comparable INTEGER,fee_comparable INTEGER,total_comparable INTEGER,
            fill_ratio TEXT,slippage_drift_bps TEXT,fee_drift_bps TEXT,total_execution_drift_bps TEXT,
            observation_latency_ms INTEGER,observed_at TEXT,reason_code TEXT);
        """);}
    private async Task ExecuteAsync(string sql,params (string Name,object Value)[] values){Directory.CreateDirectory(_directory);await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;foreach(var value in values)q.Parameters.AddWithValue(value.Name,value.Value);await q.ExecuteNonQueryAsync();}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

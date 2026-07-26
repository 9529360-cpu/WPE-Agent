using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PostTradeReviewTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-post-trade-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task ConfirmedCloseCreatesOneDeterministicReviewAndLongTermMemory()
    {
        var store=new AgentSqliteStore(Database);var entry=Intent("entry",false,1m,100m);var close=Intent("close",true,1m,110m);
        await store.RecordExecutionAsync("open-cycle",entry,Order(entry,"FILLED",1m,100m),"strategy-v1",default);
        await store.RecordExecutionAsync("close-cycle",close,Order(close,"PARTIALLY_FILLED",.4m,109m),"strategy-v1",default);
        Assert.Empty(await store.GetRecentPostTradeReviewsAsync(10,default));

        var filled=Order(close,"FILLED",1m,110m);await store.RecordExecutionAsync("close-cycle",close,filled,"strategy-v1",default);await store.RecordExecutionAsync("close-cycle",close,filled,"strategy-v1",default);

        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("wpe.post-trade-review/1.2",review.Schema);Assert.Null(review.StrategyId);Assert.Equal("legacy-version-only",review.AttributionBasis);Assert.Equal("estimated-static-rate",review.FeeBasis);Assert.Equal(.0004m,review.FeeRate);Assert.Equal(.084m,review.Fees);Assert.Equal("win",review.Outcome);Assert.Equal(9.916m,review.NetPnl);Assert.Equal(.09916m,review.ReturnPct);
        var memories=await store.SearchMemoriesAsync(new(Tier:"long-term",Symbol:"BTCUSDT",StrategyId:"strategy-v1"),default);var memory=Assert.Single(memories);Assert.Equal("post-trade",memory.Source);Assert.Equal("win",memory.Result);
        Assert.Equal(9.916m,(await store.GetRiskHistoryAsync(default)).DailyRealizedPnl);
    }

    [Fact]
    public async Task ReusedCloseIdentityWithDifferentFactsFailsClosed()
    {
        var store=new AgentSqliteStore(Database);var entry=Intent("entry",false,1m,100m);var close=Intent("close",true,1m,110m);await store.RecordExecutionAsync("open-cycle",entry,Order(entry,"FILLED",1m,100m),"strategy-v1",default);await store.RecordExecutionAsync("close-cycle",close,Order(close,"FILLED",1m,110m),"strategy-v1",default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.RecordExecutionAsync("close-cycle",close with{ExpectedPrice=111m},Order(close,"FILLED",1m,111m),"strategy-v1",default));
        Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));
    }

    [Fact]
    public async Task MultipleEntriesUseDeterministicWeightedCostBasis()
    {
        var store=new AgentSqliteStore(Database);var first=Intent("entry-a",false,1m,100m);var second=Intent("entry-b",false,3m,120m);var close=Intent("close-weighted",true,2m,130m);await store.RecordExecutionAsync("open-a",first,Order(first,"FILLED",1m,100m),"strategy-v1",default);await store.RecordExecutionAsync("open-b",second,Order(second,"FILLED",3m,120m),"strategy-v1",default);await store.RecordExecutionAsync("close-weighted",close,Order(close,"FILLED",2m,130m),"strategy-v1",default);var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal(115m,review.EntryPrice);Assert.Equal(29.804m,review.NetPnl);Assert.Equal(29.804m/230m,review.ReturnPct);
    }

    [Fact]
    public async Task PriorReductionPreservesAverageAndHistoricalReplaySurvivesLaterEntry()
    {
        var store=new AgentSqliteStore(Database);var first=Intent("entry-a",false,1m,100m);var second=Intent("entry-b",false,1m,120m);var close=Intent("close-first",true,1m,130m);await store.RecordExecutionAsync("open-a",first,Order(first,"FILLED",1m,100m),"strategy-v1",default);await store.RecordExecutionAsync("open-b",second,Order(second,"FILLED",1m,120m),"strategy-v1",default);var fill=Order(close,"FILLED",1m,130m);await store.RecordExecutionAsync("close-first",close,fill,"strategy-v1",default);var later=Intent("entry-later",false,1m,200m);await store.RecordExecutionAsync("open-later",later,Order(later,"FILLED",1m,200m),"strategy-v2",default);await store.RecordExecutionAsync("close-first",close,fill,"strategy-v1",default);var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default),x=>x.ClientOrderId=="close-first");Assert.Equal(110m,review.EntryPrice);Assert.Equal(19.904m,review.NetPnl);
    }

    [Fact]
    public async Task LegacyDatabaseMigratesFeeProvenanceWithoutInventingObservedFees()
    {
        Directory.CreateDirectory(_directory);await using(var connection=new SqliteConnection($"Data Source={Database}")){await connection.OpenAsync();await using var command=connection.CreateCommand();command.CommandText="CREATE TABLE trade_outcomes(id INTEGER PRIMARY KEY AUTOINCREMENT,client_order_id TEXT,cycle_id TEXT,symbol TEXT,side TEXT,entry_price TEXT,exit_price TEXT,quantity TEXT,gross_pnl TEXT,fees TEXT,net_pnl TEXT,return_pct TEXT,closed_at TEXT,strategy_version TEXT); INSERT INTO trade_outcomes(client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,gross_pnl,fees,net_pnl,return_pct,closed_at,strategy_version) VALUES('legacy-close','legacy-cycle','BTCUSDT','Long','100','110','1','10','.084','9.916','.09916','2026-07-27T00:00:00.0000000+00:00','strategy-v1');";await command.ExecuteNonQueryAsync();}
        var review=Assert.Single(await new AgentSqliteStore(Database).GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("wpe.post-trade-review/1.2",review.Schema);Assert.Null(review.StrategyId);Assert.Equal("legacy-version-only",review.AttributionBasis);Assert.Equal("estimated-static-rate",review.FeeBasis);Assert.Equal(.0004m,review.FeeRate);Assert.Equal(.084m,review.Fees);
    }

    [Fact]
    public async Task AutomaticArtifactProvidesExactStrategyAttribution()
    {
        var store=new AgentSqliteStore(Database);var now=DateTimeOffset.UtcNow;var artifact=new DurableExecutionArtifactV2(2,"close-cycle",[new(0,"BTCUSDT","Long",1m,true,0,0,"close","strategy.exit","CloseLong","Market",0,110m)],1,true,"binance","Testnet","btc-trend","v7",now.AddSeconds(-10),"book-v1",now.AddSeconds(-5),now.AddMinutes(1));Assert.True((await store.SaveAutomaticExecutionAsync("execution-attributed",artifact,default)).Succeeded);
        var entry=Intent("entry",false,1m,100m);var close=Intent("close",true,1m,110m);await store.RecordExecutionAsync("open-cycle",entry,Order(entry,"FILLED",1m,100m),"wpe-core-v2",default);await store.RecordExecutionAsync("close-cycle",close,Order(close,"FILLED",1m,110m),"wpe-core-v2",default);
        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("btc-trend",review.StrategyId);Assert.Equal("v7",review.StrategyVersion);Assert.Equal("automatic-artifact",review.AttributionBasis);var memory=Assert.Single(await store.SearchMemoriesAsync(new(StrategyId:"btc-trend"),default));Assert.Contains("strategyVersion=v7",memory.Summary,StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictingAutomaticArtifactsNeverInventStrategyAttribution()
    {
        var store=new AgentSqliteStore(Database);var now=DateTimeOffset.UtcNow;DurableExecutionArtifactV2 Artifact(string strategy,string version,string client)=>new(2,"close-cycle",[new(0,"BTCUSDT","Long",1m,true,0,0,client,"strategy.exit","CloseLong","Market",0,110m)],1,true,"binance","Testnet",strategy,version,now.AddSeconds(-10),"book-v1",now.AddSeconds(-5),now.AddMinutes(1));Assert.True((await store.SaveAutomaticExecutionAsync("execution-a",Artifact("btc-trend","v7","close-a"),default)).Succeeded);Assert.True((await store.SaveAutomaticExecutionAsync("execution-b",Artifact("btc-revert","v3","close-b"),default)).Succeeded);
        var entry=Intent("entry",false,1m,100m);var close=Intent("close",true,1m,110m);await store.RecordExecutionAsync("open-cycle",entry,Order(entry,"FILLED",1m,100m),"wpe-core-v2",default);await store.RecordExecutionAsync("close-cycle",close,Order(close,"FILLED",1m,110m),"wpe-core-v2",default);var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Null(review.StrategyId);Assert.Equal("wpe-core-v2",review.StrategyVersion);Assert.Equal("conflicting-correlation",review.AttributionBasis);
    }

    private static ExecutionIntent Intent(string id,bool reduceOnly,decimal quantity,decimal expected)=>new("BTCUSDT",PositionSide.Long,quantity,reduceOnly,90m,120m,id,"test",reduceOnly?DecisionAction.CloseLong:DecisionAction.OpenLong,ExecutionOrderType.Market,0,expected);
    private static ExchangeOrder Order(ExecutionIntent intent,string status,decimal quantity,decimal price)=>new(intent.Symbol,"order-"+intent.ClientOrderId,intent.ClientOrderId,status,quantity,price,"MARKET",intent.Side,false,DateTime.UtcNow);
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PostTradeReviewTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-post-trade-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task ConfirmedCloseCreatesOneDeterministicReview()
    {
        var store=new AgentSqliteStore(Database);var entry=Intent("entry",false,1m,100m);var close=Intent("close",true,1m,110m);
        await store.RecordExecutionAsync("open-cycle",entry,Order(entry,"FILLED",1m,100m),"strategy-v1",default);
        await store.RecordExecutionAsync("close-cycle",close,Order(close,"PARTIALLY_FILLED",.4m,109m),"strategy-v1",default);
        Assert.Empty(await store.GetRecentPostTradeReviewsAsync(10,default));

        var filled=Order(close,"FILLED",1m,110m);await store.RecordExecutionAsync("close-cycle",close,filled,"strategy-v1",default);await store.RecordExecutionAsync("close-cycle",close,filled,"strategy-v1",default);

        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("wpe.post-trade-review/1.5",review.Schema);Assert.Null(review.StrategyId);Assert.Equal("legacy-version-only",review.AttributionBasis);Assert.Equal("estimated-static-rate",review.FeeBasis);Assert.Equal(.0004m,review.FeeRate);Assert.Equal(.084m,review.Fees);Assert.Equal("intent-expected-vs-fill",review.SlippageBasis);Assert.Equal(0m,review.TotalSlippageAmount);Assert.Equal("unavailable",review.FundingBasis);Assert.Equal(0m,review.FundingAmount);Assert.Equal("win",review.Outcome);Assert.Equal(9.916m,review.NetPnl);Assert.Equal(.09916m,review.ReturnPct);
        Assert.Equal(9.916m,(await store.GetRiskHistoryAsync(default)).DailyRealizedPnl);
    }

    [Fact]
    public async Task RiskHistoryIgnoresSmokeOutcomesForDailyLoss()
    {
        var store=new AgentSqliteStore(Database);
        await store.GetRiskHistoryAsync(default);
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="""
            INSERT INTO trade_outcomes(client_order_id,cycle_id,net_pnl,closed_at) VALUES
            ('real-win','real-cycle-win','2',$now),
            ('real-loss','real-cycle-loss','-5',$now),
            ('smoke-cycle-loss','SMOKE-diagnostic','-1',$now),
            ('WPE-SMOKE-CLOSE-diagnostic','diagnostic-cycle','-1',$now);
            """;
        command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();

        var history=await store.GetRiskHistoryAsync(default);
        Assert.Equal(-3m,history.DailyRealizedPnl);
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
        var review=Assert.Single(await new AgentSqliteStore(Database).GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("wpe.post-trade-review/1.5",review.Schema);Assert.Null(review.StrategyId);Assert.Equal("legacy-version-only",review.AttributionBasis);Assert.Equal("estimated-static-rate",review.FeeBasis);Assert.Equal(.0004m,review.FeeRate);Assert.Equal(.084m,review.Fees);Assert.Equal("unavailable",review.SlippageBasis);Assert.Equal(0m,review.TotalSlippageAmount);Assert.Equal("unavailable",review.FundingBasis);Assert.Equal(0m,review.FundingAmount);Assert.Equal("unavailable",review.ExcursionBasis);Assert.Equal(0,review.ExcursionSamples);Assert.Equal(0m,review.MaeReturnPct);Assert.Equal(0m,review.MfeReturnPct);Assert.Equal("unclassified",review.ExitReason);
    }

    [Fact]
    public async Task AutomaticArtifactProvidesExactStrategyAttribution()
    {
        var store=new AgentSqliteStore(Database);var now=DateTimeOffset.UtcNow;var artifact=new DurableExecutionArtifactV2(2,"close-cycle",[new(0,"BTCUSDT","Long",1m,true,0,0,"close","strategy.exit","CloseLong","Market",0,110m)],1,true,"binance","Testnet","btc-trend","v7",now.AddSeconds(-10),"book-v1",now.AddSeconds(-5),now.AddMinutes(1));Assert.True((await store.SaveAutomaticExecutionAsync("execution-attributed",artifact,default)).Succeeded);
        var entry=Intent("entry",false,1m,100m);var close=Intent("close",true,1m,110m);await store.RecordExecutionAsync("open-cycle",entry,Order(entry,"FILLED",1m,100m),"wpe-core-v2",default);await store.RecordExecutionAsync("close-cycle",close,Order(close,"FILLED",1m,110m),"wpe-core-v2",default);
        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("btc-trend",review.StrategyId);Assert.Equal("v7",review.StrategyVersion);Assert.Equal("automatic-artifact",review.AttributionBasis);
    }

    [Fact]
    public async Task ConflictingAutomaticArtifactsNeverInventStrategyAttribution()
    {
        var store=new AgentSqliteStore(Database);var now=DateTimeOffset.UtcNow;DurableExecutionArtifactV2 Artifact(string strategy,string version,string client)=>new(2,"close-cycle",[new(0,"BTCUSDT","Long",1m,true,0,0,client,"strategy.exit","CloseLong","Market",0,110m)],1,true,"binance","Testnet",strategy,version,now.AddSeconds(-10),"book-v1",now.AddSeconds(-5),now.AddMinutes(1));Assert.True((await store.SaveAutomaticExecutionAsync("execution-a",Artifact("btc-trend","v7","close-a"),default)).Succeeded);Assert.True((await store.SaveAutomaticExecutionAsync("execution-b",Artifact("btc-revert","v3","close-b"),default)).Succeeded);
        var entry=Intent("entry",false,1m,100m);var close=Intent("close",true,1m,110m);await store.RecordExecutionAsync("open-cycle",entry,Order(entry,"FILLED",1m,100m),"wpe-core-v2",default);await store.RecordExecutionAsync("close-cycle",close,Order(close,"FILLED",1m,110m),"wpe-core-v2",default);var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Null(review.StrategyId);Assert.Equal("wpe-core-v2",review.StrategyVersion);Assert.Equal("conflicting-correlation",review.AttributionBasis);
    }

    [Fact]
    public async Task LongSlippageUsesDirectionalIntentVsFillWithoutDoubleChargingPnl()
    {
        var store=new AgentSqliteStore(Database);var entry=Intent("entry",false,1m,100m);var close=Intent("close",true,1m,110m);await store.RecordExecutionAsync("open",entry,Order(entry,"FILLED",1m,101m),"v1",default);await store.RecordExecutionAsync("close",close,Order(close,"FILLED",1m,109m),"v1",default);var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal(1m,review.EntrySlippageAmount);Assert.Equal(1m,review.ExitSlippageAmount);Assert.Equal(2m,review.TotalSlippageAmount);Assert.Equal(7.916m,review.NetPnl);
    }

    [Fact]
    public async Task ShortFavorableSlippageIsNegativeAndPartialReductionDoesNotReuseEntryAttribution()
    {
        var store=new AgentSqliteStore(Database);ExecutionIntent Make(string id,bool reduce,decimal quantity,decimal expected)=>new("BTCUSDT",PositionSide.Short,quantity,reduce,110m,80m,id,"test",reduce?DecisionAction.ReduceShort:DecisionAction.OpenShort,ExecutionOrderType.Market,0,expected);var entry=Make("entry",false,2m,100m);var first=Make("first",true,1m,90m);var second=Make("second",true,1m,88m);await store.RecordExecutionAsync("open",entry,Order(entry,"FILLED",2m,101m),"v1",default);await store.RecordExecutionAsync("first",first,Order(first,"FILLED",1m,89m),"v1",default);await store.RecordExecutionAsync("second",second,Order(second,"FILLED",1m,87m),"v1",default);var reviews=await store.GetRecentPostTradeReviewsAsync(10,default);Assert.Equal(2,reviews.Count);Assert.All(reviews,x=>Assert.Equal(-2m,x.TotalSlippageAmount));Assert.Equal(-1m,reviews.Single(x=>x.ClientOrderId=="first").EntrySlippageAmount);Assert.Equal(-1m,reviews.Single(x=>x.ClientOrderId=="second").EntrySlippageAmount);
    }

    [Fact]
    public async Task FullClosePersistsObservedExcursionsExitReasonAndAppendOnlyMarks()
    {
        var now=new DateTimeOffset(2026,9,20,10,0,0,TimeSpan.Zero);
        var store=new AgentSqliteStore(Database,()=>now);
        var entry=Intent("entry-excursion",false,1m,100m);
        var entryOrder=new ExchangeOrder("BTCUSDT","order-entry",entry.ClientOrderId,"FILLED",1m,100m,"MARKET",PositionSide.Long,false,now.AddMinutes(-5).UtcDateTime);
        await store.RecordExecutionAsync("open-excursion",entry,entryOrder,"hypothesis-v1",default);
        await store.SavePositionMarkObservationsAsync([new("BTCUSDT",PositionSide.Long,1m,100m,97m,-3m,2m,true,50m)],now.AddMinutes(-4),default);
        await store.SavePositionMarkObservationsAsync([new("BTCUSDT",PositionSide.Long,1m,100m,106m,6m,2m,true,50m)],now.AddMinutes(-2),default);
        var close=new ExecutionIntent("BTCUSDT",PositionSide.Long,1m,true,0,0,"close-excursion","strategy.exit",DecisionAction.CloseLong,ExpectedPrice:102m);
        var closeOrder=new ExchangeOrder("BTCUSDT","order-close",close.ClientOrderId,"FILLED",1m,102m,"MARKET",PositionSide.Long,false,now.UtcDateTime);
        await store.RecordExecutionAsync("close-excursion",close,closeOrder,"hypothesis-v1",default);

        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));
        Assert.Equal(-.03m,review.MaeReturnPct);
        Assert.Equal(.06m,review.MfeReturnPct);
        Assert.Equal("runtime-mark-observations",review.ExcursionBasis);
        Assert.Equal(2,review.ExcursionSamples);
        Assert.Equal("strategy.exit",review.ExitReason);
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var count=connection.CreateCommand();
        count.CommandText="SELECT COUNT(*) FROM position_mark_observations";
        Assert.Equal(2L,(long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task StableReasonCodeWinsOverLocalizedDisplayReason()
    {
        var store=new AgentSqliteStore(Database);
        var entry=Intent("entry-structure-code",false,1m,100m);
        await store.RecordExecutionAsync("open-structure-code",entry,Order(entry,"FILLED",1m,100m),"hypothesis-v1",default);

        var close=new ExecutionIntent(
            "BTCUSDT",PositionSide.Long,1m,true,0,0,"close-structure-code",
            "本地K线结构已否定当前持仓逻辑，执行风险退出",
            DecisionAction.CloseLong,
            ExpectedPrice:98m,
            ReasonCode:PositionExitReasonCodes.StructureInvalidated);
        await store.RecordExecutionAsync(
            "close-structure-code",
            close,
            new ExchangeOrder("BTCUSDT","order-close-structure-code",close.ClientOrderId,"FILLED",1m,98m,"MARKET",PositionSide.Long,false,DateTime.UtcNow),
            "hypothesis-v1",
            default);

        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));
        Assert.Equal(PositionExitReasonCodes.StructureInvalidated,review.ExitReason);
    }

    [Fact]
    public async Task LocalizedDisplayReasonWithoutCodeCannotBecomeMachineExitReason()
    {
        var store=new AgentSqliteStore(Database);
        var entry=Intent("entry-localized-fallback",false,1m,100m);
        await store.RecordExecutionAsync("open-localized-fallback",entry,Order(entry,"FILLED",1m,100m),"v1",default);
        var close=new ExecutionIntent(
            "BTCUSDT",PositionSide.Long,1m,true,0,0,"close-localized-fallback",
            "强平安全缓冲不足",
            DecisionAction.CloseLong,
            ExpectedPrice:99m);
        await store.RecordExecutionAsync(
            "close-localized-fallback",
            close,
            new ExchangeOrder("BTCUSDT","order-close-localized-fallback",close.ClientOrderId,"FILLED",1m,99m,"MARKET",PositionSide.Long,false,DateTime.UtcNow),
            "v1",
            default);

        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));
        Assert.Equal("action.closelong",review.ExitReason);
    }

    [Fact]
    public async Task ProtectionFillCannotBeRelabeledAsNonProtectionExit()
    {
        var store=new AgentSqliteStore(Database);
        var entry=Intent("entry-protection-class",false,1m,100m);
        await store.RecordExecutionAsync("open-protection-class",entry,Order(entry,"FILLED",1m,100m),"v1",default);
        var close=new ExecutionIntent(
            "BTCUSDT",PositionSide.Long,1m,true,0,0,"close-protection-class",
            "unexpected display text",
            DecisionAction.CloseLong,
            ExpectedPrice:90m,
            ReasonCode:PositionExitReasonCodes.StructureInvalidated);
        var protectionOrder=new ExchangeOrder(
            "BTCUSDT","order-close-protection-class",close.ClientOrderId,"FILLED",1m,90m,
            "STOP_MARKET",PositionSide.Long,true,DateTime.UtcNow);
        await store.RecordExecutionAsync("close-protection-class",close,protectionOrder,"v1",default);

        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));
        Assert.Equal(PositionExitReasonCodes.ProtectionFillReconciled,review.ExitReason);
    }

    [Fact]
    public async Task LegacyAutomaticRiskApprovedCodeCannotBecomeExitCause()
    {
        var store=new AgentSqliteStore(Database);
        var entry=Intent("entry-legacy-authority",false,1m,100m);
        await store.RecordExecutionAsync("open-legacy-authority",entry,Order(entry,"FILLED",1m,100m),"v1",default);
        var close=new ExecutionIntent(
            "BTCUSDT",PositionSide.Long,1m,true,0,0,"close-legacy-authority",
            "automatic.risk-approved",
            DecisionAction.CloseLong,
            ExpectedPrice:99m,
            ReasonCode:"automatic.risk-approved");
        await store.RecordExecutionAsync(
            "close-legacy-authority",
            close,
            new ExchangeOrder("BTCUSDT","order-close-legacy-authority",close.ClientOrderId,"FILLED",1m,99m,"MARKET",PositionSide.Long,false,DateTime.UtcNow),
            "v1",
            default);

        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));
        Assert.Equal("action.closelong",review.ExitReason);
    }

    [Fact]
    public async Task ReplayedCloseCannotChangeExpectedPriceOnly()
    {
        var store=new AgentSqliteStore(Database);var entry=Intent("entry",false,1m,100m);var close=Intent("close",true,1m,110m);await store.RecordExecutionAsync("open",entry,Order(entry,"FILLED",1m,100m),"v1",default);var fill=Order(close,"FILLED",1m,110m);await store.RecordExecutionAsync("close",close,fill,"v1",default);await Assert.ThrowsAsync<InvalidOperationException>(()=>store.RecordExecutionAsync("close",close with{ExpectedPrice=111m},fill,"v1",default));
    }

    private static ExecutionIntent Intent(string id,bool reduceOnly,decimal quantity,decimal expected)=>new("BTCUSDT",PositionSide.Long,quantity,reduceOnly,90m,120m,id,"test",reduceOnly?DecisionAction.CloseLong:DecisionAction.OpenLong,ExecutionOrderType.Market,0,expected);
    private static ExchangeOrder Order(ExecutionIntent intent,string status,decimal quantity,decimal price)=>new(intent.Symbol,"order-"+intent.ClientOrderId,intent.ClientOrderId,status,quantity,price,"MARKET",intent.Side,false,DateTime.UtcNow);
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

using Microsoft.Data.Sqlite;
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

        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("wpe.post-trade-review/1.0",review.Schema);Assert.Equal("win",review.Outcome);Assert.Equal(9.916m,review.NetPnl);Assert.Equal(.09916m,review.ReturnPct);
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

    private static ExecutionIntent Intent(string id,bool reduceOnly,decimal quantity,decimal expected)=>new("BTCUSDT",PositionSide.Long,quantity,reduceOnly,90m,120m,id,"test",reduceOnly?DecisionAction.CloseLong:DecisionAction.OpenLong,ExecutionOrderType.Market,0,expected);
    private static ExchangeOrder Order(ExecutionIntent intent,string status,decimal quantity,decimal price)=>new(intent.Symbol,"order-"+intent.ClientOrderId,intent.ClientOrderId,status,quantity,price,"MARKET",intent.Side,false,DateTime.UtcNow);
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

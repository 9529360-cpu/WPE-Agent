using System.Text.Json;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExchangeFundingEvidenceTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-funding-evidence-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;

    [Fact]
    public void BinanceIncomeProducesSignedCanonicalEvents()
    {
        using var document=JsonDocument.Parse($$"""[{"symbol":"BTCUSDT","incomeType":"FUNDING_FEE","income":"-0.25","asset":"USDT","time":{{Now.AddMinutes(-5).ToUnixTimeMilliseconds()}},"tranId":101},{"symbol":"BTCUSDT","incomeType":"FUNDING_FEE","income":"0.10","asset":"USDT","time":{{Now.AddMinutes(-1).ToUnixTimeMilliseconds()}},"tranId":102}]""");
        var events=BinanceFundingIncomeParserV1.Parse(document.RootElement,"BTCUSDT","BTCUSDT",Now.AddHours(-1),Now);
        Assert.Equal([-0.25m,0.10m],events.Select(x=>x.Amount));Assert.All(events,x=>Assert.True(FundingEvidenceCanonicalizerV1.IsCanonical(x)));
        var window=FundingEvidenceCanonicalizerV1.Window("binance-futures","Testnet","BTCUSDT",Now.AddHours(-1),Now,Now,FundingObservationStateV1.Available,events,new string('a',64));Assert.True(FundingEvidenceCanonicalizerV1.IsCanonical(new(window,events),Now));
    }

    [Theory]
    [InlineData("OTHER","BTCUSDT","1")]
    [InlineData("FUNDING_FEE","ETHUSDT","1")]
    [InlineData("FUNDING_FEE","BTCUSDT","bad")]
    public void WrongTypeSymbolOrAmountIsRejected(string type,string symbol,string amount)
    {
        using var document=JsonDocument.Parse($$"""[{"symbol":"{{symbol}}","incomeType":"{{type}}","income":"{{amount}}","asset":"USDT","time":{{Now.ToUnixTimeMilliseconds()}},"tranId":1}]""");
        Assert.Throws<InvalidOperationException>(()=>BinanceFundingIncomeParserV1.Parse(document.RootElement,"BTCUSDT","BTCUSDT",Now.AddMinutes(-1),Now));
    }

    [Fact]
    public async Task PersistenceIsIdempotentAndRejectsTransactionConflictAndTampering()
    {
        var store=new AgentSqliteStore(Database,()=>Now);var first=Observation(.2m,"tx-1");Assert.True(await store.SaveFundingObservationAsync(first,default));Assert.False(await store.SaveFundingObservationAsync(first,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveFundingObservationAsync(Observation(.3m,"tx-1"),default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveFundingObservationAsync(first with{Window=first.Window with{EventCount=9}},default));
    }

    [Theory]
    [InlineData(0,9.916)]
    [InlineData(2,11.916)]
    [InlineData(-2,7.916)]
    public async Task CompleteSingleSideCloseUsesConfirmedFundingExactlyOnce(decimal fundingAmount,decimal expectedNet)
    {
        var store=new AgentSqliteStore(Database,()=>Now);var entry=Intent("entry",PositionSide.Long,false,1m,100m);await store.RecordExecutionAsync("open",entry,Order(entry,100m,Now.AddMinutes(-30)),"v1",default);
        var observation=Observation(fundingAmount,"funding",Now.AddMinutes(-30),Now);await store.SaveFundingObservationAsync(observation,default);
        var close=Intent("close",PositionSide.Long,true,1m,110m);var closeOrder=Order(close,110m,Now.AddSeconds(-1));await store.RecordExecutionAsync("close",close,closeOrder,"v1",default);await store.RecordExecutionAsync("close",close,closeOrder,"v1",default);
        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("exchange-reported-window",review.FundingBasis);Assert.Equal(fundingAmount,review.FundingAmount);Assert.Equal(expectedNet,review.NetPnl);
    }

    [Fact]
    public async Task PartialReductionAndOppositeExposureRemainUnavailable()
    {
        var store=new AgentSqliteStore(Database,()=>Now);var longEntry=Intent("long",PositionSide.Long,false,2m,100m);await store.RecordExecutionAsync("long",longEntry,Order(longEntry,100m,Now.AddMinutes(-30)),"v1",default);var shortEntry=Intent("short",PositionSide.Short,false,1m,100m);await store.RecordExecutionAsync("short",shortEntry,Order(shortEntry,100m,Now.AddMinutes(-20)),"v1",default);await store.SaveFundingObservationAsync(Observation(5m,"ambiguous",Now.AddMinutes(-30),Now),default);
        var close=Intent("partial",PositionSide.Long,true,1m,110m);await store.RecordExecutionAsync("partial",close,Order(close,110m,Now.AddSeconds(-1)),"v1",default);var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("unavailable",review.FundingBasis);Assert.Equal(0m,review.FundingAmount);Assert.Equal(9.916m,review.NetPnl);
    }

    private static FundingObservationResultV1 Observation(decimal amount,string transactionId,DateTimeOffset? start=null,DateTimeOffset? end=null)
    {
        var from=(start??Now.AddHours(-1)).ToUniversalTime();var through=(end??Now).ToUniversalTime();var events=amount==0?Array.Empty<FundingIncomeEventV1>():[FundingEvidenceCanonicalizerV1.Event("binance-futures","Testnet","BTCUSDT",transactionId,from.AddMinutes(1),amount,"USDT")];var window=FundingEvidenceCanonicalizerV1.Window("binance-futures","Testnet","BTCUSDT",from,through,Now,FundingObservationStateV1.Available,events,new string('b',64));return new(window,events);
    }
    private static ExecutionIntent Intent(string id,PositionSide side,bool reduce,decimal quantity,decimal expected)=>new("BTCUSDT",side,quantity,reduce,90m,120m,id,"test",reduce?(side==PositionSide.Long?DecisionAction.CloseLong:DecisionAction.CloseShort):(side==PositionSide.Long?DecisionAction.OpenLong:DecisionAction.OpenShort),ExecutionOrderType.Market,0,expected);
    private static ExchangeOrder Order(ExecutionIntent intent,decimal price,DateTimeOffset updated)=>new(intent.Symbol,"order-"+intent.ClientOrderId,intent.ClientOrderId,"FILLED",intent.Quantity,price,"MARKET",intent.Side,false,updated.UtcDateTime);
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExchangeOrderFeeEvidenceTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-fee-evidence-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;

    [Fact]
    public void BinanceTradesProduceCanonicalConfirmedEvidence()
    {
        var evidence=Parse("""[{"symbol":"BTCUSDT","id":1,"orderId":42,"price":"100","qty":"0.4","commission":"0.01","commissionAsset":"USDT"},{"symbol":"BTCUSDT","id":2,"orderId":42,"price":"101","qty":"0.6","commission":"0.02","commissionAsset":"USDT"}]""");
        Assert.Equal(ExchangeOrderFeeEvidenceStateV1.Confirmed,evidence.State);Assert.Equal(2,evidence.FillCount);Assert.Equal(1m,evidence.ExecutedQuantity);Assert.Equal(.03m,evidence.FeeAmount);Assert.True(ExchangeOrderFeeEvidenceCanonicalizerV1.IsCanonical(evidence));
    }

    [Theory]
    [InlineData("[{\"symbol\":\"BTCUSDT\",\"id\":1,\"orderId\":42,\"price\":\"100\",\"qty\":\"0.4\",\"commission\":\"0.01\",\"commissionAsset\":\"USDT\"},{\"symbol\":\"BTCUSDT\",\"id\":1,\"orderId\":42,\"price\":\"100\",\"qty\":\"0.6\",\"commission\":\"0.02\",\"commissionAsset\":\"USDT\"}]")]
    [InlineData("[{\"symbol\":\"BTCUSDT\",\"id\":1,\"orderId\":99,\"price\":\"100\",\"qty\":\"1\",\"commission\":\"0.01\",\"commissionAsset\":\"USDT\"}]")]
    [InlineData("[{\"symbol\":\"BTCUSDT\",\"id\":1,\"orderId\":42,\"price\":\"100\",\"qty\":\"0.5\",\"commission\":\"0.01\",\"commissionAsset\":\"USDT\"}]")]
    [InlineData("[{\"symbol\":\"BTCUSDT\",\"id\":1,\"orderId\":42,\"price\":\"100\",\"qty\":\"0.5\",\"commission\":\"0.01\",\"commissionAsset\":\"USDT\"},{\"symbol\":\"BTCUSDT\",\"id\":2,\"orderId\":42,\"price\":\"100\",\"qty\":\"0.5\",\"commission\":\"0.01\",\"commissionAsset\":\"BNB\"}]")]
    [InlineData("[{\"symbol\":\"BTCUSDT\",\"id\":1,\"orderId\":42,\"price\":\"100\",\"qty\":\"1\",\"commission\":\"-0.01\",\"commissionAsset\":\"USDT\"}]")]
    public void InvalidOrIncompleteTradesFailClosed(string json)=>Assert.Equal(ExchangeOrderFeeEvidenceStateV1.Invalid,Parse(json).State);

    [Fact]
    public async Task CanonicalEvidenceIsAppendOnlyAndRejectsTamperingReplayAndStaleness()
    {
        var store=new AgentSqliteStore(Database,()=>Now);var evidence=Fee("entry",.02m,Now);Assert.True(await store.SaveExchangeOrderFeeEvidenceAsync(evidence,default));Assert.False(await store.SaveExchangeOrderFeeEvidenceAsync(evidence,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveExchangeOrderFeeEvidenceAsync(evidence with{FeeAmount=.03m},default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveExchangeOrderFeeEvidenceAsync(Fee("entry",.02m,Now.AddMinutes(-6)),default));
        Assert.True(await store.SaveExchangeOrderFeeEvidenceAsync(Fee("entry",.03m,Now.AddSeconds(1)),default));
    }

    [Fact]
    public async Task PostTradeUsesReportedUsdtFeesOnlyWhenEntryAndCloseAreConfirmed()
    {
        var store=new AgentSqliteStore(Database,()=>Now);var entry=Intent("entry",false,100m);var close=Intent("close",true,110m);await store.SaveExchangeOrderFeeEvidenceAsync(Fee("entry",.02m,Now),default);await store.RecordExecutionAsync("open",entry,Order(entry,100m),"v1",default);await store.SaveExchangeOrderFeeEvidenceAsync(Fee("close",.03m,Now),default);await store.RecordExecutionAsync("close",close,Order(close,110m),"v1",default);
        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("exchange-reported-usdt",review.FeeBasis);Assert.Equal(0m,review.FeeRate);Assert.Equal(.05m,review.Fees);Assert.Equal(9.95m,review.NetPnl);
    }

    [Fact]
    public async Task NonUsdtOrMissingEvidenceKeepsExplicitEstimatedFallback()
    {
        var store=new AgentSqliteStore(Database,()=>Now);var entry=Intent("entry",false,100m);var close=Intent("close",true,110m);await store.SaveExchangeOrderFeeEvidenceAsync(Fee("entry",.02m,Now,"BNB"),default);await store.RecordExecutionAsync("open",entry,Order(entry,100m),"v1",default);await store.SaveExchangeOrderFeeEvidenceAsync(Fee("close",.03m,Now),default);await store.RecordExecutionAsync("close",close,Order(close,110m),"v1",default);
        var review=Assert.Single(await store.GetRecentPostTradeReviewsAsync(10,default));Assert.Equal("estimated-static-rate",review.FeeBasis);Assert.Equal(.0004m,review.FeeRate);Assert.Equal(.084m,review.Fees);
    }

    private static ExchangeOrderFeeEvidenceV1 Parse(string json){using var document=JsonDocument.Parse(json);return BinanceOrderFeeEvidenceParserV1.Parse(document.RootElement,new("BTCUSDT","42","client","FILLED",1m,100m,"MARKET",PositionSide.Long,false,Now.UtcDateTime),Now);}
    private static ExchangeOrderFeeEvidenceV1 Fee(string client,decimal fee,DateTimeOffset observed,string asset="USDT")=>ExchangeOrderFeeEvidenceCanonicalizerV1.Create("binance-futures","Testnet","BTCUSDT","order-"+client,client,1,1m,fee,asset,observed,ExchangeOrderFeeEvidenceStateV1.Confirmed);
    private static ExecutionIntent Intent(string id,bool reduceOnly,decimal price)=>new("BTCUSDT",PositionSide.Long,1m,reduceOnly,90m,120m,id,"test",reduceOnly?DecisionAction.CloseLong:DecisionAction.OpenLong,ExecutionOrderType.Market,0,price);
    private static ExchangeOrder Order(ExecutionIntent intent,decimal price)=>new(intent.Symbol,"order-"+intent.ClientOrderId,intent.ClientOrderId,"FILLED",1m,price,"MARKET",intent.Side,false,Now.UtcDateTime);
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

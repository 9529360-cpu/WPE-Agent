using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class RealtimeMarketIntegrityTests : IDisposable
{
    private readonly string _database=Path.Combine(Path.GetTempPath(),"wpe-realtime-integrity-"+Guid.NewGuid().ToString("N")+".db");
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{File.Delete(_database);}catch(IOException){}}

    [Fact]
    public async Task UnknownAndMalformedKnownEventsDoNotRefreshMarketState()
    {
        await using var hub=Hub();
        await hub.HandleMarketAsync("{\"data\":{\"e\":\"unknown\",\"s\":\"BTCUSDT\"}}",default);
        await hub.HandleMarketAsync("{\"data\":{\"e\":\"bookTicker\",\"s\":\"BTCUSDT\",\"b\":\"101\",\"a\":\"100\",\"B\":\"1\",\"A\":\"1\"}}",default);
        await hub.HandleMarketAsync("{\"data\":{\"e\":\"aggTrade\",\"s\":\"BTCUSDT\",\"p\":\"100\",\"q\":\"0\",\"m\":false}}",default);
        var snapshot=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));
        Assert.Equal(0,snapshot.Messages);Assert.Equal(default,snapshot.UpdatedAt);Assert.Equal(0,snapshot.LastPrice);Assert.Equal(0,snapshot.BestBid);
    }

    [Fact]
    public void OnlyCompleteFreshConnectedSnapshotIsEligibleForEnrichment()
    {
        var now=DateTime.UtcNow;
        Assert.False(new RealtimeMarketSnapshot("BTCUSDT",100,0,0,0,0,0,0,0,now,1,true).EligibleForEnrichment);
        Assert.False(new RealtimeMarketSnapshot("BTCUSDT",100,99,101,1,1,0,0,0,now,2,false).EligibleForEnrichment);
        Assert.True(new RealtimeMarketSnapshot("BTCUSDT",100,99,101,1,1,0,0,0,now,3,true).EligibleForEnrichment);
    }

    [Fact]
    public async Task ValidKnownEventsAloneAdvanceMessageCount()
    {
        await using var hub=Hub();
        await hub.HandleMarketAsync("{\"data\":{\"e\":\"bookTicker\",\"s\":\"BTCUSDT\",\"b\":\"99\",\"a\":\"101\",\"B\":\"2\",\"A\":\"3\"}}",default);
        await hub.HandleMarketAsync("{\"data\":{\"e\":\"aggTrade\",\"s\":\"BTCUSDT\",\"p\":\"100\",\"q\":\"1\",\"m\":false}}",default);
        var snapshot=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));
        Assert.Equal(2,snapshot.Messages);Assert.Equal(100,snapshot.LastPrice);Assert.Equal(99,snapshot.BestBid);Assert.Equal(101,snapshot.BestAsk);Assert.NotEqual(default,snapshot.UpdatedAt);
    }

    [Fact]
    public async Task ClosedMinuteKlineIsCachedOnceForCanonicalConfirmationEvidence()
    {
        await using var hub=Hub();
        var openTime=DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds();
        var json=$"{{\"data\":{{\"e\":\"kline\",\"s\":\"BTCUSDT\",\"k\":{{\"t\":{openTime},\"o\":\"100\",\"h\":\"102\",\"l\":\"99\",\"c\":\"101.5\",\"v\":\"12\",\"q\":\"1218\",\"n\":24,\"V\":\"7\",\"x\":true}}}}}}";

        await hub.HandleMarketAsync(json,default);
        await hub.HandleMarketAsync(json,default);
        var snapshot=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));

        var candle=Assert.Single(snapshot.ClosedMinuteCandles);
        Assert.Equal(100,candle.Open);Assert.Equal(102,candle.High);Assert.Equal(99,candle.Low);Assert.Equal(101.5m,candle.Close);
        Assert.Equal(12,candle.Volume);Assert.Equal(1218,candle.QuoteVolume);Assert.Equal(24,candle.Trades);Assert.Equal(7,candle.TakerBuyVolume);
    }

    [Fact]
    public void RealtimeMergeKeepsDepthBookSeparateFromAggTradeFlow()
    {
        var baseline=new MarketQualityEvidence{BestBid=98,BestAsk=102,SpreadBps=4,OrderBookImbalance=.42,QualityScore=90,SourceCount=4,Anomalies=["book_ticker_missing","order_book_missing"]};
        var live=new RealtimeMarketSnapshot("BTCUSDT",100,99,101,2,3,1,9,10,DateTime.UtcNow,5,true);

        var merged=RealTimeMarketHub.MergeQuality(baseline,live);

        Assert.Equal(.42,merged.OrderBookImbalance,10);
        Assert.True(merged.OrderFlowAvailable);
        Assert.Equal(-.8,merged.OrderFlowImbalance,10);
        Assert.DoesNotContain("book_ticker_missing",merged.Anomalies);
        Assert.Contains("order_book_missing",merged.Anomalies);
    }

    private RealTimeMarketHub Hub()=>new(ExchangeEnvironment.Testnet,["BTCUSDT"],"test-key",new AgentSqliteStore(_database));
}

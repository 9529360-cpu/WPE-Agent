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
        Assert.Equal(0,snapshot.Messages);Assert.Equal(default,snapshot.UpdatedAt);Assert.Equal(default,snapshot.BookUpdatedAt);Assert.Equal(0,snapshot.LastPrice);Assert.Equal(0,snapshot.BestBid);
    }

    [Fact]
    public void OnlyCompleteFreshConnectedSnapshotIsEligibleForEnrichment()
    {
        var now=DateTime.UtcNow;
        Assert.False(new RealtimeMarketSnapshot("BTCUSDT",100,0,0,0,0,0,0,0,now,1,true).EligibleForEnrichment);
        Assert.False(new RealtimeMarketSnapshot("BTCUSDT",100,99,101,1,1,0,0,0,now,2,false).EligibleForEnrichment);
        Assert.False(new RealtimeMarketSnapshot("BTCUSDT",100,99,101,1,1,0,0,0,now,now.AddMinutes(-1),3,true).EligibleForEnrichment);
        Assert.True(new RealtimeMarketSnapshot("BTCUSDT",100,99,101,1,1,0,0,0,now,3,true).EligibleForEnrichment);
    }

    [Fact]
    public async Task TradeMessagesCannotRefreshBookFreshness()
    {
        await using var hub=Hub();
        var bookMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await hub.HandleMarketAsync($"{{\"data\":{{\"e\":\"bookTicker\",\"E\":{bookMs},\"T\":{bookMs},\"s\":\"BTCUSDT\",\"b\":\"99\",\"a\":\"101\",\"B\":\"2\",\"A\":\"3\"}}}}",default);
        var book=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));
        Assert.NotEqual(default,book.BookUpdatedAt);

        await Task.Delay(20);
        await hub.HandleMarketAsync("{\"data\":{\"e\":\"aggTrade\",\"s\":\"BTCUSDT\",\"p\":\"100\",\"q\":\"1\",\"m\":false}}",default);
        var trade=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));

        Assert.True(trade.UpdatedAt>book.UpdatedAt);
        Assert.Equal(book.BookUpdatedAt,trade.BookUpdatedAt);
    }

    [Fact]
    public async Task BookTickerUsesExchangeTimeAndRejectsMissingFutureOrOlderSnapshots()
    {
        await using var hub=Hub();
        var now=DateTimeOffset.UtcNow;
        var freshMs=now.AddSeconds(-1).ToUnixTimeMilliseconds();
        await hub.HandleMarketAsync($"{{\"data\":{{\"e\":\"bookTicker\",\"E\":{freshMs},\"T\":{freshMs},\"s\":\"BTCUSDT\",\"b\":\"99\",\"a\":\"101\",\"B\":\"2\",\"A\":\"3\"}}}}",default);
        var fresh=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(freshMs).UtcDateTime,fresh.BookUpdatedAt);
        Assert.Equal(1,fresh.Messages);

        await hub.HandleMarketAsync("{\"data\":{\"e\":\"bookTicker\",\"s\":\"BTCUSDT\",\"b\":\"98\",\"a\":\"102\",\"B\":\"4\",\"A\":\"5\"}}",default);
        var missing=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));
        Assert.Equal(fresh.BookUpdatedAt,missing.BookUpdatedAt);
        Assert.Equal(fresh.BestBid,missing.BestBid);
        Assert.Equal(1,missing.Messages);

        var futureMs=now.AddMinutes(1).ToUnixTimeMilliseconds();
        await hub.HandleMarketAsync($"{{\"data\":{{\"e\":\"bookTicker\",\"E\":{futureMs},\"T\":{futureMs},\"s\":\"BTCUSDT\",\"b\":\"98\",\"a\":\"102\",\"B\":\"4\",\"A\":\"5\"}}}}",default);
        var future=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));
        Assert.Equal(fresh.BookUpdatedAt,future.BookUpdatedAt);
        Assert.Equal(1,future.Messages);

        var olderMs=now.AddSeconds(-5).ToUnixTimeMilliseconds();
        await hub.HandleMarketAsync($"{{\"data\":{{\"e\":\"bookTicker\",\"E\":{olderMs},\"T\":{olderMs},\"s\":\"BTCUSDT\",\"b\":\"97\",\"a\":\"103\",\"B\":\"6\",\"A\":\"7\"}}}}",default);
        var older=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));
        Assert.Equal(fresh.BookUpdatedAt,older.BookUpdatedAt);
        Assert.Equal(fresh.BestBid,older.BestBid);
        Assert.Equal(1,older.Messages);
    }

    [Fact]
    public async Task ValidKnownEventsAloneAdvanceMessageCount()
    {
        await using var hub=Hub();
        var bookMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await hub.HandleMarketAsync($"{{\"data\":{{\"e\":\"bookTicker\",\"E\":{bookMs},\"T\":{bookMs},\"s\":\"BTCUSDT\",\"b\":\"99\",\"a\":\"101\",\"B\":\"2\",\"A\":\"3\"}}}}",default);
        await hub.HandleMarketAsync("{\"data\":{\"e\":\"aggTrade\",\"s\":\"BTCUSDT\",\"p\":\"100\",\"q\":\"1\",\"m\":false}}",default);
        var snapshot=Assert.IsType<RealtimeMarketSnapshot>(hub.GetSnapshot("BTCUSDT"));
        Assert.Equal(2,snapshot.Messages);Assert.Equal(100,snapshot.LastPrice);Assert.Equal(99,snapshot.BestBid);Assert.Equal(101,snapshot.BestAsk);Assert.NotEqual(default,snapshot.UpdatedAt);Assert.NotEqual(default,snapshot.BookUpdatedAt);
    }

    private RealTimeMarketHub Hub()=>new(ExchangeEnvironment.Testnet,["BTCUSDT"],"test-key",new AgentSqliteStore(_database));
}

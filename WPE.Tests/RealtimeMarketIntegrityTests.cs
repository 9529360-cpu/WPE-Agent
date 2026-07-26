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

    private RealTimeMarketHub Hub()=>new(ExchangeEnvironment.Testnet,["BTCUSDT"],"test-key",new AgentSqliteStore(_database));
}

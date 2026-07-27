using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class HistoricalNewsResearchTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,27,12,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-historical-news-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    public HistoricalNewsResearchTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public async Task HistoricalQueryReturnsOnlyTheRequestedSymbolAndPointInTimeRange()
    {
        var store=new AgentSqliteStore(DatabasePath,()=>Now);
        await store.SaveNewsAsync([
            News("btc-old",["BTC"],Now.AddYears(-5)),
            News("eth-old",["ETH"],Now.AddYears(-5)),
            News("btc-future",["BTC"],Now.AddDays(1))
        ],CancellationToken.None);

        var rows=await store.GetHistoricalNewsFeaturesAsync("BTCUSDT",Now.AddYears(-6),Now,100,CancellationToken.None);

        var row=Assert.Single(rows);
        Assert.Equal("BTC",row.Asset);
        Assert.Equal(Now.AddYears(-5).UtcDateTime,row.PublishedAtUtc);
    }

    [Fact]
    public void NewsMomentumBacktestUsesOnlyNewsPublishedBeforeEachClosedCandle()
    {
        var start=Now.AddHours(-700);
        var candles=Enumerable.Range(0,700).Select(i=>
        {
            var close=100m+i*.05m;
            return new CandleEvidence(start.AddHours(i).UtcDateTime,close-.1m,close+.2m,close-.2m,close,100,10000,10,50);
        }).ToArray();
        var profile=new StrategyProfile{Id="NEWS-POINT-IN-TIME",Version="news-v1",Symbol="BTCUSDT",Family=StrategyFamily.NewsMomentum,Parameters=LocalStrategyParameters.For(StrategyFamily.NewsMomentum,0)};
        NewsFeature[] past=[new("BTC",1,.9,2,"MARKET",candles[500].OpenTime)];
        NewsFeature[] future=[new("BTC",1,.9,2,"MARKET",candles[^1].OpenTime.AddHours(1))];
        var engine=new HistoricalResearchEngine();

        var withPast=engine.Validate(profile,candles,past,new RiskLimits{MinimumBacktestTrades=1});
        var withFuture=engine.Validate(profile,candles,future,new RiskLimits{MinimumBacktestTrades=1});

        Assert.True(withPast.Trades>0);
        Assert.Equal(0,withFuture.Trades);
    }

    private static NewsEvidence News(string id,IReadOnlyList<string> assets,DateTimeOffset published)=>
        new("SEC",id,"https://www.sec.gov/news/"+id,published.UtcDateTime,published.AddMinutes(1).UtcDateTime,"official",id,assets,"historical",.9,2,"MARKET",false,.8);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

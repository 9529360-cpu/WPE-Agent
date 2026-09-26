using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class OpportunityUniverseV1Tests
{
    [Fact]
    public void DefaultOpportunityDensityRemainsBoundedButBroad()
    {
        Assert.Equal(64,OpportunityUniverseSelectorV1.DefaultWatchLimit);
        Assert.Equal(12,OpportunityUniverseSelectorV1.DefaultDeepLimit);
        Assert.Equal(TimeSpan.FromMinutes(2),OpportunityUniverseSelectorV1.DefaultRefreshInterval);
    }

    [Fact]
    public void MarketWideScanKeepsConfiguredSymbolsAndBoundsDeepAnalysis()
    {
        var tickers=Enumerable.Range(1,120)
            .Select(i=>new OpportunityTickerV1(
                $"COIN{i}USDT",
                10m+i,
                1_000_000m*i,
                i%2==0?i/10m:-i/10m))
            .Append(new OpportunityTickerV1("BTCUSDT",84_000m,50_000_000_000m,1.2m))
            .Append(new OpportunityTickerV1("ETHUSDT",4_000m,20_000_000_000m,-2.1m))
            .ToArray();

        var universe=OpportunityUniverseSelectorV1.Select(
            tickers,
            ["BTCUSDT","ETHUSDT"],
            new DateTimeOffset(2026,9,26,20,0,0,TimeSpan.Zero));

        Assert.Equal(OpportunityUniverseSelectorV1.DefaultWatchLimit,universe.WatchSymbols.Count);
        Assert.Equal(OpportunityUniverseSelectorV1.DefaultDeepLimit,universe.DeepAnalysisSymbols.Count);
        Assert.Contains("BTCUSDT",universe.WatchSymbols);
        Assert.Contains("ETHUSDT",universe.WatchSymbols);
        Assert.Contains("BTCUSDT",universe.DeepAnalysisSymbols);
        Assert.Contains("ETHUSDT",universe.DeepAnalysisSymbols);
        Assert.Equal(universe.WatchSymbols.Count,universe.WatchSymbols.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(universe.DeepAnalysisSymbols.Count,universe.DeepAnalysisSymbols.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(universe.DeepAnalysisSymbols,symbol=>Assert.Contains(symbol,universe.WatchSymbols,StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeepAnalysisUsesLiquidityAndActivityWithoutACompositeScore()
    {
        var tickers=new[]
        {
            new OpportunityTickerV1("BTCUSDT",84_000m,90_000_000_000m,.5m),
            new OpportunityTickerV1("ETHUSDT",4_000m,40_000_000_000m,1m),
            new OpportunityTickerV1("SOLUSDT",200m,15_000_000_000m,2m),
            new OpportunityTickerV1("DOGEUSDT",.2m,12_000_000_000m,18m),
            new OpportunityTickerV1("XRPUSDT",2m,10_000_000_000m,-15m),
            new OpportunityTickerV1("ADAUSDT",1m,8_000_000_000m,3m),
            new OpportunityTickerV1("AVAXUSDT",60m,7_000_000_000m,4m),
            new OpportunityTickerV1("LINKUSDT",30m,6_000_000_000m,5m),
            new OpportunityTickerV1("BADBTC",100m,999_000_000_000m,99m)
        };

        var universe=OpportunityUniverseSelectorV1.Select(
            tickers,
            ["BTCUSDT"],
            DateTimeOffset.UtcNow,
            watchLimit:8,
            deepLimit:4);

        Assert.Equal("BTCUSDT",universe.DeepAnalysisSymbols[0]);
        Assert.Contains("ETHUSDT",universe.DeepAnalysisSymbols);
        Assert.Contains("DOGEUSDT",universe.DeepAnalysisSymbols);
        Assert.DoesNotContain("BADBTC",universe.WatchSymbols);
    }

    [Fact]
    public void FallbackNeverInventsUnconfiguredSymbols()
    {
        var universe=OpportunityUniverseSelectorV1.ConfiguredOnly(
            ["btcusdt","ETHUSDT","BTCUSDT"],
            DateTimeOffset.UtcNow);

        Assert.Equal(["BTCUSDT","ETHUSDT"],universe.DeepAnalysisSymbols);
        Assert.Equal("configured-fallback",universe.Source);
    }
}

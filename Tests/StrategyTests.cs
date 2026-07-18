#if UNIT_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Strategies;
using Xunit;

namespace 币安量化机器人.Tests;

public class StrategyTests
{
    [Fact]
    public async Task MeanReversionProducesSignal()
    {
        var analyzer = new MultiTimeframeAnalyzer();
        var ml = new RandomForestSignalGenerator();
        var featureStore = new Application.Services.InMemoryFeatureStore();
        var strategy = new MeanReversionStrategy(analyzer, ml, featureStore, new StrategyParameters(new Dictionary<string, double>
        {
            ["entry_z_score"] = 1.0,
            ["base_quantity"] = 1
        }));

        var context = new Application.Services.StrategyContext("BTCUSDT", new FakeMarketDataService(), new Core.Risk.RiskManager(), featureStore, (_, _) => ValueTask.CompletedTask, (_, _) => ValueTask.CompletedTask);
        strategy.Initialize(context);
        var observation = new MarketObservation("BTCUSDT", TimeSpan.FromMinutes(1), DateTime.UtcNow, 100, 101, 99, 100.5, 10,
            new Dictionary<string, double> { ["sma"] = 100, ["std"] = 0.5 }, null);
        var decision = await strategy.EvaluateAsync(observation);
        Assert.NotEqual(Core.Models.TradeActionType.Hold, decision.Action.ActionType);
    }

    private sealed class FakeMarketDataService : Core.Abstractions.IMarketDataService
    {
        public IAsyncEnumerable<MarketObservation> StreamAsync(string symbol, IEnumerable<TimeSpan> timeframes, System.Threading.CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<MarketObservation>();

        public ValueTask<TimeframeSeries> GetSeriesAsync(string symbol, TimeSpan timeframe, DateTime start, DateTime end, System.Threading.CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new TimeframeSeries(timeframe, new List<MarketObservation>()));
    }
}
#endif

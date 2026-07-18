using System;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Risk;

namespace 币安量化机器人.Core.Abstractions;

public interface IStrategyContext
{
    string Symbol { get; }

    IMarketDataService MarketData { get; }

    IRiskManager RiskManager { get; }

    IFeatureStore FeatureStore { get; }

    ValueTask PublishSignalAsync(TradeSignal signal, CancellationToken cancellationToken = default);

    ValueTask RecordMetricsAsync(StrategyPerformanceSnapshot snapshot, CancellationToken cancellationToken = default);
}

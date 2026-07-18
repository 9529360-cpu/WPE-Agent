using System;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Risk;

namespace 币安量化机器人.Application.Services;

public class StrategyContext : IStrategyContext
{
    private readonly Func<TradeSignal, CancellationToken, ValueTask> _signalPublisher;
    private readonly Func<StrategyPerformanceSnapshot, CancellationToken, ValueTask> _metricsRecorder;

    public StrategyContext(
        string symbol,
        IMarketDataService marketData,
        IRiskManager riskManager,
        IFeatureStore featureStore,
        Func<TradeSignal, CancellationToken, ValueTask> signalPublisher,
        Func<StrategyPerformanceSnapshot, CancellationToken, ValueTask> metricsRecorder)
    {
        Symbol = symbol;
        MarketData = marketData;
        RiskManager = riskManager;
        FeatureStore = featureStore;
        _signalPublisher = signalPublisher;
        _metricsRecorder = metricsRecorder;
    }

    public string Symbol { get; }

    public IMarketDataService MarketData { get; }

    public IRiskManager RiskManager { get; }

    public IFeatureStore FeatureStore { get; }

    public ValueTask PublishSignalAsync(TradeSignal signal, CancellationToken cancellationToken = default)
        => _signalPublisher.Invoke(signal, cancellationToken);

    public ValueTask RecordMetricsAsync(StrategyPerformanceSnapshot snapshot, CancellationToken cancellationToken = default)
        => _metricsRecorder.Invoke(snapshot, cancellationToken);
}

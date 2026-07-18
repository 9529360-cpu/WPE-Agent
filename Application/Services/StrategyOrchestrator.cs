using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Monitoring;
using 币安量化机器人.Services.Execution;

namespace 币安量化机器人.Application.Services;

public class StrategyOrchestrator
{
    private readonly IMarketDataService _marketDataService;
    private readonly IRiskManager _riskManager;
    private readonly IFeatureStore _featureStore;
    private readonly ITradeMonitoringHub _monitoringHub;
    private readonly StrategySignalExecutor _signalExecutor;
    private readonly OrderLifecycleManager _orderLifecycleManager;

    public StrategyOrchestrator(
        IMarketDataService marketDataService,
        IRiskManager riskManager,
        IFeatureStore featureStore,
        ITradeMonitoringHub monitoringHub,
        StrategySignalExecutor signalExecutor,
        OrderLifecycleManager orderLifecycleManager)
    {
        _marketDataService = marketDataService;
        _riskManager = riskManager;
        _featureStore = featureStore;
        _monitoringHub = monitoringHub;
        _signalExecutor = signalExecutor;
        _orderLifecycleManager = orderLifecycleManager;
    }

    public async Task RunAsync(ITradingStrategy strategy, string symbol, IEnumerable<TimeSpan> timeframes, CancellationToken cancellationToken = default)
    {
        await _orderLifecycleManager.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var context = new StrategyContext(symbol, _marketDataService, _riskManager, _featureStore, PublishSignalAsync, RecordMetricsAsync);
        strategy.Initialize(context);

        await foreach (var observation in _marketDataService.StreamAsync(symbol, timeframes, cancellationToken))
        {
            await strategy.EvaluateAsync(observation, cancellationToken);
        }
    }

    private ValueTask PublishSignalAsync(TradeSignal signal, CancellationToken cancellationToken)
        => _signalExecutor.HandleAsync(signal, cancellationToken);

    private ValueTask RecordMetricsAsync(StrategyPerformanceSnapshot snapshot, CancellationToken cancellationToken)
        => _monitoringHub.BroadcastMetricsAsync(snapshot, cancellationToken);
}

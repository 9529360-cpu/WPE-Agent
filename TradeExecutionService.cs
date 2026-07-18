using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Models;
using 币安量化机器人.Monitoring;
using 币安量化机器人.Services;

namespace 币安量化机器人.Application.Services;

/// <summary>
/// Service that listens to trade signals published on the monitoring hub and
/// executes corresponding orders via the Binance API client after consulting
/// the risk manager. This bridges strategy signals to actual trades.
/// </summary>
public class TradeExecutionService
{
    private readonly BinanceApiClient _api;
    private readonly IRiskManager _riskManager;
    private readonly InMemoryTradeMonitoringHub _monitoringHub;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TradeExecutionService"/> class.
    /// </summary>
    /// <param name="api">Injected Binance API client used for order placement.</param>
    /// <param name="riskManager">Risk manager used to approve or reject trades.</param>
    /// <param name="monitoringHub">Monitoring hub from which trade signals are consumed.</param>
    public TradeExecutionService(BinanceApiClient api, IRiskManager riskManager, InMemoryTradeMonitoringHub monitoringHub)
    {
        _api = api;
        _riskManager = riskManager;
        _monitoringHub = monitoringHub;
    }

    /// <summary>
    /// Starts the background loop that consumes trade signals and executes orders.
    /// </summary>
    public void Start()
    {
        _ = Task.Run(ProcessSignalsAsync);
    }

    /// <summary>
    /// Requests cancellation of the processing loop.
    /// </summary>
    public void Stop() => _cts.Cancel();

    private async Task ProcessSignalsAsync()
    {
        try
        {
            await foreach (var ev in _monitoringHub.Reader.ReadAllAsync(_cts.Token))
            {
                var signal = ev.Signal;
                if (signal == null)
                    continue;

                if (signal.Action.ActionType == TradeActionType.Hold)
                    continue;

                if (!_riskManager.Approve(signal.Action))
                    continue;

                try
                {
                    switch (signal.Action.ActionType)
                    {
                        case TradeActionType.EnterLong:
                        case TradeActionType.EnterShort:
                            await ExecuteEntryAsync(signal);
                            break;
                        case TradeActionType.Exit:
                            await ExecuteExitAsync(signal);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Trade execution error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful cancellation
        }
    }

    private async Task ExecuteEntryAsync(TradeSignal signal)
    {
        var side = signal.Action.ActionType == TradeActionType.EnterLong ? OrderSide.Buy : OrderSide.Sell;
        var request = new OrderRequest
        {
            Symbol = signal.Symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = (decimal)signal.Action.Quantity
        };

        var response = await _api.PlaceOrderAsync(request);

        await _riskManager.RecordFillAsync(new TradeFill(
            signal.Symbol,
            (double)response.ExecutedQuantity,
            (double)response.Price,
            DateTime.UtcNow,
            signal.Action.ActionType));
    }

    private async Task ExecuteExitAsync(TradeSignal signal)
    {
        var positions = await _api.GetPositionsAsync();
        var position = positions.FirstOrDefault(p => p.Symbol.Equals(signal.Symbol, StringComparison.OrdinalIgnoreCase));
        if (position == null || position.PositionAmt == 0)
            return;

        var side = position.PositionAmt > 0 ? OrderSide.Sell : OrderSide.Buy;
        var request = new OrderRequest
        {
            Symbol = signal.Symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = Math.Abs(position.PositionAmt)
        };
        var response = await _api.PlaceOrderAsync(request);
        await _riskManager.RecordFillAsync(new TradeFill(
            signal.Symbol,
            (double)response.ExecutedQuantity,
            (double)response.Price,
            DateTime.UtcNow,
            signal.Action.ActionType));
    }
}
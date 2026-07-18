using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services.Execution;

public class ExecutionService
{
    private readonly BinanceApiClient _apiClient;
    private readonly IOrderStateStore _stateStore;
    private readonly AppSettings _settings;

    public ExecutionService(BinanceApiClient apiClient, IOrderStateStore stateStore, AppSettings settings)
    {
        _apiClient = apiClient;
        _stateStore = stateStore;
        _settings = settings;
    }

    public async Task<ExecutionResult> SubmitAsync(TradeIntent intent, CancellationToken cancellationToken = default)
    {
        ValidateIntent(intent);

        var response = await _apiClient.PlaceOrderAsync(intent.ToOrderRequest(), cancellationToken).ConfigureAwait(false);
        var state = OrderState.FromResponse(response);
        await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);

        return new ExecutionResult(response.Symbol, response.OrderId, response.ClientOrderId, response.ExecutedQuantity, response.AvgPrice, response.Status, response.Time);
    }

    public async Task CancelAsync(string symbol, long orderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol is required", nameof(symbol));

        await _apiClient.CancelOrderAsync(symbol, orderId, cancellationToken).ConfigureAwait(false);
        var existing = await _stateStore.GetAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var canceled = existing with { Status = "CANCELED", UpdatedAt = DateTime.UtcNow };
            await _stateStore.SaveAsync(canceled, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyCollection<OrderState>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
        => _stateStore.GetOpenAsync(cancellationToken);

    private void ValidateIntent(TradeIntent intent)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        if (string.IsNullOrWhiteSpace(intent.Symbol))
            throw new InvalidOperationException("Symbol is required before submitting an order");

        if (intent.Quantity <= 0)
            throw new InvalidOperationException("Order quantity must be greater than zero");

        var maxQty = _settings.MaxOrderQuantity <= 0 ? 50m : _settings.MaxOrderQuantity;
        if (intent.Quantity > maxQty)
        {
            throw new InvalidOperationException($"Quantity {intent.Quantity.ToString(CultureInfo.InvariantCulture)} exceeds configured cap {maxQty.ToString(CultureInfo.InvariantCulture)}");
        }

        if (RequiresLimitPrice(intent.Type) && intent.Price is null)
            throw new InvalidOperationException("Limit order requires a price");

        if (RequiresStopPrice(intent.Type) && intent.StopPrice is null)
            throw new InvalidOperationException("Stop order requires a stop price");
    }

    private static bool RequiresLimitPrice(OrderType type)
        => type is OrderType.Limit or OrderType.StopLossLimit or OrderType.TakeProfitLimit;

    private static bool RequiresStopPrice(OrderType type)
        => type is OrderType.StopLoss or OrderType.StopLossLimit or OrderType.TakeProfit or OrderType.TakeProfitLimit;
}

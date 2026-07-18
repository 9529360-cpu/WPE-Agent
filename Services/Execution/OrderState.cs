using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services.Execution;

public record OrderState
{
    public long ExchangeOrderId { get; init; }
    public string ClientOrderId { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public decimal RequestedQuantity { get; init; }
    public decimal ExecutedQuantity { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime UpdatedAt { get; init; }

    public bool IsTerminal => string.Equals(Status, "FILLED", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(Status, "CANCELED", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(Status, "REJECTED", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(Status, "EXPIRED", StringComparison.OrdinalIgnoreCase);

    public static OrderState FromResponse(OrderResponse response)
    {
        if (response is null)
            throw new ArgumentNullException(nameof(response));

        return new OrderState
        {
            ExchangeOrderId = response.OrderId,
            ClientOrderId = response.ClientOrderId,
            Symbol = response.Symbol,
            RequestedQuantity = response.ExecutedQuantity,
            ExecutedQuantity = response.ExecutedQuantity,
            Status = response.Status,
            UpdatedAt = response.Time
        };
    }
}

public interface IOrderStateStore
{
    Task SaveAsync(OrderState state, CancellationToken cancellationToken = default);

    Task<OrderState?> GetAsync(long orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<OrderState>> GetOpenAsync(CancellationToken cancellationToken = default);
}

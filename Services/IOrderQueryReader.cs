using 币安量化机器人.Models;

namespace 币安量化机器人.Services;

public interface IOrderQueryReader
{
    Task<IReadOnlyList<OrderResponse>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TradeExecution>> GetRecentTradesAsync(string symbol, int limit = 20, CancellationToken cancellationToken = default);
}

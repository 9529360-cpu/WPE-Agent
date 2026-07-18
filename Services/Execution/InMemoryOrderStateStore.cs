using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace 币安量化机器人.Services.Execution;

public class InMemoryOrderStateStore : IOrderStateStore
{
    private readonly ConcurrentDictionary<long, OrderState> _states = new();

    public Task SaveAsync(OrderState state, CancellationToken cancellationToken = default)
    {
        _states.AddOrUpdate(state.ExchangeOrderId, state, (_, _) => state);
        return Task.CompletedTask;
    }

    public Task<OrderState?> GetAsync(long orderId, CancellationToken cancellationToken = default)
    {
        _states.TryGetValue(orderId, out var state);
        return Task.FromResult(state);
    }

    public Task<IReadOnlyCollection<OrderState>> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        var open = _states.Values.Where(s => !s.IsTerminal).ToArray();
        return Task.FromResult((IReadOnlyCollection<OrderState>)open);
    }
}

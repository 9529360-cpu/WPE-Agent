using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.RuntimeServices;

/// <summary>Thread-safe, provider-neutral positions and open orders observed from the active provider.</summary>
public sealed class RuntimeTradingStateStore
{
    private readonly object _gate = new();
    private RuntimeTradingState _current = RuntimeTradingState.Unsupported("Trading runtime has not started.");

    public RuntimeTradingState Read()
    {
        lock (_gate) return _current;
    }

    public void Publish(IReadOnlyList<ManagedPosition> positions, IReadOnlyList<ExchangeOrder> orders, DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(orders);
        var runtimePositions = positions
            .Select(x => new RuntimePositionV1(x.Symbol, x.Side.ToString(), x.Quantity, x.EntryPrice, x.UnrealizedPnl))
            .OrderBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Side, StringComparer.Ordinal).ToArray();
        var runtimeOrders = orders
            .Select(x => new RuntimeOrderV1(x.OrderId, x.Symbol, x.PositionSide?.ToString() ?? "Unknown", x.Type, x.Status,
                x.ExecutedQuantity, x.AvgPrice > 0 ? x.AvgPrice : null))
            .OrderBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.OrderId, StringComparer.Ordinal).ToArray();
        lock (_gate) _current = new(RuntimeCollectionState.Available, runtimePositions, runtimeOrders, observedAt ?? DateTimeOffset.UtcNow, null);
    }

    public void PublishError(string message)
    {
        lock (_gate) _current = RuntimeTradingState.Error(message);
    }

    public void MarkUnsupported(string message)
    {
        lock (_gate) _current = RuntimeTradingState.Unsupported(message);
    }
}

public sealed record RuntimeTradingState(
    RuntimeCollectionState State,
    IReadOnlyList<RuntimePositionV1> Positions,
    IReadOnlyList<RuntimeOrderV1> Orders,
    DateTimeOffset? UpdatedAt,
    string? Message)
{
    public static RuntimeTradingState Unsupported(string message) => new(RuntimeCollectionState.Unsupported, [], [], null, message);
    public static RuntimeTradingState Error(string message) => new(RuntimeCollectionState.Error, [], [], DateTimeOffset.UtcNow, message);
}

using WpeAgent.RuntimeContracts;

namespace WpeAgent.RuntimeServices;

/// <summary>Thread-safe, provider-neutral market state published by a live capability probe.</summary>
public sealed class RuntimeMarketStateStore
{
    private readonly object _gate = new();
    private RuntimeMarketState _current = RuntimeMarketState.Unsupported("Provider capability probe has not run.");
    public RuntimeMarketState Read() { lock (_gate) return _current; }
    public void Publish(IReadOnlyDictionary<string, ExchangeCapability> capabilities, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var items = capabilities.Values.OrderBy(x => x.ExchangeId).ThenBy(x => x.CanonicalSymbol, StringComparer.OrdinalIgnoreCase).ToArray();
        var markets = items.Where(x => x.Status == CapabilityStatus.Available && x.CanRead).Select(x => new RuntimeMarketV1(x.ExchangeId, x.ProviderId, x.CanonicalSymbol, x.NativeSymbol, "available")).ToArray();
        var state = items.Length == 0 ? RuntimeCollectionState.Unsupported : items.Any(x => x.Status == CapabilityStatus.Error) ? RuntimeCollectionState.Error : items.Any(x => x.Status != CapabilityStatus.Available) ? RuntimeCollectionState.Unsupported : RuntimeCollectionState.Available;
        var updatedAt = items.Length == 0 ? DateTimeOffset.UtcNow : items.Max(x => x.CheckedAt);
        lock (_gate) _current = new RuntimeMarketState(state, markets, items.Select(ToRuntime).ToArray(), updatedAt, message);
    }
    public void PublishError(string message) { lock (_gate) _current = RuntimeMarketState.Error(message); }
    private static RuntimeCapabilityV1 ToRuntime(ExchangeCapability x) => new(x.ExchangeId, x.ProviderId, x.CanonicalSymbol, x.NativeSymbol, x.MarketType, x.Status, x.CanRead, x.CanTrade, x.TestnetAvailable, x.CheckedAt, x.Failure);
}

public sealed record RuntimeMarketState(RuntimeCollectionState State, IReadOnlyList<RuntimeMarketV1> Markets, IReadOnlyList<RuntimeCapabilityV1> Capabilities, DateTimeOffset? UpdatedAt, string? Message)
{
    public static RuntimeMarketState Unsupported(string message) => new(RuntimeCollectionState.Unsupported, [], [], null, message);
    public static RuntimeMarketState Error(string message) => new(RuntimeCollectionState.Error, [], [], DateTimeOffset.UtcNow, message);
}

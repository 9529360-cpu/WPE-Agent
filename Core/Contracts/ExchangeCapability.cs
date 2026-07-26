namespace WpeAgent.RuntimeContracts;

/// <summary>Instrument identity independent of an exchange's native symbol spelling.</summary>
public sealed record Instrument(
    string CanonicalSymbol,
    string NativeSymbol,
    string ExchangeId,
    string ProviderId,
    MarketType MarketType = MarketType.Perpetual);

public enum MarketType
{
    Spot,
    Perpetual,
    Futures,
    Options
}

public enum CapabilityStatus
{
    Available,
    Unsupported,
    Stale,
    Error
}

/// <summary>A point-in-time provider capability observation. Missing capability is never inferred.</summary>
public sealed record ExchangeCapability(
    string ExchangeId,
    string ProviderId,
    string CanonicalSymbol,
    string NativeSymbol,
    MarketType MarketType,
    CapabilityStatus Status,
    bool CanRead,
    bool CanTrade,
    bool TestnetAvailable,
    DateTimeOffset CheckedAt,
    string? Failure = null);

public sealed record CapabilityCheckResult(bool Allowed, string Reason)
{
    public static CapabilityCheckResult Reject(string reason) => new(false, reason);
    public static CapabilityCheckResult Allow() => new(true, "capability available");
}

/// <summary>Minimal precondition contract for providers before any caller may request trading.</summary>
public interface IProviderCapabilityPrecondition
{
    CapabilityCheckResult Check(Instrument instrument, ExchangeCapability? capability, bool testnet);
}

/// <summary>Deterministic, fail-closed capability gate. It does not place or cancel orders.</summary>
public sealed class ProviderCapabilityPrecondition : IProviderCapabilityPrecondition
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(15);

    public CapabilityCheckResult Check(Instrument instrument, ExchangeCapability? capability, bool testnet)
    {
        if (capability is null)
            return CapabilityCheckResult.Reject("capability unknown");
        if (!string.Equals(capability.ExchangeId, instrument.ExchangeId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(capability.ProviderId, instrument.ProviderId, StringComparison.OrdinalIgnoreCase))
            return CapabilityCheckResult.Reject("provider or exchange mismatch");
        if (!string.Equals(capability.CanonicalSymbol, instrument.CanonicalSymbol, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(capability.NativeSymbol, instrument.NativeSymbol, StringComparison.OrdinalIgnoreCase) ||
            capability.MarketType != instrument.MarketType)
            return CapabilityCheckResult.Reject("instrument unsupported");
        if (capability.Status != CapabilityStatus.Available)
            return CapabilityCheckResult.Reject($"capability {capability.Status.ToString().ToLowerInvariant()}");
        var age = DateTimeOffset.UtcNow - capability.CheckedAt;
        if (capability.CheckedAt == default || age > MaximumAge || age < -MaximumAge)
            return CapabilityCheckResult.Reject("capability stale");
        if (!capability.CanTrade)
            return CapabilityCheckResult.Reject("trading unsupported");
        if (testnet && !capability.TestnetAvailable)
            return CapabilityCheckResult.Reject("testnet unavailable");
        return CapabilityCheckResult.Allow();
    }
}

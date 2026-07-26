namespace WpeAgent.Equities;

public enum EquityProviderCapabilityStatus
{
    Available,
    Unsupported,
    Stale,
    PermissionDenied,
    RateLimited,
    Error,
    Unknown
}

public sealed record EquityProviderIdentity(string ProviderId, string DisplayName);

public sealed record EquityProviderCapabilityRequest(
    string VenueId,
    string InstrumentId,
    DateTimeOffset RequestedAtUtc,
    EquitySessionPhase Session = EquitySessionPhase.Unknown);

public sealed record EquityProviderCapability(
    EquityProviderIdentity Identity,
    EquityProviderCapabilityStatus Status,
    EquityLicenseStatus License,
    string VenueId,
    EquityProviderCapabilityStatus VenueSupport,
    string InstrumentId,
    EquityProviderCapabilityStatus InstrumentSupport,
    EquityProviderCapabilityStatus CalendarSupport,
    EquityProviderCapabilityStatus SessionSupport,
    EquityProviderCapabilityStatus QuoteFreshness,
    EquityProviderCapabilityStatus CorporateActionAdjustment,
    EquityProviderCapabilityStatus RateLimit,
    DateTimeOffset CheckedAtUtc,
    string? Message = null,
    EquitySessionPhase SupportedSession = EquitySessionPhase.Unknown,
    DateTimeOffset? DataAsOfUtc = null)
{
    public static EquityProviderCapability Unsupported(
        EquityProviderIdentity identity,
        EquityProviderCapabilityRequest request,
        string message) => new(
            identity,
            EquityProviderCapabilityStatus.Unsupported,
            EquityLicenseStatus.Unsupported,
            request.VenueId,
            EquityProviderCapabilityStatus.Unsupported,
            request.InstrumentId,
            EquityProviderCapabilityStatus.Unsupported,
            EquityProviderCapabilityStatus.Unsupported,
            EquityProviderCapabilityStatus.Unsupported,
            EquityProviderCapabilityStatus.Unsupported,
            EquityProviderCapabilityStatus.Unsupported,
            EquityProviderCapabilityStatus.Unsupported,
            request.RequestedAtUtc,
            message);
}

public interface IEquityMarketDataProvider
{
    EquityProviderIdentity Identity { get; }
    Task<EquityProviderCapability> ProbeCapabilityAsync(
        EquityProviderCapabilityRequest request,
        CancellationToken cancellationToken);
    Task<EquityMarketDataProjection> ReadMarketDataAsync(
        EquityMarketDataRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Default local provider. It has no endpoint and never produces market data.</summary>
public sealed class UnsupportedEquityMarketDataProvider : IEquityMarketDataProvider
{
    private const string Message = "No authorized equity market-data provider is configured.";
    public EquityProviderIdentity Identity { get; } = new("unsupported", "Unsupported equity provider");

    public Task<EquityProviderCapability> ProbeCapabilityAsync(
        EquityProviderCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(EquityProviderCapability.Unsupported(Identity, request, Message));
    }

    public Task<EquityMarketDataProjection> ReadMarketDataAsync(
        EquityMarketDataRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(EquityMarketDataProjection.Unsupported(Message));
    }
}

public sealed record EquityProviderConformanceResult(
    bool ReadAttempted,
    bool CanRead,
    EquityProviderCapability Capability,
    EquityMarketDataProjection Projection);

/// <summary>Purely offline contract harness. Network ownership, credentials, and retries remain provider concerns.</summary>
public sealed class EquityMarketDataProviderConformanceHarness
{
    public static readonly TimeSpan CapabilityMaxAge = TimeSpan.FromMinutes(5);

    public async Task<EquityProviderConformanceResult> EvaluateAsync(
        IEquityMarketDataProvider provider,
        EquityProviderCapabilityRequest capabilityRequest,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(capabilityRequest);

        EquityProviderCapability capability;
        try
        {
            capability = await provider.ProbeCapabilityAsync(capabilityRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            capability = ErrorCapability(provider.Identity, capabilityRequest, evaluatedAtUtc,
                $"Equity provider capability probe failed: {ex.Message}");
            return Blocked(capability, EquityMarketDataStatus.Error);
        }

        var blockedStatus = ValidateCapability(provider.Identity, capabilityRequest, capability, evaluatedAtUtc);
        if (blockedStatus is not null)
            return Blocked(capability, blockedStatus.Value);

        try
        {
            var projection = await provider.ReadMarketDataAsync(
                new([capabilityRequest.InstrumentId], evaluatedAtUtc), cancellationToken);
            var store = new EquityMarketDataStateStore();
            store.Publish(projection);
            var normalized = store.Read(evaluatedAtUtc);
            return new(true, normalized.Status == EquityMarketDataStatus.Available, capability, normalized);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(true, false, capability,
                EquityMarketDataProjection.Error($"Equity provider read failed: {ex.Message}", evaluatedAtUtc));
        }
    }

    private static EquityMarketDataStatus? ValidateCapability(
        EquityProviderIdentity providerIdentity,
        EquityProviderCapabilityRequest request,
        EquityProviderCapability capability,
        DateTimeOffset evaluatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(providerIdentity.ProviderId) ||
            string.IsNullOrWhiteSpace(capability.Identity.ProviderId) ||
            !string.Equals(providerIdentity.ProviderId, capability.Identity.ProviderId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.VenueId, capability.VenueId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.InstrumentId, capability.InstrumentId, StringComparison.OrdinalIgnoreCase))
            return EquityMarketDataStatus.Error;

        if (capability.CheckedAtUtc > evaluatedAtUtc ||
            evaluatedAtUtc - capability.CheckedAtUtc > CapabilityMaxAge ||
            capability.Status == EquityProviderCapabilityStatus.Stale ||
            capability.QuoteFreshness == EquityProviderCapabilityStatus.Stale)
            return EquityMarketDataStatus.Stale;

        if (capability.License != EquityLicenseStatus.Authorized)
            return EquityMarketDataStatus.Unsupported;

        var dimensions = new[]
        {
            capability.Status,
            capability.VenueSupport,
            capability.InstrumentSupport,
            capability.CalendarSupport,
            capability.SessionSupport,
            capability.QuoteFreshness,
            capability.CorporateActionAdjustment,
            capability.RateLimit
        };
        if (dimensions.Any(x => x is EquityProviderCapabilityStatus.Error or EquityProviderCapabilityStatus.RateLimited))
            return EquityMarketDataStatus.Error;
        if (dimensions.Any(x => x != EquityProviderCapabilityStatus.Available))
            return EquityMarketDataStatus.Unsupported;
        return null;
    }

    private static EquityProviderConformanceResult Blocked(
        EquityProviderCapability capability,
        EquityMarketDataStatus status) => new(
            false,
            false,
            capability,
            status == EquityMarketDataStatus.Error
                ? EquityMarketDataProjection.Error(capability.Message ?? "Equity provider capability error.", capability.CheckedAtUtc)
                : EquityMarketDataProjection.Unsupported(capability.Message ?? "Equity provider capability is unavailable.") with
                {
                    Status = status,
                    License = capability.License
                });

    private static EquityProviderCapability ErrorCapability(
        EquityProviderIdentity identity,
        EquityProviderCapabilityRequest request,
        DateTimeOffset checkedAtUtc,
        string message) => EquityProviderCapability.Unsupported(identity, request, message) with
        {
            Status = EquityProviderCapabilityStatus.Error,
            CheckedAtUtc = checkedAtUtc
        };
}

namespace WpeAgent.Equities;

public sealed record EquityProviderSelectionPreference(string ProviderId, int Priority);

public sealed record EquityProviderSelectionResult(
    EquityMarketDataStatus Status,
    IEquityMarketDataProvider? Provider,
    EquityProviderCapability? Capability,
    string Message)
{
    public bool CanRead => Status == EquityMarketDataStatus.Available && Provider is not null;

    public static EquityProviderSelectionResult Unsupported(string message) =>
        new(EquityMarketDataStatus.Unsupported, null, null, message);
}

/// <summary>Explicit provider registry and deterministic selection policy. It never reads or combines quotes.</summary>
public sealed class EquityMarketDataProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IEquityMarketDataProvider> _providers;
    private readonly bool _hasIdentityConflict;

    public EquityMarketDataProviderRegistry(IEnumerable<IEquityMarketDataProvider>? providers = null)
    {
        var items = (providers ?? []).ToArray();
        _hasIdentityConflict = items.Any(x => string.IsNullOrWhiteSpace(x.Identity.ProviderId)) ||
            items.GroupBy(x => x.Identity.ProviderId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1);
        _providers = _hasIdentityConflict
            ? new Dictionary<string, IEquityMarketDataProvider>(StringComparer.OrdinalIgnoreCase)
            : items.ToDictionary(x => x.Identity.ProviderId, StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _providers.Count;

    public async Task<EquityProviderSelectionResult> SelectAsync(
        IReadOnlyList<EquityProviderSelectionPreference> configuration,
        EquityProviderCapabilityRequest request,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(request);

        if (_providers.Count == 0)
            return EquityProviderSelectionResult.Unsupported("Equity provider registry is empty.");
        if (_hasIdentityConflict)
            return EquityProviderSelectionResult.Unsupported("Equity provider identities conflict.");
        if (configuration.Count == 0)
            return EquityProviderSelectionResult.Unsupported("No equity provider is explicitly configured.");
        if (string.IsNullOrWhiteSpace(request.VenueId) || string.IsNullOrWhiteSpace(request.InstrumentId) ||
            request.Session == EquitySessionPhase.Unknown)
            return EquityProviderSelectionResult.Unsupported("Venue, instrument, and session must be explicit.");
        if (configuration.Any(x => string.IsNullOrWhiteSpace(x.ProviderId)) ||
            configuration.GroupBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
            return EquityProviderSelectionResult.Unsupported("Equity provider configuration conflicts.");
        if (configuration.Any(x => !_providers.ContainsKey(x.ProviderId)))
            return EquityProviderSelectionResult.Unsupported("A configured equity provider is not registered.");

        var candidates = new List<Candidate>();
        foreach (var preference in configuration)
        {
            var provider = _providers[preference.ProviderId];
            EquityProviderCapability capability;
            try
            {
                capability = await provider.ProbeCapabilityAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (IsEligible(provider, capability, request, evaluatedAtUtc))
                candidates.Add(new(provider, capability, preference.Priority));
        }

        if (candidates.Count == 0)
            return EquityProviderSelectionResult.Unsupported("No configured equity provider has an available capability.");

        if (candidates.Select(x => x.Capability.DataAsOfUtc).Distinct().Skip(1).Any())
            return EquityProviderSelectionResult.Unsupported("Eligible equity providers report inconsistent data timestamps.");

        var bestPriority = candidates.Min(x => x.Priority);
        var preferred = candidates.Where(x => x.Priority == bestPriority).ToArray();
        if (preferred.Length != 1)
            return EquityProviderSelectionResult.Unsupported("Eligible equity provider priorities are tied.");

        var selected = preferred[0];
        return new(EquityMarketDataStatus.Available, selected.Provider, selected.Capability,
            "Equity provider selected from explicit configuration.");
    }

    private static bool IsEligible(
        IEquityMarketDataProvider provider,
        EquityProviderCapability capability,
        EquityProviderCapabilityRequest request,
        DateTimeOffset evaluatedAtUtc)
    {
        if (capability is null ||
            !string.Equals(provider.Identity.ProviderId, capability.Identity.ProviderId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.VenueId, capability.VenueId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.InstrumentId, capability.InstrumentId, StringComparison.OrdinalIgnoreCase) ||
            request.Session != capability.SupportedSession ||
            capability.License != EquityLicenseStatus.Authorized ||
            capability.DataAsOfUtc is null ||
            capability.DataAsOfUtc > evaluatedAtUtc ||
            evaluatedAtUtc - capability.DataAsOfUtc.Value > EquityMarketDataStateStore.StaleAfter ||
            capability.CheckedAtUtc > evaluatedAtUtc ||
            evaluatedAtUtc - capability.CheckedAtUtc > EquityMarketDataProviderConformanceHarness.CapabilityMaxAge)
            return false;

        return new[]
        {
            capability.Status,
            capability.VenueSupport,
            capability.InstrumentSupport,
            capability.CalendarSupport,
            capability.SessionSupport,
            capability.QuoteFreshness,
            capability.CorporateActionAdjustment,
            capability.RateLimit
        }.All(x => x == EquityProviderCapabilityStatus.Available);
    }

    private sealed record Candidate(
        IEquityMarketDataProvider Provider,
        EquityProviderCapability Capability,
        int Priority);
}

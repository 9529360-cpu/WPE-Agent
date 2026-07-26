namespace WpeAgent.Equities;

/// <summary>Thread-safe read-side projection. It never discovers, fetches, or synthesizes equity data.</summary>
public sealed class EquityMarketDataStateStore
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    private const string NoAuthorizedSource = "No authorized equity market-data source is connected.";
    private readonly object _gate = new();
    private EquityMarketDataProjection _current = EquityMarketDataProjection.Unsupported(NoAuthorizedSource);

    public EquityMarketDataProjection Read() => Read(DateTimeOffset.UtcNow);

    public EquityMarketDataProjection Read(DateTimeOffset asOfUtc)
    {
        lock (_gate)
        {
            if (_current.Status != EquityMarketDataStatus.Available)
                return _current;
            if (_current.UpdatedAtUtc is null || asOfUtc - _current.UpdatedAtUtc.Value > StaleAfter)
                return Withhold(_current, EquityMarketDataStatus.Stale, "Equity market-data projection has expired.");
            return _current;
        }
    }

    public void Publish(EquityMarketDataProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_gate)
        {
            if (projection.UpdatedAtUtc is not null && _current.UpdatedAtUtc is not null &&
                projection.UpdatedAtUtc.Value < _current.UpdatedAtUtc.Value)
            {
                _current = Withhold(projection, EquityMarketDataStatus.Error,
                    "Out-of-order equity market-data projection was rejected.");
                return;
            }
            _current = Normalize(projection);
        }
    }

    public void MarkUnsupported(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_gate) _current = EquityMarketDataProjection.Unsupported(message);
    }

    public void PublishError(string message, DateTimeOffset observedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_gate) _current = EquityMarketDataProjection.Error(message, observedAtUtc);
    }

    public bool TryPublishAuthoritative(
        EquityMarketDataProjection projection,
        out EquityMarketDataProjection authoritativeState)
    {
        return TryPublishAuthoritative(projection, out _, out authoritativeState);
    }

    public bool TryPublishAuthoritative(
        EquityMarketDataProjection projection,
        out EquityMarketDataProjection previousState,
        out EquityMarketDataProjection authoritativeState)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_gate)
        {
            previousState = _current;
            var normalized = Normalize(projection);
            if (normalized.Status != EquityMarketDataStatus.Available ||
                normalized.UpdatedAtUtc is null ||
                (_current.UpdatedAtUtc is not null && normalized.UpdatedAtUtc <= _current.UpdatedAtUtc))
            {
                authoritativeState = _current;
                return false;
            }

            _current = normalized;
            authoritativeState = _current;
            return true;
        }
    }

    private static EquityMarketDataProjection Normalize(EquityMarketDataProjection value)
    {
        if (value.License != EquityLicenseStatus.Authorized)
            return EquityMarketDataProjection.Unsupported(value.Message ?? NoAuthorizedSource) with { License = value.License };

        if (string.IsNullOrWhiteSpace(value.ProviderId) || value.Quotes.Any(HasMissingProvenance))
            return Withhold(value, EquityMarketDataStatus.Unsupported,
                "Equity market-data provenance is incomplete.");

        if (value.UpdatedAtUtc is null || value.UpdatedAtUtc > DateTimeOffset.UtcNow || HasTimestampAfterProjection(value))
            return Withhold(value, EquityMarketDataStatus.Error,
                "Equity market-data projection contains inconsistent timestamps.");

        if (HasProviderConflict(value) || HasCurrencyConflict(value))
            return Withhold(value, EquityMarketDataStatus.Error,
                "Equity market-data provenance contains provider or currency conflicts.");

        if (HasSessionConflict(value))
            return Withhold(value, EquityMarketDataStatus.Error,
                "Equity quote session does not match the venue session projection.");

        if (HasInvalidAdjustmentBasis(value))
            return Withhold(value, EquityMarketDataStatus.Unsupported,
                "Equity quote adjustment basis is missing or unadjusted.");

        var effectiveStatus = value.Status;
        if (value.Freshness != EquityFreshnessStatus.Fresh)
            effectiveStatus = value.Freshness == EquityFreshnessStatus.Stale
                ? EquityMarketDataStatus.Stale
                : EquityMarketDataStatus.Unsupported;
        if (value.Quality != EquityQualityStatus.Verified)
            effectiveStatus = value.Quality switch
            {
                EquityQualityStatus.Degraded => EquityMarketDataStatus.Stale,
                EquityQualityStatus.Invalid => EquityMarketDataStatus.Error,
                _ => EquityMarketDataStatus.Unsupported
            };

        if (effectiveStatus != EquityMarketDataStatus.Available)
            return Withhold(value, effectiveStatus, value.Message);

        if (value.Halts.Any(x => x.Status != EquityTradingHaltStatus.NotHalted))
            return Withhold(value, EquityMarketDataStatus.Unsupported,
                "Equity quote is unavailable while trading is halted or halt state is uncertain.");

        if (HasUnadjustedCorporateAction(value))
            return Withhold(value, EquityMarketDataStatus.Unsupported,
                "Equity quote is not adjusted for an effective corporate action.");

        return value with
        {
            Quotes = value.Quotes.ToArray(),
            Sessions = value.Sessions.ToArray(),
            Halts = value.Halts.ToArray(),
            CorporateActions = value.CorporateActions.ToArray()
        };
    }

    private static bool HasTimestampAfterProjection(EquityMarketDataProjection value)
    {
        var updatedAt = value.UpdatedAtUtc!.Value;
        return value.Quotes.Any(x => x.AsOfUtc > updatedAt ||
                                     x.ObservedAtUtc > updatedAt ||
                                     x.ExchangeTimestampUtc > updatedAt ||
                                     x.AsOfUtc != x.ExchangeTimestampUtc ||
                                     x.ObservedAtUtc < x.ExchangeTimestampUtc) ||
               value.Sessions.Any(x => x.ObservedAtUtc > updatedAt) ||
               value.Halts.Any(x => x.ObservedAtUtc > updatedAt) ||
               value.CorporateActions.Any(x => x.ObservedAtUtc > updatedAt);
    }

    private static bool HasMissingProvenance(EquityQuote quote) =>
        string.IsNullOrWhiteSpace(quote.InstrumentId) ||
        string.IsNullOrWhiteSpace(quote.VenueId) ||
        string.IsNullOrWhiteSpace(quote.ProviderId) ||
        string.IsNullOrWhiteSpace(quote.SourceId) ||
        string.IsNullOrWhiteSpace(quote.Currency) ||
        quote.ObservedAtUtc is null ||
        quote.ExchangeTimestampUtc is null ||
        quote.Session == EquitySessionPhase.Unknown ||
        quote.AdjustmentBasis == EquityAdjustmentBasis.Unknown;

    private static bool HasProviderConflict(EquityMarketDataProjection value) =>
        value.Quotes.Any(x => !string.Equals(x.ProviderId, value.ProviderId, StringComparison.OrdinalIgnoreCase)) ||
        value.CorporateActions.Any(x => !string.Equals(x.SourceId, value.ProviderId, StringComparison.OrdinalIgnoreCase));

    private static bool HasCurrencyConflict(EquityMarketDataProjection value) =>
        value.Quotes.Any(x => !IsIsoCurrency(x.Currency)) ||
        value.Quotes.Select(x => x.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any() ||
        value.CorporateActions.Any(action => action.Currency is not null &&
            (!IsIsoCurrency(action.Currency) || value.Quotes.Any(quote =>
                string.Equals(quote.InstrumentId, action.InstrumentId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(quote.Currency, action.Currency, StringComparison.OrdinalIgnoreCase))));

    private static bool HasSessionConflict(EquityMarketDataProjection value) =>
        value.Quotes.Any(quote =>
            quote.Session is EquitySessionPhase.Unknown or EquitySessionPhase.Closed or EquitySessionPhase.Break ||
            !value.Sessions.Any(session =>
                string.Equals(session.VenueId, quote.VenueId, StringComparison.OrdinalIgnoreCase) &&
                session.Phase == quote.Session));

    private static bool HasInvalidAdjustmentBasis(EquityMarketDataProjection value) =>
        value.Quotes.Any(quote => quote.AdjustmentStatus switch
        {
            EquityPriceAdjustmentStatus.Adjusted => quote.AdjustmentBasis is not
                (EquityAdjustmentBasis.SplitAdjusted or EquityAdjustmentBasis.TotalReturnAdjusted),
            EquityPriceAdjustmentStatus.NotApplicable => quote.AdjustmentBasis != EquityAdjustmentBasis.NotApplicable,
            _ => true
        });

    private static bool IsIsoCurrency(string value) =>
        value.Length == 3 && value.All(character => character is >= 'A' and <= 'Z');

    private static bool HasUnadjustedCorporateAction(EquityMarketDataProjection value) =>
        value.Quotes.Any(quote =>
            quote.AdjustmentStatus is EquityPriceAdjustmentStatus.Unadjusted or EquityPriceAdjustmentStatus.Unknown &&
            value.CorporateActions.Any(action =>
                string.Equals(action.InstrumentId, quote.InstrumentId, StringComparison.OrdinalIgnoreCase) &&
                action.EffectiveDate <= DateOnly.FromDateTime(quote.AsOfUtc.UtcDateTime)));

    private static EquityMarketDataProjection Withhold(
        EquityMarketDataProjection value,
        EquityMarketDataStatus status,
        string? message) => value with
        {
            Status = status,
            Quotes = [],
            Sessions = [],
            Halts = [],
            CorporateActions = [],
            Message = message
        };
}

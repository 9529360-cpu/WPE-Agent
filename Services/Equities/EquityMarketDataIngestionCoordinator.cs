namespace WpeAgent.Equities;

public sealed record EquityMarketDataIngestionResult(
    bool Accepted,
    EquityMarketDataProjection AuthoritativeState,
    string Message,
    EquityIngestionAuditReceipt? Receipt = null);

public static class EquityIngestionReasonCodes
{
    public const string Accepted = "ACCEPTED";
    public const string SelectionInvalid = "SELECTION_INVALID";
    public const string ProviderReadError = "PROVIDER_READ_ERROR";
    public const string BatchProvenanceMismatch = "BATCH_PROVENANCE_MISMATCH";
    public const string QualityRejected = "QUALITY_REJECTED";
    public const string NotNewer = "NOT_NEWER";
}

/// <summary>Validates one selected provider batch and atomically replaces, never merges, authoritative state.</summary>
public sealed class EquityMarketDataIngestionCoordinator
{
    private readonly EquityIngestionAuditContext _auditContext;

    public EquityMarketDataIngestionCoordinator(
        EquityIngestionAuditLedger? auditLedger = null,
        EquityIngestionAuditContext? auditContext = null)
    {
        AuditLedger = auditLedger ?? new EquityIngestionAuditLedger();
        _auditContext = auditContext ?? new("unconfigured", "unknown", "uncorrelated");
    }

    public EquityIngestionAuditLedger AuditLedger { get; }

    public async Task<EquityMarketDataIngestionResult> IngestAsync(
        EquityProviderSelectionResult selection,
        EquityProviderCapabilityRequest request,
        EquityMarketDataStateStore stateStore,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stateStore);

        if (!SelectionMatchesRequest(selection, request, evaluatedAtUtc))
            return Rejected(selection, request, stateStore, null, evaluatedAtUtc,
                EquityIngestionReasonCodes.SelectionInvalid,
                "Selected equity provider capability is unavailable or inconsistent.");

        EquityMarketDataProjection batch;
        try
        {
            batch = await selection.Provider!.ReadMarketDataAsync(
                new([request.InstrumentId], evaluatedAtUtc), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Rejected(selection, request, stateStore, null, evaluatedAtUtc,
                EquityIngestionReasonCodes.ProviderReadError,
                $"Selected equity provider read failed: {ex.Message}");
        }

        if (!BatchMatchesSelection(batch, selection, request))
            return Rejected(selection, request, stateStore, batch, evaluatedAtUtc,
                EquityIngestionReasonCodes.BatchProvenanceMismatch,
                "Equity market-data batch provenance does not match the selected capability.");

        var staging = new EquityMarketDataStateStore();
        staging.Publish(batch);
        var normalized = staging.Read(evaluatedAtUtc);
        if (normalized.Status != EquityMarketDataStatus.Available)
            return Rejected(selection, request, stateStore, batch, evaluatedAtUtc,
                EquityIngestionReasonCodes.QualityRejected,
                normalized.Message ?? "Equity market-data batch failed quality validation.");

        if (!stateStore.TryPublishAuthoritative(normalized, out var previous, out var authoritative))
            return Complete(selection, request, batch, evaluatedAtUtc, false,
                EquityIngestionReasonCodes.NotNewer, previous, authoritative,
                "Equity market-data batch is not newer than authoritative state.");

        return Complete(selection, request, batch, evaluatedAtUtc, true,
            EquityIngestionReasonCodes.Accepted, previous, authoritative,
            "Equity market-data batch accepted atomically.");
    }

    private static bool SelectionMatchesRequest(
        EquityProviderSelectionResult selection,
        EquityProviderCapabilityRequest request,
        DateTimeOffset evaluatedAtUtc)
    {
        var provider = selection.Provider;
        var capability = selection.Capability;
        return selection.CanRead && provider is not null && capability is not null &&
               capability.Status == EquityProviderCapabilityStatus.Available &&
               capability.License == EquityLicenseStatus.Authorized &&
               string.Equals(provider.Identity.ProviderId, capability.Identity.ProviderId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(request.VenueId, capability.VenueId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(request.InstrumentId, capability.InstrumentId, StringComparison.OrdinalIgnoreCase) &&
               request.Session != EquitySessionPhase.Unknown &&
               request.Session == capability.SupportedSession &&
               capability.DataAsOfUtc is not null &&
               capability.DataAsOfUtc <= evaluatedAtUtc &&
               evaluatedAtUtc - capability.DataAsOfUtc.Value <= EquityMarketDataStateStore.StaleAfter &&
               capability.CheckedAtUtc <= evaluatedAtUtc &&
               evaluatedAtUtc - capability.CheckedAtUtc <= EquityMarketDataProviderConformanceHarness.CapabilityMaxAge &&
               new[]
               {
                   capability.VenueSupport,
                   capability.InstrumentSupport,
                   capability.CalendarSupport,
                   capability.SessionSupport,
                   capability.QuoteFreshness,
                   capability.CorporateActionAdjustment,
                   capability.RateLimit
               }.All(status => status == EquityProviderCapabilityStatus.Available);
    }

    private static bool BatchMatchesSelection(
        EquityMarketDataProjection batch,
        EquityProviderSelectionResult selection,
        EquityProviderCapabilityRequest request)
    {
        var capability = selection.Capability!;
        var providerId = selection.Provider!.Identity.ProviderId;
        return batch is not null &&
               batch.Status == EquityMarketDataStatus.Available &&
               batch.License == EquityLicenseStatus.Authorized &&
               string.Equals(batch.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
               batch.UpdatedAtUtc is not null &&
               batch.Quotes.Count > 0 &&
               batch.Quotes.All(quote =>
                   string.Equals(quote.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(quote.InstrumentId, request.InstrumentId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(quote.VenueId, request.VenueId, StringComparison.OrdinalIgnoreCase) &&
                   quote.Session == request.Session &&
                   quote.ExchangeTimestampUtc == capability.DataAsOfUtc &&
                   quote.ObservedAtUtc is not null &&
                   quote.ExchangeTimestampUtc is not null &&
                   quote.ObservedAtUtc >= quote.ExchangeTimestampUtc &&
                   !string.IsNullOrWhiteSpace(quote.Currency) &&
                   quote.AdjustmentBasis != EquityAdjustmentBasis.Unknown) &&
               batch.Sessions.Any(session =>
                   string.Equals(session.VenueId, request.VenueId, StringComparison.OrdinalIgnoreCase) &&
                   session.Phase == request.Session) &&
               batch.Sessions.All(session =>
                   string.Equals(session.VenueId, request.VenueId, StringComparison.OrdinalIgnoreCase) &&
                   session.Phase == request.Session) &&
               batch.Halts.All(halt =>
                   string.Equals(halt.InstrumentId, request.InstrumentId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(halt.VenueId, request.VenueId, StringComparison.OrdinalIgnoreCase)) &&
               batch.CorporateActions.All(action =>
                   string.Equals(action.InstrumentId, request.InstrumentId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(action.SourceId, providerId, StringComparison.OrdinalIgnoreCase));
    }

    private EquityMarketDataIngestionResult Rejected(
        EquityProviderSelectionResult selection,
        EquityProviderCapabilityRequest request,
        EquityMarketDataStateStore stateStore,
        EquityMarketDataProjection? batch,
        DateTimeOffset evaluatedAtUtc,
        string reasonCode,
        string message)
    {
        var authoritative = stateStore.Read(evaluatedAtUtc);
        return Complete(selection, request, batch, evaluatedAtUtc, false,
            reasonCode, authoritative, authoritative, message);
    }

    private EquityMarketDataIngestionResult Complete(
        EquityProviderSelectionResult selection,
        EquityProviderCapabilityRequest request,
        EquityMarketDataProjection? batch,
        DateTimeOffset evaluatedAtUtc,
        bool accepted,
        string reasonCode,
        EquityMarketDataProjection previous,
        EquityMarketDataProjection authoritative,
        string message)
    {
        var receipt = AuditLedger.Append(_auditContext, new(
            evaluatedAtUtc,
            selection.Provider?.Identity.ProviderId ?? selection.Capability?.Identity.ProviderId ?? "unknown",
            request.InstrumentId,
            request.VenueId,
            EquityIngestionCanonicalHash.HashProjection(batch),
            EquityIngestionCanonicalHash.HashCapability(selection.Capability),
            batch?.UpdatedAtUtc ?? selection.Capability?.DataAsOfUtc,
            accepted ? EquityIngestionQualityDecision.Accepted : EquityIngestionQualityDecision.Rejected,
            reasonCode,
            EquityIngestionCanonicalHash.HashProjection(previous),
            EquityIngestionCanonicalHash.HashProjection(authoritative)));
        return new(accepted, authoritative, message, receipt);
    }
}

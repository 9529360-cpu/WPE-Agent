using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WpeAgent.Equities;

public enum EquityIngestionQualityDecision { Accepted, Rejected }

public sealed record EquityIngestionAuditContext(
    string ConfigurationId,
    string ConfigurationVersion,
    string CorrelationId);

public sealed record EquityIngestionAuditReceipt(
    long Sequence,
    DateTimeOffset RecordedAtUtc,
    string ProviderId,
    string ConfigurationId,
    string ConfigurationVersion,
    string InstrumentId,
    string VenueId,
    string BatchHash,
    string AuthorizationCapabilityHash,
    DateTimeOffset? AsOfUtc,
    EquityIngestionQualityDecision QualityDecision,
    string ReasonCode,
    string PreviousSnapshotHash,
    string NewSnapshotHash,
    string CorrelationId,
    string PreviousReceiptHash,
    string ReceiptHash);

public sealed record EquityIngestionAuditDraft(
    DateTimeOffset RecordedAtUtc,
    string ProviderId,
    string InstrumentId,
    string VenueId,
    string BatchHash,
    string AuthorizationCapabilityHash,
    DateTimeOffset? AsOfUtc,
    EquityIngestionQualityDecision QualityDecision,
    string ReasonCode,
    string PreviousSnapshotHash,
    string NewSnapshotHash);

public sealed class EquityIngestionAuditLedger
{
    public const string GenesisHash = "GENESIS";
    private readonly object _gate = new();
    private readonly List<EquityIngestionAuditReceipt> _receipts = [];

    public EquityIngestionAuditReceipt Append(EquityIngestionAuditContext context, EquityIngestionAuditDraft draft)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        lock (_gate)
        {
            var sequence = _receipts.Count + 1L;
            var previousHash = _receipts.Count == 0 ? GenesisHash : _receipts[^1].ReceiptHash;
            var unsigned = new EquityIngestionAuditReceipt(
                sequence, draft.RecordedAtUtc, draft.ProviderId,
                context.ConfigurationId, context.ConfigurationVersion,
                draft.InstrumentId, draft.VenueId, draft.BatchHash,
                draft.AuthorizationCapabilityHash, draft.AsOfUtc,
                draft.QualityDecision, draft.ReasonCode,
                draft.PreviousSnapshotHash, draft.NewSnapshotHash,
                context.CorrelationId, previousHash, string.Empty);
            var receipt = unsigned with { ReceiptHash = EquityIngestionCanonicalHash.HashReceipt(unsigned) };
            _receipts.Add(receipt);
            return receipt;
        }
    }

    public IReadOnlyList<EquityIngestionAuditReceipt> Read()
    {
        lock (_gate) return _receipts.ToArray();
    }

    public static bool VerifyChain(IReadOnlyList<EquityIngestionAuditReceipt> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        var previousHash = GenesisHash;
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            if (receipt.Sequence != index + 1L ||
                !string.Equals(receipt.PreviousReceiptHash, previousHash, StringComparison.Ordinal) ||
                !string.Equals(receipt.ReceiptHash, EquityIngestionCanonicalHash.HashReceipt(receipt), StringComparison.Ordinal))
                return false;
            previousHash = receipt.ReceiptHash;
        }
        return true;
    }
}

public static class EquityIngestionCanonicalHash
{
    public static string HashProjection(EquityMarketDataProjection? value)
    {
        var canonical = new CanonicalBuilder();
        if (value is null) return canonical.Add("NO_BATCH").Hash();
        canonical.Add(value.Status).Add(value.Freshness).Add(value.Quality).Add(value.License)
            .Add(value.ProviderId).Add(value.UpdatedAtUtc).Add(value.Message);
        foreach (var quote in value.Quotes.OrderBy(x => x.InstrumentId, StringComparer.Ordinal)
                     .ThenBy(x => x.VenueId, StringComparer.Ordinal).ThenBy(x => x.Currency, StringComparer.Ordinal)
                     .ThenBy(x => x.ExchangeTimestampUtc))
            canonical.Add(quote.InstrumentId).Add(quote.VenueId).Add(quote.Currency)
                .Add(quote.Last).Add(quote.Bid).Add(quote.Ask).Add(quote.AsOfUtc)
                .Add(quote.SourceId).Add(quote.AdjustmentStatus).Add(quote.ProviderId)
                .Add(quote.ObservedAtUtc).Add(quote.ExchangeTimestampUtc).Add(quote.Session).Add(quote.AdjustmentBasis);
        foreach (var session in value.Sessions.OrderBy(x => x.VenueId, StringComparer.Ordinal).ThenBy(x => x.TradingDate))
            canonical.Add(session.VenueId).Add(session.TimeZoneId).Add(session.TradingDate)
                .Add(session.Phase).Add(session.OpensAtUtc).Add(session.ClosesAtUtc).Add(session.ObservedAtUtc);
        foreach (var halt in value.Halts.OrderBy(x => x.InstrumentId, StringComparer.Ordinal)
                     .ThenBy(x => x.VenueId, StringComparer.Ordinal))
            canonical.Add(halt.InstrumentId).Add(halt.VenueId).Add(halt.Status).Add(halt.ObservedAtUtc)
                .Add(halt.Reason).Add(halt.ExpectedResumeAtUtc);
        foreach (var action in value.CorporateActions.OrderBy(x => x.ActionId, StringComparer.Ordinal))
            canonical.Add(action.ActionId).Add(action.InstrumentId).Add(action.Type).Add(action.EffectiveDate)
                .Add(action.ObservedAtUtc).Add(action.SourceId).Add(action.CashAmount).Add(action.Currency)
                .Add(action.Ratio).Add(action.NewInstrumentId);
        return canonical.Hash();
    }

    public static string HashCapability(EquityProviderCapability? value)
    {
        var canonical = new CanonicalBuilder();
        if (value is null) return canonical.Add("NO_CAPABILITY").Hash();
        return canonical.Add(value.Identity.ProviderId).Add(value.Identity.DisplayName)
            .Add(value.Status).Add(value.License).Add(value.VenueId).Add(value.VenueSupport)
            .Add(value.InstrumentId).Add(value.InstrumentSupport).Add(value.CalendarSupport)
            .Add(value.SessionSupport).Add(value.QuoteFreshness).Add(value.CorporateActionAdjustment)
            .Add(value.RateLimit).Add(value.CheckedAtUtc).Add(value.Message)
            .Add(value.SupportedSession).Add(value.DataAsOfUtc).Hash();
    }

    public static string HashReceipt(EquityIngestionAuditReceipt value) => new CanonicalBuilder()
        .Add(value.Sequence).Add(value.RecordedAtUtc).Add(value.ProviderId)
        .Add(value.ConfigurationId).Add(value.ConfigurationVersion)
        .Add(value.InstrumentId).Add(value.VenueId).Add(value.BatchHash)
        .Add(value.AuthorizationCapabilityHash).Add(value.AsOfUtc)
        .Add(value.QualityDecision).Add(value.ReasonCode)
        .Add(value.PreviousSnapshotHash).Add(value.NewSnapshotHash)
        .Add(value.CorrelationId).Add(value.PreviousReceiptHash).Hash();

    private sealed class CanonicalBuilder
    {
        private readonly StringBuilder _value = new();

        public CanonicalBuilder Add(object? value)
        {
            var text = value switch
            {
                null => "<null>",
                DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                decimal number => number.ToString(CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
            _value.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('|');
            return this;
        }

        public string Hash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_value.ToString())));
    }
}

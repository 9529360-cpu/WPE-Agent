namespace WpeAgent.Equities;

public enum EquityMarketDataStatus { Available, Unsupported, Stale, Error }
public enum EquityFreshnessStatus { Fresh, Stale, Unknown }
public enum EquityQualityStatus { Verified, Degraded, Invalid, Unknown }
public enum EquityLicenseStatus { Authorized, Restricted, Unsupported, Unknown }
public enum EquitySessionPhase { Unknown, PreMarket, Open, Auction, Break, PostMarket, Closed }
public enum EquityTradingHaltStatus { NotHalted, Halted, Resuming, Unknown }
public enum EquityCorporateActionType { Dividend, Split, ReverseSplit, Merger, Spinoff, RightsIssue, SymbolChange, Delisting, Other }
public enum EquityPriceAdjustmentStatus { Adjusted, Unadjusted, NotApplicable, Unknown }
public enum EquityAdjustmentBasis { Unknown, Raw, SplitAdjusted, TotalReturnAdjusted, NotApplicable }

public sealed record EquityMarketDataRequest(
    IReadOnlyList<string> InstrumentIds,
    DateTimeOffset RequestedAtUtc);

public sealed record EquityQuote(
    string InstrumentId,
    string VenueId,
    string Currency,
    decimal Last,
    decimal? Bid,
    decimal? Ask,
    DateTimeOffset AsOfUtc,
    string SourceId,
    EquityPriceAdjustmentStatus AdjustmentStatus = EquityPriceAdjustmentStatus.Unknown,
    string? ProviderId = null,
    DateTimeOffset? ObservedAtUtc = null,
    DateTimeOffset? ExchangeTimestampUtc = null,
    EquitySessionPhase Session = EquitySessionPhase.Unknown,
    EquityAdjustmentBasis AdjustmentBasis = EquityAdjustmentBasis.Unknown);

public sealed record EquitySessionProjection(
    string VenueId,
    string TimeZoneId,
    DateOnly TradingDate,
    EquitySessionPhase Phase,
    DateTimeOffset? OpensAtUtc,
    DateTimeOffset? ClosesAtUtc,
    DateTimeOffset ObservedAtUtc);

public sealed record EquityTradingHaltProjection(
    string InstrumentId,
    string VenueId,
    EquityTradingHaltStatus Status,
    DateTimeOffset ObservedAtUtc,
    string? Reason = null,
    DateTimeOffset? ExpectedResumeAtUtc = null);

public sealed record EquityCorporateActionProjection(
    string ActionId,
    string InstrumentId,
    EquityCorporateActionType Type,
    DateOnly EffectiveDate,
    DateTimeOffset ObservedAtUtc,
    string SourceId,
    decimal? CashAmount = null,
    string? Currency = null,
    decimal? Ratio = null,
    string? NewInstrumentId = null);

public sealed record EquityMarketDataProjection(
    EquityMarketDataStatus Status,
    EquityFreshnessStatus Freshness,
    EquityQualityStatus Quality,
    EquityLicenseStatus License,
    IReadOnlyList<EquityQuote> Quotes,
    IReadOnlyList<EquitySessionProjection> Sessions,
    IReadOnlyList<EquityTradingHaltProjection> Halts,
    IReadOnlyList<EquityCorporateActionProjection> CorporateActions,
    DateTimeOffset? UpdatedAtUtc,
    string? Message = null,
    string? ProviderId = null)
{
    public static EquityMarketDataProjection Unsupported(string message) =>
        new(EquityMarketDataStatus.Unsupported, EquityFreshnessStatus.Unknown,
            EquityQualityStatus.Unknown, EquityLicenseStatus.Unsupported,
            [], [], [], [], null, message);

    public static EquityMarketDataProjection Error(string message, DateTimeOffset observedAtUtc) =>
        new(EquityMarketDataStatus.Error, EquityFreshnessStatus.Unknown,
            EquityQualityStatus.Invalid, EquityLicenseStatus.Unknown,
            [], [], [], [], observedAtUtc, message);
}

public interface IEquityMarketDataReader
{
    Task<EquityMarketDataProjection> ReadAsync(EquityMarketDataRequest request, CancellationToken cancellationToken);
}

namespace 币安量化机器人.Core.Equities;

public enum EquityContractState
{
    Unknown = 0,
    Available,
    Unsupported,
    Stale,
    Error
}

public enum EquityInstrumentType
{
    Unknown = 0,
    CommonStock,
    PreferredStock,
    DepositaryReceipt,
    ExchangeTradedFund,
    RealEstateInvestmentTrust,
    ClosedEndFund
}

public enum EquitySessionKind
{
    Unknown = 0,
    PreMarket,
    Regular,
    AfterHours,
    Overnight,
    Auction
}

public enum EquityMarketPhase
{
    Unknown = 0,
    Closed,
    PreOpen,
    OpeningAuction,
    Continuous,
    ClosingAuction,
    PostClose
}

public enum EquityCorporateActionType
{
    Unknown = 0,
    CashDividend,
    StockDividend,
    Split,
    ReverseSplit,
    RightsIssue,
    SpinOff,
    Merger,
    Acquisition,
    SymbolChange,
    Delisting
}

public enum EquityAccountType
{
    Unknown = 0,
    Cash,
    BrokerageCredit,
    Retirement,
    Custody
}

public enum EquityPositionSide
{
    Unknown = 0,
    Flat,
    Long,
    Short
}

public enum EquityOrderSide
{
    Unknown = 0,
    Buy,
    Sell,
    SellShort,
    BuyToCover
}

public enum EquityOrderType
{
    Unknown = 0,
    Market,
    Limit,
    Stop,
    StopLimit,
    MarketOnOpen,
    LimitOnOpen,
    MarketOnClose,
    LimitOnClose
}

public enum EquityTimeInForce
{
    Unknown = 0,
    Day,
    GoodTillCanceled,
    ImmediateOrCancel,
    FillOrKill,
    OnOpen,
    OnClose
}

public enum EquityOrderStatus
{
    Unknown = 0,
    PendingReview,
    Approved,
    PendingSubmit,
    Submitted,
    PartiallyFilled,
    Filled,
    PendingCancel,
    Canceled,
    Rejected,
    Expired
}

public enum EquitySettlementStatus
{
    Unknown = 0,
    Pending,
    Settled,
    Failed,
    Reversed
}

public enum EquityShortableStatus
{
    Unknown = 0,
    NotShortable,
    Shortable
}

public enum EquityBorrowStatus
{
    Unknown = 0,
    NotRequired,
    Unavailable,
    Available,
    LocateRequired,
    Located
}

public enum EquityExtendedHoursStatus
{
    Unknown = 0,
    Unsupported,
    Disabled,
    Enabled
}

public enum EquityHaltStatus
{
    Unknown = 0,
    NotHalted,
    Halted,
    ResumptionPending
}

public readonly record struct MicCode(string Value);

public readonly record struct EquityCurrency(string Code);

public sealed record EquityVenue(
    string VenueId,
    MicCode Mic,
    string Name,
    string CountryCode,
    string TimeZoneId,
    EquityContractState State);

public sealed record EquityInstrument(
    string InstrumentId,
    string Symbol,
    string NativeSymbol,
    EquityInstrumentType Type,
    EquityVenue PrimaryVenue,
    EquityCurrency TradingCurrency,
    decimal TickSize,
    decimal QuantityIncrement,
    EquityContractState State);

public sealed record EquityTradingSession(
    EquitySessionKind Kind,
    TimeOnly OpensAt,
    TimeOnly ClosesAt,
    string TimeZoneId,
    EquityMarketPhase Phase,
    EquityContractState State);

public sealed record EquityTradingDay(
    DateOnly Date,
    bool IsTradingDay,
    IReadOnlyList<EquityTradingSession> Sessions,
    EquityContractState State,
    string? ClosureReason = null);

public sealed record EquityTradingCalendar(
    string CalendarId,
    MicCode Mic,
    string TimeZoneId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<EquityTradingDay> Days,
    EquityContractState State);

public sealed record EquityCorporateAction(
    string ActionId,
    string InstrumentId,
    EquityCorporateActionType Type,
    DateOnly? AnnouncementDate,
    DateOnly? ExDate,
    DateOnly? RecordDate,
    DateOnly? PayableDate,
    decimal? CashAmount,
    decimal? Ratio,
    EquityCurrency? Currency,
    EquityContractState State);

public sealed record EquityAccount(
    string AccountId,
    string ProviderId,
    EquityAccountType Type,
    EquityCurrency BaseCurrency,
    decimal Cash,
    decimal BuyingPower,
    EquityContractState State,
    DateTimeOffset ObservedAt);

public sealed record EquityPosition(
    string AccountId,
    string InstrumentId,
    EquityPositionSide Side,
    decimal Quantity,
    decimal AveragePrice,
    decimal MarketPrice,
    EquityCurrency Currency,
    DateTimeOffset ObservedAt,
    EquityContractState State);

public sealed record EquityShortSaleAvailability(
    EquityShortableStatus Shortable,
    EquityBorrowStatus Borrowable,
    decimal? AvailableQuantity,
    decimal? AnnualizedBorrowRate,
    DateTimeOffset ObservedAt,
    EquityContractState State);

public sealed record EquityExtendedHours(
    EquityExtendedHoursStatus Status,
    IReadOnlySet<EquitySessionKind> SupportedSessions,
    DateTimeOffset ObservedAt,
    EquityContractState State);

public sealed record EquityHalt(
    EquityHaltStatus Status,
    string? ReasonCode,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ExpectedResumeAt,
    EquityContractState State);

public sealed record EquityOrder(
    string OrderId,
    string ClientOrderId,
    string AccountId,
    string InstrumentId,
    EquityOrderSide Side,
    EquityOrderType Type,
    EquityTimeInForce TimeInForce,
    decimal Quantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    bool AllowExtendedHours,
    EquityOrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string CorrelationId);

public sealed record EquityFill(
    string FillId,
    string OrderId,
    string InstrumentId,
    decimal Quantity,
    decimal Price,
    EquityCurrency Currency,
    decimal Commission,
    DateTimeOffset ExecutedAt,
    string ExecutionVenueId,
    string CorrelationId);

public sealed record EquitySettlement(
    string SettlementId,
    string FillId,
    DateOnly TradeDate,
    DateOnly ContractualSettlementDate,
    DateOnly? ActualSettlementDate,
    decimal NetAmount,
    EquityCurrency Currency,
    EquitySettlementStatus Status,
    EquityContractState State);

public sealed record EquityOrderEligibility(
    EquityInstrument Instrument,
    EquityAccount Account,
    EquityTradingCalendar Calendar,
    EquityTradingSession Session,
    EquityShortSaleAvailability ShortSaleAvailability,
    EquityExtendedHours ExtendedHours,
    EquityHalt Halt);

public sealed record EquityContractCheck(bool IsValid, string ReasonCode)
{
    public static EquityContractCheck Pass(string reasonCode) => new(true, reasonCode);
    public static EquityContractCheck Fail(string reasonCode) => new(false, reasonCode);
}

public static class EquityContractInvariants
{
    public static EquityContractCheck CheckSession(EquityTradingSession session)
    {
        if (session.State != EquityContractState.Available ||
            session.Kind == EquitySessionKind.Unknown ||
            session.Phase == EquityMarketPhase.Unknown ||
            string.IsNullOrWhiteSpace(session.TimeZoneId))
            return EquityContractCheck.Fail("equity_session_unknown");
        if (session.OpensAt == session.ClosesAt)
            return EquityContractCheck.Fail("equity_session_window_invalid");
        if (session.Kind != EquitySessionKind.Overnight && session.OpensAt > session.ClosesAt)
            return EquityContractCheck.Fail("equity_session_window_invalid");
        if (session.Kind == EquitySessionKind.Auction &&
            session.Phase is not (EquityMarketPhase.OpeningAuction or EquityMarketPhase.ClosingAuction))
            return EquityContractCheck.Fail("equity_session_phase_invalid");
        if (session.Kind != EquitySessionKind.Auction &&
            session.Phase is EquityMarketPhase.OpeningAuction or EquityMarketPhase.ClosingAuction)
            return EquityContractCheck.Fail("equity_session_phase_invalid");
        if (session.Phase is EquityMarketPhase.Closed or EquityMarketPhase.PreOpen or EquityMarketPhase.PostClose)
            return EquityContractCheck.Fail("equity_session_not_tradable");

        return EquityContractCheck.Pass("equity_session_valid");
    }

    public static EquityContractCheck CheckCalendar(
        EquityTradingCalendar calendar,
        EquityTradingSession selectedSession)
    {
        if (calendar.State != EquityContractState.Available ||
            string.IsNullOrWhiteSpace(calendar.CalendarId) ||
            string.IsNullOrWhiteSpace(calendar.Mic.Value) ||
            string.IsNullOrWhiteSpace(calendar.TimeZoneId))
            return EquityContractCheck.Fail("equity_calendar_unknown");
        if (!StringComparer.Ordinal.Equals(calendar.TimeZoneId, selectedSession.TimeZoneId))
            return EquityContractCheck.Fail("equity_calendar_timezone_mismatch");

        var matchingDays = calendar.Days.Where(day => day.Sessions.Contains(selectedSession)).ToArray();
        if (matchingDays.Length != 1 ||
            matchingDays[0].State != EquityContractState.Available ||
            !matchingDays[0].IsTradingDay)
            return EquityContractCheck.Fail("equity_calendar_session_unavailable");

        return EquityContractCheck.Pass("equity_calendar_valid");
    }

    public static EquityContractCheck CheckCorporateAction(EquityCorporateAction action)
    {
        if (action.State != EquityContractState.Available ||
            action.Type == EquityCorporateActionType.Unknown ||
            string.IsNullOrWhiteSpace(action.ActionId) ||
            string.IsNullOrWhiteSpace(action.InstrumentId))
            return EquityContractCheck.Fail("equity_corporate_action_unknown");
        if (!DatesAreOrdered(action.AnnouncementDate, action.PayableDate))
            return EquityContractCheck.Fail("equity_corporate_action_dates_invalid");

        if (action.Type == EquityCorporateActionType.CashDividend &&
            (action.CashAmount is null or <= 0 ||
             action.Currency is null ||
             string.IsNullOrWhiteSpace(action.Currency.Value.Code) ||
             action.ExDate is null ||
             action.PayableDate is null))
            return EquityContractCheck.Fail("equity_corporate_action_cash_terms_invalid");

        if (action.Type is EquityCorporateActionType.StockDividend or
            EquityCorporateActionType.Split or
            EquityCorporateActionType.ReverseSplit or
            EquityCorporateActionType.RightsIssue or
            EquityCorporateActionType.SpinOff &&
            (action.Ratio is null or <= 0 || action.ExDate is null))
            return EquityContractCheck.Fail("equity_corporate_action_ratio_terms_invalid");

        return EquityContractCheck.Pass("equity_corporate_action_valid");
    }

    public static EquityContractCheck CheckSettlement(EquitySettlement settlement)
    {
        if (settlement.State != EquityContractState.Available ||
            settlement.Status == EquitySettlementStatus.Unknown ||
            string.IsNullOrWhiteSpace(settlement.SettlementId) ||
            string.IsNullOrWhiteSpace(settlement.FillId) ||
            string.IsNullOrWhiteSpace(settlement.Currency.Code))
            return EquityContractCheck.Fail("equity_settlement_unknown");
        if (settlement.ContractualSettlementDate < settlement.TradeDate)
            return EquityContractCheck.Fail("equity_settlement_dates_invalid");
        if (settlement.Status == EquitySettlementStatus.Settled &&
            (settlement.ActualSettlementDate is null ||
             settlement.ActualSettlementDate < settlement.TradeDate))
            return EquityContractCheck.Fail("equity_settlement_actual_date_required");
        if (settlement.Status == EquitySettlementStatus.Pending && settlement.ActualSettlementDate is not null)
            return EquityContractCheck.Fail("equity_settlement_pending_has_actual_date");

        return EquityContractCheck.Pass("equity_settlement_valid");
    }

    public static EquityContractCheck CheckShortSaleAvailability(EquityShortSaleAvailability availability)
    {
        if (availability.State != EquityContractState.Available ||
            availability.Shortable == EquityShortableStatus.Unknown ||
            availability.Borrowable == EquityBorrowStatus.Unknown)
            return EquityContractCheck.Fail("equity_shortability_unknown");
        if (availability.AnnualizedBorrowRate < 0 || availability.AvailableQuantity < 0)
            return EquityContractCheck.Fail("equity_borrow_terms_invalid");
        if (availability.Shortable == EquityShortableStatus.NotShortable &&
            availability.Borrowable is EquityBorrowStatus.Available or EquityBorrowStatus.Located)
            return EquityContractCheck.Fail("equity_shortability_conflict");
        if (availability.Borrowable is EquityBorrowStatus.Available or EquityBorrowStatus.Located &&
            availability.AvailableQuantity is null or <= 0)
            return EquityContractCheck.Fail("equity_borrow_quantity_unavailable");

        return EquityContractCheck.Pass("equity_shortability_valid");
    }

    public static EquityContractCheck CheckExtendedHours(EquityExtendedHours extendedHours)
    {
        if (extendedHours.State != EquityContractState.Available ||
            extendedHours.Status == EquityExtendedHoursStatus.Unknown)
            return EquityContractCheck.Fail("equity_extended_hours_unknown");

        var invalidSession = extendedHours.SupportedSessions.Any(kind =>
            kind is EquitySessionKind.Unknown or EquitySessionKind.Regular or EquitySessionKind.Auction);
        if (invalidSession)
            return EquityContractCheck.Fail("equity_extended_hours_sessions_invalid");
        if (extendedHours.Status == EquityExtendedHoursStatus.Enabled && extendedHours.SupportedSessions.Count == 0)
            return EquityContractCheck.Fail("equity_extended_hours_sessions_missing");
        if (extendedHours.Status is EquityExtendedHoursStatus.Disabled or EquityExtendedHoursStatus.Unsupported &&
            extendedHours.SupportedSessions.Count != 0)
            return EquityContractCheck.Fail("equity_extended_hours_status_conflict");

        return EquityContractCheck.Pass("equity_extended_hours_valid");
    }

    public static EquityContractCheck CheckHalt(EquityHalt halt)
    {
        if (halt.State != EquityContractState.Available || halt.Status == EquityHaltStatus.Unknown)
            return EquityContractCheck.Fail("equity_halt_unknown");
        if (halt.Status == EquityHaltStatus.Halted && string.IsNullOrWhiteSpace(halt.ReasonCode))
            return EquityContractCheck.Fail("equity_halt_reason_required");
        if (halt.Status == EquityHaltStatus.ResumptionPending &&
            (string.IsNullOrWhiteSpace(halt.ReasonCode) || halt.ExpectedResumeAt is null))
            return EquityContractCheck.Fail("equity_halt_resumption_unknown");
        if (halt.Status == EquityHaltStatus.NotHalted && halt.ExpectedResumeAt is not null)
            return EquityContractCheck.Fail("equity_halt_status_conflict");

        return EquityContractCheck.Pass("equity_halt_valid");
    }

    private static bool DatesAreOrdered(DateOnly? first, DateOnly? second) =>
        first is null || second is null || first <= second;
}

public static class EquityContractGuard
{
    public static bool CanRoute(EquityOrder order, EquityOrderEligibility eligibility, out string reasonCode)
    {
        if (!HasKnownOrderTerms(order))
            return Deny("equity_order_terms_unknown", out reasonCode);
        if (order.Status is not (EquityOrderStatus.Approved or EquityOrderStatus.PendingSubmit))
            return Deny("equity_order_not_approved", out reasonCode);
        if (!Matches(order, eligibility))
            return Deny("equity_order_identity_mismatch", out reasonCode);
        if (eligibility.Instrument.State != EquityContractState.Available ||
            eligibility.Instrument.Type == EquityInstrumentType.Unknown ||
            !HasKnownVenue(eligibility.Instrument.PrimaryVenue))
            return Deny("equity_instrument_unavailable", out reasonCode);
        if (eligibility.Account.State != EquityContractState.Available ||
            eligibility.Account.Type == EquityAccountType.Unknown)
            return Deny("equity_account_unavailable", out reasonCode);
        var sessionCheck = EquityContractInvariants.CheckSession(eligibility.Session);
        if (!sessionCheck.IsValid)
            return Deny(sessionCheck.ReasonCode, out reasonCode);
        var calendarCheck = EquityContractInvariants.CheckCalendar(eligibility.Calendar, eligibility.Session);
        if (!calendarCheck.IsValid)
            return Deny(calendarCheck.ReasonCode, out reasonCode);
        var haltCheck = EquityContractInvariants.CheckHalt(eligibility.Halt);
        if (!haltCheck.IsValid || eligibility.Halt.Status != EquityHaltStatus.NotHalted)
            return Deny("equity_halt_not_clear", out reasonCode);
        var isExtendedSession = eligibility.Session.Kind is
            EquitySessionKind.PreMarket or EquitySessionKind.AfterHours or EquitySessionKind.Overnight;
        if (order.AllowExtendedHours &&
            (EquityContractInvariants.CheckExtendedHours(eligibility.ExtendedHours).IsValid == false ||
             eligibility.ExtendedHours.Status != EquityExtendedHoursStatus.Enabled))
            return Deny("equity_extended_hours_unavailable", out reasonCode);
        if (isExtendedSession && (!order.AllowExtendedHours || !CanUseExtendedHours(eligibility)))
            return Deny("equity_extended_hours_unavailable", out reasonCode);
        if (order.Side == EquityOrderSide.SellShort && !CanSellShort(eligibility.ShortSaleAvailability))
            return Deny("equity_borrow_unavailable", out reasonCode);

        reasonCode = "equity_order_contract_eligible";
        return true;
    }

    private static bool HasKnownOrderTerms(EquityOrder order) =>
        !string.IsNullOrWhiteSpace(order.OrderId) &&
        !string.IsNullOrWhiteSpace(order.ClientOrderId) &&
        !string.IsNullOrWhiteSpace(order.AccountId) &&
        !string.IsNullOrWhiteSpace(order.InstrumentId) &&
        !string.IsNullOrWhiteSpace(order.CorrelationId) &&
        order.Side != EquityOrderSide.Unknown &&
        order.Type != EquityOrderType.Unknown &&
        order.TimeInForce != EquityTimeInForce.Unknown &&
        order.Status != EquityOrderStatus.Unknown &&
        order.Quantity > 0;

    private static bool HasKnownVenue(EquityVenue venue) =>
        venue.State == EquityContractState.Available &&
        !string.IsNullOrWhiteSpace(venue.VenueId) &&
        !string.IsNullOrWhiteSpace(venue.Mic.Value) &&
        !string.IsNullOrWhiteSpace(venue.TimeZoneId);

    private static bool Matches(EquityOrder order, EquityOrderEligibility eligibility) =>
        StringComparer.Ordinal.Equals(order.AccountId, eligibility.Account.AccountId) &&
        StringComparer.Ordinal.Equals(order.InstrumentId, eligibility.Instrument.InstrumentId) &&
        eligibility.Calendar.Mic == eligibility.Instrument.PrimaryVenue.Mic;

    private static bool CanUseExtendedHours(EquityOrderEligibility eligibility) =>
        EquityContractInvariants.CheckExtendedHours(eligibility.ExtendedHours).IsValid &&
        eligibility.ExtendedHours.Status == EquityExtendedHoursStatus.Enabled &&
        eligibility.ExtendedHours.SupportedSessions.Contains(eligibility.Session.Kind);

    private static bool CanSellShort(EquityShortSaleAvailability availability) =>
        EquityContractInvariants.CheckShortSaleAvailability(availability).IsValid &&
        availability.Shortable == EquityShortableStatus.Shortable &&
        availability.Borrowable is EquityBorrowStatus.Available or EquityBorrowStatus.Located;

    private static bool Deny(string reason, out string reasonCode)
    {
        reasonCode = reason;
        return false;
    }
}

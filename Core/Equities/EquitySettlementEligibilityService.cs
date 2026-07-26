namespace 币安量化机器人.Core.Equities;

public enum EquitySettlementCycleState
{
    Unknown = 0,
    ExplicitBusinessDays
}

public enum EquitySettlementEligibilityStatus
{
    Unknown = 0,
    Eligible,
    Unsupported
}

public sealed record EquitySettlementCycle(
    EquitySettlementCycleState State,
    int BusinessDays);

public sealed record EquitySettlementEligibilityRequest(
    string TradeId,
    EquityVenue Venue,
    EquityTradingCalendar Calendar,
    DateOnly? TradeDate,
    DateTimeOffset? ExecutedAt,
    EquitySettlementCycle Cycle);

public sealed record EquitySettlementEligibilityResult(
    EquitySettlementEligibilityStatus Status,
    string ReasonCode,
    string TradeId,
    DateOnly? TradeDate,
    DateOnly? ContractualSettlementDate,
    int? ExplicitBusinessDays);

public sealed class EquitySettlementEligibilityService
{
    public EquitySettlementEligibilityResult Calculate(EquitySettlementEligibilityRequest request)
    {
        if (!HasKnownVenue(request.Venue) || !HasKnownCalendar(request.Calendar))
            return Unsupported(request, "equity_settlement_venue_or_calendar_unknown");
        if (request.Venue.Mic != request.Calendar.Mic ||
            !StringComparer.Ordinal.Equals(request.Venue.TimeZoneId, request.Calendar.TimeZoneId))
            return Unsupported(request, "equity_settlement_venue_calendar_mismatch");
        if (string.IsNullOrWhiteSpace(request.TradeId) ||
            request.TradeDate is null ||
            request.ExecutedAt is null ||
            request.ExecutedAt == default)
            return Unsupported(request, "equity_settlement_trade_fact_missing");
        if (request.Cycle is null ||
            request.Cycle.State != EquitySettlementCycleState.ExplicitBusinessDays ||
            request.Cycle.BusinessDays < 0)
            return Unsupported(request, "equity_settlement_cycle_unknown");

        if (!TryGetVenueLocalExecution(request, out var localExecution))
            return Unsupported(request, "equity_settlement_timezone_unknown");
        if (DateOnly.FromDateTime(localExecution.DateTime) != request.TradeDate.Value)
            return Unsupported(request, "equity_settlement_trade_date_mismatch");

        var indexedDays = IndexCalendarDays(request.Calendar.Days);
        if (indexedDays is null)
            return Unsupported(request, "equity_settlement_calendar_ambiguous");
        if (!indexedDays.TryGetValue(request.TradeDate.Value, out var tradeDay) ||
            !IsTradingDayFactValid(tradeDay) ||
            !ContainsExecutionSession(tradeDay, TimeOnly.FromDateTime(localExecution.DateTime)))
            return Unsupported(request, "equity_settlement_trade_day_ineligible");

        var settlementDate = request.TradeDate.Value;
        var remainingBusinessDays = request.Cycle.BusinessDays;
        while (remainingBusinessDays > 0)
        {
            if (settlementDate == DateOnly.MaxValue)
                return Unsupported(request, "equity_settlement_calendar_incomplete");

            settlementDate = settlementDate.AddDays(1);
            if (!indexedDays.TryGetValue(settlementDate, out var day) || !IsCalendarDayFactValid(day))
                return Unsupported(request, "equity_settlement_calendar_incomplete");
            if (!day.IsTradingDay)
                continue;

            remainingBusinessDays--;
        }

        return new EquitySettlementEligibilityResult(
            EquitySettlementEligibilityStatus.Eligible,
            "equity_settlement_eligible",
            request.TradeId,
            request.TradeDate,
            settlementDate,
            request.Cycle.BusinessDays);
    }

    private static bool HasKnownVenue(EquityVenue? venue) =>
        venue is not null &&
        venue.State == EquityContractState.Available &&
        !string.IsNullOrWhiteSpace(venue.VenueId) &&
        !string.IsNullOrWhiteSpace(venue.Mic.Value) &&
        !string.IsNullOrWhiteSpace(venue.TimeZoneId);

    private static bool HasKnownCalendar(EquityTradingCalendar? calendar) =>
        calendar is not null &&
        calendar.State == EquityContractState.Available &&
        !string.IsNullOrWhiteSpace(calendar.CalendarId) &&
        !string.IsNullOrWhiteSpace(calendar.Mic.Value) &&
        !string.IsNullOrWhiteSpace(calendar.TimeZoneId) &&
        calendar.Days is not null;

    private static bool TryGetVenueLocalExecution(
        EquitySettlementEligibilityRequest request,
        out DateTimeOffset localExecution)
    {
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.Venue.TimeZoneId);
            localExecution = TimeZoneInfo.ConvertTime(request.ExecutedAt!.Value, timeZone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            localExecution = default;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            localExecution = default;
            return false;
        }
    }

    private static Dictionary<DateOnly, EquityTradingDay>? IndexCalendarDays(
        IReadOnlyList<EquityTradingDay> days)
    {
        var indexed = new Dictionary<DateOnly, EquityTradingDay>();
        foreach (var day in days)
        {
            if (!indexed.TryAdd(day.Date, day))
                return null;
        }

        return indexed;
    }

    private static bool IsCalendarDayFactValid(EquityTradingDay day)
    {
        if (day.State != EquityContractState.Available)
            return false;
        if (!day.IsTradingDay)
            return day.Sessions.Count == 0;

        return day.Sessions.Count > 0 && day.Sessions.All(session =>
            EquityContractInvariants.CheckSession(session).IsValid);
    }

    private static bool IsTradingDayFactValid(EquityTradingDay day) =>
        day.IsTradingDay && IsCalendarDayFactValid(day);

    private static bool ContainsExecutionSession(EquityTradingDay day, TimeOnly localTime) =>
        day.Sessions.Any(session => IsWithinSession(localTime, session));

    private static bool IsWithinSession(TimeOnly localTime, EquityTradingSession session)
    {
        if (!EquityContractInvariants.CheckSession(session).IsValid)
            return false;
        if (session.Kind == EquitySessionKind.Overnight && session.OpensAt > session.ClosesAt)
            return localTime >= session.OpensAt || localTime <= session.ClosesAt;

        return localTime >= session.OpensAt && localTime <= session.ClosesAt;
    }

    private static EquitySettlementEligibilityResult Unsupported(
        EquitySettlementEligibilityRequest request,
        string reasonCode) =>
        new(
            EquitySettlementEligibilityStatus.Unsupported,
            reasonCode,
            request.TradeId ?? string.Empty,
            request.TradeDate,
            null,
            request.Cycle?.State == EquitySettlementCycleState.ExplicitBusinessDays
                ? request.Cycle.BusinessDays
                : null);
}

namespace 币安量化机器人.Core.Equities;

public enum EquityTradingCalendarResolutionStatus
{
    Unknown = 0,
    Resolved,
    Closed,
    Unsupported
}

public sealed record EquityTradingCalendarResolutionRequest(
    EquityVenue Venue,
    EquityTradingCalendar Calendar,
    DateTimeOffset? Instant,
    EquityExtendedHours ExtendedHours);

public sealed record EquityTradingCalendarResolutionResult(
    EquityTradingCalendarResolutionStatus Status,
    string ReasonCode,
    DateOnly? LocalDate,
    TimeOnly? LocalTime,
    DateOnly? SessionDate,
    EquitySessionKind SessionKind,
    EquityMarketPhase Phase,
    bool IsExtendedHours);

public sealed class EquityTradingCalendarResolutionService
{
    public EquityTradingCalendarResolutionResult Resolve(EquityTradingCalendarResolutionRequest request)
    {
        if (!HasKnownVenue(request.Venue) || !HasKnownCalendar(request.Calendar))
            return Unsupported("equity_calendar_resolution_unknown");
        if (request.Venue.Mic != request.Calendar.Mic ||
            !StringComparer.Ordinal.Equals(request.Venue.TimeZoneId, request.Calendar.TimeZoneId))
            return Unsupported("equity_calendar_resolution_mismatch");
        if (request.Instant is null || request.Instant == default)
            return Unsupported("equity_calendar_instant_missing");
        if (!TryConvert(request.Instant.Value, request.Venue.TimeZoneId, out var localInstant))
            return Unsupported("equity_calendar_timezone_unknown");

        var localDate = DateOnly.FromDateTime(localInstant.DateTime);
        var localTime = TimeOnly.FromDateTime(localInstant.DateTime);
        var days = IndexDays(request.Calendar.Days);
        if (days is null || !days.TryGetValue(localDate, out var localDay))
            return Unsupported("equity_calendar_day_unknown", localDate, localTime);
        if (!IsDayFactValid(localDay))
            return Unsupported("equity_calendar_day_invalid", localDate, localTime);

        var candidates = new List<(DateOnly SessionDate, EquityTradingSession Session)>();
        AddSameDayCandidates(localDay, localDate, localTime, candidates);
        if (localDate != DateOnly.MinValue && days.TryGetValue(localDate.AddDays(-1), out var priorDay))
        {
            if (!IsDayFactValid(priorDay))
                return Unsupported("equity_calendar_day_invalid", localDate, localTime);
            AddPriorOvernightCandidates(priorDay, localDate.AddDays(-1), localTime, candidates);
        }

        if (candidates.Count == 0)
            return Closed(localDay, localDate, localTime);
        if (candidates.Count != 1)
            return Unsupported("equity_calendar_session_ambiguous", localDate, localTime);

        var resolved = candidates[0];
        var isExtended = resolved.Session.Kind is
            EquitySessionKind.PreMarket or EquitySessionKind.AfterHours or EquitySessionKind.Overnight;
        if (isExtended && !HasExplicitExtendedHours(request.ExtendedHours, resolved.Session.Kind))
            return Unsupported("equity_calendar_extended_hours_unsupported", localDate, localTime);

        return new EquityTradingCalendarResolutionResult(
            EquityTradingCalendarResolutionStatus.Resolved,
            "equity_calendar_session_resolved",
            localDate,
            localTime,
            resolved.SessionDate,
            resolved.Session.Kind,
            resolved.Session.Phase,
            isExtended);
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

    private static bool HasExplicitExtendedHours(EquityExtendedHours? extendedHours, EquitySessionKind kind) =>
        extendedHours is not null &&
        EquityContractInvariants.CheckExtendedHours(extendedHours).IsValid &&
        extendedHours.Status == EquityExtendedHoursStatus.Enabled &&
        extendedHours.SupportedSessions.Contains(kind);

    private static bool TryConvert(DateTimeOffset instant, string timeZoneId, out DateTimeOffset localInstant)
    {
        try
        {
            localInstant = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            localInstant = default;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            localInstant = default;
            return false;
        }
    }

    private static Dictionary<DateOnly, EquityTradingDay>? IndexDays(IReadOnlyList<EquityTradingDay> days)
    {
        var indexed = new Dictionary<DateOnly, EquityTradingDay>();
        foreach (var day in days)
        {
            if (!indexed.TryAdd(day.Date, day))
                return null;
        }

        return indexed;
    }

    private static bool IsDayFactValid(EquityTradingDay day)
    {
        if (day.State != EquityContractState.Available)
            return false;
        if (!day.IsTradingDay)
            return day.Sessions.Count == 0;

        return day.Sessions.Count > 0 && day.Sessions.All(session =>
            EquityContractInvariants.CheckSession(session).IsValid);
    }

    private static void AddSameDayCandidates(
        EquityTradingDay day,
        DateOnly sessionDate,
        TimeOnly localTime,
        ICollection<(DateOnly SessionDate, EquityTradingSession Session)> candidates)
    {
        if (!day.IsTradingDay)
            return;

        foreach (var session in day.Sessions)
        {
            var contains = session.Kind == EquitySessionKind.Overnight && session.OpensAt > session.ClosesAt
                ? localTime >= session.OpensAt
                : localTime >= session.OpensAt && localTime < session.ClosesAt;
            if (contains)
                candidates.Add((sessionDate, session));
        }
    }

    private static void AddPriorOvernightCandidates(
        EquityTradingDay priorDay,
        DateOnly sessionDate,
        TimeOnly localTime,
        ICollection<(DateOnly SessionDate, EquityTradingSession Session)> candidates)
    {
        if (!priorDay.IsTradingDay)
            return;

        foreach (var session in priorDay.Sessions.Where(session =>
                     session.Kind == EquitySessionKind.Overnight &&
                     session.OpensAt > session.ClosesAt &&
                     localTime < session.ClosesAt))
        {
            candidates.Add((sessionDate, session));
        }
    }

    private static EquityTradingCalendarResolutionResult Closed(
        EquityTradingDay day,
        DateOnly localDate,
        TimeOnly localTime) =>
        new(
            EquityTradingCalendarResolutionStatus.Closed,
            day.IsTradingDay ? "equity_calendar_outside_session" : "equity_calendar_non_trading_day",
            localDate,
            localTime,
            null,
            EquitySessionKind.Unknown,
            EquityMarketPhase.Closed,
            false);

    private static EquityTradingCalendarResolutionResult Unsupported(
        string reasonCode,
        DateOnly? localDate = null,
        TimeOnly? localTime = null) =>
        new(
            EquityTradingCalendarResolutionStatus.Unsupported,
            reasonCode,
            localDate,
            localTime,
            null,
            EquitySessionKind.Unknown,
            EquityMarketPhase.Unknown,
            false);
}

using 币安量化机器人.Core.Equities;

namespace WPE.Tests;

public sealed class EquityTradingCalendarResolutionTests
{
    private readonly EquityTradingCalendarResolutionService _service = new();

    [Fact]
    public void RegularSession_ResolvesFromVenueLocalTime()
    {
        var result = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-02T15:00:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), Regular())));

        Assert.Equal(EquityTradingCalendarResolutionStatus.Resolved, result.Status);
        Assert.Equal(EquitySessionKind.Regular, result.SessionKind);
        Assert.Equal(EquityMarketPhase.Continuous, result.Phase);
        Assert.False(result.IsExtendedHours);
    }

    [Fact]
    public void UtcDateBoundary_UsesVenueLocalCalendarDate()
    {
        var localDate = new DateOnly(2026, 7, 2);
        var result = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-03T00:30:00Z"),
            TradingDay(localDate, Regular(close: new TimeOnly(21, 0)))));

        Assert.Equal(EquityTradingCalendarResolutionStatus.Resolved, result.Status);
        Assert.Equal(localDate, result.LocalDate);
    }

    [Fact]
    public void ExplicitPremarketCapability_ResolvesExtendedSession()
    {
        var result = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-02T08:00:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), Premarket())));

        Assert.Equal(EquityTradingCalendarResolutionStatus.Resolved, result.Status);
        Assert.Equal(EquitySessionKind.PreMarket, result.SessionKind);
        Assert.True(result.IsExtendedHours);
    }

    [Fact]
    public void PremarketWithoutExplicitCapability_IsUnsupported()
    {
        var request = CreateRequest(
            DateTimeOffset.Parse("2026-07-02T08:00:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), Premarket()));
        request = request with
        {
            ExtendedHours = request.ExtendedHours with
            {
                Status = EquityExtendedHoursStatus.Unknown,
                State = EquityContractState.Unknown
            }
        };

        var result = _service.Resolve(request);

        Assert.Equal(EquityTradingCalendarResolutionStatus.Unsupported, result.Status);
        Assert.Equal("equity_calendar_extended_hours_unsupported", result.ReasonCode);
    }

    [Fact]
    public void AuctionSession_IsResolvedWithoutExtendedHoursSemantics()
    {
        var auction = new EquityTradingSession(
            EquitySessionKind.Auction,
            new TimeOnly(9, 20),
            new TimeOnly(9, 30),
            "America/New_York",
            EquityMarketPhase.OpeningAuction,
            EquityContractState.Available);

        var result = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-02T09:25:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), auction)));

        Assert.Equal(EquityTradingCalendarResolutionStatus.Resolved, result.Status);
        Assert.Equal(EquitySessionKind.Auction, result.SessionKind);
        Assert.False(result.IsExtendedHours);
    }

    [Fact]
    public void NonTradingDay_ReturnsClosedFact()
    {
        var date = new DateOnly(2026, 7, 4);
        var day = new EquityTradingDay(date, false, [], EquityContractState.Available, "holiday");

        var result = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-04T12:00:00-04:00"), day));

        Assert.Equal(EquityTradingCalendarResolutionStatus.Closed, result.Status);
        Assert.Equal("equity_calendar_non_trading_day", result.ReasonCode);
        Assert.Equal(EquityMarketPhase.Closed, result.Phase);
    }

    [Fact]
    public void TradingDayOutsideSessions_ReturnsClosedFact()
    {
        var result = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-02T18:00:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), Regular())));

        Assert.Equal(EquityTradingCalendarResolutionStatus.Closed, result.Status);
        Assert.Equal("equity_calendar_outside_session", result.ReasonCode);
    }

    [Fact]
    public void MissingCalendarDate_IsUnsupportedNotClosed()
    {
        var result = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-03T12:00:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), Regular())));

        Assert.Equal(EquityTradingCalendarResolutionStatus.Unsupported, result.Status);
        Assert.Equal("equity_calendar_day_unknown", result.ReasonCode);
    }

    [Fact]
    public void OverlappingSessions_AreUnsupported()
    {
        var overlapping = Regular(open: new TimeOnly(10, 0), close: new TimeOnly(15, 0));
        var result = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-02T12:00:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), Regular(), overlapping)));

        Assert.Equal(EquityTradingCalendarResolutionStatus.Unsupported, result.Status);
        Assert.Equal("equity_calendar_session_ambiguous", result.ReasonCode);
    }

    [Fact]
    public void OvernightSession_AfterMidnightRetainsPriorSessionDate()
    {
        var priorDate = new DateOnly(2026, 7, 2);
        var localDate = new DateOnly(2026, 7, 3);
        var overnight = new EquityTradingSession(
            EquitySessionKind.Overnight,
            new TimeOnly(20, 0),
            new TimeOnly(4, 0),
            "America/New_York",
            EquityMarketPhase.Continuous,
            EquityContractState.Available);

        var result = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-03T02:00:00-04:00"),
            TradingDay(priorDate, overnight),
            TradingDay(localDate, Regular())));

        Assert.Equal(EquityTradingCalendarResolutionStatus.Resolved, result.Status);
        Assert.Equal(EquitySessionKind.Overnight, result.SessionKind);
        Assert.Equal(priorDate, result.SessionDate);
        Assert.True(result.IsExtendedHours);
    }

    [Theory]
    [InlineData(EquityContractState.Unknown)]
    [InlineData(EquityContractState.Unsupported)]
    [InlineData(EquityContractState.Stale)]
    [InlineData(EquityContractState.Error)]
    public void UnavailableCalendar_FailsClosed(EquityContractState state)
    {
        var request = CreateRequest(
            DateTimeOffset.Parse("2026-07-02T12:00:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), Regular()));
        request = request with { Calendar = request.Calendar with { State = state } };

        var result = _service.Resolve(request);

        Assert.Equal(EquityTradingCalendarResolutionStatus.Unsupported, result.Status);
        Assert.Equal(EquitySessionKind.Unknown, result.SessionKind);
        Assert.Equal(EquityMarketPhase.Unknown, result.Phase);
    }

    [Fact]
    public void ServiceIsPureDomainAndHasNoClockOrExternalDependency()
    {
        var constructor = typeof(EquityTradingCalendarResolutionService).GetConstructors().Single();
        var first = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-02T12:00:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), Regular())));
        var second = _service.Resolve(CreateRequest(
            DateTimeOffset.Parse("2026-07-02T12:00:00-04:00"),
            TradingDay(new DateOnly(2026, 7, 2), Regular())));

        Assert.Empty(constructor.GetParameters());
        Assert.Equal(first, second);
    }

    private static EquityTradingCalendarResolutionRequest CreateRequest(
        DateTimeOffset instant,
        params EquityTradingDay[] days)
    {
        var venue = new EquityVenue(
            "nasdaq",
            new MicCode("XNAS"),
            "Nasdaq",
            "US",
            "America/New_York",
            EquityContractState.Available);
        var calendar = new EquityTradingCalendar(
            "xnas-calendar",
            venue.Mic,
            venue.TimeZoneId,
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            days,
            EquityContractState.Available);
        var extended = new EquityExtendedHours(
            EquityExtendedHoursStatus.Enabled,
            new HashSet<EquitySessionKind>
            {
                EquitySessionKind.PreMarket,
                EquitySessionKind.AfterHours,
                EquitySessionKind.Overnight
            },
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            EquityContractState.Available);

        return new EquityTradingCalendarResolutionRequest(venue, calendar, instant, extended);
    }

    private static EquityTradingDay TradingDay(DateOnly date, params EquityTradingSession[] sessions) =>
        new(date, true, sessions, EquityContractState.Available);

    private static EquityTradingSession Regular(TimeOnly? open = null, TimeOnly? close = null) =>
        new(
            EquitySessionKind.Regular,
            open ?? new TimeOnly(9, 30),
            close ?? new TimeOnly(16, 0),
            "America/New_York",
            EquityMarketPhase.Continuous,
            EquityContractState.Available);

    private static EquityTradingSession Premarket() =>
        new(
            EquitySessionKind.PreMarket,
            new TimeOnly(4, 0),
            new TimeOnly(9, 30),
            "America/New_York",
            EquityMarketPhase.Continuous,
            EquityContractState.Available);
}

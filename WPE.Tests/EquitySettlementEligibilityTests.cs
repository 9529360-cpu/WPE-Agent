using 币安量化机器人.Core.Equities;

namespace WPE.Tests;

public sealed class EquitySettlementEligibilityTests
{
    private readonly EquitySettlementEligibilityService _service = new();

    [Fact]
    public void ExplicitTPlusTwo_SkipsWeekendAndHolidayFacts()
    {
        var request = CreateRequest(
            tradeDate: new DateOnly(2026, 7, 2),
            executedAt: DateTimeOffset.Parse("2026-07-02T15:00:00-04:00"),
            businessDays: 2,
            Day(new DateOnly(2026, 7, 2), true),
            Day(new DateOnly(2026, 7, 3), false, "holiday"),
            Day(new DateOnly(2026, 7, 4), false, "weekend"),
            Day(new DateOnly(2026, 7, 5), false, "weekend"),
            Day(new DateOnly(2026, 7, 6), true),
            Day(new DateOnly(2026, 7, 7), true));

        var result = _service.Calculate(request);

        Assert.Equal(EquitySettlementEligibilityStatus.Eligible, result.Status);
        Assert.Equal(new DateOnly(2026, 7, 7), result.ContractualSettlementDate);
        Assert.Equal(2, result.ExplicitBusinessDays);
    }

    [Fact]
    public void VenueTimezone_DeterminesTradeDateAcrossUtcBoundary()
    {
        var tradeDate = new DateOnly(2026, 7, 2);
        var request = CreateRequest(
            tradeDate,
            DateTimeOffset.Parse("2026-07-03T00:30:00Z"),
            0,
            Day(tradeDate, true, closeAt: new TimeOnly(21, 0)));

        var result = _service.Calculate(request);

        Assert.Equal(EquitySettlementEligibilityStatus.Eligible, result.Status);
        Assert.Equal(tradeDate, result.TradeDate);
        Assert.Equal(tradeDate, result.ContractualSettlementDate);
    }

    [Theory]
    [InlineData(EquityContractState.Unknown)]
    [InlineData(EquityContractState.Unsupported)]
    [InlineData(EquityContractState.Stale)]
    [InlineData(EquityContractState.Error)]
    public void UnknownOrUnavailableVenue_IsUnsupported(EquityContractState state)
    {
        var request = StandardRequest() with
        {
            Venue = StandardRequest().Venue with { State = state }
        };

        var result = _service.Calculate(request);

        Assert.Equal(EquitySettlementEligibilityStatus.Unsupported, result.Status);
        Assert.Null(result.ContractualSettlementDate);
    }

    [Theory]
    [InlineData(EquityContractState.Unknown)]
    [InlineData(EquityContractState.Unsupported)]
    [InlineData(EquityContractState.Stale)]
    [InlineData(EquityContractState.Error)]
    public void UnknownOrUnavailableCalendar_IsUnsupported(EquityContractState state)
    {
        var request = StandardRequest();
        request = request with { Calendar = request.Calendar with { State = state } };

        var result = _service.Calculate(request);

        Assert.Equal(EquitySettlementEligibilityStatus.Unsupported, result.Status);
        Assert.Null(result.ContractualSettlementDate);
    }

    [Fact]
    public void MissingTradeDate_IsUnsupported()
    {
        var result = _service.Calculate(StandardRequest() with { TradeDate = null });

        Assert.Equal(EquitySettlementEligibilityStatus.Unsupported, result.Status);
        Assert.Equal("equity_settlement_trade_fact_missing", result.ReasonCode);
    }

    [Fact]
    public void MissingExecutionDate_IsUnsupported()
    {
        var result = _service.Calculate(StandardRequest() with { ExecutedAt = null });

        Assert.Equal(EquitySettlementEligibilityStatus.Unsupported, result.Status);
        Assert.Equal("equity_settlement_trade_fact_missing", result.ReasonCode);
    }

    [Fact]
    public void UnknownCycle_IsUnsupportedAndDoesNotGuess()
    {
        var request = StandardRequest() with
        {
            Cycle = new EquitySettlementCycle(EquitySettlementCycleState.Unknown, 2)
        };

        var result = _service.Calculate(request);

        Assert.Equal(EquitySettlementEligibilityStatus.Unsupported, result.Status);
        Assert.Equal("equity_settlement_cycle_unknown", result.ReasonCode);
        Assert.Null(result.ContractualSettlementDate);
        Assert.Null(result.ExplicitBusinessDays);
    }

    [Fact]
    public void NonTradingTradeDate_IsUnsupported()
    {
        var request = CreateRequest(
            new DateOnly(2026, 7, 6),
            DateTimeOffset.Parse("2026-07-06T15:00:00-04:00"),
            1,
            Day(new DateOnly(2026, 7, 6), false, "holiday"),
            Day(new DateOnly(2026, 7, 7), true));

        var result = _service.Calculate(request);

        Assert.Equal(EquitySettlementEligibilityStatus.Unsupported, result.Status);
        Assert.Equal("equity_settlement_trade_day_ineligible", result.ReasonCode);
    }

    [Fact]
    public void CalendarGap_IsUnsupportedInsteadOfAssumingWeekendOrHoliday()
    {
        var request = CreateRequest(
            new DateOnly(2026, 7, 2),
            DateTimeOffset.Parse("2026-07-02T15:00:00-04:00"),
            1,
            Day(new DateOnly(2026, 7, 2), true),
            Day(new DateOnly(2026, 7, 4), false, "weekend"),
            Day(new DateOnly(2026, 7, 5), false, "weekend"),
            Day(new DateOnly(2026, 7, 6), true));

        var result = _service.Calculate(request);

        Assert.Equal(EquitySettlementEligibilityStatus.Unsupported, result.Status);
        Assert.Equal("equity_settlement_calendar_incomplete", result.ReasonCode);
        Assert.Null(result.ContractualSettlementDate);
    }

    [Fact]
    public void HolidayWithTradingSession_IsContradictoryAndUnsupported()
    {
        var request = CreateRequest(
            new DateOnly(2026, 7, 2),
            DateTimeOffset.Parse("2026-07-02T15:00:00-04:00"),
            1,
            Day(new DateOnly(2026, 7, 2), true),
            Day(new DateOnly(2026, 7, 3), false, "holiday") with { Sessions = [RegularSession()] },
            Day(new DateOnly(2026, 7, 4), true));

        var result = _service.Calculate(request);

        Assert.Equal(EquitySettlementEligibilityStatus.Unsupported, result.Status);
        Assert.Equal("equity_settlement_calendar_incomplete", result.ReasonCode);
    }

    [Fact]
    public void ExecutionOutsideExplicitSession_IsUnsupported()
    {
        var request = StandardRequest() with
        {
            ExecutedAt = DateTimeOffset.Parse("2026-07-02T20:00:00-04:00")
        };

        var result = _service.Calculate(request);

        Assert.Equal(EquitySettlementEligibilityStatus.Unsupported, result.Status);
        Assert.Equal("equity_settlement_trade_day_ineligible", result.ReasonCode);
    }

    [Fact]
    public void HaltIsNotAnInputAndCannotChangeExecutedTradeSettlement()
    {
        var haltedAfterExecution = new EquityHalt(
            EquityHaltStatus.Halted,
            "regulatory",
            DateTimeOffset.Parse("2026-07-02T20:30:00Z"),
            null,
            EquityContractState.Available);
        var method = typeof(EquitySettlementEligibilityService).GetMethod(nameof(EquitySettlementEligibilityService.Calculate));

        var first = _service.Calculate(StandardRequest());
        var second = _service.Calculate(StandardRequest());

        Assert.NotNull(haltedAfterExecution);
        Assert.NotNull(method);
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(EquityHalt));
        Assert.Equal(first, second);
        Assert.Equal(EquitySettlementEligibilityStatus.Eligible, first.Status);
    }

    [Fact]
    public void ServiceHasNoRuntimeUiOrNetworkDependency()
    {
        var constructor = typeof(EquitySettlementEligibilityService).GetConstructors().Single();
        var allowedNamespaces = new[]
        {
            typeof(EquitySettlementEligibilityRequest).Namespace,
            typeof(DateOnly).Namespace
        };

        Assert.Empty(constructor.GetParameters());
        Assert.DoesNotContain(
            typeof(EquitySettlementEligibilityRequest).GetProperties(),
            property => property.PropertyType == typeof(EquityHalt));
        Assert.All(
            typeof(EquitySettlementEligibilityRequest).GetProperties(),
            property => Assert.Contains(property.PropertyType.Namespace, allowedNamespaces));
    }

    private static EquitySettlementEligibilityRequest StandardRequest()
    {
        var tradeDate = new DateOnly(2026, 7, 2);
        return CreateRequest(
            tradeDate,
            DateTimeOffset.Parse("2026-07-02T15:00:00-04:00"),
            1,
            Day(tradeDate, true),
            Day(new DateOnly(2026, 7, 3), true));
    }

    private static EquitySettlementEligibilityRequest CreateRequest(
        DateOnly tradeDate,
        DateTimeOffset executedAt,
        int businessDays,
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

        return new EquitySettlementEligibilityRequest(
            "trade-1",
            venue,
            calendar,
            tradeDate,
            executedAt,
            new EquitySettlementCycle(EquitySettlementCycleState.ExplicitBusinessDays, businessDays));
    }

    private static EquityTradingDay Day(
        DateOnly date,
        bool isTradingDay,
        string? reason = null,
        TimeOnly? closeAt = null) =>
        new(
            date,
            isTradingDay,
            isTradingDay ? [RegularSession(closeAt)] : [],
            EquityContractState.Available,
            reason);

    private static EquityTradingSession RegularSession(TimeOnly? closeAt = null) =>
        new(
            EquitySessionKind.Regular,
            new TimeOnly(9, 30),
            closeAt ?? new TimeOnly(16, 0),
            "America/New_York",
            EquityMarketPhase.Continuous,
            EquityContractState.Available);
}

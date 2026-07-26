using 币安量化机器人.Core.Equities;
using System.Text.Json;

namespace WPE.Tests;

public sealed class EquityContractsTests
{
    [Fact]
    public void CanRoute_AllExplicitlyAvailableContracts_AllowsApprovedOrder()
    {
        var (order, eligibility) = CreateEligibleOrder();

        var allowed = EquityContractGuard.CanRoute(order, eligibility, out var reason);

        Assert.True(allowed);
        Assert.Equal("equity_order_contract_eligible", reason);
    }

    [Theory]
    [InlineData(EquityOrderSide.Unknown, EquityOrderType.Limit, EquityTimeInForce.Day)]
    [InlineData(EquityOrderSide.Buy, EquityOrderType.Unknown, EquityTimeInForce.Day)]
    [InlineData(EquityOrderSide.Buy, EquityOrderType.Limit, EquityTimeInForce.Unknown)]
    public void CanRoute_UnknownOrderTerms_FailsClosed(
        EquityOrderSide side,
        EquityOrderType type,
        EquityTimeInForce timeInForce)
    {
        var (order, eligibility) = CreateEligibleOrder();
        order = order with { Side = side, Type = type, TimeInForce = timeInForce };

        var allowed = EquityContractGuard.CanRoute(order, eligibility, out var reason);

        Assert.False(allowed);
        Assert.Equal("equity_order_terms_unknown", reason);
    }

    [Theory]
    [InlineData(EquityContractState.Unknown)]
    [InlineData(EquityContractState.Unsupported)]
    [InlineData(EquityContractState.Stale)]
    [InlineData(EquityContractState.Error)]
    public void CanRoute_NonAvailableInstrumentState_FailsClosed(EquityContractState state)
    {
        var (order, eligibility) = CreateEligibleOrder();
        eligibility = eligibility with { Instrument = eligibility.Instrument with { State = state } };

        var allowed = EquityContractGuard.CanRoute(order, eligibility, out var reason);

        Assert.False(allowed);
        Assert.Equal("equity_instrument_unavailable", reason);
    }

    [Fact]
    public void CanRoute_UnknownHaltState_FailsClosed()
    {
        var (order, eligibility) = CreateEligibleOrder();
        eligibility = eligibility with
        {
            Halt = eligibility.Halt with
            {
                Status = EquityHaltStatus.Unknown,
                State = EquityContractState.Unknown
            }
        };

        var allowed = EquityContractGuard.CanRoute(order, eligibility, out var reason);

        Assert.False(allowed);
        Assert.Equal("equity_halt_not_clear", reason);
    }

    [Fact]
    public void CanRoute_ExtendedHoursWithoutExplicitSupport_FailsClosed()
    {
        var (order, eligibility) = CreateEligibleOrder();
        order = order with { AllowExtendedHours = true };
        eligibility = eligibility with
        {
            ExtendedHours = eligibility.ExtendedHours with
            {
                Status = EquityExtendedHoursStatus.Unknown,
                State = EquityContractState.Unknown
            }
        };

        var allowed = EquityContractGuard.CanRoute(order, eligibility, out var reason);

        Assert.False(allowed);
        Assert.Equal("equity_extended_hours_unavailable", reason);
    }

    [Theory]
    [InlineData(EquityShortableStatus.Unknown, EquityBorrowStatus.Unknown)]
    [InlineData(EquityShortableStatus.NotShortable, EquityBorrowStatus.Available)]
    [InlineData(EquityShortableStatus.Shortable, EquityBorrowStatus.Unavailable)]
    [InlineData(EquityShortableStatus.Shortable, EquityBorrowStatus.LocateRequired)]
    public void CanRoute_ShortSaleWithoutConfirmedBorrow_FailsClosed(
        EquityShortableStatus shortable,
        EquityBorrowStatus borrowable)
    {
        var (order, eligibility) = CreateEligibleOrder();
        order = order with { Side = EquityOrderSide.SellShort };
        eligibility = eligibility with
        {
            ShortSaleAvailability = eligibility.ShortSaleAvailability with
            {
                Shortable = shortable,
                Borrowable = borrowable
            }
        };

        var allowed = EquityContractGuard.CanRoute(order, eligibility, out var reason);

        Assert.False(allowed);
        Assert.Equal("equity_borrow_unavailable", reason);
    }

    [Fact]
    public void Contracts_ModelEquityLifecycleWithoutPerpetualSemantics()
    {
        var contractTypes = typeof(EquityOrder).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(EquityOrder).Namespace)
            .ToArray();
        var memberNames = contractTypes
            .SelectMany(type => type.GetMembers())
            .Select(member => member.Name)
            .ToArray();

        Assert.Contains(contractTypes, type => type == typeof(EquityCorporateAction));
        Assert.Contains(contractTypes, type => type == typeof(EquitySettlement));
        Assert.DoesNotContain(memberNames, name => name.Contains("Leverage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(memberNames, name => name.Contains("Margin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(memberNames, name => name.Contains("Funding", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(memberNames, name => name.Contains("Liquidation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanRoute_SessionAbsentFromTradingCalendar_FailsClosed()
    {
        var (order, eligibility) = CreateEligibleOrder();
        eligibility = eligibility with
        {
            Calendar = eligibility.Calendar with { Days = [] }
        };

        var allowed = EquityContractGuard.CanRoute(order, eligibility, out var reason);

        Assert.False(allowed);
        Assert.Equal("equity_calendar_session_unavailable", reason);
    }

    [Fact]
    public void CanRoute_ExtendedSessionRequiresOrderAndVenuePermission()
    {
        var (order, eligibility) = CreateEligibleOrder();
        var session = eligibility.Session with
        {
            Kind = EquitySessionKind.PreMarket,
            OpensAt = new TimeOnly(4, 0),
            ClosesAt = new TimeOnly(9, 30)
        };
        eligibility = eligibility with
        {
            Session = session,
            Calendar = eligibility.Calendar with
            {
                Days = [eligibility.Calendar.Days[0] with { Sessions = [session] }]
            }
        };

        Assert.False(EquityContractGuard.CanRoute(order, eligibility, out var blockedReason));
        Assert.Equal("equity_extended_hours_unavailable", blockedReason);

        Assert.True(EquityContractGuard.CanRoute(
            order with { AllowExtendedHours = true }, eligibility, out var allowedReason));
        Assert.Equal("equity_order_contract_eligible", allowedReason);
    }

    [Theory]
    [InlineData(EquitySessionKind.Regular, EquityMarketPhase.OpeningAuction)]
    [InlineData(EquitySessionKind.Auction, EquityMarketPhase.Continuous)]
    [InlineData(EquitySessionKind.Regular, EquityMarketPhase.Closed)]
    public void Session_InvalidKindAndPhaseCombination_FailsClosed(
        EquitySessionKind kind,
        EquityMarketPhase phase)
    {
        var (_, eligibility) = CreateEligibleOrder();

        var result = EquityContractInvariants.CheckSession(
            eligibility.Session with { Kind = kind, Phase = phase });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Halt_HaltedWithoutReason_IsInvalidAndCannotRoute()
    {
        var (order, eligibility) = CreateEligibleOrder();
        var halt = eligibility.Halt with { Status = EquityHaltStatus.Halted, ReasonCode = null };

        var check = EquityContractInvariants.CheckHalt(halt);
        var allowed = EquityContractGuard.CanRoute(order, eligibility with { Halt = halt }, out var reason);

        Assert.False(check.IsValid);
        Assert.Equal("equity_halt_reason_required", check.ReasonCode);
        Assert.False(allowed);
        Assert.Equal("equity_halt_not_clear", reason);
    }

    [Fact]
    public void CorporateAction_CashDividendRequiresCashCurrencyAndDates()
    {
        var invalid = new EquityCorporateAction(
            "action-1", "instrument-1", EquityCorporateActionType.CashDividend,
            null, null, null, null, null, null, null, EquityContractState.Available);
        var valid = invalid with
        {
            ExDate = new DateOnly(2026, 7, 20),
            PayableDate = new DateOnly(2026, 8, 1),
            CashAmount = 0.25m,
            Currency = new EquityCurrency("USD")
        };

        Assert.False(EquityContractInvariants.CheckCorporateAction(invalid).IsValid);
        Assert.True(EquityContractInvariants.CheckCorporateAction(valid).IsValid);
    }

    [Fact]
    public void CorporateAction_SplitRequiresPositiveRatioAndExDate()
    {
        var action = new EquityCorporateAction(
            "action-1", "instrument-1", EquityCorporateActionType.Split,
            null, null, null, null, null, 0m, null, EquityContractState.Available);

        var result = EquityContractInvariants.CheckCorporateAction(action);

        Assert.False(result.IsValid);
        Assert.Equal("equity_corporate_action_ratio_terms_invalid", result.ReasonCode);
    }

    [Theory]
    [InlineData(EquitySettlementStatus.Pending, false, true)]
    [InlineData(EquitySettlementStatus.Pending, true, false)]
    [InlineData(EquitySettlementStatus.Settled, false, false)]
    [InlineData(EquitySettlementStatus.Settled, true, true)]
    public void Settlement_StatusAndActualDateMustAgree(
        EquitySettlementStatus status,
        bool hasActualDate,
        bool expectedValid)
    {
        var settlement = new EquitySettlement(
            "settlement-1", "fill-1", new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 21),
            hasActualDate ? new DateOnly(2026, 7, 21) : null,
            1_000m, new EquityCurrency("USD"), status, EquityContractState.Available);

        Assert.Equal(expectedValid, EquityContractInvariants.CheckSettlement(settlement).IsValid);
    }

    [Fact]
    public void Shortability_ConfirmedBorrowRequiresPositiveAvailableQuantity()
    {
        var (_, eligibility) = CreateEligibleOrder();
        var availability = eligibility.ShortSaleAvailability with { AvailableQuantity = 0m };

        var result = EquityContractInvariants.CheckShortSaleAvailability(availability);

        Assert.False(result.IsValid);
        Assert.Equal("equity_borrow_quantity_unavailable", result.ReasonCode);
    }

    [Fact]
    public void ExtendedHours_DisabledStatusCannotAdvertiseSessions()
    {
        var (_, eligibility) = CreateEligibleOrder();
        var extendedHours = eligibility.ExtendedHours with { Status = EquityExtendedHoursStatus.Disabled };

        var result = EquityContractInvariants.CheckExtendedHours(extendedHours);

        Assert.False(result.IsValid);
        Assert.Equal("equity_extended_hours_status_conflict", result.ReasonCode);
    }

    [Fact]
    public void Deserialize_MissingEnumValues_DefaultToUnknownAndFailClosed()
    {
        const string json = """
            {
              "reasonCode": null,
              "observedAt": "2026-07-21T00:00:00Z",
              "expectedResumeAt": null
            }
            """;

        var halt = JsonSerializer.Deserialize<EquityHalt>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(halt);
        Assert.Equal(EquityHaltStatus.Unknown, halt.Status);
        Assert.Equal(EquityContractState.Unknown, halt.State);
        Assert.False(EquityContractInvariants.CheckHalt(halt).IsValid);
    }

    [Fact]
    public void EveryEquityEnum_ZeroDeserializesToNamedUnknown()
    {
        var enumTypes = typeof(EquityOrder).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(EquityOrder).Namespace && type.IsEnum)
            .ToArray();

        Assert.NotEmpty(enumTypes);
        foreach (var enumType in enumTypes)
        {
            var value = JsonSerializer.Deserialize("0", enumType);

            Assert.NotNull(value);
            Assert.Equal("Unknown", Enum.GetName(enumType, value));
        }
    }

    [Theory]
    [InlineData("0", EquityOrderStatus.Unknown)]
    [InlineData("999", (EquityOrderStatus)999)]
    public void Deserialize_UnknownOrderStatusNeverBecomesApproved(
        string json,
        EquityOrderStatus expected)
    {
        var status = JsonSerializer.Deserialize<EquityOrderStatus>(json);

        Assert.Equal(expected, status);
        Assert.NotEqual(EquityOrderStatus.Approved, status);
        Assert.NotEqual(EquityOrderStatus.PendingSubmit, status);
    }

    private static (EquityOrder Order, EquityOrderEligibility Eligibility) CreateEligibleOrder()
    {
        var now = DateTimeOffset.Parse("2026-07-21T10:00:00+09:00");
        var mic = new MicCode("XNAS");
        var currency = new EquityCurrency("USD");
        var venue = new EquityVenue("nasdaq", mic, "Nasdaq", "US", "America/New_York", EquityContractState.Available);
        var instrument = new EquityInstrument(
            "instrument-1", "EXAMPLE", "EXAMPLE", EquityInstrumentType.CommonStock,
            venue, currency, 0.01m, 1m, EquityContractState.Available);
        var account = new EquityAccount(
            "account-1", "unconfigured-provider", EquityAccountType.Cash, currency,
            10_000m, 10_000m, EquityContractState.Available, now);
        var session = new EquityTradingSession(
            EquitySessionKind.Regular, new TimeOnly(9, 30), new TimeOnly(16, 0),
            "America/New_York", EquityMarketPhase.Continuous, EquityContractState.Available);
        var calendar = new EquityTradingCalendar(
            "calendar-1", mic, "America/New_York", now,
            [new EquityTradingDay(DateOnly.FromDateTime(now.Date), true, [session], EquityContractState.Available)],
            EquityContractState.Available);
        var order = new EquityOrder(
            "order-1", "client-order-1", account.AccountId, instrument.InstrumentId,
            EquityOrderSide.Buy, EquityOrderType.Limit, EquityTimeInForce.Day,
            10m, 100m, null, false, EquityOrderStatus.Approved, now, null, "trace-1");
        var shortSale = new EquityShortSaleAvailability(
            EquityShortableStatus.Shortable, EquityBorrowStatus.Available, 1_000m, 0.02m,
            now, EquityContractState.Available);
        var extendedHours = new EquityExtendedHours(
            EquityExtendedHoursStatus.Enabled,
            new HashSet<EquitySessionKind> { EquitySessionKind.PreMarket, EquitySessionKind.AfterHours },
            now,
            EquityContractState.Available);
        var halt = new EquityHalt(
            EquityHaltStatus.NotHalted, null, now, null, EquityContractState.Available);

        return (order, new EquityOrderEligibility(
            instrument, account, calendar, session, shortSale, extendedHours, halt));
    }
}

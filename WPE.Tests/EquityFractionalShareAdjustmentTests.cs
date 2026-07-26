using 币安量化机器人.Core.Equities;
using System.Globalization;

namespace WPE.Tests;

public sealed class EquityFractionalShareAdjustmentTests
{
    private readonly EquityFractionalShareAdjustmentService _service = new();

    [Theory]
    [InlineData(EquityCorporateActionType.Split, 1.5, 15, 0, 15, 0, 0)]
    [InlineData(EquityCorporateActionType.Split, 1.333, 13.33, 2, 13.33, 0, 0)]
    [InlineData(EquityCorporateActionType.ReverseSplit, 0.333, 3.33, 2, 3.33, 0, 0)]
    public void SplitAndReverseSplit_UseExplicitTowardZeroPrecision(
        EquityCorporateActionType type,
        decimal ratio,
        decimal expectedExact,
        int precision,
        decimal expectedRounded,
        decimal expectedFractional,
        decimal expectedCash)
    {
        var request = CreateRequest(type, ratio) with
        {
            RoundingPolicy = new EquityFractionalShareRoundingPolicy(
                EquityFractionalShareRoundingMode.TowardZero,
                precision)
        };

        var result = _service.Calculate(request);

        Assert.Equal(EquityFractionalShareAdjustmentStatus.Calculated, result.Status);
        Assert.Equal(expectedExact, result.ExactAdjustedUnits);
        Assert.Equal(expectedRounded, result.RoundedUnits);
        Assert.Equal(expectedFractional, result.FractionalUnits);
        Assert.Equal(expectedCash, result.CashInLieuAmount);
    }

    [Fact]
    public void FractionalResidual_UsesOnlyAuthorizedCashInLieuPrice()
    {
        var result = _service.Calculate(CreateRequest(
            EquityCorporateActionType.ReverseSplit,
            0.3333m));

        Assert.Equal(3.33m, result.RoundedUnits);
        Assert.Equal(0.003m, result.FractionalUnits);
        Assert.Equal(0.30m, result.CashInLieuAmount);
        Assert.Equal(new EquityCurrency("USD"), result.Currency);
        Assert.Equal("licensed-close", result.PriceSourceId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T19:59:00Z"), result.PriceAsOf);
    }

    [Theory]
    [InlineData(EquityFractionalShareRoundingMode.Unknown, 2)]
    [InlineData(EquityFractionalShareRoundingMode.TowardZero, -1)]
    [InlineData(EquityFractionalShareRoundingMode.TowardZero, 29)]
    public void UnknownOrInvalidRoundingPolicy_IsUnsupported(
        EquityFractionalShareRoundingMode mode,
        int precision)
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m) with
        {
            RoundingPolicy = new EquityFractionalShareRoundingPolicy(mode, precision)
        };

        var result = _service.Calculate(request);

        AssertUnsupported(result, "equity_fractional_rounding_policy_unknown");
    }

    [Theory]
    [InlineData(EquityCorporateActionType.Unknown)]
    [InlineData(EquityCorporateActionType.CashDividend)]
    [InlineData(EquityCorporateActionType.SymbolChange)]
    public void UnknownOrNonSplitAction_IsUnsupported(EquityCorporateActionType type)
    {
        var result = _service.Calculate(CreateRequest(type, 1.5m));

        AssertUnsupported(result, "equity_fractional_action_type_unsupported");
    }

    [Fact]
    public void MissingEffectiveDate_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with { Action = request.Action with { ExDate = null } };

        AssertUnsupported(
            _service.Calculate(request),
            "equity_fractional_effective_date_missing");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    public void MissingOrInvalidRatio_IsUnsupported(string? ratioText)
    {
        decimal? ratio = ratioText is null
            ? null
            : decimal.Parse(ratioText, CultureInfo.InvariantCulture);
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with { Action = request.Action with { Ratio = ratio } };

        AssertUnsupported(
            _service.Calculate(request),
            "equity_fractional_units_or_ratio_invalid");
    }

    [Fact]
    public void MissingPriceSource_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with
        {
            CashInLieuPrice = request.CashInLieuPrice with { SourceId = "" }
        };

        AssertUnsupported(_service.Calculate(request), "equity_fractional_price_fact_unknown");
    }

    [Fact]
    public void MissingUnitPrice_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with
        {
            CashInLieuPrice = request.CashInLieuPrice with { UnitPrice = null }
        };

        AssertUnsupported(_service.Calculate(request), "equity_fractional_price_fact_unknown");
    }

    [Fact]
    public void MissingCurrency_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with
        {
            CashInLieuPrice = request.CashInLieuPrice with { Currency = new EquityCurrency("") }
        };

        AssertUnsupported(_service.Calculate(request), "equity_fractional_price_fact_unknown");
    }

    [Fact]
    public void MissingPriceAsOf_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with
        {
            CashInLieuPrice = request.CashInLieuPrice with { AsOf = null }
        };

        AssertUnsupported(_service.Calculate(request), "equity_fractional_price_fact_unknown");
    }

    [Fact]
    public void StalePrice_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with
        {
            CashInLieuPrice = request.CashInLieuPrice with
            {
                AsOf = DateTimeOffset.Parse("2026-07-21T19:00:00Z")
            }
        };

        AssertUnsupported(_service.Calculate(request), "equity_fractional_price_stale");
    }

    [Fact]
    public void MissingMaximumPriceAge_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m) with
        {
            MaximumPriceAge = null
        };

        AssertUnsupported(_service.Calculate(request), "equity_fractional_price_stale");
    }

    [Fact]
    public void FuturePriceFact_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with
        {
            CashInLieuPrice = request.CashInLieuPrice with
            {
                AsOf = DateTimeOffset.Parse("2026-07-21T20:01:00Z")
            }
        };

        AssertUnsupported(_service.Calculate(request), "equity_fractional_price_stale");
    }

    [Theory]
    [InlineData(false, "price-authorization", "trace-1")]
    [InlineData(true, "", "trace-1")]
    [InlineData(true, "price-authorization", "other-trace")]
    public void PriceAuthorizationMustBeExplicitAndCorrelated(
        bool authorized,
        string authorizationId,
        string correlationId)
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with
        {
            CashInLieuPrice = request.CashInLieuPrice with
            {
                Authorization = request.CashInLieuPrice.Authorization with
                {
                    IsAuthorized = authorized,
                    AuthorizationId = authorizationId,
                    CorrelationId = correlationId
                }
            }
        };

        AssertUnsupported(
            _service.Calculate(request),
            "equity_fractional_price_authorization_required");
    }

    [Fact]
    public void FutureActionAuthorization_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, 1.5m);
        request = request with
        {
            ActionAuthorization = request.ActionAuthorization with
            {
                AuthorizedAt = DateTimeOffset.Parse("2026-07-21T20:01:00Z")
            }
        };

        AssertUnsupported(
            _service.Calculate(request),
            "equity_fractional_action_authorization_required");
    }

    [Fact]
    public void SameFacts_AreDeterministicAndIdempotent()
    {
        var request = CreateRequest(EquityCorporateActionType.ReverseSplit, 0.3333m);

        var first = _service.Calculate(request);
        var second = _service.Calculate(request);

        Assert.Equal(first, second);
        Assert.NotNull(first.IdempotencyKey);
        Assert.Equal(64, first.IdempotencyKey.Length);
        Assert.Equal("trace-1", first.CorrelationId);
    }

    [Fact]
    public void RetryCorrelation_IsAuditedWithoutChangingIdempotencyKey()
    {
        var request = CreateRequest(EquityCorporateActionType.ReverseSplit, 0.3333m);
        var retry = request with
        {
            ActionAuthorization = request.ActionAuthorization with { CorrelationId = "trace-2" },
            CashInLieuPrice = request.CashInLieuPrice with
            {
                Authorization = request.CashInLieuPrice.Authorization with { CorrelationId = "trace-2" }
            }
        };

        var first = _service.Calculate(request);
        var second = _service.Calculate(retry);

        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.Equal("trace-1", first.CorrelationId);
        Assert.Equal("trace-2", second.CorrelationId);
    }

    [Fact]
    public void ResultDoesNotInventPriceOrTaxFields()
    {
        var members = typeof(EquityFractionalShareAdjustmentResult)
            .GetMembers()
            .Select(member => member.Name)
            .ToArray();

        Assert.DoesNotContain(members, name => name.Contains("GeneratedPrice", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Contains("Tax", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(typeof(EquityFractionalShareAdjustmentService).GetConstructors().Single().GetParameters());
    }

    private static EquityFractionalShareAdjustmentRequest CreateRequest(
        EquityCorporateActionType type,
        decimal ratio)
    {
        var action = new EquityCorporateAction(
            "action-1",
            "instrument-1",
            type,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 20),
            null,
            null,
            null,
            ratio,
            null,
            EquityContractState.Available);
        var actionAuthorization = new EquityCorporateActionAuthorization(
            true,
            "action-authorization",
            "trace-1",
            DateTimeOffset.Parse("2026-07-21T19:58:00Z"));
        var priceAuthorization = new EquityCashInLieuAuthorization(
            true,
            "price-authorization",
            "trace-1",
            DateTimeOffset.Parse("2026-07-21T19:59:30Z"));
        var price = new EquityCashInLieuPriceFact(
            "licensed-close",
            100m,
            new EquityCurrency("USD"),
            DateTimeOffset.Parse("2026-07-21T19:59:00Z"),
            EquityContractState.Available,
            priceAuthorization);

        return new EquityFractionalShareAdjustmentRequest(
            action,
            actionAuthorization,
            10m,
            new DateOnly(2026, 7, 21),
            new EquityFractionalShareRoundingPolicy(
                EquityFractionalShareRoundingMode.TowardZero,
                2),
            price,
            DateTimeOffset.Parse("2026-07-21T20:00:00Z"),
            TimeSpan.FromMinutes(5));
    }

    private static void AssertUnsupported(
        EquityFractionalShareAdjustmentResult result,
        string reasonCode)
    {
        Assert.Equal(EquityFractionalShareAdjustmentStatus.Unsupported, result.Status);
        Assert.Equal(reasonCode, result.ReasonCode);
        Assert.Null(result.ExactAdjustedUnits);
        Assert.Null(result.RoundedUnits);
        Assert.Null(result.CashInLieuAmount);
        Assert.Null(result.IdempotencyKey);
    }
}

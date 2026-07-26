using 币安量化机器人.Core.Equities;

namespace WPE.Tests;

public sealed class EquityBrokerFractionalDispositionTests
{
    private readonly EquityBrokerFractionalDispositionService _service = new();

    [Fact]
    public void RetainFraction_PreservesExactUnitsWithoutCashPath()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);

        var result = _service.Evaluate(request);

        Assert.Equal(EquityFractionalDispositionStatus.Eligible, result.Status);
        Assert.Equal(3.333m, result.RetainedUnits);
        Assert.Null(result.WholeOrRoundedUnits);
        Assert.False(result.CashInLieuPermitted);
        Assert.Equal("trace-1", result.CorrelationId);
    }

    [Fact]
    public void RetainFraction_ExplicitlyForbidsCashInLieuPath()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m) with
        {
            CashInLieuPathRequested = true
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_retain_forbids_cash_in_lieu");
    }

    [Fact]
    public void CashInLieu_EmitsResidualForSeparateAuthorizedValuation()
    {
        var request = CreateRequest(EquityFractionalDisposition.CashInLieu, 3.333m) with
        {
            CashInLieuPathRequested = true
        };

        var result = _service.Evaluate(request);

        Assert.Equal(EquityFractionalDispositionStatus.Eligible, result.Status);
        Assert.Equal(3.33m, result.WholeOrRoundedUnits);
        Assert.Equal(0.003m, result.FractionalUnits);
        Assert.True(result.CashInLieuPermitted);
        Assert.Equal(new EquityCurrency("USD"), result.Currency);
    }

    [Fact]
    public void CashInLieu_RequiresExplicitPathRequest()
    {
        AssertUnsupported(
            _service.Evaluate(CreateRequest(EquityFractionalDisposition.CashInLieu, 3.333m)),
            "equity_fractional_cash_in_lieu_path_required");
    }

    [Fact]
    public void WholeShareOnly_AllowsExactWholeResult()
    {
        var request = CreateRequest(EquityFractionalDisposition.WholeShareOnly, 3m) with
        {
            RequestedRounding = new EquityFractionalShareRoundingPolicy(
                EquityFractionalShareRoundingMode.TowardZero,
                0),
            Capability = CreateCapability(EquityFractionalDisposition.WholeShareOnly) with { Precision = 0 }
        };

        var result = _service.Evaluate(request);

        Assert.Equal(EquityFractionalDispositionStatus.Eligible, result.Status);
        Assert.Equal(3m, result.WholeOrRoundedUnits);
        Assert.Equal(0m, result.FractionalUnits);
    }

    [Fact]
    public void WholeShareOnly_NeverSilentlyDiscardsResidual()
    {
        var request = CreateRequest(EquityFractionalDisposition.WholeShareOnly, 3.333m) with
        {
            RequestedRounding = new EquityFractionalShareRoundingPolicy(
                EquityFractionalShareRoundingMode.TowardZero,
                0),
            Capability = CreateCapability(EquityFractionalDisposition.WholeShareOnly) with { Precision = 0 }
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_whole_share_residual_unresolved");
    }

    [Fact]
    public void WholeShareOnly_ForbidsCashInLieuPath()
    {
        var request = CreateRequest(EquityFractionalDisposition.WholeShareOnly, 3m) with
        {
            RequestedRounding = new EquityFractionalShareRoundingPolicy(
                EquityFractionalShareRoundingMode.TowardZero,
                0),
            Capability = CreateCapability(EquityFractionalDisposition.WholeShareOnly) with { Precision = 0 },
            CashInLieuPathRequested = true
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_whole_share_forbids_cash_in_lieu");
    }

    [Fact]
    public void UnknownDisposition_IsUnsupported()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);
        request = request with
        {
            RequestedDisposition = EquityFractionalDisposition.Unknown,
            Capability = CreateCapability(EquityFractionalDisposition.Unknown)
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_uncertified");
    }

    [Theory]
    [InlineData(false, EquityContractState.Available)]
    [InlineData(true, EquityContractState.Unknown)]
    [InlineData(true, EquityContractState.Unsupported)]
    [InlineData(true, EquityContractState.Stale)]
    [InlineData(true, EquityContractState.Error)]
    public void UncertifiedOrUnavailableCapability_IsUnsupported(
        bool certified,
        EquityContractState state)
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);
        request = request with
        {
            Capability = request.Capability with { IsCertified = certified, State = state }
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_uncertified");
    }

    [Theory]
    [InlineData("2026-06-30T23:59:59Z")]
    [InlineData("2026-08-01T00:00:00Z")]
    public void CapabilityOutsideEffectiveWindow_IsUnsupported(string evaluatedAt)
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m) with
        {
            EvaluatedAt = DateTimeOffset.Parse(evaluatedAt)
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_expired");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingCapabilityVersion_IsUnsupported(string version)
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);
        request = request with { Capability = request.Capability with { Version = version } };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_uncertified");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(29)]
    public void InvalidCertifiedPrecision_IsUnsupported(int precision)
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);
        request = request with { Capability = request.Capability with { Precision = precision } };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_uncertified");
    }

    [Fact]
    public void WholeShareOnlyCapabilityRequiresZeroPrecision()
    {
        var request = CreateRequest(EquityFractionalDisposition.WholeShareOnly, 3m);

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_uncertified");
    }

    [Fact]
    public void UnknownCertifiedRoundingMode_IsUnsupported()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);
        request = request with
        {
            Capability = request.Capability with
            {
                RoundingMode = EquityFractionalShareRoundingMode.Unknown
            }
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_uncertified");
    }

    [Fact]
    public void MissingCertifiedCurrency_IsUnsupported()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);
        request = request with
        {
            Capability = request.Capability with { Currency = new EquityCurrency("") }
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_uncertified");
    }

    [Fact]
    public void RequestedDispositionMustMatchCertifiedCapability()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m) with
        {
            RequestedDisposition = EquityFractionalDisposition.CashInLieu,
            CashInLieuPathRequested = true
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_policy_conflict");
    }

    [Fact]
    public void RequestedPrecisionMustMatchCertifiedCapability()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m) with
        {
            RequestedRounding = new EquityFractionalShareRoundingPolicy(
                EquityFractionalShareRoundingMode.TowardZero,
                3)
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_policy_conflict");
    }

    [Fact]
    public void RequestedCurrencyMustMatchCertifiedCapability()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m) with
        {
            RequestedCurrency = new EquityCurrency("JPY")
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_policy_conflict");
    }

    [Fact]
    public void UnsupportedAction_IsRejectedByCapability()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);
        request = request with
        {
            Action = request.Action with { Type = EquityCorporateActionType.CashDividend }
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_action_conflict");
    }

    [Fact]
    public void CapabilityActionSetMustExplicitlyContainAction()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);
        request = request with
        {
            Capability = request.Capability with
            {
                SupportedActions = new HashSet<EquityCorporateActionType>
                {
                    EquityCorporateActionType.ReverseSplit
                }
            }
        };

        AssertUnsupported(
            _service.Evaluate(request),
            "equity_fractional_capability_action_conflict");
    }

    [Fact]
    public void ServiceIsPureDomainAndDeterministic()
    {
        var request = CreateRequest(EquityFractionalDisposition.RetainFraction, 3.333m);

        Assert.Empty(typeof(EquityBrokerFractionalDispositionService).GetConstructors().Single().GetParameters());
        Assert.Equal(_service.Evaluate(request), _service.Evaluate(request));
    }

    private static EquityBrokerFractionalDispositionRequest CreateRequest(
        EquityFractionalDisposition disposition,
        decimal exactUnits)
    {
        var action = new EquityCorporateAction(
            "action-1",
            "instrument-1",
            EquityCorporateActionType.Split,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 20),
            null,
            null,
            null,
            0.3333m,
            null,
            EquityContractState.Available);

        return new EquityBrokerFractionalDispositionRequest(
            action,
            exactUnits,
            new DateOnly(2026, 7, 21),
            DateTimeOffset.Parse("2026-07-21T20:00:00Z"),
            disposition,
            new EquityFractionalShareRoundingPolicy(
                EquityFractionalShareRoundingMode.TowardZero,
                2),
            new EquityCurrency("USD"),
            false,
            "trace-1",
            CreateCapability(disposition));
    }

    private static EquityBrokerFractionalCapability CreateCapability(
        EquityFractionalDisposition disposition) =>
        new(
            "certified-broker",
            "fractional-disposition",
            true,
            "certification-1",
            "2026.07",
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            disposition,
            2,
            EquityFractionalShareRoundingMode.TowardZero,
            new EquityCurrency("USD"),
            new HashSet<EquityCorporateActionType>
            {
                EquityCorporateActionType.Split,
                EquityCorporateActionType.ReverseSplit
            },
            EquityContractState.Available);

    private static void AssertUnsupported(
        EquityBrokerFractionalDispositionResult result,
        string reasonCode)
    {
        Assert.Equal(EquityFractionalDispositionStatus.Unsupported, result.Status);
        Assert.Equal(reasonCode, result.ReasonCode);
        Assert.Equal(EquityFractionalDisposition.Unknown, result.Disposition);
        Assert.Null(result.RetainedUnits);
        Assert.Null(result.WholeOrRoundedUnits);
        Assert.Null(result.FractionalUnits);
        Assert.False(result.CashInLieuPermitted);
        Assert.Null(result.Currency);
    }
}

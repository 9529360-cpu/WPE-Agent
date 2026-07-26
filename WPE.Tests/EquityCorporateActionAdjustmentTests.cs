using 币安量化机器人.Core.Equities;

namespace WPE.Tests;

public sealed class EquityCorporateActionAdjustmentTests
{
    private readonly EquityCorporateActionAdjustmentService _service = new();

    [Theory]
    [InlineData(EquityCorporateActionType.Split, 4, 40)]
    [InlineData(EquityCorporateActionType.ReverseSplit, 0.2, 2)]
    public void SplitActions_AdjustOnlyProvidedUnits(
        EquityCorporateActionType type,
        decimal ratio,
        decimal expectedUnits)
    {
        var request = CreateRequest(type, ratio: ratio);

        var result = _service.Adjust(request);

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Applied, result.Status);
        Assert.Equal(expectedUnits, result.AdjustedUnits);
        Assert.Null(result.CashEntitlement);
        Assert.Equal(EquityAdjustmentDataState.Adjusted, result.DataState);
    }

    [Fact]
    public void CashDividend_CalculatesEntitlementFromAuthorizedAmountAndUnits()
    {
        var request = CreateRequest(
            EquityCorporateActionType.CashDividend,
            cashAmount: 0.25m,
            currency: new EquityCurrency("USD"));

        var result = _service.Adjust(request);

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Applied, result.Status);
        Assert.Equal(10m, result.AdjustedUnits);
        Assert.Equal(2.5m, result.CashEntitlement);
        Assert.Equal(new EquityCurrency("USD"), result.CashCurrency);
    }

    [Fact]
    public void SymbolChange_UsesOnlyExplicitReplacementSymbol()
    {
        var request = CreateRequest(
            EquityCorporateActionType.SymbolChange,
            replacementSymbol: "NEW");

        var result = _service.Adjust(request);

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Applied, result.Status);
        Assert.Equal("NEW", result.Symbol);
        Assert.Equal(10m, result.AdjustedUnits);
    }

    [Theory]
    [InlineData(EquityCorporateActionType.Unknown)]
    [InlineData(EquityCorporateActionType.StockDividend)]
    [InlineData(EquityCorporateActionType.Merger)]
    public void UnsupportedAction_DoesNotProduceAdjustment(EquityCorporateActionType type)
    {
        var result = _service.Adjust(CreateRequest(type));

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Unsupported, result.Status);
        Assert.Equal(EquityAdjustmentDataState.Unsupported, result.DataState);
        Assert.Null(result.AdjustedUnits);
        Assert.Null(result.CashEntitlement);
        Assert.Null(result.IdempotencyKey);
    }

    [Fact]
    public void MissingEffectiveDate_IsUnsupported()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, ratio: 2m);
        request = request with { Action = request.Action with { ExDate = null } };

        var result = _service.Adjust(request);

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Unsupported, result.Status);
        Assert.Equal("equity_action_effective_date_missing", result.ReasonCode);
        Assert.Equal(EquityAdjustmentDataState.Unsupported, result.DataState);
    }

    [Theory]
    [InlineData(EquityCorporateActionType.Split)]
    [InlineData(EquityCorporateActionType.ReverseSplit)]
    public void SplitWithoutPositiveRatio_IsError(EquityCorporateActionType type)
    {
        var result = _service.Adjust(CreateRequest(type, ratio: null));

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Error, result.Status);
        Assert.Equal("equity_action_ratio_invalid", result.ReasonCode);
        Assert.Null(result.AdjustedUnits);
    }

    [Fact]
    public void CashDividendWithoutAmountOrCurrency_IsError()
    {
        var result = _service.Adjust(CreateRequest(EquityCorporateActionType.CashDividend));

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Error, result.Status);
        Assert.Equal("equity_action_cash_terms_invalid", result.ReasonCode);
        Assert.Null(result.CashEntitlement);
    }

    [Fact]
    public void SymbolChangeWithoutReplacementSymbol_IsError()
    {
        var result = _service.Adjust(CreateRequest(EquityCorporateActionType.SymbolChange));

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Error, result.Status);
        Assert.Equal("equity_action_replacement_symbol_invalid", result.ReasonCode);
        Assert.Equal("OLD", result.Symbol);
    }

    [Theory]
    [InlineData(false, "authorization-1", "trace-1")]
    [InlineData(true, "", "trace-1")]
    [InlineData(true, "authorization-1", "")]
    public void AuthorizationMustBeExplicit(bool authorized, string authorizationId, string correlationId)
    {
        var request = CreateRequest(EquityCorporateActionType.Split, ratio: 2m);
        request = request with
        {
            Authorization = request.Authorization with
            {
                IsAuthorized = authorized,
                AuthorizationId = authorizationId,
                CorrelationId = correlationId
            }
        };

        var result = _service.Adjust(request);

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Error, result.Status);
        Assert.Equal("equity_action_authorization_required", result.ReasonCode);
        Assert.Null(result.AdjustedUnits);
    }

    [Fact]
    public void SameAuthorizedInput_ProducesIdenticalResultAndIdempotencyKey()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, ratio: 2m);

        var first = _service.Adjust(request);
        var second = _service.Adjust(request);

        Assert.Equal(first, second);
        Assert.NotNull(first.IdempotencyKey);
        Assert.Equal(64, first.IdempotencyKey.Length);
        Assert.Equal("trace-1", first.CorrelationId);
    }

    [Fact]
    public void RetryCorrelation_IsAuditedButDoesNotChangeIdempotencyIdentity()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, ratio: 2m);
        var retry = request with
        {
            Authorization = request.Authorization with { CorrelationId = "trace-2" }
        };

        var first = _service.Adjust(request);
        var second = _service.Adjust(retry);

        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.Equal("trace-1", first.CorrelationId);
        Assert.Equal("trace-2", second.CorrelationId);
    }

    [Fact]
    public void AppliedActionId_WithAdjustedInput_IsIdempotentNoOp()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, ratio: 2m) with
        {
            InputDataState = EquityAdjustmentDataState.Adjusted,
            AppliedActionIds = new HashSet<string>(StringComparer.Ordinal) { "action-1" }
        };

        var result = _service.Adjust(request);

        Assert.Equal(EquityCorporateActionAdjustmentStatus.AlreadyApplied, result.Status);
        Assert.Equal(10m, result.AdjustedUnits);
        Assert.Equal(EquityAdjustmentDataState.Adjusted, result.DataState);
        Assert.Equal("trace-1", result.CorrelationId);
    }

    [Fact]
    public void AppliedActionId_CannotMarkUnadjustedInputAsAdjusted()
    {
        var request = CreateRequest(EquityCorporateActionType.Split, ratio: 2m) with
        {
            AppliedActionIds = new HashSet<string>(StringComparer.Ordinal) { "action-1" }
        };

        var result = _service.Adjust(request);

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Error, result.Status);
        Assert.Equal("equity_action_unadjusted_marked_applied", result.ReasonCode);
        Assert.Equal(EquityAdjustmentDataState.Error, result.DataState);
        Assert.Null(result.AdjustedUnits);
    }

    [Theory]
    [InlineData(EquityAdjustmentDataState.Unknown)]
    [InlineData(EquityAdjustmentDataState.Unsupported)]
    [InlineData(EquityAdjustmentDataState.Error)]
    public void UnavailableInputState_NeverBecomesAdjusted(EquityAdjustmentDataState state)
    {
        var request = CreateRequest(EquityCorporateActionType.Split, ratio: 2m) with
        {
            InputDataState = state
        };

        var result = _service.Adjust(request);

        Assert.Equal(EquityCorporateActionAdjustmentStatus.Error, result.Status);
        Assert.NotEqual(EquityAdjustmentDataState.Adjusted, result.DataState);
        Assert.Null(result.AdjustedUnits);
    }

    [Fact]
    public void ResultContract_HasNoPriceMemberAndServiceHasNoRuntimeOrNetworkDependency()
    {
        var resultMembers = typeof(EquityCorporateActionAdjustmentResult)
            .GetMembers()
            .Select(member => member.Name)
            .ToArray();
        var constructor = typeof(EquityCorporateActionAdjustmentService).GetConstructors().Single();

        Assert.DoesNotContain(resultMembers, name => name.Contains("Price", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(constructor.GetParameters());
    }

    private static EquityCorporateActionAdjustmentRequest CreateRequest(
        EquityCorporateActionType type,
        decimal? ratio = null,
        decimal? cashAmount = null,
        EquityCurrency? currency = null,
        string? replacementSymbol = null)
    {
        var action = new EquityCorporateAction(
            "action-1",
            "instrument-1",
            type,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 20),
            null,
            null,
            cashAmount,
            ratio,
            currency,
            EquityContractState.Available);
        var authorization = new EquityCorporateActionAuthorization(
            true,
            "authorization-1",
            "trace-1",
            DateTimeOffset.Parse("2026-07-19T00:00:00Z"));

        return new EquityCorporateActionAdjustmentRequest(
            action,
            "OLD",
            replacementSymbol,
            10m,
            new DateOnly(2026, 7, 21),
            EquityAdjustmentDataState.Unadjusted,
            authorization,
            new HashSet<string>(StringComparer.Ordinal));
    }
}

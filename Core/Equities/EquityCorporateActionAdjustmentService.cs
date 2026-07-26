using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 币安量化机器人.Core.Equities;

public enum EquityCorporateActionAdjustmentStatus
{
    Unknown = 0,
    Applied,
    AlreadyApplied,
    Unsupported,
    Error
}

public enum EquityAdjustmentDataState
{
    Unknown = 0,
    Unadjusted,
    Adjusted,
    Unsupported,
    Error
}

public sealed record EquityCorporateActionAuthorization(
    bool IsAuthorized,
    string AuthorizationId,
    string CorrelationId,
    DateTimeOffset AuthorizedAt);

public sealed record EquityCorporateActionAdjustmentRequest(
    EquityCorporateAction Action,
    string CurrentSymbol,
    string? ReplacementSymbol,
    decimal Units,
    DateOnly AsOfDate,
    EquityAdjustmentDataState InputDataState,
    EquityCorporateActionAuthorization Authorization,
    IReadOnlySet<string> AppliedActionIds);

public sealed record EquityCorporateActionAdjustmentResult(
    EquityCorporateActionAdjustmentStatus Status,
    string ReasonCode,
    string CorrelationId,
    string? IdempotencyKey,
    string InstrumentId,
    string Symbol,
    decimal? AdjustedUnits,
    decimal? CashEntitlement,
    EquityCurrency? CashCurrency,
    EquityAdjustmentDataState DataState);

public sealed class EquityCorporateActionAdjustmentService
{
    public EquityCorporateActionAdjustmentResult Adjust(EquityCorporateActionAdjustmentRequest request)
    {
        var correlationId = request.Authorization?.CorrelationId ?? string.Empty;
        var instrumentId = request.Action?.InstrumentId ?? string.Empty;
        var symbol = request.CurrentSymbol ?? string.Empty;

        if (!HasExplicitAuthorization(request.Authorization))
            return Error("equity_action_authorization_required", correlationId, instrumentId, symbol);
        if (request.Action is null || string.IsNullOrWhiteSpace(request.Action.ActionId))
            return Error("equity_action_identity_required", correlationId, instrumentId, symbol);
        if (request.InputDataState == EquityAdjustmentDataState.Unknown)
            return Error("equity_action_input_state_unknown", correlationId, instrumentId, symbol);
        if (request.InputDataState is EquityAdjustmentDataState.Unsupported or EquityAdjustmentDataState.Error)
            return Error("equity_action_input_unavailable", correlationId, instrumentId, symbol);
        if (string.IsNullOrWhiteSpace(instrumentId) || string.IsNullOrWhiteSpace(symbol) || request.Units < 0)
            return Error("equity_action_input_invalid", correlationId, instrumentId, symbol);

        var action = request.Action;
        if (action.State != EquityContractState.Available)
            return Error("equity_action_unavailable", correlationId, instrumentId, symbol);
        if (!IsSupported(action.Type))
            return Unsupported("equity_action_type_unsupported", correlationId, instrumentId, symbol);

        var effectiveDate = action.ExDate;
        if (effectiveDate is null)
            return Unsupported("equity_action_effective_date_missing", correlationId, instrumentId, symbol);
        if (request.AsOfDate < effectiveDate.Value)
            return Unsupported("equity_action_not_effective", correlationId, instrumentId, symbol);

        var idempotencyKey = BuildIdempotencyKey(request);
        var alreadyApplied = request.AppliedActionIds?.Contains(action.ActionId) == true;
        if (alreadyApplied)
        {
            if (request.InputDataState != EquityAdjustmentDataState.Adjusted)
                return Error(
                    "equity_action_unadjusted_marked_applied",
                    correlationId,
                    instrumentId,
                    symbol,
                    idempotencyKey);

            return new EquityCorporateActionAdjustmentResult(
                EquityCorporateActionAdjustmentStatus.AlreadyApplied,
                "equity_action_already_applied",
                correlationId,
                idempotencyKey,
                instrumentId,
                symbol,
                request.Units,
                null,
                null,
                EquityAdjustmentDataState.Adjusted);
        }

        if (request.InputDataState != EquityAdjustmentDataState.Unadjusted)
            return Error(
                "equity_action_adjustment_state_conflict",
                correlationId,
                instrumentId,
                symbol,
                idempotencyKey);

        return action.Type switch
        {
            EquityCorporateActionType.Split => AdjustSplit(request, idempotencyKey),
            EquityCorporateActionType.ReverseSplit => AdjustSplit(request, idempotencyKey),
            EquityCorporateActionType.CashDividend => AdjustCashDividend(request, idempotencyKey),
            EquityCorporateActionType.SymbolChange => AdjustSymbol(request, idempotencyKey),
            _ => Unsupported("equity_action_type_unsupported", correlationId, instrumentId, symbol)
        };
    }

    private static EquityCorporateActionAdjustmentResult AdjustSplit(
        EquityCorporateActionAdjustmentRequest request,
        string idempotencyKey)
    {
        if (request.Action.Ratio is null or <= 0)
            return Error(
                "equity_action_ratio_invalid",
                request.Authorization.CorrelationId,
                request.Action.InstrumentId,
                request.CurrentSymbol,
                idempotencyKey);

        return Applied(
            request,
            idempotencyKey,
            request.CurrentSymbol,
            request.Units * request.Action.Ratio.Value,
            null,
            null);
    }

    private static EquityCorporateActionAdjustmentResult AdjustCashDividend(
        EquityCorporateActionAdjustmentRequest request,
        string idempotencyKey)
    {
        if (request.Action.CashAmount is null or <= 0 ||
            request.Action.Currency is null ||
            string.IsNullOrWhiteSpace(request.Action.Currency.Value.Code))
            return Error(
                "equity_action_cash_terms_invalid",
                request.Authorization.CorrelationId,
                request.Action.InstrumentId,
                request.CurrentSymbol,
                idempotencyKey);

        return Applied(
            request,
            idempotencyKey,
            request.CurrentSymbol,
            request.Units,
            request.Units * request.Action.CashAmount.Value,
            request.Action.Currency);
    }

    private static EquityCorporateActionAdjustmentResult AdjustSymbol(
        EquityCorporateActionAdjustmentRequest request,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(request.ReplacementSymbol) ||
            StringComparer.Ordinal.Equals(request.CurrentSymbol, request.ReplacementSymbol))
            return Error(
                "equity_action_replacement_symbol_invalid",
                request.Authorization.CorrelationId,
                request.Action.InstrumentId,
                request.CurrentSymbol,
                idempotencyKey);

        return Applied(
            request,
            idempotencyKey,
            request.ReplacementSymbol,
            request.Units,
            null,
            null);
    }

    private static EquityCorporateActionAdjustmentResult Applied(
        EquityCorporateActionAdjustmentRequest request,
        string idempotencyKey,
        string symbol,
        decimal adjustedUnits,
        decimal? cashEntitlement,
        EquityCurrency? cashCurrency) =>
        new(
            EquityCorporateActionAdjustmentStatus.Applied,
            "equity_action_applied",
            request.Authorization.CorrelationId,
            idempotencyKey,
            request.Action.InstrumentId,
            symbol,
            adjustedUnits,
            cashEntitlement,
            cashCurrency,
            EquityAdjustmentDataState.Adjusted);

    private static bool HasExplicitAuthorization(EquityCorporateActionAuthorization? authorization) =>
        authorization is
        {
            IsAuthorized: true,
            AuthorizationId.Length: > 0,
            CorrelationId.Length: > 0
        } &&
        !string.IsNullOrWhiteSpace(authorization.AuthorizationId) &&
        !string.IsNullOrWhiteSpace(authorization.CorrelationId) &&
        authorization.AuthorizedAt != default;

    private static bool IsSupported(EquityCorporateActionType type) =>
        type is EquityCorporateActionType.Split or
            EquityCorporateActionType.ReverseSplit or
            EquityCorporateActionType.CashDividend or
            EquityCorporateActionType.SymbolChange;

    private static string BuildIdempotencyKey(EquityCorporateActionAdjustmentRequest request)
    {
        var canonical = string.Join(
            "|",
            request.Action.ActionId,
            request.Action.InstrumentId,
            ((int)request.Action.Type).ToString(CultureInfo.InvariantCulture),
            request.Action.ExDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            request.Action.Ratio?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.Action.CashAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.Action.Currency?.Code ?? string.Empty,
            request.CurrentSymbol,
            request.ReplacementSymbol ?? string.Empty,
            request.Units.ToString(CultureInfo.InvariantCulture),
            request.Authorization.AuthorizationId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static EquityCorporateActionAdjustmentResult Unsupported(
        string reason,
        string correlationId,
        string instrumentId,
        string symbol) =>
        new(
            EquityCorporateActionAdjustmentStatus.Unsupported,
            reason,
            correlationId,
            null,
            instrumentId,
            symbol,
            null,
            null,
            null,
            EquityAdjustmentDataState.Unsupported);

    private static EquityCorporateActionAdjustmentResult Error(
        string reason,
        string correlationId,
        string instrumentId,
        string symbol,
        string? idempotencyKey = null) =>
        new(
            EquityCorporateActionAdjustmentStatus.Error,
            reason,
            correlationId,
            idempotencyKey,
            instrumentId,
            symbol,
            null,
            null,
            null,
            EquityAdjustmentDataState.Error);
}

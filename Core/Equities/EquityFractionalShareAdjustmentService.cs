using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 币安量化机器人.Core.Equities;

public enum EquityFractionalShareRoundingMode
{
    Unknown = 0,
    TowardZero
}

public enum EquityFractionalShareAdjustmentStatus
{
    Unknown = 0,
    Calculated,
    Unsupported
}

public sealed record EquityFractionalShareRoundingPolicy(
    EquityFractionalShareRoundingMode Mode,
    int Precision);

public sealed record EquityCashInLieuAuthorization(
    bool IsAuthorized,
    string AuthorizationId,
    string CorrelationId,
    DateTimeOffset AuthorizedAt);

public sealed record EquityCashInLieuPriceFact(
    string SourceId,
    decimal? UnitPrice,
    EquityCurrency Currency,
    DateTimeOffset? AsOf,
    EquityContractState State,
    EquityCashInLieuAuthorization Authorization);

public sealed record EquityFractionalShareAdjustmentRequest(
    EquityCorporateAction Action,
    EquityCorporateActionAuthorization ActionAuthorization,
    decimal OriginalUnits,
    DateOnly? AdjustmentDate,
    EquityFractionalShareRoundingPolicy RoundingPolicy,
    EquityCashInLieuPriceFact CashInLieuPrice,
    DateTimeOffset? ValuationAt,
    TimeSpan? MaximumPriceAge);

public sealed record EquityFractionalShareAdjustmentResult(
    EquityFractionalShareAdjustmentStatus Status,
    string ReasonCode,
    string CorrelationId,
    string? IdempotencyKey,
    string InstrumentId,
    decimal? ExactAdjustedUnits,
    decimal? RoundedUnits,
    decimal? FractionalUnits,
    decimal? CashInLieuAmount,
    EquityCurrency? Currency,
    string? PriceSourceId,
    DateTimeOffset? PriceAsOf);

public sealed class EquityFractionalShareAdjustmentService
{
    public EquityFractionalShareAdjustmentResult Calculate(EquityFractionalShareAdjustmentRequest request)
    {
        var correlationId = request.ActionAuthorization?.CorrelationId ?? string.Empty;
        var instrumentId = request.Action?.InstrumentId ?? string.Empty;

        if (!HasActionAuthorization(request.ActionAuthorization) ||
            request.ValuationAt is { } valuationAt &&
            request.ActionAuthorization is { } authorization &&
            authorization.AuthorizedAt > valuationAt)
            return Unsupported("equity_fractional_action_authorization_required", correlationId, instrumentId);
        if (request.Action is null ||
            request.Action.State != EquityContractState.Available ||
            string.IsNullOrWhiteSpace(request.Action.ActionId) ||
            string.IsNullOrWhiteSpace(instrumentId))
            return Unsupported("equity_fractional_action_unknown", correlationId, instrumentId);
        if (request.Action.Type is not (EquityCorporateActionType.Split or EquityCorporateActionType.ReverseSplit))
            return Unsupported("equity_fractional_action_type_unsupported", correlationId, instrumentId);
        if (request.Action.ExDate is null || request.AdjustmentDate is null)
            return Unsupported("equity_fractional_effective_date_missing", correlationId, instrumentId);
        if (request.AdjustmentDate < request.Action.ExDate)
            return Unsupported("equity_fractional_action_not_effective", correlationId, instrumentId);
        if (request.Action.Ratio is null or <= 0 || request.OriginalUnits < 0)
            return Unsupported("equity_fractional_units_or_ratio_invalid", correlationId, instrumentId);
        if (!HasExplicitRoundingPolicy(request.RoundingPolicy))
            return Unsupported("equity_fractional_rounding_policy_unknown", correlationId, instrumentId);
        if (!HasAuthorizedPrice(request, out var priceReason))
            return Unsupported(priceReason, correlationId, instrumentId);

        decimal exactUnits;
        decimal roundedUnits;
        decimal cashAmount;
        try
        {
            exactUnits = request.OriginalUnits * request.Action.Ratio.Value;
            roundedUnits = decimal.Round(
                exactUnits,
                request.RoundingPolicy.Precision,
                MidpointRounding.ToZero);
            var fractionalUnits = exactUnits - roundedUnits;
            cashAmount = fractionalUnits * request.CashInLieuPrice.UnitPrice!.Value;

            var idempotencyKey = BuildIdempotencyKey(request);
            return new EquityFractionalShareAdjustmentResult(
                EquityFractionalShareAdjustmentStatus.Calculated,
                "equity_fractional_adjustment_calculated",
                correlationId,
                idempotencyKey,
                instrumentId,
                exactUnits,
                roundedUnits,
                fractionalUnits,
                cashAmount,
                request.CashInLieuPrice.Currency,
                request.CashInLieuPrice.SourceId,
                request.CashInLieuPrice.AsOf);
        }
        catch (OverflowException)
        {
            return Unsupported("equity_fractional_calculation_overflow", correlationId, instrumentId);
        }
    }

    private static bool HasActionAuthorization(EquityCorporateActionAuthorization? authorization) =>
        authorization is
        {
            IsAuthorized: true,
            AuthorizationId.Length: > 0,
            CorrelationId.Length: > 0
        } &&
        !string.IsNullOrWhiteSpace(authorization.AuthorizationId) &&
        !string.IsNullOrWhiteSpace(authorization.CorrelationId) &&
        authorization.AuthorizedAt != default;

    private static bool HasExplicitRoundingPolicy(EquityFractionalShareRoundingPolicy? policy) =>
        policy is
        {
            Mode: EquityFractionalShareRoundingMode.TowardZero,
            Precision: >= 0 and <= 28
        };

    private static bool HasAuthorizedPrice(
        EquityFractionalShareAdjustmentRequest request,
        out string reasonCode)
    {
        if (request.CashInLieuPrice is null ||
            request.CashInLieuPrice.State != EquityContractState.Available ||
            string.IsNullOrWhiteSpace(request.CashInLieuPrice.SourceId) ||
            request.CashInLieuPrice.UnitPrice is null or <= 0 ||
            string.IsNullOrWhiteSpace(request.CashInLieuPrice.Currency.Code) ||
            request.CashInLieuPrice.AsOf is null)
        {
            reasonCode = "equity_fractional_price_fact_unknown";
            return false;
        }
        if (request.ValuationAt is null ||
            request.ValuationAt == default ||
            request.MaximumPriceAge is null ||
            request.MaximumPriceAge <= TimeSpan.Zero ||
            request.CashInLieuPrice.AsOf > request.ValuationAt ||
            request.ValuationAt - request.CashInLieuPrice.AsOf > request.MaximumPriceAge)
        {
            reasonCode = "equity_fractional_price_stale";
            return false;
        }

        var authorization = request.CashInLieuPrice.Authorization;
        if (authorization is null ||
            !authorization.IsAuthorized ||
            string.IsNullOrWhiteSpace(authorization.AuthorizationId) ||
            string.IsNullOrWhiteSpace(authorization.CorrelationId) ||
            authorization.AuthorizedAt == default ||
            authorization.AuthorizedAt > request.ValuationAt ||
            !StringComparer.Ordinal.Equals(
                authorization.CorrelationId,
                request.ActionAuthorization.CorrelationId))
        {
            reasonCode = "equity_fractional_price_authorization_required";
            return false;
        }

        reasonCode = "equity_fractional_price_authorized";
        return true;
    }

    private static string BuildIdempotencyKey(EquityFractionalShareAdjustmentRequest request)
    {
        var canonical = string.Join(
            "|",
            request.Action.ActionId,
            request.Action.InstrumentId,
            ((int)request.Action.Type).ToString(CultureInfo.InvariantCulture),
            request.Action.ExDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            request.Action.Ratio?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.OriginalUnits.ToString(CultureInfo.InvariantCulture),
            request.AdjustmentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            ((int)request.RoundingPolicy.Mode).ToString(CultureInfo.InvariantCulture),
            request.RoundingPolicy.Precision.ToString(CultureInfo.InvariantCulture),
            request.CashInLieuPrice.SourceId,
            request.CashInLieuPrice.UnitPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.CashInLieuPrice.Currency.Code,
            request.CashInLieuPrice.AsOf?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            request.ActionAuthorization.AuthorizationId,
            request.CashInLieuPrice.Authorization.AuthorizationId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static EquityFractionalShareAdjustmentResult Unsupported(
        string reasonCode,
        string correlationId,
        string instrumentId) =>
        new(
            EquityFractionalShareAdjustmentStatus.Unsupported,
            reasonCode,
            correlationId,
            null,
            instrumentId,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
}

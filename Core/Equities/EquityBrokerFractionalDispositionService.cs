namespace 币安量化机器人.Core.Equities;

public enum EquityFractionalDisposition
{
    Unknown = 0,
    RetainFraction,
    CashInLieu,
    WholeShareOnly
}

public enum EquityFractionalDispositionStatus
{
    Unknown = 0,
    Eligible,
    Unsupported
}

public sealed record EquityBrokerFractionalCapability(
    string BrokerId,
    string CapabilityId,
    bool IsCertified,
    string CertificationId,
    string Version,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? ExpiresAt,
    EquityFractionalDisposition Disposition,
    int Precision,
    EquityFractionalShareRoundingMode RoundingMode,
    EquityCurrency Currency,
    IReadOnlySet<EquityCorporateActionType> SupportedActions,
    EquityContractState State);

public sealed record EquityBrokerFractionalDispositionRequest(
    EquityCorporateAction Action,
    decimal ExactAdjustedUnits,
    DateOnly? AdjustmentDate,
    DateTimeOffset? EvaluatedAt,
    EquityFractionalDisposition RequestedDisposition,
    EquityFractionalShareRoundingPolicy RequestedRounding,
    EquityCurrency RequestedCurrency,
    bool CashInLieuPathRequested,
    string CorrelationId,
    EquityBrokerFractionalCapability Capability);

public sealed record EquityBrokerFractionalDispositionResult(
    EquityFractionalDispositionStatus Status,
    string ReasonCode,
    string CorrelationId,
    string BrokerId,
    string CapabilityId,
    string CapabilityVersion,
    EquityFractionalDisposition Disposition,
    decimal? RetainedUnits,
    decimal? WholeOrRoundedUnits,
    decimal? FractionalUnits,
    bool CashInLieuPermitted,
    EquityCurrency? Currency);

public sealed class EquityBrokerFractionalDispositionService
{
    public EquityBrokerFractionalDispositionResult Evaluate(
        EquityBrokerFractionalDispositionRequest request)
    {
        var correlationId = request.CorrelationId ?? string.Empty;
        var capability = request.Capability;
        if (!HasCertifiedCapability(capability) || string.IsNullOrWhiteSpace(correlationId))
            return Unsupported(request, "equity_fractional_capability_uncertified");
        if (request.EvaluatedAt is null || request.EvaluatedAt == default ||
            request.EvaluatedAt < capability.EffectiveFrom ||
            request.EvaluatedAt >= capability.ExpiresAt)
            return Unsupported(request, "equity_fractional_capability_expired");
        if (!HasSupportedAction(request))
            return Unsupported(request, "equity_fractional_capability_action_conflict");
        if (!MatchesCertifiedPolicy(request))
            return Unsupported(request, "equity_fractional_capability_policy_conflict");

        return capability.Disposition switch
        {
            EquityFractionalDisposition.RetainFraction => RetainFraction(request),
            EquityFractionalDisposition.CashInLieu => PermitCashInLieu(request),
            EquityFractionalDisposition.WholeShareOnly => RequireWholeShares(request),
            _ => Unsupported(request, "equity_fractional_capability_unknown")
        };
    }

    private static EquityBrokerFractionalDispositionResult RetainFraction(
        EquityBrokerFractionalDispositionRequest request)
    {
        if (request.CashInLieuPathRequested)
            return Unsupported(request, "equity_fractional_retain_forbids_cash_in_lieu");

        return Eligible(
            request,
            "equity_fractional_retain_eligible",
            request.ExactAdjustedUnits,
            null,
            null,
            false);
    }

    private static EquityBrokerFractionalDispositionResult PermitCashInLieu(
        EquityBrokerFractionalDispositionRequest request)
    {
        if (!request.CashInLieuPathRequested)
            return Unsupported(request, "equity_fractional_cash_in_lieu_path_required");

        var rounded = Round(request.ExactAdjustedUnits, request.Capability.Precision);
        return Eligible(
            request,
            "equity_fractional_cash_in_lieu_eligible",
            null,
            rounded,
            request.ExactAdjustedUnits - rounded,
            true);
    }

    private static EquityBrokerFractionalDispositionResult RequireWholeShares(
        EquityBrokerFractionalDispositionRequest request)
    {
        if (request.CashInLieuPathRequested)
            return Unsupported(request, "equity_fractional_whole_share_forbids_cash_in_lieu");

        var rounded = Round(request.ExactAdjustedUnits, request.Capability.Precision);
        if (rounded != request.ExactAdjustedUnits)
            return Unsupported(request, "equity_fractional_whole_share_residual_unresolved");

        return Eligible(
            request,
            "equity_fractional_whole_share_eligible",
            null,
            rounded,
            0m,
            false);
    }

    private static bool HasCertifiedCapability(EquityBrokerFractionalCapability? capability) =>
        capability is not null &&
        capability.IsCertified &&
        capability.State == EquityContractState.Available &&
        capability.Disposition != EquityFractionalDisposition.Unknown &&
        !string.IsNullOrWhiteSpace(capability.BrokerId) &&
        !string.IsNullOrWhiteSpace(capability.CapabilityId) &&
        !string.IsNullOrWhiteSpace(capability.CertificationId) &&
        !string.IsNullOrWhiteSpace(capability.Version) &&
        capability.EffectiveFrom is not null &&
        capability.ExpiresAt is not null &&
        capability.EffectiveFrom < capability.ExpiresAt &&
        capability.Precision is >= 0 and <= 28 &&
        (capability.Disposition != EquityFractionalDisposition.WholeShareOnly || capability.Precision == 0) &&
        capability.RoundingMode == EquityFractionalShareRoundingMode.TowardZero &&
        !string.IsNullOrWhiteSpace(capability.Currency.Code) &&
        capability.SupportedActions is not null &&
        capability.SupportedActions.Count > 0 &&
        capability.SupportedActions.All(action => action is
            EquityCorporateActionType.Split or EquityCorporateActionType.ReverseSplit);

    private static bool HasSupportedAction(EquityBrokerFractionalDispositionRequest request) =>
        request.Action is not null &&
        request.Action.State == EquityContractState.Available &&
        request.Action.Type is EquityCorporateActionType.Split or EquityCorporateActionType.ReverseSplit &&
        request.Action.ExDate is not null &&
        request.AdjustmentDate is not null &&
        request.AdjustmentDate >= request.Action.ExDate &&
        request.ExactAdjustedUnits >= 0 &&
        request.Capability.SupportedActions.Contains(request.Action.Type);

    private static bool MatchesCertifiedPolicy(EquityBrokerFractionalDispositionRequest request) =>
        request.RequestedDisposition != EquityFractionalDisposition.Unknown &&
        request.RequestedDisposition == request.Capability.Disposition &&
        request.RequestedRounding is not null &&
        request.RequestedRounding.Mode == request.Capability.RoundingMode &&
        request.RequestedRounding.Precision == request.Capability.Precision &&
        request.RequestedCurrency == request.Capability.Currency;

    private static decimal Round(decimal units, int precision) =>
        decimal.Round(units, precision, MidpointRounding.ToZero);

    private static EquityBrokerFractionalDispositionResult Eligible(
        EquityBrokerFractionalDispositionRequest request,
        string reasonCode,
        decimal? retainedUnits,
        decimal? roundedUnits,
        decimal? fractionalUnits,
        bool cashInLieuPermitted) =>
        new(
            EquityFractionalDispositionStatus.Eligible,
            reasonCode,
            request.CorrelationId,
            request.Capability.BrokerId,
            request.Capability.CapabilityId,
            request.Capability.Version,
            request.Capability.Disposition,
            retainedUnits,
            roundedUnits,
            fractionalUnits,
            cashInLieuPermitted,
            request.Capability.Currency);

    private static EquityBrokerFractionalDispositionResult Unsupported(
        EquityBrokerFractionalDispositionRequest request,
        string reasonCode) =>
        new(
            EquityFractionalDispositionStatus.Unsupported,
            reasonCode,
            request.CorrelationId ?? string.Empty,
            request.Capability?.BrokerId ?? string.Empty,
            request.Capability?.CapabilityId ?? string.Empty,
            request.Capability?.Version ?? string.Empty,
            EquityFractionalDisposition.Unknown,
            null,
            null,
            null,
            false,
            null);
}

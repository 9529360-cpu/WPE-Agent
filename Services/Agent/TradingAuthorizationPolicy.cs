using WpeAgent.TradingAuthorization;

namespace 币安量化机器人.Services.Agent;

public static class TradingAuthorizationPolicy
{
    public static TradingAuthorizationDecision Evaluate(TradingAuthorizationRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.IsDefined(request.Mode))
            return Deny("authorization.invalid-mode");
        if (request.Mode == TradingAuthorizationMode.Research)
            return Deny("authorization.research-read-only");
        if (request.Mode == TradingAuthorizationMode.Signal)
            return Deny("authorization.signal-no-mutation");
        if (!request.IsTestnet)
            return Deny("authorization.testnet-required");
        if (string.IsNullOrWhiteSpace(request.CorrelationId) || string.IsNullOrWhiteSpace(request.IntentHash))
            return Deny("authorization.context-invalid");

        var risk = ValidateRiskReceipt(request, nowUtc);
        if (risk is not null)
            return risk;

        if (request.Mode == TradingAuthorizationMode.Auto)
            return Allow("authorization.auto-approved");

        if (string.IsNullOrWhiteSpace(request.UserId) ||
            string.IsNullOrWhiteSpace(request.DeviceId) ||
            string.IsNullOrWhiteSpace(request.SessionId))
            return Deny("authorization.review-context-invalid");

        return ValidateApprovalReceipt(request, nowUtc) ?? Allow("authorization.review-approved");
    }

    private static TradingAuthorizationDecision? ValidateRiskReceipt(TradingAuthorizationRequest request, DateTimeOffset nowUtc)
    {
        var receipt = request.RiskReceipt;
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.ReceiptId))
            return Deny("authorization.risk-required");
        if (!receipt.Approved)
            return Deny("authorization.risk-rejected");
        if (receipt.RevokedAtUtc is not null)
            return Deny("authorization.risk-revoked");
        if (receipt.ExpiresAtUtc <= receipt.IssuedAtUtc || receipt.IssuedAtUtc > nowUtc)
            return Deny("authorization.risk-not-valid");
        if (receipt.ExpiresAtUtc <= nowUtc)
            return Deny("authorization.risk-expired");
        if (!Matches(receipt.CorrelationId, request.CorrelationId) || !Matches(receipt.IntentHash, request.IntentHash))
            return Deny("authorization.risk-context-mismatch");
        return null;
    }

    private static TradingAuthorizationDecision? ValidateApprovalReceipt(TradingAuthorizationRequest request, DateTimeOffset nowUtc)
    {
        var receipt = request.ApprovalReceipt;
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.ReceiptId))
            return Deny("authorization.review-approval-required");
        if (!receipt.Approved)
            return Deny("authorization.review-rejected");
        if (receipt.RevokedAtUtc is not null)
            return Deny("authorization.review-revoked");
        if (receipt.ConsumedAtUtc is not null)
            return Deny("authorization.review-consumed");
        if (receipt.ExpiresAtUtc <= receipt.IssuedAtUtc || receipt.IssuedAtUtc > nowUtc)
            return Deny("authorization.review-not-valid");
        if (receipt.ExpiresAtUtc <= nowUtc)
            return Deny("authorization.review-expired");
        if (!Matches(receipt.CorrelationId, request.CorrelationId) ||
            !Matches(receipt.IntentHash, request.IntentHash) ||
            !Matches(receipt.UserId, request.UserId) ||
            !Matches(receipt.DeviceId, request.DeviceId) ||
            !Matches(receipt.SessionId, request.SessionId))
            return Deny("authorization.review-context-mismatch");
        return null;
    }

    private static bool Matches(string receiptValue, string requestValue)
        => !string.IsNullOrWhiteSpace(receiptValue) &&
           string.Equals(receiptValue, requestValue, StringComparison.Ordinal);

    private static TradingAuthorizationDecision Allow(string code) => new(true, code);
    private static TradingAuthorizationDecision Deny(string code) => new(false, code);
}

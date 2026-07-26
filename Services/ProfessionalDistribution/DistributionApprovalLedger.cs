using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WpeAgent.ProfessionalDistribution;

public enum DistributionLedgerStatus
{
    Denied,
    Authorized,
    Withdrawn,
    Error
}

public sealed record DistributionApprovalReceipt(
    string ReceiptId,
    long Sequence,
    string RequestId,
    string ArtifactId,
    string ContentVersion,
    string TraceId,
    DistributionRiskLevel RiskLevel,
    string PolicyHash,
    string ContentFactsHash,
    string ConsentScopeHash,
    string ConsentVersion,
    string SuitabilityVersion,
    string ConsentId,
    string ConsentClientId,
    DateTime ConsentExpiresAtUtc,
    string PrimaryReviewId,
    string PrimaryReviewerId,
    string SecondaryReviewId,
    string SecondaryReviewerId,
    string SuitabilityAssessmentId,
    DateTime SuitabilityExpiresAtUtc,
    string WithdrawalId,
    bool ConsentWithdrawn,
    DateTime? ConsentWithdrawnAtUtc,
    string WithdrawnConsentId,
    string WithdrawalClientId,
    string WithdrawalTraceId,
    string WithdrawalAuditId,
    string AuditId,
    string AuditRequestId,
    string AuditArtifactId,
    string AuditContentVersion,
    string AuditTraceId,
    DistributionRequestState DecisionState,
    DateTime RecordedAtUtc,
    string PreviousHash,
    string ImmutableHash);

public sealed record DistributionLedgerProjection(
    DistributionLedgerStatus Status,
    bool Allowed,
    int ReceiptCount,
    string HeadHash,
    string? RequestId,
    string? ConsentId,
    DateTime? SuitabilityExpiresAtUtc,
    IReadOnlyList<string> ReasonCodes);

public static class DistributionApprovalReceiptFactory
{
    public const string GenesisHash = "GENESIS";

    public static DistributionApprovalReceipt Create(
        string receiptId,
        long sequence,
        DistributionRequest request,
        DistributionDecision decision,
        DateTime recordedAtUtc,
        string previousHash = GenesisHash,
        ConsentWithdrawalEvidence? consentWithdrawal = null)
    {
        var receipt = new DistributionApprovalReceipt(
            receiptId,
            sequence,
            request.RequestId,
            request.ArtifactId,
            request.ContentVersion,
            request.TraceId,
            request.RiskLevel,
            request.VersionBinding?.PolicyHash ?? string.Empty,
            DistributionVersionBindingPolicy.ComputeContentFactsHash(request.Facts),
            request.VersionBinding?.ConsentScopeHash ?? string.Empty,
            request.VersionBinding?.ConsentVersion ?? string.Empty,
            request.VersionBinding?.SuitabilityVersion ?? string.Empty,
            request.ClientConsent?.ConsentId ?? string.Empty,
            request.ClientConsent?.ClientId ?? string.Empty,
            request.ClientConsent?.ExpiresAtUtc ?? DateTime.MinValue,
            request.HumanReview?.ReviewId ?? string.Empty,
            request.HumanReview?.ReviewerId ?? string.Empty,
            request.SecondaryHumanReview?.ReviewId ?? string.Empty,
            request.SecondaryHumanReview?.ReviewerId ?? string.Empty,
            request.Suitability?.AssessmentId ?? string.Empty,
            request.Suitability?.ExpiresAtUtc ?? DateTime.MinValue,
            request.Withdrawal?.WithdrawalId ?? string.Empty,
            consentWithdrawal is not null,
            consentWithdrawal?.WithdrawnAtUtc,
            consentWithdrawal?.ConsentId ?? string.Empty,
            consentWithdrawal?.ClientId ?? string.Empty,
            consentWithdrawal?.TraceId ?? string.Empty,
            consentWithdrawal?.AuditId ?? string.Empty,
            request.Audit?.AuditId ?? string.Empty,
            request.Audit?.RequestId ?? string.Empty,
            request.Audit?.ArtifactId ?? string.Empty,
            request.Audit?.ContentVersion ?? string.Empty,
            request.Audit?.TraceId ?? string.Empty,
            consentWithdrawal is null ? decision.State : DistributionRequestState.Withdrawn,
            recordedAtUtc,
            previousHash,
            string.Empty);

        return receipt with { ImmutableHash = DistributionApprovalLedger.ComputeHash(receipt) };
    }
}

/// <summary>Validates immutable local receipts and returns a read-only status projection.</summary>
public static class DistributionApprovalLedger
{
    public static DistributionLedgerProjection Project(
        IReadOnlyList<DistributionApprovalReceipt>? receipts,
        DateTime nowUtc)
    {
        if (receipts is null || receipts.Count == 0)
            return Denied("LEDGER_EMPTY");

        var receiptIds = new HashSet<string>(StringComparer.Ordinal);
        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        var previousHash = DistributionApprovalReceiptFactory.GenesisHash;

        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            if (receipt is null) return Error(receipts.Count, previousHash, "RECEIPT_MISSING");
            if (receipt.Sequence != index + 1L) return Error(receipts.Count, previousHash, "LEDGER_SEQUENCE_BROKEN");
            if (!receiptIds.Add(receipt.ReceiptId) || !requestIds.Add(receipt.RequestId))
                return Error(receipts.Count, previousHash, "LEDGER_DUPLICATE");
            if (!string.Equals(receipt.PreviousHash, previousHash, StringComparison.Ordinal))
                return Error(receipts.Count, previousHash, "LEDGER_CHAIN_BROKEN");
            if (string.IsNullOrWhiteSpace(receipt.ImmutableHash))
                return Error(receipts.Count, previousHash, "RECEIPT_TAMPERED");
            var immutableHash = receipt.ImmutableHash;
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(ComputeHash(receipt)),
                    Encoding.ASCII.GetBytes(immutableHash)))
                return Error(receipts.Count, previousHash, "RECEIPT_TAMPERED");
            if (!ValidCorrelation(receipt))
                return Error(receipts.Count, previousHash, "AUDIT_CORRELATION_BROKEN");
            previousHash = immutableHash;
        }

        var head = receipts[^1];
        if (head.ConsentWithdrawn)
            return new(DistributionLedgerStatus.Withdrawn, false, receipts.Count, previousHash,
                head.RequestId, head.ConsentId, head.SuitabilityExpiresAtUtc, ["CONSENT_WITHDRAWN"]);
        if (head.DecisionState != DistributionRequestState.Authorized)
            return new(DistributionLedgerStatus.Denied, false, receipts.Count, previousHash,
                head.RequestId, head.ConsentId, head.SuitabilityExpiresAtUtc, ["APPROVAL_DENIED"]);
        if (head.ConsentExpiresAtUtc <= nowUtc)
            return new(DistributionLedgerStatus.Denied, false, receipts.Count, previousHash,
                head.RequestId, head.ConsentId, head.SuitabilityExpiresAtUtc, ["CONSENT_EXPIRED"]);
        if (head.SuitabilityExpiresAtUtc <= nowUtc)
            return new(DistributionLedgerStatus.Denied, false, receipts.Count, previousHash,
                head.RequestId, head.ConsentId, head.SuitabilityExpiresAtUtc, ["SUITABILITY_EXPIRED"]);

        return new(DistributionLedgerStatus.Authorized, true, receipts.Count, previousHash,
            head.RequestId, head.ConsentId, head.SuitabilityExpiresAtUtc, Array.Empty<string>());
    }

    public static string ComputeHash(DistributionApprovalReceipt receipt)
    {
        var canonical = new StringBuilder();
        Add(canonical, receipt.ReceiptId);
        Add(canonical, receipt.Sequence.ToString(CultureInfo.InvariantCulture));
        Add(canonical, receipt.RequestId);
        Add(canonical, receipt.ArtifactId);
        Add(canonical, receipt.ContentVersion);
        Add(canonical, receipt.TraceId);
        Add(canonical, ((int)receipt.RiskLevel).ToString(CultureInfo.InvariantCulture));
        Add(canonical, receipt.PolicyHash);
        Add(canonical, receipt.ContentFactsHash);
        Add(canonical, receipt.ConsentScopeHash);
        Add(canonical, receipt.ConsentVersion);
        Add(canonical, receipt.SuitabilityVersion);
        Add(canonical, receipt.ConsentId);
        Add(canonical, receipt.ConsentClientId);
        Add(canonical, receipt.ConsentExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(canonical, receipt.PrimaryReviewId);
        Add(canonical, receipt.PrimaryReviewerId);
        Add(canonical, receipt.SecondaryReviewId);
        Add(canonical, receipt.SecondaryReviewerId);
        Add(canonical, receipt.SuitabilityAssessmentId);
        Add(canonical, receipt.SuitabilityExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(canonical, receipt.WithdrawalId);
        Add(canonical, receipt.ConsentWithdrawn ? "1" : "0");
        Add(canonical, receipt.ConsentWithdrawnAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        Add(canonical, receipt.WithdrawnConsentId);
        Add(canonical, receipt.WithdrawalClientId);
        Add(canonical, receipt.WithdrawalTraceId);
        Add(canonical, receipt.WithdrawalAuditId);
        Add(canonical, receipt.AuditId);
        Add(canonical, receipt.AuditRequestId);
        Add(canonical, receipt.AuditArtifactId);
        Add(canonical, receipt.AuditContentVersion);
        Add(canonical, receipt.AuditTraceId);
        Add(canonical, ((int)receipt.DecisionState).ToString(CultureInfo.InvariantCulture));
        Add(canonical, receipt.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(canonical, receipt.PreviousHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static bool ValidCorrelation(DistributionApprovalReceipt receipt) =>
        !string.IsNullOrWhiteSpace(receipt.ReceiptId) &&
        !string.IsNullOrWhiteSpace(receipt.RequestId) &&
        DistributionVersionBindingPolicy.IsSha256(receipt.PolicyHash) &&
        DistributionVersionBindingPolicy.IsSha256(receipt.ContentFactsHash) &&
        DistributionVersionBindingPolicy.IsSha256(receipt.ConsentScopeHash) &&
        DistributionVersionBindingPolicy.IsKnownVersion(receipt.ConsentVersion) &&
        DistributionVersionBindingPolicy.IsKnownVersion(receipt.SuitabilityVersion) &&
        !string.IsNullOrWhiteSpace(receipt.ConsentId) &&
        !string.IsNullOrWhiteSpace(receipt.ConsentClientId) &&
        !string.IsNullOrWhiteSpace(receipt.PrimaryReviewId) &&
        !string.IsNullOrWhiteSpace(receipt.SecondaryReviewId) &&
        !string.Equals(receipt.PrimaryReviewerId, receipt.SecondaryReviewerId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(receipt.SuitabilityAssessmentId) &&
        !string.IsNullOrWhiteSpace(receipt.WithdrawalId) &&
        !string.IsNullOrWhiteSpace(receipt.AuditId) &&
        string.Equals(receipt.AuditRequestId, receipt.RequestId, StringComparison.Ordinal) &&
        string.Equals(receipt.AuditArtifactId, receipt.ArtifactId, StringComparison.Ordinal) &&
        string.Equals(receipt.AuditContentVersion, receipt.ContentVersion, StringComparison.Ordinal) &&
        string.Equals(receipt.AuditTraceId, receipt.TraceId, StringComparison.Ordinal) &&
        (!receipt.ConsentWithdrawn ||
            receipt.ConsentWithdrawnAtUtc is not null &&
            string.Equals(receipt.WithdrawnConsentId, receipt.ConsentId, StringComparison.Ordinal) &&
            string.Equals(receipt.WithdrawalClientId, receipt.ConsentClientId, StringComparison.Ordinal) &&
            string.Equals(receipt.WithdrawalTraceId, receipt.TraceId, StringComparison.Ordinal) &&
            string.Equals(receipt.WithdrawalAuditId, receipt.AuditId, StringComparison.Ordinal));

    private static void Add(StringBuilder target, string? value)
    {
        value ??= string.Empty;
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private static DistributionLedgerProjection Denied(string reason) =>
        new(DistributionLedgerStatus.Denied, false, 0, DistributionApprovalReceiptFactory.GenesisHash,
            null, null, null, [reason]);

    private static DistributionLedgerProjection Error(int count, string headHash, string reason) =>
        new(DistributionLedgerStatus.Error, false, count, headHash, null, null, null, [reason]);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WpeAgent.ProfessionalDistribution;

public enum DistributionReadStatus
{
    Denied,
    Authorized,
    Withdrawn,
    Tombstone,
    Error
}

public enum DistributionApprovalRole
{
    PrimaryReviewer,
    SecondaryReviewer
}

public sealed record DistributionRetentionPolicy(
    TimeSpan MetadataRetention,
    TimeSpan AuditTombstoneRetention);

public sealed record DistributionReadProjection(
    DistributionReadStatus Status,
    bool Allowed,
    IReadOnlyList<DistributionApprovalRole> ApprovalRoles,
    DateTime? ValidUntilUtc,
    bool Withdrawn,
    string? AuditCorrelationHash,
    string? ReceiptHash,
    IReadOnlyList<string> ReasonCodes);

/// <summary>Produces a minimized local projection without message content, customer PII, or review text.</summary>
public static class DistributionReadProjector
{
    public static DistributionReadProjection Project(
        IReadOnlyList<DistributionApprovalReceipt>? receipts,
        DistributionRetentionPolicy? retention,
        DateTime nowUtc)
    {
        if (retention is null || retention.MetadataRetention <= TimeSpan.Zero ||
            retention.AuditTombstoneRetention < retention.MetadataRetention)
            return Error("RETENTION_POLICY_INVALID");

        var ledger = DistributionApprovalLedger.Project(receipts, nowUtc);
        if (ledger.Status == DistributionLedgerStatus.Error)
            return Error(ledger.ReasonCodes.ToArray());
        if (receipts is null || receipts.Count == 0)
            return Denied("LEDGER_EMPTY");

        var head = receipts[^1];
        if (head is null || head.RecordedAtUtc > nowUtc)
            return Error("RECEIPT_TIME_INVALID");

        var age = nowUtc - head.RecordedAtUtc;
        if (age > retention.AuditTombstoneRetention)
            return Denied("AUDIT_RETENTION_EXPIRED");

        var auditHash = AuditCorrelationHash(head);
        if (age > retention.MetadataRetention)
            return new(DistributionReadStatus.Tombstone, false, Array.Empty<DistributionApprovalRole>(),
                null, head.ConsentWithdrawn, auditHash, head.ImmutableHash, ["METADATA_TOMBSTONED"]);

        var validUntil = head.ConsentExpiresAtUtc <= head.SuitabilityExpiresAtUtc
            ? head.ConsentExpiresAtUtc
            : head.SuitabilityExpiresAtUtc;
        var roles = new[] { DistributionApprovalRole.PrimaryReviewer, DistributionApprovalRole.SecondaryReviewer };

        return ledger.Status switch
        {
            DistributionLedgerStatus.Authorized =>
                new(DistributionReadStatus.Authorized, true, roles, validUntil, false,
                    auditHash, head.ImmutableHash, Array.Empty<string>()),
            DistributionLedgerStatus.Withdrawn =>
                new(DistributionReadStatus.Withdrawn, false, roles, validUntil, true,
                    auditHash, head.ImmutableHash, ledger.ReasonCodes),
            _ => new(DistributionReadStatus.Denied, false, roles, validUntil, head.ConsentWithdrawn,
                auditHash, head.ImmutableHash, ledger.ReasonCodes)
        };
    }

    private static string AuditCorrelationHash(DistributionApprovalReceipt receipt)
    {
        var canonical = new StringBuilder();
        Add(canonical, receipt.AuditId);
        Add(canonical, receipt.AuditRequestId);
        Add(canonical, receipt.AuditArtifactId);
        Add(canonical, receipt.AuditContentVersion);
        Add(canonical, receipt.AuditTraceId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Add(StringBuilder target, string? value)
    {
        value ??= string.Empty;
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private static DistributionReadProjection Denied(params string[] reasons) =>
        new(DistributionReadStatus.Denied, false, Array.Empty<DistributionApprovalRole>(),
            null, false, null, null, reasons);

    private static DistributionReadProjection Error(params string[] reasons) =>
        new(DistributionReadStatus.Error, false, Array.Empty<DistributionApprovalRole>(),
            null, false, null, null, reasons);
}

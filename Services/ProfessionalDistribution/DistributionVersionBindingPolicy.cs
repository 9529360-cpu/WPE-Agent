using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WpeAgent.ProfessionalDistribution;

public sealed record CurrentDistributionApprovalBinding(
    string PolicyHash,
    IReadOnlyList<DistributionFactReference> Facts,
    string ConsentScopeHash,
    string ConsentVersion,
    string SuitabilityVersion);

public sealed record DistributionReapprovalDecision(
    bool Allowed,
    bool RequiresFreshDualReview,
    string? ReceiptHash,
    IReadOnlyList<string> ReasonCodes);

public static class DistributionVersionBindingPolicy
{
    public static DistributionReapprovalDecision Evaluate(
        IReadOnlyList<DistributionApprovalReceipt>? receipts,
        CurrentDistributionApprovalBinding? current,
        DateTime nowUtc)
    {
        var ledger = DistributionApprovalLedger.Project(receipts, nowUtc);
        if (ledger.Status != DistributionLedgerStatus.Authorized || receipts is null || receipts.Count == 0)
            return Reapproval(ledger.HeadHash == DistributionApprovalReceiptFactory.GenesisHash ? null : ledger.HeadHash,
                ledger.ReasonCodes.Append("FRESH_DUAL_REVIEW_REQUIRED").Distinct(StringComparer.Ordinal).ToArray());

        var head = receipts[^1];
        if (head is null || current is null || !IsKnown(current))
            return Reapproval(head?.ImmutableHash, "VERSION_BINDING_UNKNOWN", "FRESH_DUAL_REVIEW_REQUIRED");

        var reasons = new List<string>();
        if (!string.Equals(head.PolicyHash, current.PolicyHash, StringComparison.Ordinal))
            reasons.Add("POLICY_CHANGED");
        if (!string.Equals(head.ContentFactsHash, ComputeContentFactsHash(current.Facts), StringComparison.Ordinal))
            reasons.Add("CONTENT_FACTS_CHANGED");
        if (!string.Equals(head.ConsentScopeHash, current.ConsentScopeHash, StringComparison.Ordinal) ||
            !string.Equals(head.ConsentVersion, current.ConsentVersion, StringComparison.Ordinal))
            reasons.Add("CONSENT_SCOPE_OR_VERSION_CHANGED");
        if (!string.Equals(head.SuitabilityVersion, current.SuitabilityVersion, StringComparison.Ordinal))
            reasons.Add("SUITABILITY_VERSION_CHANGED");

        if (reasons.Count > 0)
        {
            reasons.Add("FRESH_DUAL_REVIEW_REQUIRED");
            return Reapproval(head.ImmutableHash, reasons.ToArray());
        }

        return new(true, false, head.ImmutableHash, Array.Empty<string>());
    }

    public static string ComputeContentFactsHash(IReadOnlyList<DistributionFactReference>? facts)
    {
        if (facts is null || facts.Count == 0) return string.Empty;
        var canonical = new StringBuilder();
        foreach (var fact in facts.OrderBy(x => x.ArtifactId, StringComparer.Ordinal)
                     .ThenBy(x => x.ArtifactType, StringComparer.Ordinal)
                     .ThenBy(x => x.TraceId, StringComparer.Ordinal))
        {
            Add(canonical, fact.ArtifactId);
            Add(canonical, fact.ArtifactType);
            Add(canonical, fact.TraceId);
            Add(canonical, fact.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Add(canonical, fact.ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Add(canonical, ((int)fact.Status).ToString(CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public static bool IsKnown(DistributionVersionBinding? binding) =>
        binding is not null && IsSha256(binding.PolicyHash) && IsSha256(binding.ConsentScopeHash) &&
        IsKnownVersion(binding.ConsentVersion) && IsKnownVersion(binding.SuitabilityVersion);

    public static bool IsKnown(CurrentDistributionApprovalBinding? binding) =>
        binding is not null && IsSha256(binding.PolicyHash) && IsSha256(binding.ConsentScopeHash) &&
        IsKnownVersion(binding.ConsentVersion) && IsKnownVersion(binding.SuitabilityVersion) &&
        IsSha256(ComputeContentFactsHash(binding.Facts));

    public static bool IsKnownVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static DistributionReapprovalDecision Reapproval(string? receiptHash, params string[] reasons) =>
        new(false, true, receiptHash, reasons);

    private static void Add(StringBuilder target, string? value)
    {
        value ??= string.Empty;
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Security;

public sealed record BackupEnvelopeInventory(
    int EnvelopeVersion,
    StorageKeyReference KeyReference,
    int RecordCount,
    string RecordSetSha256);

public sealed record BackupRestoreDrillRequest(
    string BackupId,
    EncryptedBackupManifest Manifest,
    bool ManifestAuthenticated,
    StorageKeyDescriptor? RecoveryKey,
    IReadOnlyDictionary<string, string> ObservedFileHashes,
    IReadOnlyList<BackupEnvelopeInventory> Inventory,
    IReadOnlyList<StorageKeyReference> AvailableKeys,
    int RecordCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string RestoreTarget,
    bool RestoreTargetIsEmpty,
    string AuditCorrelationId);

public sealed record BackupRestoreDrillArtifact(
    int ArtifactVersion,
    string BackupId,
    string ManifestSha256,
    string InventorySha256,
    int RecordCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string RestoreTarget,
    string AuditCorrelationId,
    string PlanSha256);

public sealed record BackupRestoreDrillPlan(
    bool Allowed,
    string ReasonCode,
    BackupRestoreDrillArtifact? Artifact);

public sealed record BackupRestoreDrillEvidence(
    BackupRestoreDrillArtifact Artifact,
    IReadOnlyList<BackupEnvelopeInventory> RestoredInventory,
    int RestoredRecordCount,
    string RestoreTarget,
    bool ManifestVerified,
    bool EnvelopeAuthenticationComplete,
    bool RecordHashesVerified,
    bool AuditRecorded);

public sealed record BackupRestoreDrillVerification(
    bool Allowed,
    string ReasonCode,
    string BackupId,
    string PlanSha256,
    string AuditCorrelationId);

public static class BackupRestoreDrillPlanner
{
    public const int CurrentArtifactVersion = 1;

    public static BackupRestoreDrillPlan CreatePlan(
        BackupRestoreDrillRequest request,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BackupId) ||
            !string.Equals(request.BackupId, request.Manifest.BackupId, StringComparison.Ordinal))
            return RejectPlan("drill.backup-id-mismatch");
        if (request.CreatedAtUtc != request.Manifest.CreatedAtUtc)
            return RejectPlan("drill.created-at-mismatch");
        if (request.CreatedAtUtc > nowUtc || request.ExpiresAtUtc <= request.CreatedAtUtc || request.ExpiresAtUtc <= nowUtc)
            return RejectPlan("drill.expired-or-invalid-window");
        if (string.IsNullOrWhiteSpace(request.RestoreTarget))
            return RejectPlan("drill.restore-target-missing");
        if (!request.RestoreTargetIsEmpty)
            return RejectPlan("drill.restore-target-not-empty");
        if (string.IsNullOrWhiteSpace(request.AuditCorrelationId))
            return RejectPlan("drill.audit-correlation-missing");

        var restore = EncryptedBackupPolicy.CanRestore(new(
            request.Manifest,
            request.RecoveryKey,
            request.ManifestAuthenticated,
            true,
            request.RestoreTargetIsEmpty,
            request.ObservedFileHashes));
        if (!restore.Allowed)
            return RejectPlan(restore.ReasonCode);

        var inventoryFailure = ValidateInventory(
            request.Inventory,
            request.AvailableKeys,
            request.RecordCount);
        if (inventoryFailure is not null)
            return RejectPlan(inventoryFailure);

        var inventoryHash = ComputeInventoryHash(request.Inventory);
        var artifact = new BackupRestoreDrillArtifact(
            CurrentArtifactVersion,
            request.BackupId,
            request.Manifest.ManifestSha256,
            inventoryHash,
            request.RecordCount,
            request.CreatedAtUtc,
            request.ExpiresAtUtc,
            request.RestoreTarget,
            request.AuditCorrelationId,
            string.Empty);
        artifact = artifact with { PlanSha256 = ComputePlanHash(artifact) };
        return new(true, "drill.plan-ready", artifact);
    }

    public static BackupRestoreDrillVerification Verify(
        BackupRestoreDrillEvidence evidence,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var artifact = evidence.Artifact;
        if (artifact.ArtifactVersion != CurrentArtifactVersion)
            return RejectVerification("drill.artifact-version-unsupported", artifact);
        if (!IsSha256(artifact.PlanSha256) ||
            !string.Equals(artifact.PlanSha256, ComputePlanHash(artifact), StringComparison.OrdinalIgnoreCase))
            return RejectVerification("drill.plan-integrity-failed", artifact);
        if (nowUtc < artifact.CreatedAtUtc || nowUtc >= artifact.ExpiresAtUtc)
            return RejectVerification("drill.plan-expired", artifact);
        if (!string.Equals(evidence.RestoreTarget, artifact.RestoreTarget, StringComparison.Ordinal))
            return RejectVerification("drill.restore-target-mismatch", artifact);
        if (!evidence.ManifestVerified || !evidence.EnvelopeAuthenticationComplete ||
            !evidence.RecordHashesVerified || !evidence.AuditRecorded)
            return RejectVerification("drill.verification-incomplete", artifact);
        if (evidence.RestoredRecordCount != artifact.RecordCount ||
            evidence.RestoredInventory.Sum(item => item.RecordCount) != artifact.RecordCount)
            return RejectVerification("drill.record-count-mismatch", artifact);
        if (!string.Equals(
                ComputeInventoryHash(evidence.RestoredInventory),
                artifact.InventorySha256,
                StringComparison.OrdinalIgnoreCase))
            return RejectVerification("drill.inventory-mismatch", artifact);

        return new(true, "drill.verified", artifact.BackupId, artifact.PlanSha256, artifact.AuditCorrelationId);
    }

    public static string ComputePlanHash(BackupRestoreDrillArtifact artifact)
    {
        var canonical = $"{artifact.ArtifactVersion}|{artifact.BackupId}|{artifact.ManifestSha256.ToUpperInvariant()}|" +
                        $"{artifact.InventorySha256.ToUpperInvariant()}|{artifact.RecordCount}|{artifact.CreatedAtUtc:O}|" +
                        $"{artifact.ExpiresAtUtc:O}|{artifact.RestoreTarget}|{artifact.AuditCorrelationId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string ComputeInventoryHash(IReadOnlyList<BackupEnvelopeInventory> inventory)
    {
        var canonical = string.Join("\n", inventory
            .OrderBy(item => item.EnvelopeVersion)
            .ThenBy(item => item.KeyReference.KeyId, StringComparer.Ordinal)
            .ThenBy(item => item.KeyReference.Version)
            .Select(item => $"{item.EnvelopeVersion}|{item.KeyReference.KeyId.Length}:{item.KeyReference.KeyId}|" +
                            $"{item.KeyReference.Version}|{item.RecordCount}|{item.RecordSetSha256.ToUpperInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string? ValidateInventory(
        IReadOnlyList<BackupEnvelopeInventory> inventory,
        IReadOnlyList<StorageKeyReference> availableKeys,
        int expectedRecordCount)
    {
        if (inventory.Count == 0 || expectedRecordCount <= 0)
            return "drill.inventory-missing";
        if (inventory.Sum(item => item.RecordCount) != expectedRecordCount)
            return "drill.record-count-mismatch";

        var keys = availableKeys.ToHashSet();
        foreach (var item in inventory)
        {
            if (item.EnvelopeVersion != VersionedEnvelopeEncryptionService.CurrentVersion)
                return "drill.envelope-version-unsupported";
            if (string.IsNullOrWhiteSpace(item.KeyReference.KeyId) || item.KeyReference.Version <= 0)
                return "drill.key-version-invalid";
            if (!keys.Contains(item.KeyReference))
                return "drill.required-key-missing";
            if (item.RecordCount <= 0 || !IsSha256(item.RecordSetSha256))
                return "drill.inventory-entry-invalid";
        }

        return null;
    }

    private static BackupRestoreDrillPlan RejectPlan(string reasonCode)
        => new(false, reasonCode, null);

    private static BackupRestoreDrillVerification RejectVerification(
        string reasonCode,
        BackupRestoreDrillArtifact artifact)
        => new(false, reasonCode, artifact.BackupId, artifact.PlanSha256, artifact.AuditCorrelationId);

    private static bool IsSha256(string value)
        => !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);
}

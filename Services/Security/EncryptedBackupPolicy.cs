using System.Security.Cryptography;
using System.Text;

namespace 币安量化机器人.Services.Security;

public sealed record EncryptedBackupFile(
    string LogicalName,
    long CiphertextLength,
    string Sha256);

public sealed record EncryptedBackupManifest(
    int FormatVersion,
    string BackupId,
    DateTimeOffset CreatedAtUtc,
    StorageKeyReference KeyReference,
    string CipherSuite,
    IReadOnlyList<EncryptedBackupFile> Files,
    string ManifestSha256);

public sealed record BackupRestoreContext(
    EncryptedBackupManifest? Manifest,
    StorageKeyDescriptor? RecoveryKey,
    bool ManifestAuthenticated,
    bool CipherProviderAvailable,
    bool TargetIsEmpty,
    IReadOnlyDictionary<string, string> ObservedFileHashes);

public static class EncryptedBackupPolicy
{
    public const int CurrentFormatVersion = 1;
    public const string RequiredCipherSuite = "AES-256-GCM";

    public static StoragePolicyDecision CanRestore(BackupRestoreContext context)
    {
        var manifest = context.Manifest;
        if (manifest is null)
            return StoragePolicyDecision.Deny("backup.manifest-missing");
        if (!context.ManifestAuthenticated)
            return StoragePolicyDecision.Deny("backup.manifest-unauthenticated");
        if (!IsSha256(manifest.ManifestSha256) ||
            !string.Equals(manifest.ManifestSha256, ComputeManifestHash(manifest), StringComparison.OrdinalIgnoreCase))
            return StoragePolicyDecision.Deny("backup.manifest-integrity-failed");
        if (manifest.FormatVersion != CurrentFormatVersion)
            return StoragePolicyDecision.Deny("backup.format-unsupported");
        if (!string.Equals(manifest.CipherSuite, RequiredCipherSuite, StringComparison.Ordinal))
            return StoragePolicyDecision.Deny("backup.cipher-suite-unsupported");
        if (!context.CipherProviderAvailable)
            return StoragePolicyDecision.Deny("backup.cipher-provider-unavailable");
        if (!context.TargetIsEmpty)
            return StoragePolicyDecision.Deny("backup.restore-target-not-empty");
        if (context.RecoveryKey is null)
            return StoragePolicyDecision.Deny("backup.recovery-key-missing");
        if (context.RecoveryKey.State is StorageKeyState.Revoked or StorageKeyState.Pending or StorageKeyState.RotationPending)
            return StoragePolicyDecision.Deny("backup.recovery-key-invalid-state");
        if (context.RecoveryKey.KeyId != manifest.KeyReference.KeyId ||
            context.RecoveryKey.Version != manifest.KeyReference.Version)
            return StoragePolicyDecision.Deny("backup.recovery-key-mismatch");
        if (manifest.Files.Count == 0)
            return StoragePolicyDecision.Deny("backup.files-missing");

        foreach (var file in manifest.Files)
        {
            if (file.CiphertextLength <= 0 || !IsSha256(file.Sha256))
                return StoragePolicyDecision.Deny("backup.file-metadata-invalid");
            if (!context.ObservedFileHashes.TryGetValue(file.LogicalName, out var observed) ||
                !string.Equals(file.Sha256, observed, StringComparison.OrdinalIgnoreCase))
                return StoragePolicyDecision.Deny("backup.file-integrity-failed");
        }

        return StoragePolicyDecision.Allow("backup.restore-ready");
    }

    public static string ComputeManifestHash(EncryptedBackupManifest manifest)
    {
        var files = string.Join("\n", manifest.Files
            .OrderBy(file => file.LogicalName, StringComparer.Ordinal)
            .Select(file => $"{file.LogicalName}|{file.CiphertextLength}|{file.Sha256.ToUpperInvariant()}"));
        var canonical = $"{manifest.FormatVersion}|{manifest.BackupId}|{manifest.CreatedAtUtc:O}|" +
                        $"{manifest.KeyReference.KeyId}|{manifest.KeyReference.Version}|{manifest.CipherSuite}\n{files}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);
}

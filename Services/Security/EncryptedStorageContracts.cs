namespace 币安量化机器人.Services.Security;

public enum StorageKeyState
{
    Pending,
    Active,
    RotationPending,
    Retired,
    Revoked,
    RecoveryOnly
}

public enum PlatformKeyStoreKind
{
    WindowsDpapiCurrentUser,
    MacOsKeychain,
    LinuxSecretService
}

public enum EncryptedStorageMigrationState
{
    NotStarted,
    InventoryVerified,
    BackupVerified,
    MigrationInProgress,
    VerificationPending,
    Complete,
    Failed
}

public enum StorageKeyRotationState
{
    None,
    NewKeyCreated,
    RekeyInProgress,
    VerificationPending,
    Complete,
    Interrupted
}

public sealed record StorageKeyDescriptor(
    string KeyId,
    int Version,
    StorageKeyState State,
    PlatformKeyStoreKind KeyStore,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ActivatedAtUtc = null,
    DateTimeOffset? RetiredAtUtc = null,
    DateTimeOffset? RevokedAtUtc = null);

public sealed record StorageKeyReference(string KeyId, int Version);

public sealed record ProtectedStorageKey(
    StorageKeyReference Reference,
    PlatformKeyStoreKind KeyStore,
    byte[] ProtectedKeyMaterial);

public interface IPlatformStorageKeyStore
{
    PlatformKeyStoreKind Kind { get; }

    Task<StorageKeyDescriptor> CreateAsync(CancellationToken cancellationToken);

    Task<ProtectedStorageKey?> LoadAsync(StorageKeyReference reference, CancellationToken cancellationToken);

    Task<StorageKeyDescriptor> BeginRotationAsync(StorageKeyReference current, CancellationToken cancellationToken);

    Task RetireAsync(StorageKeyReference reference, CancellationToken cancellationToken);

    Task RevokeAsync(StorageKeyReference reference, string reasonCode, CancellationToken cancellationToken);

    Task<ProtectedStorageKey?> LoadForRecoveryAsync(StorageKeyReference reference, CancellationToken cancellationToken);
}

public sealed record EncryptedStorageActivationContext(
    EncryptedStorageMigrationState MigrationState,
    StorageKeyDescriptor? ActiveKey,
    bool CipherProviderAvailable,
    bool ExistingFilesInventoried,
    bool VerifiedEncryptedBackupExists,
    bool PlaintextDatabaseDetected);

public sealed record StoragePolicyDecision(bool Allowed, string ReasonCode)
{
    public static StoragePolicyDecision Allow(string reasonCode) => new(true, reasonCode);

    public static StoragePolicyDecision Deny(string reasonCode) => new(false, reasonCode);
}

public sealed record MigrationDryRunContext(
    EncryptedStorageActivationContext Activation,
    bool KeyStoreAvailable,
    StorageKeyRotationState RotationState,
    BackupRestoreContext BackupVerification,
    bool WritersStopped,
    bool SourceSetPreserved,
    bool SourceSetVerified,
    bool RollbackKeyAvailable);

public sealed record MigrationDryRunCheck(string CheckCode, bool Passed, string ReasonCode);

public sealed record MigrationDryRunPlan(
    bool Allowed,
    string ReasonCode,
    IReadOnlyList<MigrationDryRunCheck> Checks)
{
    public bool IsReadOnly => true;
}

public sealed record MigrationRollbackContext(
    EncryptedStorageMigrationState MigrationState,
    StorageKeyRotationState RotationState,
    bool SourceSetPreserved,
    bool SourceSetVerified,
    bool RollbackKeyAvailable,
    StorageKeyDescriptor? RollbackKey,
    BackupRestoreContext BackupVerification);

public static class EncryptedStoragePolicy
{
    public static StoragePolicyDecision CanOpenEncryptedStore(EncryptedStorageActivationContext context)
    {
        if (!context.CipherProviderAvailable)
            return StoragePolicyDecision.Deny("storage.cipher-provider-unavailable");
        if (context.ActiveKey is null)
            return StoragePolicyDecision.Deny("storage.active-key-missing");
        if (context.ActiveKey.State != StorageKeyState.Active)
            return StoragePolicyDecision.Deny("storage.active-key-invalid-state");
        if (context.PlaintextDatabaseDetected)
            return StoragePolicyDecision.Deny("storage.plaintext-database-detected");
        if (context.MigrationState != EncryptedStorageMigrationState.Complete)
            return StoragePolicyDecision.Deny("storage.migration-incomplete");

        return StoragePolicyDecision.Allow("storage.encrypted-store-ready");
    }

    public static StoragePolicyDecision CanStartMigration(EncryptedStorageActivationContext context)
    {
        if (!context.CipherProviderAvailable)
            return StoragePolicyDecision.Deny("migration.cipher-provider-unavailable");
        if (!context.ExistingFilesInventoried)
            return StoragePolicyDecision.Deny("migration.inventory-unverified");
        if (!context.VerifiedEncryptedBackupExists)
            return StoragePolicyDecision.Deny("migration.verified-backup-required");
        if (context.ActiveKey is null || context.ActiveKey.State != StorageKeyState.Active)
            return StoragePolicyDecision.Deny("migration.active-key-required");
        if (context.MigrationState is not EncryptedStorageMigrationState.BackupVerified)
            return StoragePolicyDecision.Deny("migration.invalid-state");

        return StoragePolicyDecision.Allow("migration.ready");
    }

    public static MigrationDryRunPlan CreateDryRunPlan(MigrationDryRunContext context)
    {
        var checks = new List<MigrationDryRunCheck>();

        Add(checks, "keystore.available", context.KeyStoreAvailable,
            "migration.keystore-unavailable");
        Add(checks, "rotation.stable", IsStableRotation(context.RotationState),
            "migration.rotation-interrupted");

        var activation = CanStartMigration(context.Activation);
        checks.Add(new("migration.prerequisites", activation.Allowed, activation.ReasonCode));

        var backup = EncryptedBackupPolicy.CanRestore(context.BackupVerification);
        checks.Add(new("backup.verified", backup.Allowed, backup.ReasonCode));

        Add(checks, "writers.stopped", context.WritersStopped,
            "migration.writers-active");
        Add(checks, "rollback.source-preserved", context.SourceSetPreserved,
            "rollback.source-set-missing");
        Add(checks, "rollback.source-verified", context.SourceSetVerified,
            "rollback.source-set-unverified");
        Add(checks, "rollback.key-available", context.RollbackKeyAvailable,
            "rollback.key-unavailable");

        var firstFailure = checks.FirstOrDefault(check => !check.Passed);
        return firstFailure is null
            ? new(true, "migration.dry-run-ready", checks)
            : new(false, firstFailure.ReasonCode, checks);
    }

    public static StoragePolicyDecision CanRollback(MigrationRollbackContext context)
    {
        if (context.MigrationState is EncryptedStorageMigrationState.NotStarted or
            EncryptedStorageMigrationState.Complete)
            return StoragePolicyDecision.Deny("rollback.invalid-migration-state");
        if (!context.SourceSetPreserved)
            return StoragePolicyDecision.Deny("rollback.source-set-missing");
        if (!context.SourceSetVerified)
            return StoragePolicyDecision.Deny("rollback.source-set-unverified");
        if (!context.RollbackKeyAvailable || context.RollbackKey is null)
            return StoragePolicyDecision.Deny("rollback.key-unavailable");
        if (context.RollbackKey.State is StorageKeyState.Revoked or
            StorageKeyState.Pending or StorageKeyState.RotationPending)
            return StoragePolicyDecision.Deny("rollback.key-invalid-state");

        var backup = EncryptedBackupPolicy.CanRestore(context.BackupVerification);
        if (!backup.Allowed)
            return StoragePolicyDecision.Deny("rollback.backup-unverified");
        if (context.RotationState == StorageKeyRotationState.Complete)
            return StoragePolicyDecision.Deny("rollback.rotation-already-complete");

        return StoragePolicyDecision.Allow("rollback.ready");
    }

    private static bool IsStableRotation(StorageKeyRotationState state)
        => state is StorageKeyRotationState.None or StorageKeyRotationState.Complete;

    private static void Add(
        ICollection<MigrationDryRunCheck> checks,
        string checkCode,
        bool passed,
        string failureReasonCode)
        => checks.Add(new(checkCode, passed, passed ? $"{checkCode}.passed" : failureReasonCode));
}

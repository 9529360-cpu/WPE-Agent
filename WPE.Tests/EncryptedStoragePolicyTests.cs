using 币安量化机器人.Services.Security;

namespace WPE.Tests;

public sealed class EncryptedStoragePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly StorageKeyDescriptor ActiveKey = new(
        "storage-key-1", 3, StorageKeyState.Active,
        PlatformKeyStoreKind.WindowsDpapiCurrentUser, Now.AddDays(-1), Now.AddDays(-1));

    [Fact]
    public void StoreOpen_FailsClosedUntilMigrationAndKeyAreReady()
    {
        var missingKey = ReadyContext() with { ActiveKey = null };
        var plaintext = ReadyContext() with { PlaintextDatabaseDetected = true };
        var incomplete = ReadyContext() with { MigrationState = EncryptedStorageMigrationState.VerificationPending };

        Assert.Equal("storage.active-key-missing", EncryptedStoragePolicy.CanOpenEncryptedStore(missingKey).ReasonCode);
        Assert.Equal("storage.plaintext-database-detected", EncryptedStoragePolicy.CanOpenEncryptedStore(plaintext).ReasonCode);
        Assert.Equal("storage.migration-incomplete", EncryptedStoragePolicy.CanOpenEncryptedStore(incomplete).ReasonCode);
        Assert.False(EncryptedStoragePolicy.CanOpenEncryptedStore(missingKey).Allowed);
        Assert.True(EncryptedStoragePolicy.CanOpenEncryptedStore(ReadyContext()).Allowed);
    }

    [Fact]
    public void Migration_RequiresInventoryVerifiedEncryptedBackupAndActiveKey()
    {
        var context = ReadyContext() with { MigrationState = EncryptedStorageMigrationState.BackupVerified };

        Assert.False(EncryptedStoragePolicy.CanStartMigration(context with { ExistingFilesInventoried = false }).Allowed);
        Assert.False(EncryptedStoragePolicy.CanStartMigration(context with { VerifiedEncryptedBackupExists = false }).Allowed);
        Assert.False(EncryptedStoragePolicy.CanStartMigration(context with { ActiveKey = ActiveKey with { State = StorageKeyState.Revoked } }).Allowed);
        Assert.True(EncryptedStoragePolicy.CanStartMigration(context).Allowed);
    }

    [Fact]
    public void Restore_RejectsRevokedOrMismatchedKeysAndTamperedFiles()
    {
        var context = ReadyRestoreContext();

        Assert.Equal("backup.recovery-key-invalid-state", EncryptedBackupPolicy.CanRestore(
            context with { RecoveryKey = ActiveKey with { State = StorageKeyState.Revoked } }).ReasonCode);
        Assert.Equal("backup.recovery-key-mismatch", EncryptedBackupPolicy.CanRestore(
            context with { RecoveryKey = ActiveKey with { Version = 4 } }).ReasonCode);
        Assert.Equal("backup.file-integrity-failed", EncryptedBackupPolicy.CanRestore(
            context with { ObservedFileHashes = new Dictionary<string, string> { ["agent.db.enc"] = new('B', 64) } }).ReasonCode);
        Assert.True(EncryptedBackupPolicy.CanRestore(context).Allowed);
    }

    [Fact]
    public void ManifestHash_IsDeterministicAcrossFileOrdering()
    {
        var first = Manifest([
            new("agent.db.enc", 128, new('A', 64)),
            new("audit.db.enc", 256, new('B', 64))]);
        var second = Manifest(first.Files.Reverse().ToArray());

        Assert.Equal(
            EncryptedBackupPolicy.ComputeManifestHash(first),
            EncryptedBackupPolicy.ComputeManifestHash(second));
    }

    [Fact]
    public void DryRun_RejectsUnavailableKeyStoreWithoutPerformingWork()
    {
        var plan = EncryptedStoragePolicy.CreateDryRunPlan(ReadyDryRun() with { KeyStoreAvailable = false });

        Assert.False(plan.Allowed);
        Assert.True(plan.IsReadOnly);
        Assert.Equal("migration.keystore-unavailable", plan.ReasonCode);
        Assert.Contains(plan.Checks, check => check.CheckCode == "keystore.available" && !check.Passed);
    }

    [Theory]
    [InlineData(StorageKeyRotationState.NewKeyCreated)]
    [InlineData(StorageKeyRotationState.RekeyInProgress)]
    [InlineData(StorageKeyRotationState.VerificationPending)]
    [InlineData(StorageKeyRotationState.Interrupted)]
    public void DryRun_RejectsIncompleteOrInterruptedRotation(StorageKeyRotationState rotationState)
    {
        var plan = EncryptedStoragePolicy.CreateDryRunPlan(ReadyDryRun() with { RotationState = rotationState });

        Assert.False(plan.Allowed);
        Assert.Equal("migration.rotation-interrupted", plan.ReasonCode);
    }

    [Fact]
    public void DryRun_RejectsTamperedBackupManifest()
    {
        var context = ReadyDryRun();
        var tampered = context.BackupVerification.Manifest! with { BackupId = "tampered-backup" };

        var plan = EncryptedStoragePolicy.CreateDryRunPlan(context with
        {
            BackupVerification = context.BackupVerification with { Manifest = tampered }
        });

        Assert.False(plan.Allowed);
        Assert.Equal("backup.manifest-integrity-failed", plan.ReasonCode);
    }

    [Fact]
    public void Rollback_RejectsMissingSourceEvidenceAndCompletedRotation()
    {
        var context = ReadyRollback();

        Assert.Equal("rollback.source-set-missing", EncryptedStoragePolicy.CanRollback(
            context with { SourceSetPreserved = false }).ReasonCode);
        Assert.Equal("rollback.source-set-unverified", EncryptedStoragePolicy.CanRollback(
            context with { SourceSetVerified = false }).ReasonCode);
        Assert.Equal("rollback.rotation-already-complete", EncryptedStoragePolicy.CanRollback(
            context with { RotationState = StorageKeyRotationState.Complete }).ReasonCode);
        Assert.True(EncryptedStoragePolicy.CanRollback(context).Allowed);
    }

    private static EncryptedStorageActivationContext ReadyContext() => new(
        EncryptedStorageMigrationState.Complete, ActiveKey, true, true, true, false);

    private static BackupRestoreContext ReadyRestoreContext()
    {
        var manifest = Manifest([new("agent.db.enc", 128, new('A', 64))]);
        return new(manifest, ActiveKey, true, true, true,
            new Dictionary<string, string> { ["agent.db.enc"] = new('A', 64) });
    }

    private static MigrationDryRunContext ReadyDryRun() => new(
        ReadyContext() with { MigrationState = EncryptedStorageMigrationState.BackupVerified },
        true,
        StorageKeyRotationState.None,
        ReadyRestoreContext(),
        true,
        true,
        true,
        true);

    private static MigrationRollbackContext ReadyRollback() => new(
        EncryptedStorageMigrationState.Failed,
        StorageKeyRotationState.Interrupted,
        true,
        true,
        true,
        ActiveKey,
        ReadyRestoreContext());

    private static EncryptedBackupManifest Manifest(IReadOnlyList<EncryptedBackupFile> files)
    {
        var manifest = new EncryptedBackupManifest(
            EncryptedBackupPolicy.CurrentFormatVersion,
            "backup-20260721-001",
            Now,
            new StorageKeyReference(ActiveKey.KeyId, ActiveKey.Version),
            EncryptedBackupPolicy.RequiredCipherSuite,
            files,
            string.Empty);
        return manifest with { ManifestSha256 = EncryptedBackupPolicy.ComputeManifestHash(manifest) };
    }
}

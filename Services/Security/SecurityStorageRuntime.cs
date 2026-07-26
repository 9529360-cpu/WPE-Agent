using System.Security.Cryptography;
using System.Text;

namespace 币安量化机器人.Services.Security;

public enum SecurityStorageRuntimeState
{
    Unknown,
    Ready,
    Committed,
    Failed
}

public sealed record SecurityStorageRuntimeStatus(
    SecurityStorageRuntimeState State,
    string ReasonCode,
    int EnvelopeVersion,
    int RecordCount,
    string? EvidenceSha256);

public sealed record TrustedStorageRecordMetadata(
    string RecordType,
    string RecordId,
    int RecordVersion,
    string ExpectedStoredSha256,
    LegacyFormat LegacyFormat);

public sealed record TrustedStorageRecord(
    TrustedStorageRecordMetadata Metadata,
    byte[] StoredValue);

public interface ISecurityStorageInventory
{
    Task<IReadOnlyList<TrustedStorageRecord>> EnumerateAsync(CancellationToken cancellationToken);
}

public interface ISecurityStorageTransaction
{
    Task CommitMigrationAsync(
        IReadOnlyList<RecordMigrationWrite> writes,
        RecordMigrationCheckpoint checkpoint,
        CancellationToken cancellationToken);

    Task CommitRestoreAsync(
        BackupRestoreDrillVerification verification,
        CancellationToken cancellationToken);

    Task CommitRotationAsync(
        KeyRotationArtifact committedArtifact,
        CancellationToken cancellationToken);
}

public sealed class SecurityStorageRuntime
{
    private readonly object _gate=new();
    private readonly IPlatformStorageKeyStore _keyStore;
    private readonly ISecurityStorageInventory _inventory;
    private readonly ISecurityStorageTransaction _transactions;
    private readonly EncryptedRecordMigrationPlanner _migration;
    private SecurityStorageRuntimeStatus _status=new(
        SecurityStorageRuntimeState.Unknown,"security-storage.not-run",
        VersionedEnvelopeEncryptionService.CurrentVersion,0,null);

    public SecurityStorageRuntime(
        IPlatformStorageKeyStore keyStore,
        IPlatformKeyProtector keyProtector,
        ISecurityStorageInventory inventory,
        ISecurityStorageTransaction transactions)
    {
        _keyStore=keyStore??throw new ArgumentNullException(nameof(keyStore));
        _inventory=inventory??throw new ArgumentNullException(nameof(inventory));
        _transactions=transactions??throw new ArgumentNullException(nameof(transactions));
        _migration=new(new VersionedEnvelopeEncryptionService(
            keyProtector??throw new ArgumentNullException(nameof(keyProtector))));
    }

    public SecurityStorageRuntimeStatus Read()
    {
        lock(_gate)return _status;
    }

    public async Task<SecurityStorageRuntimeStatus> MigrateBatchAsync(
        int batchSize,
        RecordMigrationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var stored=await _inventory.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            var records=stored.Select(record=>new RecordMigrationInput(
                record.Metadata.RecordType,record.Metadata.RecordId,record.Metadata.RecordVersion,
                record.StoredValue,record.Metadata.ExpectedStoredSha256,record.Metadata.LegacyFormat)).ToArray();
            var plan=_migration.PlanBatch(records,batchSize,checkpoint);
            if(!plan.Allowed)return Publish(SecurityStorageRuntimeState.Failed,plan.ReasonCode,records.Length,null);
            await _transactions.CommitMigrationAsync(plan.Writes,plan.Checkpoint,cancellationToken).ConfigureAwait(false);
            return Publish(SecurityStorageRuntimeState.Committed,plan.ReasonCode,records.Length,CheckpointHash(plan.Checkpoint));
        }
        catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){throw;}
        catch{return Publish(SecurityStorageRuntimeState.Failed,"security-storage.migration-transaction-failed",0,null);}
    }

    public async Task<SecurityStorageRuntimeStatus> CommitRotationAndRetireOldKeyAsync(
        KeyRotationArtifact verifiedArtifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifiedArtifact);
        var commit=RecoverableKeyRotationStateMachine.Commit(verifiedArtifact);
        if(!commit.Allowed||!commit.Artifact.CanRetireOldKey)
            return Publish(SecurityStorageRuntimeState.Failed,commit.ReasonCode,verifiedArtifact.TotalRecords,null);
        try
        {
            await _transactions.CommitRotationAsync(commit.Artifact,cancellationToken).ConfigureAwait(false);
            await _keyStore.RetireAsync(commit.Artifact.OldKey,cancellationToken).ConfigureAwait(false);
            return Publish(SecurityStorageRuntimeState.Committed,"security-storage.old-key-retired",
                commit.Artifact.TotalRecords,commit.Artifact.InventorySha256);
        }
        catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){throw;}
        catch{return Publish(SecurityStorageRuntimeState.Failed,"security-storage.rotation-commit-or-retirement-failed",verifiedArtifact.TotalRecords,null);}
    }

    public async Task<SecurityStorageRuntimeStatus> RestoreAsync(
        BackupRestoreContext policyContext,
        BackupRestoreDrillEvidence drillEvidence,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var policy=EncryptedBackupPolicy.CanRestore(policyContext);
        if(!policy.Allowed)return Publish(SecurityStorageRuntimeState.Failed,policy.ReasonCode,0,null);
        var drill=BackupRestoreDrillPlanner.Verify(drillEvidence,nowUtc);
        if(!drill.Allowed)return Publish(SecurityStorageRuntimeState.Failed,drill.ReasonCode,0,null);
        try
        {
            await _transactions.CommitRestoreAsync(drill,cancellationToken).ConfigureAwait(false);
            return Publish(SecurityStorageRuntimeState.Committed,"security-storage.restore-committed",
                drillEvidence.RestoredRecordCount,drill.PlanSha256);
        }
        catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){throw;}
        catch{return Publish(SecurityStorageRuntimeState.Failed,"security-storage.restore-transaction-failed",0,null);}
    }

    private SecurityStorageRuntimeStatus Publish(SecurityStorageRuntimeState state,string reason,int count,string? hash)
    {
        var value=new SecurityStorageRuntimeStatus(state,reason,
            VersionedEnvelopeEncryptionService.CurrentVersion,count,IsSha256(hash)?hash:null);
        lock(_gate)_status=value;
        return value;
    }

    private static string CheckpointHash(RecordMigrationCheckpoint checkpoint)
    {
        var canonical=new StringBuilder().Append(checkpoint.TargetEnvelopeVersion).Append('\n');
        foreach(var item in checkpoint.CompletedRecordHashes.OrderBy(item=>item.Key,StringComparer.Ordinal))
            canonical.Append(item.Key.Length).Append(':').Append(item.Key).Append('|').Append(item.Value.ToUpperInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static bool IsSha256(string? value)=>value is{Length:64}&&value.All(Uri.IsHexDigit);
}

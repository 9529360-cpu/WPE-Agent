using System.Security.Cryptography;
using 币安量化机器人.Services.Security;

namespace WPE.Tests;

public sealed class SecurityStorageRuntimeTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,22,12,0,0,TimeSpan.Zero);
    private readonly MemoryProtector _protector=new();

    [Fact]
    public async Task Migration_UsesOnlyTrustedLegacyMetadataAndCommitsWritesWithCheckpointOnce()
    {
        var stored=new byte[]{1,2,3};var storage=new FakeStorage{Records=[Record("r1",stored,LegacyFormat.WindowsDpapiCurrentUser)]};
        var runtime=Runtime(storage);

        var status=await runtime.MigrateBatchAsync(10,Checkpoint(),CancellationToken.None);

        Assert.Equal(SecurityStorageRuntimeState.Committed,status.State);
        Assert.Equal(1,status.RecordCount);Assert.Equal(64,status.EvidenceSha256!.Length);
        Assert.Equal(1,storage.MigrationCommits);Assert.Single(storage.CommittedWrites);Assert.Single(storage.CommittedCheckpoint!.CompletedRecordHashes);
        Assert.DoesNotContain("r1",status.ToString(),StringComparison.Ordinal);
        Assert.DoesNotContain(_protector.KeyId,status.ToString(),StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4C6567616379466F726D61743D57696E646F7773447061706943757272656E7455736572","migration.legacy-format-required")]
    [InlineData("01020304","migration.legacy-format-required")]
    [InlineData("57504558","migration.envelope-magic-invalid")]
    public async Task PayloadClaimsUnknownAndCorruptFormatsFailClosed(string hex,string reason)
    {
        var value=Convert.FromHexString(hex);var storage=new FakeStorage{Records=[Record("r1",value,LegacyFormat.None)]};

        var status=await Runtime(storage).MigrateBatchAsync(10,Checkpoint(),CancellationToken.None);

        Assert.Equal(SecurityStorageRuntimeState.Failed,status.State);Assert.Equal(reason,status.ReasonCode);
        Assert.Equal(0,storage.MigrationCommits);Assert.Empty(storage.CommittedWrites);
    }

    [Fact]
    public async Task MigrationTransactionFailureRollsBackWholeBatchAndCheckpoint()
    {
        var storage=new FakeStorage{Records=[Record("r1",[1],LegacyFormat.WindowsDpapiCurrentUser),Record("r2",[2],LegacyFormat.WindowsDpapiCurrentUser)],FailMigration=true};

        var status=await Runtime(storage).MigrateBatchAsync(10,Checkpoint(),CancellationToken.None);

        Assert.Equal("security-storage.migration-transaction-failed",status.ReasonCode);
        Assert.Empty(storage.CommittedWrites);Assert.Null(storage.CommittedCheckpoint);
    }

    [Fact]
    public async Task OldKeyRetiresOnlyAfterVerifiedArtifactIsCommitted()
    {
        var storage=new FakeStorage();var runtime=Runtime(storage);var artifact=RotationArtifact(RecoverableKeyRotationStatus.InProgress);
        Assert.Equal("rotation.state-not-committable",(await runtime.CommitRotationAndRetireOldKeyAsync(artifact,CancellationToken.None)).ReasonCode);
        Assert.Equal(0,storage.RetireCalls);

        var verified=artifact with{Status=RecoverableKeyRotationStatus.Verified};
        Assert.Equal("security-storage.old-key-retired",(await runtime.CommitRotationAndRetireOldKeyAsync(verified,CancellationToken.None)).ReasonCode);
        Assert.Equal(1,storage.RotationCommits);Assert.Equal(RecoverableKeyRotationStatus.Committed,storage.CommittedRotation!.Status);Assert.Equal(1,storage.RetireCalls);
    }

    [Fact]
    public async Task RotationCommitFailureNeverRetiresOldKey()
    {
        var storage=new FakeStorage{FailRotation=true};

        var status=await Runtime(storage).CommitRotationAndRetireOldKeyAsync(
            RotationArtifact(RecoverableKeyRotationStatus.Verified),CancellationToken.None);

        Assert.Equal("security-storage.rotation-commit-or-retirement-failed",status.ReasonCode);
        Assert.Equal(0,storage.RetireCalls);Assert.Null(storage.CommittedRotation);
    }

    [Fact]
    public async Task RestoreRequiresPolicyAndVerifiedDrillBeforeTransaction()
    {
        var storage=new FakeStorage();var runtime=Runtime(storage);var (context,evidence)=RestoreInputs();
        Assert.Equal("backup.manifest-unauthenticated",(await runtime.RestoreAsync(context with{ManifestAuthenticated=false},evidence,Now,CancellationToken.None)).ReasonCode);
        Assert.Equal(0,storage.RestoreCommits);
        Assert.Equal("drill.verification-incomplete",(await runtime.RestoreAsync(context,evidence with{AuditRecorded=false},Now,CancellationToken.None)).ReasonCode);
        Assert.Equal(0,storage.RestoreCommits);

        var status=await runtime.RestoreAsync(context,evidence,Now,CancellationToken.None);
        Assert.Equal("security-storage.restore-committed",status.ReasonCode);Assert.Equal(1,storage.RestoreCommits);
    }

    private SecurityStorageRuntime Runtime(FakeStorage storage)=>new(storage,_protector,storage,storage);
    private static RecordMigrationCheckpoint Checkpoint()=>new("migration-runtime",1,new Dictionary<string,string>());
    private static TrustedStorageRecord Record(string id,byte[] value,LegacyFormat format)=>new(
        new("audit",id,1,Hash(value),format),value);
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value));
    private static KeyRotationArtifact RotationArtifact(RecoverableKeyRotationStatus status)=>new(
        1,"rotation",status,new("old",1),new("new",2),1,new('A',64),new Dictionary<string,string>{{"r1",new string('B',64)}},"");

    private static (BackupRestoreContext,BackupRestoreDrillEvidence) RestoreInputs()
    {
        var key=new StorageKeyDescriptor("recovery",1,StorageKeyState.Active,PlatformKeyStoreKind.WindowsDpapiCurrentUser,Now.AddDays(-2));
        var file=new EncryptedBackupFile("backup.enc",10,new('A',64));
        var manifest=new EncryptedBackupManifest(1,"backup",Now.AddDays(-1),new("recovery",1),EncryptedBackupPolicy.RequiredCipherSuite,[file],"");
        manifest=manifest with{ManifestSha256=EncryptedBackupPolicy.ComputeManifestHash(manifest)};
        var inventory=new[]{new BackupEnvelopeInventory(1,new("recovery",1),1,new string('B',64))};
        var request=new BackupRestoreDrillRequest("backup",manifest,true,key,new Dictionary<string,string>{{"backup.enc",new string('A',64)}},inventory,[new("recovery",1)],1,manifest.CreatedAtUtc,Now.AddHours(1),"empty-target",true,"audit");
        var artifact=BackupRestoreDrillPlanner.CreatePlan(request,Now.AddMinutes(-1)).Artifact!;
        var evidence=new BackupRestoreDrillEvidence(artifact,inventory,1,"empty-target",true,true,true,true);
        return(new(manifest,key,true,true,true,new Dictionary<string,string>{{"backup.enc",new string('A',64)}}),evidence);
    }

    public void Dispose()=>_protector.Dispose();

    private sealed class FakeStorage:IPlatformStorageKeyStore,ISecurityStorageInventory,ISecurityStorageTransaction
    {
        public PlatformKeyStoreKind Kind=>PlatformKeyStoreKind.WindowsDpapiCurrentUser;
        public IReadOnlyList<TrustedStorageRecord> Records{get;init;}=[];public bool FailMigration{get;init;}public bool FailRotation{get;init;}
        public int MigrationCommits{get;private set;}public int RestoreCommits{get;private set;}public int RotationCommits{get;private set;}public int RetireCalls{get;private set;}
        public IReadOnlyList<RecordMigrationWrite> CommittedWrites{get;private set;}=[];public RecordMigrationCheckpoint? CommittedCheckpoint{get;private set;}
        public KeyRotationArtifact? CommittedRotation{get;private set;}
        public Task<IReadOnlyList<TrustedStorageRecord>> EnumerateAsync(CancellationToken ct)=>Task.FromResult(Records);
        public Task CommitMigrationAsync(IReadOnlyList<RecordMigrationWrite> writes,RecordMigrationCheckpoint checkpoint,CancellationToken ct){MigrationCommits++;if(FailMigration)throw new IOException("fake transaction failed");CommittedWrites=writes.ToArray();CommittedCheckpoint=checkpoint;return Task.CompletedTask;}
        public Task CommitRestoreAsync(BackupRestoreDrillVerification verification,CancellationToken ct){RestoreCommits++;return Task.CompletedTask;}
        public Task CommitRotationAsync(KeyRotationArtifact artifact,CancellationToken ct){RotationCommits++;if(FailRotation)throw new IOException("fake rotation transaction failed");CommittedRotation=artifact;return Task.CompletedTask;}
        public Task RetireAsync(StorageKeyReference reference,CancellationToken ct){RetireCalls++;return Task.CompletedTask;}
        public Task<StorageKeyDescriptor> CreateAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<ProtectedStorageKey?> LoadAsync(StorageKeyReference reference,CancellationToken ct)=>throw new NotSupportedException();
        public Task<StorageKeyDescriptor> BeginRotationAsync(StorageKeyReference current,CancellationToken ct)=>throw new NotSupportedException();
        public Task RevokeAsync(StorageKeyReference reference,string reasonCode,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ProtectedStorageKey?> LoadForRecoveryAsync(StorageKeyReference reference,CancellationToken ct)=>throw new NotSupportedException();
    }

    private sealed class MemoryProtector:IPlatformKeyProtector,IDisposable
    {
        private readonly byte[] _key=RandomNumberGenerator.GetBytes(32);public bool IsAvailable=>true;public string KeyId=>"fake-key";
        public byte[] WrapKey(ReadOnlySpan<byte> dek,EnvelopeKeyContext context){var value=dek.ToArray();for(var i=0;i<value.Length;i++)value[i]^=_key[i];return value;}
        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedDek,EnvelopeKeyContext context)=>WrapKey(wrappedDek,context);
        public void Dispose()=>CryptographicOperations.ZeroMemory(_key);
    }
}

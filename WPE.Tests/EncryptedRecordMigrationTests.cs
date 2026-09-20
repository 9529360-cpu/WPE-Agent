using System.Buffers.Binary;
using System.Security.Cryptography;
using \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Security;

namespace WPE.Tests;

public sealed class EncryptedRecordMigrationTests
{
    [Fact]
    public void Codec_IdentifiesOnlyEnvelopeAndRoundTripsWithoutPlaintextResult()
    {
        using var fixture = new Fixture();
        var legacy = new byte[] { 10, 20, 30 };
        var encrypted = fixture.Encrypt("r1", legacy);

        Assert.Equal(EncryptedRecordKind.Unknown, EncryptedRecordCodec.Identify(legacy));
        Assert.Equal(EncryptedRecordKind.Envelope, EncryptedRecordCodec.Identify(encrypted));
        Assert.False(ContainsSequence(encrypted, legacy));
        Assert.Equal(VersionedEnvelopeEncryptionService.CurrentVersion, EncryptedRecordCodec.Decode(encrypted).Version);
    }

    [Theory]
    [InlineData("010203", "migration.legacy-format-required")]
    [InlineData("5750455801", "migration.envelope-magic-invalid")]
    [InlineData("5750", "migration.envelope-magic-invalid")]
    public void Planner_RejectsUnknownRandomOrDamagedMagicWithoutMigration(string hex,string reason)
    {
        using var fixture=new Fixture();
        var value=Convert.FromHexString(hex);
        var record=fixture.Stored("r1",value);

        var plan=fixture.Planner.PlanBatch([record],1,fixture.Checkpoint());

        Assert.False(plan.Allowed);
        Assert.Equal(reason,plan.ReasonCode);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void Planner_DamagedMagicFailsClosedEvenWithExplicitLegacyMarker()
    {
        using var fixture=new Fixture();
        var value=new byte[]{(byte)'W',(byte)'P',(byte)'E',(byte)'X',9};
        var record=fixture.Stored("r1",value) with{LegacyFormat=LegacyFormat.WindowsDpapiCurrentUser};

        var plan=fixture.Planner.PlanBatch([record],1,fixture.Checkpoint());

        Assert.False(plan.Allowed);
        Assert.Equal("migration.envelope-magic-invalid",plan.ReasonCode);
    }

    [Fact]
    public void Planner_CreatesBoundedBatchAndCheckpointWithoutPlaintext()
    {
        using var fixture = new Fixture();
        var firstPlaintext=Enumerable.Range(1,32).Select(value=>(byte)value).ToArray();
        var secondPlaintext=Enumerable.Range(33,32).Select(value=>(byte)value).ToArray();
        var records = new[] { fixture.Legacy("r1", firstPlaintext), fixture.Legacy("r2", secondPlaintext) };

        var plan = fixture.Planner.PlanBatch(records, 1, fixture.Checkpoint());

        Assert.True(plan.Allowed);
        var write = Assert.Single(plan.Writes);
        Assert.Equal("r1", write.RecordId);
        Assert.Single(plan.Checkpoint.CompletedRecordHashes);
        Assert.False(ContainsSequence(write.EncodedEnvelope, records[0].StoredValue));
    }

    [Fact]
    public void Planner_RetrySkipsAlreadyEncryptedRecordIdempotently()
    {
        using var fixture = new Fixture();
        var encrypted = fixture.Encrypt("r1", [5, 6]);
        var record = fixture.Stored("r1", encrypted);
        var checkpoint = fixture.Checkpoint(new Dictionary<string, string> { ["r1"] = record.ExpectedStoredSha256 });

        var plan = fixture.Planner.PlanBatch([record], 10, checkpoint);

        Assert.True(plan.Allowed);
        Assert.Equal("migration.batch-idempotent", plan.ReasonCode);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void Planner_AllowsCurrentEnvelopeAlongsideLegacyButRejectsMixedEnvelopeVersions()
    {
        using var fixture = new Fixture();
        var current = fixture.Stored("r1", fixture.Encrypt("r1", [1]));
        var legacy = fixture.Legacy("r2", [2]);
        Assert.True(fixture.Planner.PlanBatch([current, legacy], 10, fixture.Checkpoint()).Allowed);

        var unknown = current with
        {
            RecordId = "r3",
            StoredValue = ChangeEnvelopeVersion(current.StoredValue, 2)
        };
        unknown = unknown with { ExpectedStoredSha256 = Hash(unknown.StoredValue) };

        var rejected = fixture.Planner.PlanBatch([current, unknown], 10, fixture.Checkpoint());

        Assert.False(rejected.Allowed);
        Assert.Empty(rejected.Writes);
        Assert.Equal("migration.mixed-envelope-versions", rejected.ReasonCode);
    }

    [Fact]
    public void Planner_ChecksumFailureStopsWholeBatchWithoutWritesOrPlaintextInResult()
    {
        using var fixture = new Fixture();
        var good = fixture.Legacy("r1", [7, 8]);
        var bad = fixture.Legacy("r2", [9, 10]) with { ExpectedStoredSha256 = new('A', 64) };

        var plan = fixture.Planner.PlanBatch([good, bad], 10, fixture.Checkpoint());

        Assert.False(plan.Allowed);
        Assert.Equal("migration.record-checksum-failed", plan.ReasonCode);
        Assert.Empty(plan.Writes);
        Assert.DoesNotContain("7", plan.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("9", plan.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_RejectsCheckpointMismatch()
    {
        using var fixture = new Fixture();
        var legacy = fixture.Legacy("r1", [1, 1]);
        var checkpoint = fixture.Checkpoint(new Dictionary<string, string> { ["r1"] = Hash([2, 2]) });

        var plan = fixture.Planner.PlanBatch([legacy], 1, checkpoint);

        Assert.False(plan.Allowed);
        Assert.Equal("migration.checkpoint-record-mismatch", plan.ReasonCode);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void RollbackRequiresBackupThatPassesRestoreVerification()
    {
        using var fixture = new Fixture();
        var verified = fixture.VerifiedBackup();

        Assert.True(EncryptedRecordMigrationPlanner.CanRollback(verified).Allowed);
        Assert.Equal("migration.rollback-backup-unverified", EncryptedRecordMigrationPlanner.CanRollback(
            verified with { ManifestAuthenticated = false }).ReasonCode);
    }

    private static byte[] ChangeEnvelopeVersion(byte[] encoded, int version)
    {
        var changed = encoded.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(changed.AsSpan(5, sizeof(int)), version);
        return changed;
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static bool ContainsSequence(byte[] value, byte[] candidate)
        => candidate.Length > 0 && value.AsSpan().IndexOf(candidate) >= 0;

    private sealed class Fixture : IDisposable
    {
        private readonly MemoryKeyProtector _protector = new();
        private readonly VersionedEnvelopeEncryptionService _encryption;

        public Fixture()
        {
            _encryption = new(_protector);
            Planner = new(_encryption);
        }

        public EncryptedRecordMigrationPlanner Planner { get; }

        public RecordMigrationInput Legacy(string id, byte[] value) =>
            Stored(id, value) with { LegacyFormat = LegacyFormat.WindowsDpapiCurrentUser };

        public RecordMigrationInput Stored(string id, byte[] value)
            => new("audit", id, 1, value, Hash(value));

        public byte[] Encrypt(string id, byte[] value)
            => EncryptedRecordCodec.Encode(_encryption.Encrypt(value, new("audit", id, 1)));

        public RecordMigrationCheckpoint Checkpoint(IReadOnlyDictionary<string, string>? completed = null)
            => new("migration-test", 1, completed ?? new Dictionary<string, string>());

        public BackupRestoreContext VerifiedBackup()
        {
            var key = new StorageKeyDescriptor("backup-key", 1, StorageKeyState.Active,
                PlatformKeyStoreKind.WindowsDpapiCurrentUser, DateTimeOffset.UnixEpoch);
            var file = new EncryptedBackupFile("backup.enc", 3, new('A', 64));
            var manifest = new EncryptedBackupManifest(1, "backup-test", DateTimeOffset.UnixEpoch,
                new("backup-key", 1), EncryptedBackupPolicy.RequiredCipherSuite, [file], string.Empty);
            manifest = manifest with { ManifestSha256 = EncryptedBackupPolicy.ComputeManifestHash(manifest) };
            return new(manifest, key, true, true, true,
                new Dictionary<string, string> { ["backup.enc"] = new('A', 64) });
        }

        public void Dispose() => _protector.Dispose();
    }

    private sealed class MemoryKeyProtector : IPlatformKeyProtector, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool IsAvailable => true;
        public string KeyId => "memory-test-key";

        public byte[] WrapKey(ReadOnlySpan<byte> dek, EnvelopeKeyContext context)
        {
            var output = dek.ToArray();
            for (var i = 0; i < output.Length; i++) output[i] ^= _key[i];
            return output;
        }

        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedDek, EnvelopeKeyContext context)
            => WrapKey(wrappedDek, context);

        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }
}

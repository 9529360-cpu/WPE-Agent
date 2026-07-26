using System.Security.Cryptography;
using \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Security;

namespace WPE.Tests;

public sealed class RecoverableKeyRotationTests
{
    [Fact]
    public void Rotation_TransitionsPendingInProgressVerifiedCommittedBeforeRetirement()
    {
        using var fixture = new Fixture();
        var records = fixture.OldRecords("r1", "r2");
        var artifact = fixture.Pending(records);
        Assert.True(artifact.DualKeyWindowOpen);
        Assert.False(artifact.CanRetireOldKey);

        var first = fixture.Machine.PlanBatch(records, 2, artifact);
        var applied = Apply(records, first.Writes);
        var verified = fixture.Machine.VerifyAll(applied, first.Artifact);
        var committed = RecoverableKeyRotationStateMachine.Commit(verified.Artifact);

        Assert.True(first.Allowed);
        Assert.Equal(RecoverableKeyRotationStatus.InProgress, first.Artifact.Status);
        Assert.True(verified.Allowed);
        Assert.Equal(RecoverableKeyRotationStatus.Verified, verified.Artifact.Status);
        Assert.False(verified.Artifact.CanRetireOldKey);
        Assert.True(committed.Allowed);
        Assert.True(committed.Artifact.CanRetireOldKey);
        Assert.False(committed.Artifact.DualKeyWindowOpen);
    }

    [Fact]
    public void InProgressCheckpoint_ResumesAfterInterruptionAndSkipsCompletedRecord()
    {
        using var fixture = new Fixture();
        var records = fixture.OldRecords("r1", "r2");
        var first = fixture.Machine.PlanBatch(records, 1, fixture.Pending(records));
        var afterInterruption = Apply(records, first.Writes);

        var resumed = fixture.Machine.PlanBatch(afterInterruption, 1, first.Artifact);

        Assert.True(resumed.Allowed);
        Assert.Equal("r2", Assert.Single(resumed.Writes).RecordId);
        Assert.Equal(2, resumed.Artifact.RewrappedRecordHashes.Count);
    }

    [Theory]
    [InlineData("version", "rotation.artifact-version-unsupported")]
    [InlineData("count", "rotation.record-count-mismatch")]
    [InlineData("inventory", "rotation.inventory-hash-mismatch")]
    public void ArtifactMismatch_FailsClosed(string mismatch, string reasonCode)
    {
        using var fixture = new Fixture();
        var records = fixture.OldRecords("r1");
        var artifact = fixture.Pending(records);
        artifact = mismatch switch
        {
            "version" => artifact with { ArtifactVersion = 99 },
            "count" => artifact with { TotalRecords = 2 },
            _ => artifact with { InventorySha256 = new('A', 64) }
        };

        var plan = fixture.Machine.PlanBatch(records, 1, artifact);

        Assert.False(plan.Allowed);
        Assert.Equal(reasonCode, plan.ReasonCode);
        Assert.Empty(plan.Writes);
        Assert.Equal(RecoverableKeyRotationStatus.Failed, plan.Artifact.Status);
    }

    [Fact]
    public void RecordHashMismatchStopsBatchWithoutWrites()
    {
        using var fixture = new Fixture();
        var records = fixture.OldRecords("r1", "r2");
        records[1] = records[1] with { ExpectedSha256 = new('B', 64) };

        var plan = fixture.Machine.PlanBatch(records, 2, fixture.Pending(records));

        Assert.False(plan.Allowed);
        Assert.Equal("rotation.record-hash-mismatch", plan.ReasonCode);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void UncheckpointedNewKeyRecordIsRejected()
    {
        using var fixture = new Fixture();
        var records = fixture.OldRecords("r1");
        var artifact = fixture.Pending(records);
        var first = fixture.Machine.PlanBatch(records, 1, artifact);
        var applied = Apply(records, first.Writes);

        var rejected = fixture.Machine.PlanBatch(applied, 1, artifact);

        Assert.False(rejected.Allowed);
        Assert.Equal("rotation.uncheckpointed-new-key", rejected.ReasonCode);
    }

    [Fact]
    public void CommitRejectsPartialOrUnverifiedRotation()
    {
        using var fixture = new Fixture();
        var records = fixture.OldRecords("r1", "r2");
        var partial = fixture.Machine.PlanBatch(records, 1, fixture.Pending(records));

        var committed = RecoverableKeyRotationStateMachine.Commit(partial.Artifact);

        Assert.False(committed.Allowed);
        Assert.Equal("rotation.state-not-committable", committed.ReasonCode);
        Assert.False(committed.Artifact.CanRetireOldKey);
    }

    private static List<KeyRotationRecord> Apply(
        IReadOnlyList<KeyRotationRecord> records,
        IReadOnlyList<KeyRotationWrite> writes)
    {
        var byId = writes.ToDictionary(write => write.RecordId, StringComparer.Ordinal);
        return records.Select(record => byId.TryGetValue(record.RecordId, out var write)
            ? record with { EncodedEnvelope = write.EncodedEnvelope, ExpectedSha256 = write.EncodedSha256 }
            : record).ToList();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MemoryProtector _old = new("old-key");
        private readonly MemoryProtector _new = new("new-key");
        private readonly VersionedEnvelopeEncryptionService _oldEncryption;

        public Fixture()
        {
            _oldEncryption = new(_old);
            Machine = new(_old, _new);
        }

        public RecoverableKeyRotationStateMachine Machine { get; }

        public List<KeyRotationRecord> OldRecords(params string[] ids) => ids.Select(id =>
        {
            var envelope = _oldEncryption.Encrypt([1, 2, (byte)id.Length], new("audit", id, 1));
            var encoded = EncryptedRecordCodec.Encode(envelope);
            return new KeyRotationRecord("audit", id, 1, encoded, Hash(encoded));
        }).ToList();

        public KeyRotationArtifact Pending(IReadOnlyList<KeyRotationRecord> records)
            => RecoverableKeyRotationStateMachine.CreatePending(
                "rotation-test", new(_old.KeyId, 1), new(_new.KeyId, 2), records);

        public void Dispose()
        {
            _old.Dispose();
            _new.Dispose();
        }

        private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    }

    private sealed class MemoryProtector : IPlatformKeyProtector, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public MemoryProtector(string keyId) => KeyId = keyId;
        public bool IsAvailable => true;
        public string KeyId { get; }

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

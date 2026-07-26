using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Security;

public enum RecoverableKeyRotationStatus
{
    Pending,
    InProgress,
    Verified,
    Committed,
    Failed
}

public sealed record KeyRotationRecord(
    string RecordType,
    string RecordId,
    int RecordVersion,
    byte[] EncodedEnvelope,
    string ExpectedSha256);

public sealed record KeyRotationArtifact(
    int ArtifactVersion,
    string RotationId,
    RecoverableKeyRotationStatus Status,
    StorageKeyReference OldKey,
    StorageKeyReference NewKey,
    int TotalRecords,
    string InventorySha256,
    IReadOnlyDictionary<string, string> RewrappedRecordHashes,
    string FailureReasonCode)
{
    public bool DualKeyWindowOpen => Status is
        RecoverableKeyRotationStatus.Pending or
        RecoverableKeyRotationStatus.InProgress or
        RecoverableKeyRotationStatus.Verified;

    public bool CanRetireOldKey =>
        Status == RecoverableKeyRotationStatus.Committed &&
        RewrappedRecordHashes.Count == TotalRecords;
}

public sealed record KeyRotationWrite(string RecordId, byte[] EncodedEnvelope, string EncodedSha256);

public sealed record KeyRotationPlan(
    bool Allowed,
    string ReasonCode,
    IReadOnlyList<KeyRotationWrite> Writes,
    KeyRotationArtifact Artifact);

public sealed class RecoverableKeyRotationStateMachine
{
    public const int CurrentArtifactVersion = 1;

    private readonly IPlatformKeyProtector _oldKeyProtector;
    private readonly IPlatformKeyProtector _newKeyProtector;

    public RecoverableKeyRotationStateMachine(
        IPlatformKeyProtector oldKeyProtector,
        IPlatformKeyProtector newKeyProtector)
    {
        _oldKeyProtector = oldKeyProtector ?? throw new ArgumentNullException(nameof(oldKeyProtector));
        _newKeyProtector = newKeyProtector ?? throw new ArgumentNullException(nameof(newKeyProtector));
    }

    public static KeyRotationArtifact CreatePending(
        string rotationId,
        StorageKeyReference oldKey,
        StorageKeyReference newKey,
        IReadOnlyList<KeyRotationRecord> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return new(
            CurrentArtifactVersion,
            rotationId,
            RecoverableKeyRotationStatus.Pending,
            oldKey,
            newKey,
            inventory.Count,
            ComputeInventoryHash(inventory),
            new Dictionary<string, string>(StringComparer.Ordinal),
            string.Empty);
    }

    public KeyRotationPlan PlanBatch(
        IReadOnlyList<KeyRotationRecord> records,
        int batchSize,
        KeyRotationArtifact artifact)
    {
        var failure = ValidateArtifactAndInventory(records, artifact);
        if (failure is not null)
            return Reject(failure, artifact);
        if (batchSize <= 0)
            return Reject("rotation.batch-size-invalid", artifact);
        if (artifact.Status is not (RecoverableKeyRotationStatus.Pending or RecoverableKeyRotationStatus.InProgress))
            return Reject("rotation.state-not-rewrappable", artifact);

        var completed = new Dictionary<string, string>(artifact.RewrappedRecordHashes, StringComparer.Ordinal);
        var pending = new List<KeyRotationRecord>();
        try
        {
            foreach (var record in records.OrderBy(record => record.RecordId, StringComparer.Ordinal))
            {
                var validation = ValidateCheckpointRecord(record, completed);
                if (validation is not null)
                    return Reject(validation, artifact);
                if (!completed.ContainsKey(record.RecordId))
                    pending.Add(record);
            }
        }
        catch
        {
            return Reject("rotation.record-codec-invalid", artifact);
        }

        var writes = new List<KeyRotationWrite>();
        try
        {
            EnsureProtectors(artifact);
            var oldVerifier = new VersionedEnvelopeEncryptionService(_oldKeyProtector);
            foreach (var record in pending.Take(batchSize))
            {
                var envelope = EncryptedRecordCodec.Decode(record.EncodedEnvelope);
                if (!string.Equals(envelope.KeyId, _oldKeyProtector.KeyId, StringComparison.Ordinal))
                    return RejectAndClear("rotation.unexpected-record-key", artifact, writes);

                var verifiedPlaintext = oldVerifier.Decrypt(
                    envelope,
                    new(record.RecordType, record.RecordId, record.RecordVersion));
                CryptographicOperations.ZeroMemory(verifiedPlaintext);

                var context = CreateKeyContext(record);
                byte[]? dek = null;
                try
                {
                    dek = _oldKeyProtector.UnwrapKey(envelope.WrappedDek, context);
                    if (dek.Length != VersionedEnvelopeEncryptionService.DekLength)
                        return RejectAndClear("rotation.dek-length-invalid", artifact, writes);
                    var wrapped = _newKeyProtector.WrapKey(dek, context);
                    if (wrapped.Length == 0)
                        return RejectAndClear("rotation.wrapped-dek-invalid", artifact, writes);

                    var encoded = EncryptedRecordCodec.Encode(envelope with
                    {
                        KeyId = _newKeyProtector.KeyId,
                        WrappedDek = wrapped
                    });
                    var hash = Sha256(encoded);
                    writes.Add(new(record.RecordId, encoded, hash));
                    completed[record.RecordId] = hash;
                }
                finally
                {
                    if (dek is not null)
                        CryptographicOperations.ZeroMemory(dek);
                }
            }

            return new(
                true,
                writes.Count == 0 ? "rotation.batch-idempotent" : "rotation.batch-ready",
                writes,
                artifact with
                {
                    Status = RecoverableKeyRotationStatus.InProgress,
                    RewrappedRecordHashes = completed,
                    FailureReasonCode = string.Empty
                });
        }
        catch
        {
            return RejectAndClear("rotation.rewrap-failed", artifact, writes);
        }
    }

    public KeyRotationPlan VerifyAll(
        IReadOnlyList<KeyRotationRecord> records,
        KeyRotationArtifact artifact)
    {
        var failure = ValidateArtifactAndInventory(records, artifact);
        if (failure is not null)
            return Reject(failure, artifact);
        if (artifact.Status != RecoverableKeyRotationStatus.InProgress)
            return Reject("rotation.state-not-verifiable", artifact);
        if (artifact.RewrappedRecordHashes.Count != artifact.TotalRecords)
            return Reject("rotation.rewrapped-count-mismatch", artifact);

        try
        {
            EnsureProtectors(artifact);
            var verifier = new VersionedEnvelopeEncryptionService(_newKeyProtector);
            foreach (var record in records)
            {
                if (!string.Equals(record.ExpectedSha256, Sha256(record.EncodedEnvelope), StringComparison.OrdinalIgnoreCase))
                    return Reject("rotation.record-hash-mismatch", artifact);
                if (!artifact.RewrappedRecordHashes.TryGetValue(record.RecordId, out var checkpointHash) ||
                    !string.Equals(checkpointHash, record.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    return Reject("rotation.checkpoint-hash-mismatch", artifact);
                var envelope = EncryptedRecordCodec.Decode(record.EncodedEnvelope);
                if (!string.Equals(envelope.KeyId, _newKeyProtector.KeyId, StringComparison.Ordinal))
                    return Reject("rotation.dual-key-window-incomplete", artifact);
                var plaintext = verifier.Decrypt(
                    envelope,
                    new(record.RecordType, record.RecordId, record.RecordVersion));
                CryptographicOperations.ZeroMemory(plaintext);
            }

            return new(true, "rotation.verified", Array.Empty<KeyRotationWrite>(),
                artifact with { Status = RecoverableKeyRotationStatus.Verified, FailureReasonCode = string.Empty });
        }
        catch
        {
            return Reject("rotation.verification-failed", artifact);
        }
    }

    public static KeyRotationPlan Commit(KeyRotationArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ArtifactVersion != CurrentArtifactVersion)
            return Reject("rotation.artifact-version-unsupported", artifact);
        if (artifact.Status != RecoverableKeyRotationStatus.Verified)
            return Reject("rotation.state-not-committable", artifact);
        if (artifact.RewrappedRecordHashes.Count != artifact.TotalRecords)
            return Reject("rotation.rewrapped-count-mismatch", artifact);

        var committed = artifact with { Status = RecoverableKeyRotationStatus.Committed };
        return new(true, "rotation.committed", Array.Empty<KeyRotationWrite>(), committed);
    }

    private string? ValidateArtifactAndInventory(
        IReadOnlyList<KeyRotationRecord> records,
        KeyRotationArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ArtifactVersion != CurrentArtifactVersion)
            return "rotation.artifact-version-unsupported";
        if (string.IsNullOrWhiteSpace(artifact.RotationId) || artifact.OldKey == artifact.NewKey)
            return "rotation.artifact-invalid";
        if (artifact.TotalRecords != records.Count)
            return "rotation.record-count-mismatch";
        if (records.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count() != records.Count)
            return "rotation.duplicate-record-id";
        if (!string.Equals(artifact.InventorySha256, ComputeInventoryHash(records), StringComparison.OrdinalIgnoreCase))
            return "rotation.inventory-hash-mismatch";
        return null;
    }

    private string? ValidateCheckpointRecord(
        KeyRotationRecord record,
        IReadOnlyDictionary<string, string> completed)
    {
        if (string.IsNullOrWhiteSpace(record.RecordType) || string.IsNullOrWhiteSpace(record.RecordId) ||
            record.RecordVersion <= 0 || !IsSha256(record.ExpectedSha256))
            return "rotation.record-metadata-invalid";
        if (!string.Equals(record.ExpectedSha256, Sha256(record.EncodedEnvelope), StringComparison.OrdinalIgnoreCase))
            return "rotation.record-hash-mismatch";

        var envelope = EncryptedRecordCodec.Decode(record.EncodedEnvelope);
        if (envelope.Version != VersionedEnvelopeEncryptionService.CurrentVersion)
            return "rotation.envelope-version-mismatch";
        if (completed.TryGetValue(record.RecordId, out var completedHash))
        {
            if (!string.Equals(completedHash, record.ExpectedSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(envelope.KeyId, _newKeyProtector.KeyId, StringComparison.Ordinal))
                return "rotation.checkpoint-hash-mismatch";
        }
        else if (!string.Equals(envelope.KeyId, _oldKeyProtector.KeyId, StringComparison.Ordinal))
        {
            return "rotation.uncheckpointed-new-key";
        }

        return null;
    }

    private void EnsureProtectors(KeyRotationArtifact artifact)
    {
        if (!_oldKeyProtector.IsAvailable || !_newKeyProtector.IsAvailable)
            throw new CryptographicException("Key protector unavailable.");
        if (!string.Equals(_oldKeyProtector.KeyId, artifact.OldKey.KeyId, StringComparison.Ordinal) ||
            !string.Equals(_newKeyProtector.KeyId, artifact.NewKey.KeyId, StringComparison.Ordinal) ||
            string.Equals(_oldKeyProtector.KeyId, _newKeyProtector.KeyId, StringComparison.Ordinal))
            throw new CryptographicException("Key protector identity mismatch.");
    }

    private static EnvelopeKeyContext CreateKeyContext(KeyRotationRecord record)
    {
        var type = Encoding.UTF8.GetBytes(record.RecordType);
        var id = Encoding.UTF8.GetBytes(record.RecordId);
        var aad = new byte[sizeof(int) + type.Length + sizeof(int) + id.Length + sizeof(int)];
        var offset = 0;
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset), type.Length);
        offset += sizeof(int);
        type.CopyTo(aad, offset);
        offset += type.Length;
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset), id.Length);
        offset += sizeof(int);
        id.CopyTo(aad, offset);
        offset += id.Length;
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset), record.RecordVersion);
        return new(VersionedEnvelopeEncryptionService.CurrentVersion, SHA256.HashData(aad));
    }

    private static string ComputeInventoryHash(IReadOnlyList<KeyRotationRecord> records)
    {
        var canonical = string.Join("\n", records
            .OrderBy(record => record.RecordId, StringComparer.Ordinal)
            .Select(record => $"{record.RecordType.Length}:{record.RecordType}|{record.RecordId.Length}:{record.RecordId}|{record.RecordVersion}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static KeyRotationPlan Reject(string reasonCode, KeyRotationArtifact artifact)
        => new(false, reasonCode, Array.Empty<KeyRotationWrite>(), artifact with
        {
            Status = RecoverableKeyRotationStatus.Failed,
            FailureReasonCode = reasonCode
        });

    private static KeyRotationPlan RejectAndClear(
        string reasonCode,
        KeyRotationArtifact artifact,
        IEnumerable<KeyRotationWrite> writes)
    {
        foreach (var write in writes)
            CryptographicOperations.ZeroMemory(write.EncodedEnvelope);
        return Reject(reasonCode, artifact);
    }

    private static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static bool IsSha256(string value)
        => !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);
}

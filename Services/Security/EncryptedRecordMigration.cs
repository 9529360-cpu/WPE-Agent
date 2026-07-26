using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Security;

public enum EncryptedRecordKind
{
    Unknown,
    Envelope
}

public enum LegacyFormat
{
    None,
    WindowsDpapiCurrentUser
}

public sealed class EncryptedRecordCodecException : CryptographicException
{
    public EncryptedRecordCodecException(string reasonCode)
        : base("Encrypted record codec rejected the input.")
        => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

public static class EncryptedRecordCodec
{
    private static readonly byte[] Magic = "WPEE"u8.ToArray();
    private const byte CodecVersion = 1;
    private const int MaximumFieldLength = 64 * 1024 * 1024;

    public static EncryptedRecordKind Identify(ReadOnlySpan<byte> storedValue)
        => storedValue.StartsWith(Magic)
            ? EncryptedRecordKind.Envelope
            : EncryptedRecordKind.Unknown;

    public static byte[] Encode(EncryptedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var algorithm = Encoding.UTF8.GetBytes(envelope.Algorithm);
        var keyId = Encoding.UTF8.GetBytes(envelope.KeyId);
        var size = Magic.Length + 1 + sizeof(int) +
                   EncodedSize(algorithm) + EncodedSize(keyId) +
                   EncodedSize(envelope.WrappedDek) + EncodedSize(envelope.Nonce) +
                   EncodedSize(envelope.AuthenticationTag) + EncodedSize(envelope.Ciphertext);
        var output = new byte[size];
        var offset = 0;
        Magic.CopyTo(output, offset);
        offset += Magic.Length;
        output[offset++] = CodecVersion;
        WriteInt(output, ref offset, envelope.Version);
        WriteField(output, ref offset, algorithm);
        WriteField(output, ref offset, keyId);
        WriteField(output, ref offset, envelope.WrappedDek);
        WriteField(output, ref offset, envelope.Nonce);
        WriteField(output, ref offset, envelope.AuthenticationTag);
        WriteField(output, ref offset, envelope.Ciphertext);
        return output;
    }

    public static EncryptedEnvelope Decode(ReadOnlySpan<byte> storedValue)
    {
        if (!storedValue.StartsWith(Magic))
            throw new EncryptedRecordCodecException("record.envelope-magic-missing");

        var offset = Magic.Length;
        if (offset >= storedValue.Length || storedValue[offset++] != CodecVersion)
            throw new EncryptedRecordCodecException("record.codec-version-unsupported");

        var envelopeVersion = ReadInt(storedValue, ref offset);
        var algorithm = Encoding.UTF8.GetString(ReadField(storedValue, ref offset));
        var keyId = Encoding.UTF8.GetString(ReadField(storedValue, ref offset));
        var wrappedDek = ReadField(storedValue, ref offset).ToArray();
        var nonce = ReadField(storedValue, ref offset).ToArray();
        var tag = ReadField(storedValue, ref offset).ToArray();
        var ciphertext = ReadField(storedValue, ref offset).ToArray();
        if (offset != storedValue.Length)
            throw new EncryptedRecordCodecException("record.trailing-data-rejected");

        return new(envelopeVersion, algorithm, keyId, wrappedDek, nonce, tag, ciphertext);
    }

    private static int EncodedSize(byte[] value)
        => checked(sizeof(int) + value.Length);

    private static void WriteField(byte[] output, ref int offset, byte[] value)
    {
        WriteInt(output, ref offset, value.Length);
        value.CopyTo(output, offset);
        offset += value.Length;
    }

    private static void WriteInt(byte[] output, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(offset, sizeof(int)), value);
        offset += sizeof(int);
    }

    private static int ReadInt(ReadOnlySpan<byte> input, ref int offset)
    {
        if (input.Length - offset < sizeof(int))
            throw new EncryptedRecordCodecException("record.truncated");
        var value = BinaryPrimitives.ReadInt32BigEndian(input.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static ReadOnlySpan<byte> ReadField(ReadOnlySpan<byte> input, ref int offset)
    {
        var length = ReadInt(input, ref offset);
        if (length < 0 || length > MaximumFieldLength || input.Length - offset < length)
            throw new EncryptedRecordCodecException("record.field-length-invalid");
        var field = input.Slice(offset, length);
        offset += length;
        return field;
    }
}

public sealed record RecordMigrationInput(
    string RecordType,
    string RecordId,
    int RecordVersion,
    byte[] StoredValue,
    string ExpectedStoredSha256,
    LegacyFormat LegacyFormat = LegacyFormat.None);

public sealed record RecordMigrationCheckpoint(
    string MigrationId,
    int TargetEnvelopeVersion,
    IReadOnlyDictionary<string, string> CompletedRecordHashes);

public sealed record RecordMigrationWrite(string RecordId, byte[] EncodedEnvelope, string EncodedSha256);

public sealed record RecordMigrationBatchPlan(
    bool Allowed,
    string ReasonCode,
    IReadOnlyList<RecordMigrationWrite> Writes,
    RecordMigrationCheckpoint Checkpoint);

public sealed class EncryptedRecordMigrationPlanner
{
    private readonly VersionedEnvelopeEncryptionService _encryption;

    public EncryptedRecordMigrationPlanner(VersionedEnvelopeEncryptionService encryption)
        => _encryption = encryption ?? throw new ArgumentNullException(nameof(encryption));

    public RecordMigrationBatchPlan PlanBatch(
        IReadOnlyList<RecordMigrationInput> records,
        int batchSize,
        RecordMigrationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (batchSize <= 0)
            return Reject("migration.batch-size-invalid", checkpoint);
        if (string.IsNullOrWhiteSpace(checkpoint.MigrationId) ||
            checkpoint.TargetEnvelopeVersion != VersionedEnvelopeEncryptionService.CurrentVersion)
            return Reject("migration.checkpoint-invalid", checkpoint);
        if (records.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count() != records.Count)
            return Reject("migration.duplicate-record-id", checkpoint);

        var completed = new Dictionary<string, string>(checkpoint.CompletedRecordHashes, StringComparer.Ordinal);
        var legacy = new List<RecordMigrationInput>();
        var envelopeVersions = new HashSet<int>();

        foreach (var record in records.OrderBy(record => record.RecordId, StringComparer.Ordinal))
        {
            var validation = ValidateRecord(record, completed, envelopeVersions);
            if (validation is not null)
                return Reject(validation, checkpoint);
            if (EncryptedRecordCodec.Identify(record.StoredValue) == EncryptedRecordKind.Unknown)
                legacy.Add(record);
        }

        if (envelopeVersions.Any(version => version != checkpoint.TargetEnvelopeVersion) || envelopeVersions.Count > 1)
            return Reject("migration.mixed-envelope-versions", checkpoint);

        var writes = new List<RecordMigrationWrite>();
        try
        {
            foreach (var record in legacy.Take(batchSize))
            {
                var aad = new EnvelopeAssociatedData(record.RecordType, record.RecordId, record.RecordVersion);
                var encoded = EncryptedRecordCodec.Encode(_encryption.Encrypt(record.StoredValue, aad));
                var hash = Sha256(encoded);
                writes.Add(new(record.RecordId, encoded, hash));
                completed[record.RecordId] = hash;
            }

            return new(
                true,
                writes.Count == 0 ? "migration.batch-idempotent" : "migration.batch-ready",
                writes,
                checkpoint with { CompletedRecordHashes = completed });
        }
        catch
        {
            foreach (var write in writes)
                CryptographicOperations.ZeroMemory(write.EncodedEnvelope);
            return Reject("migration.envelope-conversion-failed", checkpoint);
        }
    }

    public static StoragePolicyDecision CanRollback(BackupRestoreContext backup)
    {
        var decision = EncryptedBackupPolicy.CanRestore(backup);
        return decision.Allowed
            ? StoragePolicyDecision.Allow("migration.rollback-verified-backup")
            : StoragePolicyDecision.Deny("migration.rollback-backup-unverified");
    }

    private string? ValidateRecord(
        RecordMigrationInput record,
        IDictionary<string, string> completed,
        ISet<int> envelopeVersions)
    {
        if (string.IsNullOrWhiteSpace(record.RecordType) || string.IsNullOrWhiteSpace(record.RecordId) ||
            record.RecordVersion <= 0 || record.StoredValue is null || !IsSha256(record.ExpectedStoredSha256))
            return "migration.record-metadata-invalid";
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(record.ExpectedStoredSha256),
                SHA256.HashData(record.StoredValue)))
            return "migration.record-checksum-failed";

        var kind = EncryptedRecordCodec.Identify(record.StoredValue);
        if (completed.TryGetValue(record.RecordId, out var completedHash))
        {
            if (kind != EncryptedRecordKind.Envelope ||
                !string.Equals(completedHash, record.ExpectedStoredSha256, StringComparison.OrdinalIgnoreCase))
                return "migration.checkpoint-record-mismatch";
        }

        if (kind == EncryptedRecordKind.Unknown)
        {
            if (LooksLikeDamagedEnvelope(record.StoredValue))
                return "migration.envelope-magic-invalid";
            return record.LegacyFormat == LegacyFormat.WindowsDpapiCurrentUser
                ? null
                : "migration.legacy-format-required";
        }
        if (record.LegacyFormat != LegacyFormat.None)
            return "migration.legacy-format-conflict";

        try
        {
            var envelope = EncryptedRecordCodec.Decode(record.StoredValue);
            envelopeVersions.Add(envelope.Version);
            if (envelope.Version != VersionedEnvelopeEncryptionService.CurrentVersion)
                return null;
            var plaintext = _encryption.Decrypt(
                envelope,
                new EnvelopeAssociatedData(record.RecordType, record.RecordId, record.RecordVersion));
            CryptographicOperations.ZeroMemory(plaintext);
            return null;
        }
        catch
        {
            return "migration.envelope-validation-failed";
        }
    }

    private static RecordMigrationBatchPlan Reject(string reasonCode, RecordMigrationCheckpoint checkpoint)
        => new(false, reasonCode, Array.Empty<RecordMigrationWrite>(), checkpoint);

    private static string Sha256(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value));

    private static bool IsSha256(string value)
        => !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool LooksLikeDamagedEnvelope(ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> prefix="WPE"u8;
        var length=Math.Min(value.Length,prefix.Length);
        return length>0&&value[..length].SequenceEqual(prefix[..length]);
    }
}

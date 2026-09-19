using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Security;

namespace 币安量化机器人.Services.Backup;

internal sealed record RuntimeStateSecurityStorageRestorePlan(
    BackupRestoreContext PolicyContext,
    BackupRestoreDrillArtifact Artifact,
    IReadOnlyList<BackupEnvelopeInventory> Inventory,
    int RecordCount);

internal sealed class RuntimeStateSecurityStorageRestoreEvidence
{
    private const int MaximumRecords = 10_000;
    private const int BackupKeyVersion = 1;
    private readonly IPlatformKeyProtector _keyProtector;
    private readonly VersionedEnvelopeEncryptionService _encryption;
    private readonly Func<DateTimeOffset> _utcNow;

    public RuntimeStateSecurityStorageRestoreEvidence(
        IPlatformKeyProtector keyProtector,
        Func<DateTimeOffset> utcNow)
    {
        _keyProtector = keyProtector ?? throw new ArgumentNullException(nameof(keyProtector));
        _encryption = new VersionedEnvelopeEncryptionService(_keyProtector);
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public async Task<RuntimeStateSecurityStorageRestorePlan?> PrepareAsync(
        RuntimeStateBackupDescriptorV1 descriptor,
        string stagedDataDirectory,
        string restoreId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.Items.Any(x => string.Equals(x.LogicalName, "security-storage.db", StringComparison.Ordinal)))
            return null;

        var stagedDatabase = Path.Combine(
            Path.GetFullPath(stagedDataDirectory),
            "security-storage.db");
        var verified = await VerifyInventoryAsync(stagedDatabase, cancellationToken).ConfigureAwait(false);

        // An initialized but empty secure store contains no encrypted records to certify.
        // The database bytes are still covered by the runtime-state descriptor verification.
        if (verified.RecordCount == 0)
            return null;

        var now = _utcNow().ToUniversalTime();
        var recoveryKey = new StorageKeyDescriptor(
            descriptor.Manifest.KeyReference.KeyId,
            descriptor.Manifest.KeyReference.Version,
            StorageKeyState.RecoveryOnly,
            PlatformKeyStoreKind.WindowsDpapiCurrentUser,
            descriptor.Manifest.CreatedAtUtc);

        var observedFileHashes = descriptor.Manifest.Files.ToDictionary(
            x => x.LogicalName,
            x => x.Sha256,
            StringComparer.Ordinal);
        var policyContext = new BackupRestoreContext(
            descriptor.Manifest,
            recoveryKey,
            true,
            _keyProtector.IsAvailable,
            true,
            observedFileHashes);
        var target = "runtime-state-security-storage:" + SafeId(restoreId);
        var request = new BackupRestoreDrillRequest(
            descriptor.Manifest.BackupId,
            descriptor.Manifest,
            true,
            recoveryKey,
            observedFileHashes,
            verified.Inventory,
            verified.AvailableKeys,
            verified.RecordCount,
            descriptor.Manifest.CreatedAtUtc,
            now.AddMinutes(15),
            target,
            true,
            restoreId);

        var plan = BackupRestoreDrillPlanner.CreatePlan(request, now);
        if (!plan.Allowed || plan.Artifact is null)
            throw new InvalidDataException(
                $"Security-storage restore evidence plan was rejected: {plan.ReasonCode}");

        return new(policyContext, plan.Artifact, verified.Inventory, verified.RecordCount);
    }

    public async Task<SecurityStorageRuntimeStatus?> CommitAsync(
        RuntimeStateSecurityStorageRestorePlan? plan,
        string activeDataDirectory,
        CancellationToken cancellationToken)
    {
        if (plan is null) return null;

        var databasePath = Path.Combine(
            Path.GetFullPath(activeDataDirectory),
            "security-storage.db");
        var verified = await VerifyInventoryAsync(databasePath, cancellationToken).ConfigureAwait(false);
        if (verified.RecordCount != plan.RecordCount ||
            !string.Equals(
                BackupRestoreDrillPlanner.ComputeInventoryHash(verified.Inventory),
                plan.Artifact.InventorySha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Activated security-storage inventory does not match the verified staging generation.");

        var evidence = new BackupRestoreDrillEvidence(
            plan.Artifact,
            verified.Inventory,
            verified.RecordCount,
            plan.Artifact.RestoreTarget,
            true,
            true,
            true,
            true);

        var storage = new SqliteSecurityStorage(databasePath, _utcNow);
        var runtime = new SecurityStorageRuntime(storage, _keyProtector, storage, storage);
        var status = await runtime.RestoreAsync(
            plan.PolicyContext,
            evidence,
            _utcNow().ToUniversalTime(),
            cancellationToken).ConfigureAwait(false);

        if (status.State != SecurityStorageRuntimeState.Committed ||
            !string.Equals(
                status.ReasonCode,
                "security-storage.restore-committed",
                StringComparison.Ordinal) ||
            !string.Equals(
                status.EvidenceSha256,
                plan.Artifact.PlanSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Security-storage restore evidence commit failed: {status.ReasonCode}");

        return status;
    }

    private async Task<VerifiedInventory> VerifyInventoryAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
            throw new InvalidDataException("Restored security-storage database is missing.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='security_storage_records';";
            var count = Convert.ToInt32(
                await schema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (count != 1)
                throw new InvalidDataException(
                    "Restored security-storage database is missing its authoritative record table.");
        }

        var records = new List<VerifiedRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT record_type,record_id,record_version,stored_value,expected_sha256,legacy_format " +
            "FROM security_storage_records ORDER BY record_type,record_id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", MaximumRecords + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (records.Count >= MaximumRecords)
                throw new InvalidDataException("Restored security-storage record inventory exceeds the bounded limit.");

            var recordType = reader.GetString(0);
            var recordId = reader.GetString(1);
            var recordVersion = reader.GetInt32(2);
            var storedValue = (byte[])reader[3];
            var expectedSha256 = reader.GetString(4);
            var legacyRaw = reader.GetInt32(5);

            if (string.IsNullOrWhiteSpace(recordType) ||
                string.IsNullOrWhiteSpace(recordId) ||
                recordVersion <= 0 ||
                !IsSha256(expectedSha256) ||
                !Enum.IsDefined(typeof(LegacyFormat), legacyRaw) ||
                (LegacyFormat)legacyRaw != LegacyFormat.None)
                throw new InvalidDataException("Restored security-storage record metadata is invalid.");

            var actualHash = SHA256.HashData(storedValue);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedSha256),
                    actualHash))
                throw new InvalidDataException("Restored security-storage record hash is invalid.");

            if (EncryptedRecordCodec.Identify(storedValue) != EncryptedRecordKind.Envelope)
                throw new InvalidDataException("Restored security-storage record is not an encrypted envelope.");

            var envelope = EncryptedRecordCodec.Decode(storedValue);
            var plaintext = _encryption.Decrypt(
                envelope,
                new EnvelopeAssociatedData(recordType, recordId, recordVersion));
            CryptographicOperations.ZeroMemory(plaintext);

            records.Add(new(
                recordType,
                recordId,
                recordVersion,
                expectedSha256.ToUpperInvariant(),
                envelope.Version,
                envelope.KeyId));
        }

        var inventory = records
            .GroupBy(x => new { x.EnvelopeVersion, x.KeyId })
            .OrderBy(x => x.Key.EnvelopeVersion)
            .ThenBy(x => x.Key.KeyId, StringComparer.Ordinal)
            .Select(group => new BackupEnvelopeInventory(
                group.Key.EnvelopeVersion,
                new StorageKeyReference(group.Key.KeyId, BackupKeyVersion),
                group.Count(),
                ComputeRecordSetSha256(group)))
            .ToArray();

        var keys = inventory
            .Select(x => x.KeyReference)
            .Distinct()
            .ToArray();

        return new(records.Count, inventory, keys);
    }

    private static string ComputeRecordSetSha256(IEnumerable<VerifiedRecord> records)
    {
        var canonical = string.Join("\n", records
            .OrderBy(x => x.RecordType, StringComparer.Ordinal)
            .ThenBy(x => x.RecordId, StringComparer.Ordinal)
            .Select(x =>
                $"{x.RecordType.Length}:{x.RecordType}|{x.RecordId.Length}:{x.RecordId}|" +
                $"{x.RecordVersion}|{x.ExpectedSha256}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string SafeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 64 ||
            value.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            throw new InvalidDataException("Runtime-state restore id is invalid.");
        return value;
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record VerifiedRecord(
        string RecordType,
        string RecordId,
        int RecordVersion,
        string ExpectedSha256,
        int EnvelopeVersion,
        string KeyId);

    private sealed record VerifiedInventory(
        int RecordCount,
        IReadOnlyList<BackupEnvelopeInventory> Inventory,
        IReadOnlyList<StorageKeyReference> AvailableKeys);
}

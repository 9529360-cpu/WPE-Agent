using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Security;

namespace 币安量化机器人.Services.Backup;

public sealed class RuntimeStateBackupService
{
    public const int ChunkSizeBytes = 4 * 1024 * 1024;
    public const string DescriptorFileName = "backup-descriptor-v1.json";
    public const string AuthenticationFileName = "manifest-auth-v1.wpeenv.json";

    private static readonly string[] AuthoritativeDatabases =
    [
        "agent.db",
        "trading.db",
        "notification-outbox.db",
        "security-storage.db"
    ];

    private static readonly string[] AuthoritativeFiles =
    [
        "agent-settings.json",
        "appsettings.json",
        "local-accounts.json",
        "device-license.dat",
        "ui-settings.json",
        "ui-preferences.json",
        "llm-calls.jsonl"
    ];

    private static readonly HashSet<string> LegacyPlaintextSecretFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "TelegramBotToken",
        "TelegramChatId",
        "WhatsAppAccessToken",
        "WhatsAppDestination",
        "WhatsAppPhoneNumberId"
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true
    };

    private readonly AppDataLayout _layout;
    private readonly IPlatformKeyProtector _keyProtector;
    private readonly VersionedEnvelopeEncryptionService _encryption;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _deviceCode;
    private readonly string _productVersion;

    public RuntimeStateBackupService(
        AppDataLayout layout,
        IPlatformKeyProtector? keyProtector = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? deviceCode = null,
        string? productVersion = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _keyProtector = keyProtector ?? new WindowsCurrentUserKeyProtector();
        _encryption = new VersionedEnvelopeEncryptionService(_keyProtector);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _deviceCode = deviceCode ?? DeviceLicenseService.GetCurrentDeviceCode;
        _productVersion = string.IsNullOrWhiteSpace(productVersion)
            ? typeof(RuntimeStateBackupService).Assembly.GetName().Version?.ToString() ?? "unknown"
            : productVersion;
    }

    public async Task<RuntimeStateBackupResult> CreateAsync(string destinationRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new ArgumentException("Backup destination root is required.", nameof(destinationRoot));
        if (!_keyProtector.IsAvailable)
            throw new InvalidOperationException("The platform backup key protector is unavailable.");

        var destination = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(destination);
        var now = _utcNow().ToUniversalTime();
        var backupId = $"wpe-state-{now:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var finalDirectory = Path.Combine(destination, backupId);
        var stagingDirectory = Path.Combine(destination, "." + backupId + ".tmp");
        if (Directory.Exists(finalDirectory) || Directory.Exists(stagingDirectory))
            throw new InvalidOperationException("Backup destination already exists.");

        var leasePath = _layout.RuntimeFile(DataRootMaintenanceLease.LeaseFileName);
        using var maintenanceLease = DataRootMaintenanceLease.AcquireExclusiveMaintenanceLease(leasePath);

        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "payload"));
        try
        {
            EnsureLegacySettingsContainNoPlaintextNotificationSecrets();

            var items = new List<RuntimeStateBackupItemV1>();
            var encryptedFiles = new List<EncryptedBackupFile>();
            var ordinal = 0;

            foreach (var database in AuthoritativeDatabases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = _layout.DataFile(database);
                if (!File.Exists(source)) continue;
                await PrepareSqliteSnapshotSourceAsync(source, cancellationToken).ConfigureAwait(false);
                items.Add(await EncryptSourceAsync(
                    source, database, RuntimeStateBackupItemKind.SqliteDatabase, backupId,
                    stagingDirectory, ordinal++, encryptedFiles, cancellationToken).ConfigureAwait(false));
            }

            foreach (var file in AuthoritativeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = _layout.DataFile(file);
                if (!File.Exists(source)) continue;
                items.Add(await EncryptSourceAsync(
                    source, file, RuntimeStateBackupItemKind.File, backupId,
                    stagingDirectory, ordinal++, encryptedFiles, cancellationToken).ConfigureAwait(false));
            }

            if (items.Count == 0)
                throw new InvalidOperationException("No authoritative WPE state exists to back up.");

            var manifest = new EncryptedBackupManifest(
                EncryptedBackupPolicy.CurrentFormatVersion,
                backupId,
                now,
                new StorageKeyReference(_keyProtector.KeyId, 1),
                EncryptedBackupPolicy.RequiredCipherSuite,
                encryptedFiles,
                string.Empty);
            manifest = manifest with { ManifestSha256 = EncryptedBackupPolicy.ComputeManifestHash(manifest) };

            var itemsSha256 = ComputeItemsSha256(items);
            var deviceSha256 = HashText(_deviceCode());
            var attestation = Encoding.UTF8.GetBytes(
                $"{RuntimeStateBackupDescriptorV1.CurrentSchema}|{backupId}|{_productVersion}|{deviceSha256}|{itemsSha256}|{manifest.ManifestSha256}");
            var authenticationEnvelope = _encryption.Encrypt(
                attestation,
                new EnvelopeAssociatedData("runtime-state-backup-manifest", backupId, 1));
            var authenticationBytes = JsonSerializer.SerializeToUtf8Bytes(authenticationEnvelope, Json);
            var authenticationPath = Path.Combine(stagingDirectory, AuthenticationFileName);
            await File.WriteAllBytesAsync(authenticationPath, authenticationBytes, cancellationToken).ConfigureAwait(false);

            var descriptor = new RuntimeStateBackupDescriptorV1(
                RuntimeStateBackupDescriptorV1.CurrentSchema,
                _productVersion,
                deviceSha256,
                itemsSha256,
                manifest,
                items,
                new(AuthenticationFileName, authenticationBytes.LongLength, Hash(authenticationBytes)));
            var descriptorPath = Path.Combine(stagingDirectory, DescriptorFileName);
            await File.WriteAllBytesAsync(
                descriptorPath,
                JsonSerializer.SerializeToUtf8Bytes(descriptor, Json),
                cancellationToken).ConfigureAwait(false);

            Directory.Move(stagingDirectory, finalDirectory);
            return new(backupId, finalDirectory, descriptor);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                try { Directory.Delete(stagingDirectory, true); } catch { }
            }
            throw;
        }
    }

    private async Task<RuntimeStateBackupItemV1> EncryptSourceAsync(
        string sourcePath,
        string logicalName,
        RuntimeStateBackupItemKind kind,
        string backupId,
        string stagingDirectory,
        int ordinal,
        ICollection<EncryptedBackupFile> encryptedFiles,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkSizeBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var plaintextHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunks = new List<string>();
        var buffer = new byte[ChunkSizeBytes];
        long plaintextLength = 0;
        var chunkIndex = 0;
        var wroteChunk = false;

        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0 && wroteChunk) break;

                var plaintext = buffer.AsSpan(0, read);
                plaintextHash.AppendData(plaintext);
                plaintextLength += read;
                var envelope = _encryption.Encrypt(
                    plaintext,
                    new EnvelopeAssociatedData(
                        "runtime-state-backup-chunk",
                        $"{backupId}:{logicalName}:{chunkIndex}",
                        1));
                var encryptedBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
                var relative = $"payload/chunk-{ordinal:D2}-{chunkIndex:D6}.wpeenv.json";
                var fullPath = Path.Combine(stagingDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                await File.WriteAllBytesAsync(fullPath, encryptedBytes, cancellationToken).ConfigureAwait(false);
                chunks.Add(relative);
                encryptedFiles.Add(new(relative, encryptedBytes.LongLength, Hash(encryptedBytes)));
                CryptographicOperations.ZeroMemory(plaintext);
                wroteChunk = true;
                chunkIndex++;

                if (read == 0) break;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        return new(
            logicalName,
            kind,
            plaintextLength,
            Convert.ToHexString(plaintextHash.GetHashAndReset()),
            chunks);
    }

    private async Task PrepareSqliteSnapshotSourceAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetInt32(0) != 0)
                throw new InvalidOperationException($"SQLite checkpoint is busy for {Path.GetFileName(sourcePath)}.");
        }

        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite integrity check failed for {Path.GetFileName(sourcePath)}.");
        }
    }

    private void EnsureLegacySettingsContainNoPlaintextNotificationSecrets()
    {
        var path = _layout.DataFile("appsettings.json");
        if (!File.Exists(path)) return;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (ContainsLegacyPlaintextSecretField(document.RootElement))
            throw new InvalidDataException("Legacy plaintext notification credentials must be migrated before backup.");
    }

    private static bool ContainsLegacyPlaintextSecretField(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (LegacyPlaintextSecretFields.Contains(property.Name) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    return true;
                if (ContainsLegacyPlaintextSecretField(property.Value)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (ContainsLegacyPlaintextSecretField(item)) return true;
        }
        return false;
    }

    public static string ComputeItemsSha256(IReadOnlyList<RuntimeStateBackupItemV1> items)
    {
        var canonical = string.Join("\n", items
            .OrderBy(item => item.LogicalName, StringComparer.Ordinal)
            .Select(item =>
                $"{item.LogicalName}|{item.Kind}|{item.PlaintextLength}|{item.PlaintextSha256.ToUpperInvariant()}|" +
                string.Join(",", item.EncryptedChunks)));
        return HashText(canonical);
    }

    private static string Hash(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value));

    private static string HashText(string value)
        => Hash(Encoding.UTF8.GetBytes(value));
}

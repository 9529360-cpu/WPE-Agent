using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Security;

namespace 币安量化机器人.Services.Backup;

public sealed record RuntimeStateBackupVerificationResult(
    string BackupId,
    string StagingDataDirectory,
    RuntimeStateBackupDescriptorV1 Descriptor,
    DateTimeOffset VerifiedAtUtc);

public sealed class RuntimeStateBackupVerifier
{
    private const long MaximumDescriptorBytes = 8L * 1024 * 1024;
    private const long MaximumAuthenticationBytes = 64L * 1024;
    private const long MaximumEnvelopeBytes = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new();

    private readonly IPlatformKeyProtector _keyProtector;
    private readonly VersionedEnvelopeEncryptionService _encryption;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _deviceCode;
    private readonly string _productVersion;

    public RuntimeStateBackupVerifier(
        IPlatformKeyProtector? keyProtector = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? deviceCode = null,
        string? productVersion = null)
    {
        _keyProtector = keyProtector ?? new WindowsCurrentUserKeyProtector();
        _encryption = new VersionedEnvelopeEncryptionService(_keyProtector);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _deviceCode = deviceCode ?? DeviceLicenseService.GetCurrentDeviceCode;
        _productVersion = string.IsNullOrWhiteSpace(productVersion)
            ? typeof(RuntimeStateBackupVerifier).Assembly.GetName().Version?.ToString() ?? "unknown"
            : productVersion;
    }

    public async Task<RuntimeStateBackupVerificationResult> VerifyToStagingAsync(
        string backupDirectory,
        string stagingDataDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
            throw new ArgumentException("Backup directory is required.", nameof(backupDirectory));
        if (string.IsNullOrWhiteSpace(stagingDataDirectory))
            throw new ArgumentException("Restore staging directory is required.", nameof(stagingDataDirectory));
        if (!_keyProtector.IsAvailable)
            throw new InvalidOperationException("The platform backup key protector is unavailable.");

        var backupRoot = Path.GetFullPath(backupDirectory);
        if (!Directory.Exists(backupRoot))
            throw new DirectoryNotFoundException("The runtime-state backup directory does not exist.");

        var stagingRoot = Path.GetFullPath(stagingDataDirectory);
        if (Directory.Exists(stagingRoot) || File.Exists(stagingRoot))
            throw new InvalidOperationException("Restore staging directory must not already exist.");

        try
        {
            var descriptorPath = Path.Combine(backupRoot, RuntimeStateBackupService.DescriptorFileName);
            var descriptorBytes = await ReadBoundedAsync(
                descriptorPath, MaximumDescriptorBytes, cancellationToken).ConfigureAwait(false);
            var descriptor = JsonSerializer.Deserialize<RuntimeStateBackupDescriptorV1>(descriptorBytes, Json)
                ?? throw new InvalidDataException("Runtime-state backup descriptor is invalid.");

            ValidateDescriptor(descriptor);
            ValidateInventory(descriptor);

            if (!string.Equals(descriptor.ProductVersion, _productVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Runtime-state backup product version is incompatible.");

            var expectedDeviceHash = HashText(_deviceCode());
            if (!FixedHexEquals(descriptor.DeviceCodeSha256, expectedDeviceHash))
                throw new InvalidDataException("Runtime-state backup device identity does not match this device.");

            var computedItemsHash = RuntimeStateBackupService.ComputeItemsSha256(descriptor.Items);
            if (!FixedHexEquals(descriptor.ItemsSha256, computedItemsHash))
                throw new InvalidDataException("Runtime-state backup item inventory failed integrity validation.");

            var authenticationPath = ResolveInside(backupRoot, descriptor.Authentication.FileName);
            var authenticationBytes = await ReadBoundedAsync(
                authenticationPath, MaximumAuthenticationBytes, cancellationToken).ConfigureAwait(false);
            if (descriptor.Authentication.Length != authenticationBytes.LongLength ||
                !FixedHexEquals(descriptor.Authentication.Sha256, Hash(authenticationBytes)))
                throw new InvalidDataException("Runtime-state backup authentication file failed integrity validation.");

            var authenticationEnvelope = JsonSerializer.Deserialize<EncryptedEnvelope>(authenticationBytes, Json)
                ?? throw new InvalidDataException("Runtime-state backup authentication envelope is invalid.");
            var authenticationPlaintext = _encryption.Decrypt(
                authenticationEnvelope,
                new EnvelopeAssociatedData(
                    "runtime-state-backup-manifest",
                    descriptor.Manifest.BackupId,
                    1));
            try
            {
                var expectedAttestation = Encoding.UTF8.GetBytes(
                    $"{RuntimeStateBackupDescriptorV1.CurrentSchema}|{descriptor.Manifest.BackupId}|{descriptor.ProductVersion}|" +
                    $"{descriptor.DeviceCodeSha256}|{descriptor.ItemsSha256}|{descriptor.Manifest.ManifestSha256}");
                if (!CryptographicOperations.FixedTimeEquals(authenticationPlaintext, expectedAttestation))
                    throw new InvalidDataException("Runtime-state backup manifest attestation does not match the descriptor.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(authenticationPlaintext);
            }

            var observedHashes = await ObserveManifestAsync(
                backupRoot, descriptor.Manifest, cancellationToken).ConfigureAwait(false);
            ValidateExactBackupFileSet(backupRoot, descriptor);

            var recoveryKey = new StorageKeyDescriptor(
                _keyProtector.KeyId,
                1,
                StorageKeyState.Active,
                PlatformKeyStoreKind.WindowsDpapiCurrentUser,
                descriptor.Manifest.CreatedAtUtc);
            var policy = EncryptedBackupPolicy.CanRestore(new(
                descriptor.Manifest,
                recoveryKey,
                true,
                true,
                true,
                observedHashes));
            if (!policy.Allowed)
                throw new InvalidDataException($"Runtime-state backup restore policy rejected the backup: {policy.ReasonCode}");

            Directory.CreateDirectory(stagingRoot);
            foreach (var item in descriptor.Items.OrderBy(x => x.LogicalName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RestoreItemAsync(
                    backupRoot,
                    stagingRoot,
                    descriptor.Manifest.BackupId,
                    item,
                    cancellationToken).ConfigureAwait(false);
            }

            return new(
                descriptor.Manifest.BackupId,
                stagingRoot,
                descriptor,
                _utcNow().ToUniversalTime());
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
            {
                try { Directory.Delete(stagingRoot, true); } catch { }
            }
            throw;
        }
    }

    internal async Task VerifyPlaintextDataDirectoryAsync(
        RuntimeStateBackupDescriptorV1 descriptor,
        string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Directory.Exists(dataDirectory))
            throw new InvalidDataException("Restored runtime-state data directory is missing.");

        var root = Path.GetFullPath(dataDirectory);
        var expected = descriptor.Items
            .Select(x => x.LogicalName)
            .ToHashSet(StringComparer.Ordinal);
        var observed = Directory
            .EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(observed))
            throw new InvalidDataException("Restored runtime-state data file inventory does not match the backup.");

        foreach (var item in descriptor.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveInside(root, item.LogicalName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != item.PlaintextLength)
                throw new InvalidDataException("Restored runtime-state item length does not match the backup.");
            var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (!FixedHexEquals(hash, item.PlaintextSha256))
                throw new InvalidDataException("Restored runtime-state item hash does not match the backup.");
            if (item.Kind == RuntimeStateBackupItemKind.SqliteDatabase)
                await VerifySqliteAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateDescriptor(RuntimeStateBackupDescriptorV1 descriptor)
    {
        if (!string.Equals(descriptor.Schema, RuntimeStateBackupDescriptorV1.CurrentSchema, StringComparison.Ordinal))
            throw new InvalidDataException("Runtime-state backup schema is unsupported.");
        if (string.IsNullOrWhiteSpace(descriptor.ProductVersion) ||
            !IsSha256(descriptor.DeviceCodeSha256) ||
            !IsSha256(descriptor.ItemsSha256))
            throw new InvalidDataException("Runtime-state backup descriptor metadata is invalid.");
        if (descriptor.Manifest is null ||
            string.IsNullOrWhiteSpace(descriptor.Manifest.BackupId) ||
            descriptor.Manifest.KeyReference is null ||
            descriptor.Manifest.Files is null ||
            descriptor.Manifest.Files.Count == 0)
            throw new InvalidDataException("Runtime-state backup manifest identity or inventory is missing.");
        if (descriptor.Authentication is null ||
            !string.Equals(
                descriptor.Authentication.FileName,
                RuntimeStateBackupService.AuthenticationFileName,
                StringComparison.Ordinal) ||
            descriptor.Authentication.Length <= 0 ||
            !IsSha256(descriptor.Authentication.Sha256))
            throw new InvalidDataException("Runtime-state backup authentication metadata is invalid.");
        if (descriptor.Items is null || descriptor.Items.Count == 0)
            throw new InvalidDataException("Runtime-state backup contains no authoritative items.");
    }

    private static void ValidateInventory(RuntimeStateBackupDescriptorV1 descriptor)
    {
        var logicalNames = new HashSet<string>(StringComparer.Ordinal);
        var chunks = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in descriptor.Items)
        {
            if (string.IsNullOrWhiteSpace(item.LogicalName) ||
                !logicalNames.Add(item.LogicalName) ||
                item.PlaintextLength < 0 ||
                !IsSha256(item.PlaintextSha256) ||
                item.EncryptedChunks is null ||
                item.EncryptedChunks.Count == 0)
                throw new InvalidDataException("Runtime-state backup item metadata is invalid.");

            var expectedKind = RuntimeStateBackupInventoryV1.KindFor(item.LogicalName);
            if (expectedKind is null || expectedKind.Value != item.Kind)
                throw new InvalidDataException("Runtime-state backup contains an unsupported authoritative item.");

            foreach (var relative in item.EncryptedChunks)
            {
                if (!IsSafeRelativePath(relative) ||
                    !relative.StartsWith("payload/", StringComparison.Ordinal) ||
                    !chunks.Add(relative))
                    throw new InvalidDataException("Runtime-state backup chunk inventory is invalid.");
            }
        }

        var manifestNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in descriptor.Manifest.Files)
        {
            if (!IsSafeRelativePath(file.LogicalName) || !manifestNames.Add(file.LogicalName))
                throw new InvalidDataException("Runtime-state backup manifest file inventory is invalid.");
        }

        if (!chunks.SetEquals(manifestNames))
            throw new InvalidDataException("Runtime-state backup manifest and item chunk inventories do not match.");
    }

    private async Task<IReadOnlyDictionary<string, string>> ObserveManifestAsync(
        string backupRoot,
        EncryptedBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var observed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            var fullPath = ResolveInside(backupRoot, file.LogicalName);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length != file.CiphertextLength || info.Length > MaximumEnvelopeBytes)
                throw new InvalidDataException("Runtime-state backup encrypted chunk metadata is invalid.");
            observed[file.LogicalName] = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        return observed;
    }

    private static void ValidateExactBackupFileSet(
        string backupRoot,
        RuntimeStateBackupDescriptorV1 descriptor)
    {
        var expected = new HashSet<string>(descriptor.Manifest.Files.Select(x => x.LogicalName), StringComparer.Ordinal)
        {
            RuntimeStateBackupService.DescriptorFileName,
            RuntimeStateBackupService.AuthenticationFileName
        };

        var observed = Directory
            .EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(backupRoot, path).Replace((char)92, '/'))
            .ToHashSet(StringComparer.Ordinal);

        if (!expected.SetEquals(observed))
            throw new InvalidDataException("Runtime-state backup contains unexpected or missing files.");
    }

    private async Task RestoreItemAsync(
        string backupRoot,
        string stagingRoot,
        string backupId,
        RuntimeStateBackupItemV1 item,
        CancellationToken cancellationToken)
    {
        var destination = ResolveInside(stagingRoot, item.LogicalName);
        using var plaintextHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long plaintextLength = 0;

        await using (var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            RuntimeStateBackupService.ChunkSizeBytes,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            for (var index = 0; index < item.EncryptedChunks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkPath = ResolveInside(backupRoot, item.EncryptedChunks[index]);
                var encryptedBytes = await ReadBoundedAsync(
                    chunkPath, MaximumEnvelopeBytes, cancellationToken).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(encryptedBytes, Json)
                    ?? throw new InvalidDataException("Runtime-state backup encrypted chunk is invalid.");
                var plaintext = _encryption.Decrypt(
                    envelope,
                    new EnvelopeAssociatedData(
                        "runtime-state-backup-chunk",
                        $"{backupId}:{item.LogicalName}:{index}",
                        1));
                try
                {
                    await output.WriteAsync(plaintext, cancellationToken).ConfigureAwait(false);
                    plaintextHash.AppendData(plaintext);
                    plaintextLength += plaintext.LongLength;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(true);
        }

        var actualHash = Convert.ToHexString(plaintextHash.GetHashAndReset());
        if (plaintextLength != item.PlaintextLength || !FixedHexEquals(actualHash, item.PlaintextSha256))
            throw new InvalidDataException("Runtime-state backup plaintext item failed integrity validation.");

        if (item.Kind == RuntimeStateBackupItemKind.SqliteDatabase)
            await VerifySqliteAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifySqliteAsync(string path, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime-state backup staged SQLite database failed integrity validation.");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException("Runtime-state backup file length is invalid.");
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static string ResolveInside(string rootDirectory, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            throw new InvalidDataException("Runtime-state backup path is invalid.");
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime-state backup path escapes its root.");
        return resolved;
    }

    private static bool IsSafeRelativePath(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           !Path.IsPathRooted(value) &&
           !value.Contains("..", StringComparison.Ordinal) &&
           !value.Contains((char)92);

    private static bool FixedHexEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string Hash(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value));

    private static string HashText(string value)
        => Hash(Encoding.UTF8.GetBytes(value));
}

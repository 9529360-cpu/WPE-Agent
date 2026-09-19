using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Backup;
using 币安量化机器人.Services.Security;

namespace WPE.Tests;

public sealed class RuntimeStateBackupServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wpe-state-backup-" + Guid.NewGuid().ToString("N"));
    private readonly AppDataLayout _layout;
    private readonly MemoryProtector _protector = new();

    public RuntimeStateBackupServiceTests()
    {
        _layout = new AppDataLayout(Path.Combine(_root, "state"));
    }

    [Fact]
    public async Task BackupEncryptsAuthoritativeStateAndExcludesSessionAndCaches()
    {
        await CreateDatabase(_layout.DataFile("agent.db"));
        File.WriteAllText(_layout.DataFile("agent-settings.json"), "{\"setup\":true}");
        File.WriteAllText(_layout.DataFile("agent-memory.json"), "{\"MemoryVersion\":7}");
        File.WriteAllText(_layout.DataFile("appsettings.json"), "{\"TradingMode\":\"Testnet\"}");
        File.WriteAllText(_layout.DataFile("local-accounts.json"), "[]");
        File.WriteAllText(_layout.DataFile("local-session.dat"), "transient-session");
        File.WriteAllText(_layout.DataFile("llm-cache.jsonl"), "cache");
        File.WriteAllText(_layout.DataFile("terminal_cache.db"), "rebuildable-cache");

        var service = Service();
        var result = await service.CreateAsync(Path.Combine(_root, "backups"));

        var logical = result.Descriptor.Items.Select(x => x.LogicalName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("agent.db", logical);
        Assert.Contains("agent-settings.json", logical);
        Assert.Contains("agent-memory.json", logical);
        Assert.Contains("appsettings.json", logical);
        Assert.Contains("local-accounts.json", logical);
        Assert.DoesNotContain("local-session.dat", logical);
        Assert.DoesNotContain("llm-cache.jsonl", logical);
        Assert.DoesNotContain("terminal_cache.db", logical);

        Assert.Equal(RuntimeStateBackupDescriptorV1.CurrentSchema, result.Descriptor.Schema);
        Assert.Equal("3.6.0-test", result.Descriptor.ProductVersion);
        Assert.Equal(HashText("DEVICE-TEST"), result.Descriptor.DeviceCodeSha256);
        Assert.Equal(RuntimeStateBackupService.ComputeItemsSha256(result.Descriptor.Items), result.Descriptor.ItemsSha256);
        Assert.Equal(
            EncryptedBackupPolicy.ComputeManifestHash(result.Descriptor.Manifest),
            result.Descriptor.Manifest.ManifestSha256,
            ignoreCase: true);

        var observed = result.Descriptor.Manifest.Files.ToDictionary(
            x => x.LogicalName,
            x => Hash(File.ReadAllBytes(Path.Combine(result.Directory, x.LogicalName.Replace('/', Path.DirectorySeparatorChar)))),
            StringComparer.Ordinal);
        var recovery = new StorageKeyDescriptor(
            _protector.KeyId, 1, StorageKeyState.Active,
            PlatformKeyStoreKind.WindowsDpapiCurrentUser, Now);
        var policy = EncryptedBackupPolicy.CanRestore(new(
            result.Descriptor.Manifest, recovery, true, true, true, observed));
        Assert.True(policy.Allowed, policy.ReasonCode);

        await AssertAuthentication(result);
        await AssertDatabaseRoundTrip(result, "agent.db");
        await AssertFileRoundTrip(result, "agent-memory.json", _layout.DataFile("agent-memory.json"));
    }

    [Fact]
    public async Task BackupDestinationInsideActiveDataFailsClosed()
    {
        await CreateDatabase(_layout.DataFile("agent.db"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().CreateAsync(Path.Combine(_layout.DataDirectory, "nested-backups")));

        Assert.Contains("outside the active WPE data directory", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupFailsClosedWhenRuntimeOwnsDataRoot()
    {
        await CreateDatabase(_layout.DataFile("agent.db"));
        using var runtime = DataRootMaintenanceLease.AcquireProcessLease(
            _layout.RuntimeFile(DataRootMaintenanceLease.LeaseFileName));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().CreateAsync(Path.Combine(_root, "backups")));
        Assert.Contains("data root is in use", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyPlaintextNotificationSecretBlocksBackup()
    {
        File.WriteAllText(_layout.DataFile("appsettings.json"), "{\"TelegramBotToken\":\"plain-secret\"}");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Service().CreateAsync(Path.Combine(_root, "backups")));
        Assert.Contains("plaintext notification credentials", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "backups")) &&
                     Directory.EnumerateDirectories(Path.Combine(_root, "backups")).Any());
    }

    private RuntimeStateBackupService Service() =>
        new(_layout, _protector, () => Now, () => "DEVICE-TEST", "3.6.0-test");

    private async Task AssertAuthentication(RuntimeStateBackupResult result)
    {
        var authPath = Path.Combine(result.Directory, result.Descriptor.Authentication.FileName);
        var bytes = await File.ReadAllBytesAsync(authPath);
        Assert.Equal(result.Descriptor.Authentication.Length, bytes.LongLength);
        Assert.Equal(result.Descriptor.Authentication.Sha256, Hash(bytes), ignoreCase: true);

        var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(bytes)!;
        var plaintext = new VersionedEnvelopeEncryptionService(_protector).Decrypt(
            envelope,
            new EnvelopeAssociatedData("runtime-state-backup-manifest", result.BackupId, 1));
        var expected =
            $"{RuntimeStateBackupDescriptorV1.CurrentSchema}|{result.BackupId}|{result.Descriptor.ProductVersion}|" +
            $"{result.Descriptor.DeviceCodeSha256}|{result.Descriptor.ItemsSha256}|{result.Descriptor.Manifest.ManifestSha256}";
        Assert.Equal(expected, Encoding.UTF8.GetString(plaintext));
        CryptographicOperations.ZeroMemory(plaintext);
    }

    private async Task AssertFileRoundTrip(
        RuntimeStateBackupResult result,
        string logicalName,
        string sourcePath)
    {
        var item = Assert.Single(result.Descriptor.Items.Where(x => x.LogicalName == logicalName));
        using var output = new MemoryStream();
        for (var index = 0; index < item.EncryptedChunks.Count; index++)
        {
            var path = Path.Combine(
                result.Directory,
                item.EncryptedChunks[index].Replace('/', Path.DirectorySeparatorChar));
            var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(await File.ReadAllBytesAsync(path))!;
            var plaintext = new VersionedEnvelopeEncryptionService(_protector).Decrypt(
                envelope,
                new EnvelopeAssociatedData(
                    "runtime-state-backup-chunk",
                    $"{result.BackupId}:{logicalName}:{index}",
                    1));
            try
            {
                await output.WriteAsync(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        var restored = output.ToArray();
        Assert.Equal(File.ReadAllBytes(sourcePath), restored);
        Assert.Equal(item.PlaintextLength, restored.LongLength);
        Assert.Equal(item.PlaintextSha256, Hash(restored), ignoreCase: true);
        CryptographicOperations.ZeroMemory(restored);
    }

    private async Task AssertDatabaseRoundTrip(RuntimeStateBackupResult result, string logicalName)
    {
        var item = Assert.Single(result.Descriptor.Items.Where(x => x.LogicalName == logicalName));
        var restored = Path.Combine(_root, "restored-agent.db");
        await using (var output = new FileStream(restored, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            for (var index = 0; index < item.EncryptedChunks.Count; index++)
            {
                var path = Path.Combine(
                    result.Directory,
                    item.EncryptedChunks[index].Replace('/', Path.DirectorySeparatorChar));
                var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(await File.ReadAllBytesAsync(path))!;
                var plaintext = new VersionedEnvelopeEncryptionService(_protector).Decrypt(
                    envelope,
                    new EnvelopeAssociatedData(
                        "runtime-state-backup-chunk",
                        $"{result.BackupId}:{logicalName}:{index}",
                        1));
                await output.WriteAsync(plaintext);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        var restoredBytes = await File.ReadAllBytesAsync(restored);
        Assert.Equal(item.PlaintextLength, restoredBytes.LongLength);
        Assert.Equal(item.PlaintextSha256, Hash(restoredBytes), ignoreCase: true);

        await using var connection = new SqliteConnection("Data Source=" + restored);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM checkpoint_test WHERE id=1";
        Assert.Equal("preserved", Convert.ToString(await command.ExecuteScalarAsync()));
    }

    private static async Task CreateDatabase(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE checkpoint_test(id INTEGER PRIMARY KEY,value TEXT NOT NULL); INSERT INTO checkpoint_test(id,value) VALUES(1,'preserved');";
        await command.ExecuteNonQueryAsync();
    }

    private static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value));

    private static string HashText(string value) =>
        Hash(Encoding.UTF8.GetBytes(value));

    public void Dispose()
    {
        _protector.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class MemoryProtector : IPlatformKeyProtector, IDisposable
    {
        private readonly byte[] _key = SHA256.HashData("runtime-state-backup-test-key"u8.ToArray());
        public bool IsAvailable => true;
        public string KeyId => "test-current-user-v1";

        public byte[] WrapKey(ReadOnlySpan<byte> dek, EnvelopeKeyContext context) => Transform(dek, context);
        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedDek, EnvelopeKeyContext context) => Transform(wrappedDek, context);

        private byte[] Transform(ReadOnlySpan<byte> value, EnvelopeKeyContext context)
        {
            var result = value.ToArray();
            for (var i = 0; i < result.Length; i++)
                result[i] ^= (byte)(_key[i % _key.Length] ^ context.AssociatedDataHash[i % context.AssociatedDataHash.Length]);
            return result;
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }
}

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Backup;
using 币安量化机器人.Services.Security;

namespace WPE.Tests;

public sealed class RuntimeStateBackupVerifierTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 13, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wpe-restore-verify-" + Guid.NewGuid().ToString("N"));
    private readonly AppDataLayout _layout;
    private readonly MemoryProtector _protector = new("primary");

    public RuntimeStateBackupVerifierTests()
    {
        _layout = new AppDataLayout(Path.Combine(_root, "state"));
    }

    [Fact]
    public async Task VerifiedBackupStagesAuthoritativeBytesAndValidSqlite()
    {
        await CreateDatabase(_layout.DataFile("agent.db"));
        File.WriteAllText(_layout.DataFile("agent-settings.json"), "{\"setup\":true}");
        File.WriteAllBytes(_layout.DataFile("ui-preferences.json"), []);

        var backup = await Backup().CreateAsync(Path.Combine(_root, "backups"));
        var staging = Path.Combine(_root, "staging-data");

        var verified = await Verifier(_protector).VerifyToStagingAsync(backup.Directory, staging);

        Assert.Equal(backup.BackupId, verified.BackupId);
        Assert.Equal(Path.GetFullPath(staging), verified.StagingDataDirectory);
        Assert.Equal(
            File.ReadAllBytes(_layout.DataFile("agent-settings.json")),
            File.ReadAllBytes(Path.Combine(staging, "agent-settings.json")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(staging, "ui-preferences.json")));

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(staging, "agent.db"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM checkpoint_test WHERE id=1";
        Assert.Equal("preserved", Convert.ToString(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task DeviceMismatchFailsBeforeLeavingPlaintextStaging()
    {
        await CreateDatabase(_layout.DataFile("agent.db"));
        var backup = await Backup().CreateAsync(Path.Combine(_root, "backups"));
        var staging = Path.Combine(_root, "staging-device");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Verifier(_protector, deviceCode: "DEVICE-OTHER").VerifyToStagingAsync(backup.Directory, staging));

        Assert.Contains("device identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task SameKeyIdWithDifferentCurrentUserKeyMaterialCannotAuthenticateBackup()
    {
        await CreateDatabase(_layout.DataFile("agent.db"));
        var backup = await Backup().CreateAsync(Path.Combine(_root, "backups"));
        var staging = Path.Combine(_root, "staging-key");
        using var wrongProtector = new MemoryProtector("different-user-material");

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            Verifier(wrongProtector).VerifyToStagingAsync(backup.Directory, staging));

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task TamperedEncryptedChunkFailsManifestIntegrityBeforeStaging()
    {
        await CreateDatabase(_layout.DataFile("agent.db"));
        var backup = await Backup().CreateAsync(Path.Combine(_root, "backups"));
        var chunk = backup.Descriptor.Manifest.Files[0];
        var path = Path.Combine(backup.Directory, chunk.LogicalName.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(path, bytes);
        var staging = Path.Combine(_root, "staging-tampered");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Verifier(_protector).VerifyToStagingAsync(backup.Directory, staging));

        Assert.Contains("restore policy rejected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task UnmanifestedBackupFileFailsClosed()
    {
        await CreateDatabase(_layout.DataFile("agent.db"));
        var backup = await Backup().CreateAsync(Path.Combine(_root, "backups"));
        File.WriteAllText(Path.Combine(backup.Directory, "unmanifested.txt"), "must-not-be-ignored");
        var staging = Path.Combine(_root, "staging-extra");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Verifier(_protector).VerifyToStagingAsync(backup.Directory, staging));

        Assert.Contains("unexpected or missing files", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(staging));
    }

    private RuntimeStateBackupService Backup() =>
        new(_layout, _protector, () => Now, () => "DEVICE-TEST", "3.6.0-test");

    private RuntimeStateBackupVerifier Verifier(
        IPlatformKeyProtector protector,
        string deviceCode = "DEVICE-TEST") =>
        new(protector, () => Now.AddMinutes(1), () => deviceCode, "3.6.0-test");

    private static async Task CreateDatabase(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE checkpoint_test(id INTEGER PRIMARY KEY,value TEXT NOT NULL);" +
            "INSERT INTO checkpoint_test(id,value) VALUES(1,'preserved');";
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _protector.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class MemoryProtector : IPlatformKeyProtector, IDisposable
    {
        private readonly byte[] _key;

        public MemoryProtector(string seed)
            => _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));

        public bool IsAvailable => true;
        public string KeyId => "test-current-user-v1";

        public byte[] WrapKey(ReadOnlySpan<byte> dek, EnvelopeKeyContext context)
            => Transform(dek, context);

        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedDek, EnvelopeKeyContext context)
            => Transform(wrappedDek, context);

        private byte[] Transform(ReadOnlySpan<byte> value, EnvelopeKeyContext context)
        {
            var result = value.ToArray();
            for (var i = 0; i < result.Length; i++)
                result[i] ^= (byte)(_key[i % _key.Length] ^
                                    context.AssociatedDataHash[i % context.AssociatedDataHash.Length]);
            return result;
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }
}

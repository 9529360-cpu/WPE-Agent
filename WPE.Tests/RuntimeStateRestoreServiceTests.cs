using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Backup;
using 币安量化机器人.Services.Security;

namespace WPE.Tests;

public sealed class RuntimeStateRestoreServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 14, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wpe-restore-activate-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryProtector _protector = new("restore-user");

    [Fact]
    public async Task RestoreActivatesVerifiedGenerationAndRetainsEncryptedSafetyCheckpoint()
    {
        var source = Layout("source");
        var target = Layout("target");
        await CreateDatabase(source.DataFile("agent.db"), "new");
        File.WriteAllText(source.DataFile("agent-settings.json"), "{\"generation\":\"new\"}");
        await CreateDatabase(target.DataFile("agent.db"), "old");
        File.WriteAllText(target.DataFile("agent-settings.json"), "{\"generation\":\"old\"}");

        var backup = await Backup(source).CreateAsync(Path.Combine(_root, "source-backups"));
        var result = await Restore(target).RestoreAsync(backup.Directory);

        Assert.Equal("new", await ReadDatabase(target.DataFile("agent.db")));
        Assert.Null(result.SecurityStorageEvidenceSha256);
        Assert.Contains("\"new\"", File.ReadAllText(target.DataFile("agent-settings.json")), StringComparison.Ordinal);
        Assert.NotNull(result.SafetyBackupDirectory);
        Assert.True(Directory.Exists(result.SafetyBackupDirectory));
        Assert.False(File.Exists(target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName)));
        Assert.False(Directory.Exists(RuntimeStateRestoreRecovery.RollbackDirectory(target, result.RestoreId)));

        var safetyStage = Path.Combine(_root, "safety-stage");
        var safety = await Verifier().VerifyToStagingAsync(result.SafetyBackupDirectory!, safetyStage);
        Assert.Equal("old", await ReadDatabase(Path.Combine(safety.StagingDataDirectory, "agent.db")));
        Assert.Contains(
            "\"old\"",
            File.ReadAllText(Path.Combine(safety.StagingDataDirectory, "agent-settings.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitBackupIdMismatchFailsBeforeSafetyBackupOrGenerationSwap()
    {
        var source = Layout("confirm-source");
        var target = Layout("confirm-target");
        await CreateDatabase(source.DataFile("agent.db"), "new");
        await CreateDatabase(target.DataFile("agent.db"), "old");

        var backup = await Backup(source).CreateAsync(Path.Combine(_root, "confirm-backups"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Restore(target).RestoreAsync(backup.Directory, "wpe-state-wrong-confirmation"));

        Assert.Contains("explicit restore confirmation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old", await ReadDatabase(target.DataFile("agent.db")));
        Assert.Empty(Directory.EnumerateDirectories(target.BackupsDirectory));
        Assert.False(File.Exists(target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName)));
        Assert.Empty(
            Directory.EnumerateDirectories(target.RuntimeDirectory)
                .Select(Path.GetFileName)
                .Where(x => x is not null && x.StartsWith("restore-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RestoreSourceThroughReparsePointFailsBeforeLeaseOrMutation()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = Layout("reparse-source");
        var target = Layout("reparse-target");
        await CreateDatabase(source.DataFile("agent.db"), "new");
        await CreateDatabase(target.DataFile("agent.db"), "old");
        var backup = await Backup(source).CreateAsync(Path.Combine(_root, "reparse-backups"));
        var link = Path.Combine(_root, "backup-link");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, backup.Directory);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or
                IOException or
                PlatformNotSupportedException)
            {
                return;
            }

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Restore(target).RestoreAsync(link));

            Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("old", await ReadDatabase(target.DataFile("agent.db")));
            Assert.False(File.Exists(target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName)));
            Assert.Empty(Directory.EnumerateDirectories(target.BackupsDirectory));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
    }

    [Fact]
    public async Task RestoreCommitsSecurityStorageEvidenceBeforeGenerationCommit()
    {
        var source = Layout("security-source");
        var target = Layout("security-target");
        await CreateDatabase(source.DataFile("agent.db"), "new");
        await CreateDatabase(target.DataFile("agent.db"), "old");
        await SeedSecurityStorage(source, validHash: true);

        var backup = await Backup(source).CreateAsync(Path.Combine(_root, "security-backups"));
        var result = await Restore(target).RestoreAsync(backup.Directory);
        var evidence = await ReadSecurityRestoreEvidence(target.DataFile("security-storage.db"));

        Assert.NotNull(result.SecurityStorageEvidenceSha256);
        Assert.Equal(64, result.SecurityStorageEvidenceSha256!.Length);
        Assert.Equal(result.BackupId, evidence.BackupId);
        Assert.Equal(result.SecurityStorageEvidenceSha256, evidence.PlanSha256, ignoreCase: true);
        Assert.Equal(result.RestoreId, evidence.AuditCorrelationId);
        Assert.Equal(1, evidence.Count);
        Assert.Equal("new", await ReadDatabase(target.DataFile("agent.db")));
    }

    [Fact]
    public async Task InvalidSecurityStorageEnvelopeInventoryFailsBeforeActivation()
    {
        var source = Layout("security-invalid-source");
        var target = Layout("security-invalid-target");
        await CreateDatabase(source.DataFile("agent.db"), "new");
        await CreateDatabase(target.DataFile("agent.db"), "old");
        await SeedSecurityStorage(source, validHash: false);

        var backup = await Backup(source).CreateAsync(Path.Combine(_root, "security-invalid-backups"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Restore(target).RestoreAsync(backup.Directory));

        Assert.Contains("security-storage record hash", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old", await ReadDatabase(target.DataFile("agent.db")));
        Assert.False(File.Exists(target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName)));
        Assert.Empty(Directory.EnumerateDirectories(target.BackupsDirectory));
    }

    [Fact]
    public async Task UnauthenticatedSecurityStorageEnvelopeFailsBeforeActivation()
    {
        var source = Layout("security-auth-source");
        var target = Layout("security-auth-target");
        await CreateDatabase(source.DataFile("agent.db"), "new");
        await CreateDatabase(target.DataFile("agent.db"), "old");
        await SeedSecurityStorage(source, validHash: true, tamperAuthentication: true);

        var backup = await Backup(source).CreateAsync(Path.Combine(_root, "security-auth-backups"));

        await Assert.ThrowsAsync<EnvelopeEncryptionException>(() =>
            Restore(target).RestoreAsync(backup.Directory));

        Assert.Equal("old", await ReadDatabase(target.DataFile("agent.db")));
        Assert.False(File.Exists(target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName)));
        Assert.Empty(Directory.EnumerateDirectories(target.BackupsDirectory));
    }

    [Fact]
    public async Task FaultAfterRestoredActivationRollsBackOriginalGeneration()
    {
        var source = Layout("fault-source");
        var target = Layout("fault-target");
        await CreateDatabase(source.DataFile("agent.db"), "new");
        await CreateDatabase(target.DataFile("agent.db"), "old");
        var backup = await Backup(source).CreateAsync(Path.Combine(_root, "fault-backups"));

        var restore = new RuntimeStateRestoreService(
            target,
            _protector,
            () => Now,
            () => "DEVICE-TEST",
            "3.6.0-test",
            (phase, _) => phase == RuntimeStateRestorePhase.RestoredActivated
                ? Task.FromException(new IOException("fault-after-activation"))
                : Task.CompletedTask);

        var error = await Assert.ThrowsAsync<IOException>(() => restore.RestoreAsync(backup.Directory));

        Assert.Contains("fault-after-activation", error.Message, StringComparison.Ordinal);
        Assert.Equal("old", await ReadDatabase(target.DataFile("agent.db")));
        Assert.False(File.Exists(target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName)));
        Assert.Empty(
            Directory.EnumerateDirectories(target.RuntimeDirectory)
                .Select(Path.GetFileName)
                .Where(x => x is not null && x.StartsWith("restore-", StringComparison.Ordinal)));
        Assert.Single(Directory.EnumerateDirectories(target.BackupsDirectory));
    }

    [Fact]
    public async Task StartupRecoveryRollsBackUncommittedSwapAndKeepsCommittedGeneration()
    {
        var target = Layout("recovery-target");
        await CreateDatabase(target.DataFile("agent.db"), "old");
        var restoreId = "restore" + Guid.NewGuid().ToString("N");
        var rollback = RuntimeStateRestoreRecovery.RollbackDirectory(target, restoreId);
        var stage = RuntimeStateRestoreRecovery.StageDirectory(target, restoreId);
        Directory.CreateDirectory(stage);
        await CreateDatabase(Path.Combine(stage, "agent.db"), "new");

        var prepared = Journal(restoreId, RuntimeStateRestorePhase.Prepared);
        RuntimeStateRestoreRecovery.WriteJournal(target, prepared, _protector);
        Directory.Move(target.DataDirectory, rollback);

        Assert.True(RuntimeStateRestoreRecovery.RecoverIfNeeded(target, _protector));
        Assert.Equal("old", await ReadDatabase(target.DataFile("agent.db")));
        Assert.False(Directory.Exists(rollback));
        Assert.False(Directory.Exists(stage));

        var committedId = "restore" + Guid.NewGuid().ToString("N");
        var committedRollback = RuntimeStateRestoreRecovery.RollbackDirectory(target, committedId);
        Directory.Move(target.DataDirectory, committedRollback);
        Directory.CreateDirectory(target.DataDirectory);
        await CreateDatabase(target.DataFile("agent.db"), "committed-new");
        RuntimeStateRestoreRecovery.WriteJournal(
            target,
            Journal(committedId, RuntimeStateRestorePhase.Committed),
            _protector);

        Assert.True(RuntimeStateRestoreRecovery.RecoverIfNeeded(target, _protector));
        Assert.Equal("committed-new", await ReadDatabase(target.DataFile("agent.db")));
        Assert.False(Directory.Exists(committedRollback));
        Assert.False(File.Exists(target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName)));
    }

    [Fact]
    public async Task StartupRecoveryRejectsAmbiguousThreeGenerationStateWithoutDeletingEvidence()
    {
        var target = Layout("ambiguous-recovery-target");
        await CreateDatabase(target.DataFile("agent.db"), "uncommitted-new");
        var restoreId = "restore" + Guid.NewGuid().ToString("N");
        var rollback = RuntimeStateRestoreRecovery.RollbackDirectory(target, restoreId);
        var failed = RuntimeStateRestoreRecovery.FailedDirectory(target, restoreId);

        Directory.CreateDirectory(rollback);
        await CreateDatabase(Path.Combine(rollback, "agent.db"), "old");
        Directory.CreateDirectory(failed);
        await CreateDatabase(Path.Combine(failed, "agent.db"), "failed-other");
        RuntimeStateRestoreRecovery.WriteJournal(
            target,
            Journal(restoreId, RuntimeStateRestorePhase.RestoredActivated),
            _protector);

        var error = Assert.Throws<InvalidDataException>(() =>
            RuntimeStateRestoreRecovery.RecoverIfNeeded(target, _protector));

        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("uncommitted-new", await ReadDatabase(target.DataFile("agent.db")));
        Assert.Equal("old", await ReadDatabase(Path.Combine(rollback, "agent.db")));
        Assert.Equal("failed-other", await ReadDatabase(Path.Combine(failed, "agent.db")));
        Assert.True(File.Exists(target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName)));
    }

    [Fact]
    public async Task BackupRecoversInterruptedRestoreBeforeTakingSnapshot()
    {
        var target = Layout("backup-recovery-target");
        await CreateDatabase(target.DataFile("agent.db"), "old");
        var restoreId = "restore" + Guid.NewGuid().ToString("N");
        var rollback = RuntimeStateRestoreRecovery.RollbackDirectory(target, restoreId);

        Directory.Move(target.DataDirectory, rollback);
        Directory.CreateDirectory(target.DataDirectory);
        await CreateDatabase(target.DataFile("agent.db"), "uncommitted-new");
        RuntimeStateRestoreRecovery.WriteJournal(
            target,
            Journal(restoreId, RuntimeStateRestorePhase.RestoredActivated),
            _protector);

        var backup = await Backup(target).CreateAsync(Path.Combine(_root, "post-crash-backups"));

        Assert.Equal("old", await ReadDatabase(target.DataFile("agent.db")));
        Assert.False(File.Exists(target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName)));
        var stage = Path.Combine(_root, "post-crash-verify");
        var verified = await Verifier().VerifyToStagingAsync(backup.Directory, stage);
        Assert.Equal("old", await ReadDatabase(Path.Combine(verified.StagingDataDirectory, "agent.db")));
    }

    [Fact]
    public async Task TamperedRestoreJournalFailsClosedWithoutMovingData()
    {
        var target = Layout("tamper-target");
        await CreateDatabase(target.DataFile("agent.db"), "old");
        var restoreId = "restore" + Guid.NewGuid().ToString("N");
        RuntimeStateRestoreRecovery.WriteJournal(
            target,
            Journal(restoreId, RuntimeStateRestorePhase.Prepared),
            _protector);

        var path = target.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName);
        var wrapper = JsonSerializer.Deserialize<RuntimeStateRestoreJournalEnvelopeV1>(
            File.ReadAllBytes(path))!;
        wrapper.Envelope.AuthenticationTag[0] ^= 0x7F;
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(wrapper));

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            Task.Run(() => RuntimeStateRestoreRecovery.RecoverIfNeeded(target, _protector)));

        Assert.Equal("old", await ReadDatabase(target.DataFile("agent.db")));
        Assert.True(File.Exists(path));
    }

    private AppDataLayout Layout(string name) =>
        new(Path.Combine(_root, name));

    private RuntimeStateBackupService Backup(AppDataLayout layout) =>
        new(layout, _protector, () => Now, () => "DEVICE-TEST", "3.6.0-test");

    private RuntimeStateBackupVerifier Verifier() =>
        new(_protector, () => Now.AddMinutes(1), () => "DEVICE-TEST", "3.6.0-test");

    private RuntimeStateRestoreService Restore(AppDataLayout layout) =>
        new(layout, _protector, () => Now.AddMinutes(1), () => "DEVICE-TEST", "3.6.0-test");

    private static RuntimeStateRestoreJournalV1 Journal(
        string restoreId,
        RuntimeStateRestorePhase phase) =>
        new(
            RuntimeStateRestoreJournalV1.CurrentSchema,
            restoreId,
            "wpe-state-test",
            "wpe-state-safety",
            new string('A', 64),
            Now,
            phase);

    private static async Task CreateDatabase(string path, string value)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE checkpoint_test(id INTEGER PRIMARY KEY,value TEXT NOT NULL);" +
            "INSERT INTO checkpoint_test(id,value) VALUES(1,$value);";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadDatabase(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM checkpoint_test WHERE id=1";
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private async Task SeedSecurityStorage(
        AppDataLayout layout,
        bool validHash,
        bool tamperAuthentication = false)
    {
        var encryption = new VersionedEnvelopeEncryptionService(_protector);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("restore-secret");
        byte[]? encoded = null;
        try
        {
            var envelope = encryption.Encrypt(
                plaintext,
                new EnvelopeAssociatedData("credential", "restore-record-1", 1));
            if (tamperAuthentication) envelope.AuthenticationTag[0] ^= 0x5A;
            encoded = EncryptedRecordCodec.Encode(envelope);
            var expectedHash = validHash
                ? Convert.ToHexString(SHA256.HashData(encoded))
                : new string('A', 64);
            var storage = new SqliteSecurityStorage(
                layout.DataFile("security-storage.db"),
                () => Now);
            await storage.AddTrustedRecordAsync(
                new TrustedStorageRecord(
                    new(
                        "credential",
                        "restore-record-1",
                        1,
                        expectedHash,
                        LegacyFormat.None),
                    encoded),
                CancellationToken.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static async Task<(string BackupId, string PlanSha256, string AuditCorrelationId, int Count)>
        ReadSecurityRestoreEvidence(string databasePath)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT backup_id,plan_sha256,audit_correlation_id," +
            "(SELECT COUNT(*) FROM security_storage_restores) " +
            "FROM security_storage_restores ORDER BY id DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
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

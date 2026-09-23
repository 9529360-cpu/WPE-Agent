using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Security;

namespace 币安量化机器人.Services.Backup;

public static class RuntimeStateRestoreRecovery
{
    public const string JournalFileName = "runtime-state-restore-journal-v1.wpeenv.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static bool RecoverIfNeeded(
        AppDataLayout layout,
        IPlatformKeyProtector? keyProtector = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var journalPath = layout.RuntimeFile(JournalFileName);
        if (!File.Exists(journalPath)) return false;

        using var lease = DataRootMaintenanceLease.AcquireExclusiveMaintenanceLease(
            layout.RuntimeFile(DataRootMaintenanceLease.LeaseFileName));
        RecoverUnderExclusiveLease(layout, lease, keyProtector);
        return true;
    }

    internal static void RecoverUnderExclusiveLease(
        AppDataLayout layout,
        DataRootMaintenanceLeaseHandle lease,
        IPlatformKeyProtector? keyProtector = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(lease);
        lease.RequireExclusiveFor(layout.RuntimeFile(DataRootMaintenanceLease.LeaseFileName));

        var journalPath = layout.RuntimeFile(JournalFileName);
        if (!File.Exists(journalPath)) return;

        var protector = keyProtector ?? new WindowsCurrentUserKeyProtector();
        if (!protector.IsAvailable)
            throw new InvalidOperationException("The restore journal key protector is unavailable.");

        var journal = ReadJournal(journalPath, protector);
        var stage = StageDirectory(layout, journal.RestoreId);
        var rollback = RollbackDirectory(layout, journal.RestoreId);
        var failed = FailedDirectory(layout, journal.RestoreId);
        var data = Path.GetFullPath(layout.DataDirectory);

        SqliteConnection.ClearAllPools();

        if (journal.Phase == RuntimeStateRestorePhase.Committed)
        {
            if (!Directory.Exists(data))
                throw new InvalidDataException("Committed runtime-state restore is missing the active data directory.");
            DeleteDirectoryIfPresent(stage);
            DeleteDirectoryIfPresent(rollback);
            DeleteDirectoryIfPresent(failed);
            File.Delete(journalPath);
            return;
        }

        if (Directory.Exists(rollback))
        {
            if (Directory.Exists(data) && Directory.Exists(failed))
                throw new InvalidDataException(
                    "Interrupted runtime-state restore has ambiguous active, rollback, and failed generations.");

            if (Directory.Exists(data))
                Directory.Move(data, failed);

            if (!Directory.Exists(data))
                Directory.Move(rollback, data);
        }
        else if (journal.Phase != RuntimeStateRestorePhase.Prepared)
        {
            if (!(Directory.Exists(data) && Directory.Exists(failed)))
                throw new InvalidDataException("Interrupted runtime-state restore cannot prove a recoverable original data generation.");
        }

        if (!Directory.Exists(data))
            throw new InvalidDataException("Interrupted runtime-state restore could not recover the original data generation.");

        DeleteDirectoryIfPresent(stage);
        DeleteDirectoryIfPresent(rollback);
        DeleteDirectoryIfPresent(failed);
        File.Delete(journalPath);
    }

    internal static void WriteJournal(
        AppDataLayout layout,
        RuntimeStateRestoreJournalV1 journal,
        IPlatformKeyProtector? keyProtector = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(journal);
        ValidateJournal(journal);

        var protector = keyProtector ?? new WindowsCurrentUserKeyProtector();
        if (!protector.IsAvailable)
            throw new InvalidOperationException("The restore journal key protector is unavailable.");

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(journal, Json);
        try
        {
            var envelope = new VersionedEnvelopeEncryptionService(protector).Encrypt(
                plaintext,
                new EnvelopeAssociatedData(
                    "runtime-state-restore-journal",
                    journal.RestoreId,
                    1));
            var wrapper = new RuntimeStateRestoreJournalEnvelopeV1(
                RuntimeStateRestoreJournalEnvelopeV1.CurrentSchema,
                journal.RestoreId,
                envelope);
            var path = layout.RuntimeFile(JournalFileName);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(wrapper, Json));
                using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stream.Flush(true);
                File.Move(temp, path, true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static string StageDirectory(AppDataLayout layout, string restoreId)
        => ResolveRuntimeDirectory(layout, "restore-stage-" + SafeId(restoreId));

    internal static string RollbackDirectory(AppDataLayout layout, string restoreId)
        => ResolveRuntimeDirectory(layout, "restore-rollback-" + SafeId(restoreId));

    internal static string FailedDirectory(AppDataLayout layout, string restoreId)
        => ResolveRuntimeDirectory(layout, "restore-failed-" + SafeId(restoreId));

    private static RuntimeStateRestoreJournalV1 ReadJournal(
        string path,
        IPlatformKeyProtector keyProtector)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > 256 * 1024)
            throw new InvalidDataException("Runtime-state restore journal length is invalid.");

        var wrapper = JsonSerializer.Deserialize<RuntimeStateRestoreJournalEnvelopeV1>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Runtime-state restore journal envelope is invalid.");
        if (!string.Equals(
                wrapper.Schema,
                RuntimeStateRestoreJournalEnvelopeV1.CurrentSchema,
                StringComparison.Ordinal))
            throw new InvalidDataException("Runtime-state restore journal envelope schema is unsupported.");

        var restoreId = SafeId(wrapper.RestoreId);
        var plaintext = new VersionedEnvelopeEncryptionService(keyProtector).Decrypt(
            wrapper.Envelope,
            new EnvelopeAssociatedData("runtime-state-restore-journal", restoreId, 1));
        try
        {
            var journal = JsonSerializer.Deserialize<RuntimeStateRestoreJournalV1>(plaintext)
                ?? throw new InvalidDataException("Runtime-state restore journal is invalid.");
            ValidateJournal(journal);
            if (!string.Equals(journal.RestoreId, restoreId, StringComparison.Ordinal))
                throw new InvalidDataException("Runtime-state restore journal identity does not match its envelope.");
            return journal;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidateJournal(RuntimeStateRestoreJournalV1 journal)
    {
        if (!string.Equals(journal.Schema, RuntimeStateRestoreJournalV1.CurrentSchema, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(journal.BackupId) ||
            string.IsNullOrWhiteSpace(journal.SafetyBackupId) ||
            !IsSha256(journal.ExpectedItemsSha256) ||
            !Enum.IsDefined(journal.Phase))
            throw new InvalidDataException("Runtime-state restore journal metadata is invalid.");
        _ = SafeId(journal.RestoreId);
    }

    private static string ResolveRuntimeDirectory(AppDataLayout layout, string name)
    {
        var root = Path.GetFullPath(layout.RuntimeDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(layout.RuntimeDirectory, name));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime-state restore directory escapes the runtime root.");
        return resolved;
    }

    private static string SafeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 64 ||
            value.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            throw new InvalidDataException("Runtime-state restore id is invalid.");
        return value;
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

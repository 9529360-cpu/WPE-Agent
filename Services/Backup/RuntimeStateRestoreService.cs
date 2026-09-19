using System.IO;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Security;

namespace 币安量化机器人.Services.Backup;

public sealed class RuntimeStateRestoreService
{
    public const string EmptyTargetSafetyBackupId = "empty-target";

    private readonly AppDataLayout _layout;
    private readonly IPlatformKeyProtector _keyProtector;
    private readonly RuntimeStateBackupService _backupService;
    private readonly RuntimeStateBackupVerifier _verifier;
    private readonly RuntimeStateSecurityStorageRestoreEvidence _securityRestoreEvidence;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<RuntimeStateRestorePhase, CancellationToken, Task>? _phaseHook;

    public RuntimeStateRestoreService(
        AppDataLayout layout,
        IPlatformKeyProtector? keyProtector = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? deviceCode = null,
        string? productVersion = null)
        : this(layout, keyProtector, utcNow, deviceCode, productVersion, null)
    {
    }

    internal RuntimeStateRestoreService(
        AppDataLayout layout,
        IPlatformKeyProtector? keyProtector,
        Func<DateTimeOffset>? utcNow,
        Func<string>? deviceCode,
        string? productVersion,
        Func<RuntimeStateRestorePhase, CancellationToken, Task>? phaseHook)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _keyProtector = keyProtector ?? new WindowsCurrentUserKeyProtector();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        var device = deviceCode ?? DeviceLicenseService.GetCurrentDeviceCode;
        var version = string.IsNullOrWhiteSpace(productVersion)
            ? typeof(RuntimeStateRestoreService).Assembly.GetName().Version?.ToString() ?? "unknown"
            : productVersion;
        _backupService = new(_layout, _keyProtector, _utcNow, device, version);
        _verifier = new(_keyProtector, _utcNow, device, version);
        _securityRestoreEvidence = new(_keyProtector, _utcNow);
        _phaseHook = phaseHook;
    }

    public async Task<RuntimeStateRestoreResult> RestoreAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
            throw new ArgumentException("Runtime-state backup directory is required.", nameof(backupDirectory));
        if (!_keyProtector.IsAvailable)
            throw new InvalidOperationException("The platform restore key protector is unavailable.");

        var backupRoot = Path.GetFullPath(backupDirectory);
        EnsureBackupOutsideActiveData(backupRoot);

        var restoreId = Guid.NewGuid().ToString("N");
        var stage = RuntimeStateRestoreRecovery.StageDirectory(_layout, restoreId);
        var rollback = RuntimeStateRestoreRecovery.RollbackDirectory(_layout, restoreId);
        var failed = RuntimeStateRestoreRecovery.FailedDirectory(_layout, restoreId);
        var leasePath = _layout.RuntimeFile(DataRootMaintenanceLease.LeaseFileName);
        using var maintenanceLease = DataRootMaintenanceLease.AcquireExclusiveMaintenanceLease(leasePath);

        RuntimeStateRestoreRecovery.RecoverUnderExclusiveLease(
            _layout, maintenanceLease, _keyProtector);

        var verified = await _verifier.VerifyToStagingAsync(
            backupRoot, stage, cancellationToken).ConfigureAwait(false);

        RuntimeStateBackupResult? safetyBackup = null;
        RuntimeStateSecurityStorageRestorePlan? securityRestorePlan = null;
        var journalWritten = false;
        try
        {
            securityRestorePlan = await _securityRestoreEvidence.PrepareAsync(
                verified.Descriptor,
                stage,
                restoreId,
                cancellationToken).ConfigureAwait(false);

            if (HasAuthoritativeActiveState())
            {
                safetyBackup = await _backupService.CreateUnderExclusiveLeaseAsync(
                    _layout.BackupsDirectory,
                    maintenanceLease,
                    cancellationToken).ConfigureAwait(false);
            }

            var safetyBackupId = safetyBackup?.BackupId ?? EmptyTargetSafetyBackupId;
            var journal = new RuntimeStateRestoreJournalV1(
                RuntimeStateRestoreJournalV1.CurrentSchema,
                restoreId,
                verified.BackupId,
                safetyBackupId,
                verified.Descriptor.ItemsSha256,
                _utcNow().ToUniversalTime(),
                RuntimeStateRestorePhase.Prepared);
            RuntimeStateRestoreRecovery.WriteJournal(
                _layout, journal, _keyProtector);
            journalWritten = true;
            await InvokePhaseHookAsync(journal.Phase, cancellationToken).ConfigureAwait(false);

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(rollback) || Directory.Exists(failed))
                throw new InvalidOperationException("Runtime-state restore working directories already exist.");
            if (!Directory.Exists(_layout.DataDirectory))
                throw new InvalidOperationException("The active WPE data directory is missing.");

            Directory.Move(_layout.DataDirectory, rollback);
            journal = journal with { Phase = RuntimeStateRestorePhase.CurrentMovedToRollback };
            RuntimeStateRestoreRecovery.WriteJournal(
                _layout, journal, _keyProtector);
            await InvokePhaseHookAsync(journal.Phase, cancellationToken).ConfigureAwait(false);

            Directory.Move(stage, _layout.DataDirectory);
            journal = journal with { Phase = RuntimeStateRestorePhase.RestoredActivated };
            RuntimeStateRestoreRecovery.WriteJournal(
                _layout, journal, _keyProtector);
            await InvokePhaseHookAsync(journal.Phase, cancellationToken).ConfigureAwait(false);

            await _verifier.VerifyPlaintextDataDirectoryAsync(
                verified.Descriptor,
                _layout.DataDirectory,
                cancellationToken).ConfigureAwait(false);

            var securityStorageStatus = await _securityRestoreEvidence.CommitAsync(
                securityRestorePlan,
                _layout.DataDirectory,
                cancellationToken).ConfigureAwait(false);

            journal = journal with { Phase = RuntimeStateRestorePhase.Committed };
            RuntimeStateRestoreRecovery.WriteJournal(
                _layout, journal, _keyProtector);
            await InvokePhaseHookAsync(journal.Phase, cancellationToken).ConfigureAwait(false);

            TryCleanupCommitted(restoreId);
            return new(
                restoreId,
                verified.BackupId,
                safetyBackupId,
                safetyBackup?.Directory,
                _utcNow().ToUniversalTime(),
                verified.Descriptor.Items.Count,
                verified.Descriptor.ItemsSha256,
                securityStorageStatus?.EvidenceSha256);
        }
        catch
        {
            if (journalWritten)
            {
                try
                {
                    RuntimeStateRestoreRecovery.RecoverUnderExclusiveLease(
                        _layout, maintenanceLease, _keyProtector);
                }
                catch
                {
                    // Leave authenticated journal and working directories intact.
                    // The next process bootstrap must recover or fail closed.
                }
            }
            else
            {
                TryDeleteDirectory(stage);
            }
            throw;
        }
    }

    private Task InvokePhaseHookAsync(
        RuntimeStateRestorePhase phase,
        CancellationToken cancellationToken)
        => _phaseHook is null ? Task.CompletedTask : _phaseHook(phase, cancellationToken);

    private bool HasAuthoritativeActiveState()
        => RuntimeStateBackupInventoryV1.AuthoritativeDatabases
               .Concat(RuntimeStateBackupInventoryV1.AuthoritativeFiles)
               .Any(name => File.Exists(Path.Combine(_layout.DataDirectory, name)));

    private void EnsureBackupOutsideActiveData(string backupRoot)
    {
        var data = Path.GetFullPath(_layout.DataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var backup = backupRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (backup.StartsWith(data, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Runtime-state restore source must be outside the active WPE data directory.");
    }

    private void TryCleanupCommitted(string restoreId)
    {
        try
        {
            TryDeleteDirectory(RuntimeStateRestoreRecovery.RollbackDirectory(_layout, restoreId));
            TryDeleteDirectory(RuntimeStateRestoreRecovery.FailedDirectory(_layout, restoreId));
            TryDeleteDirectory(RuntimeStateRestoreRecovery.StageDirectory(_layout, restoreId));
            File.Delete(_layout.RuntimeFile(RuntimeStateRestoreRecovery.JournalFileName));
        }
        catch
        {
            // A committed authenticated journal is intentionally retained.
            // Startup recovery will only clean committed rollback residue.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}

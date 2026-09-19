using System.IO;
using Serilog;
using 币安量化机器人.Services.Backup;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人.Services;

public sealed record RuntimeProcessBootstrapResult(
    LegacyMigrationResult Migration,
    DateTimeOffset InitializedAtUtc,
    string LogPath);

/// <summary>
/// Process-wide, presentation-neutral bootstrap shared by desktop and future headless entry points.
/// It owns portable-data migration, sanitized file logging and localization-core initialization.
/// </summary>
public static class RuntimeProcessBootstrap
{
    private static readonly object Gate = new();
    private static DataRootMaintenanceLeaseHandle? _dataRootLease;
    private static RuntimeProcessBootstrapResult? _current;

    public static RuntimeProcessBootstrapResult Initialize(string logFileName)
    {
        if (string.IsNullOrWhiteSpace(logFileName) || Path.IsPathRooted(logFileName))
            throw new ArgumentException("A relative process log filename is required.", nameof(logFileName));

        lock (Gate)
        {
            if (_current is not null) return _current;

            var leasePath = AppDataPaths.RuntimeFile(DataRootMaintenanceLease.LeaseFileName);
            _dataRootLease = DataRootMaintenanceLease.AcquireProcessLease(leasePath);
            try
            {
                var migration = AppDataPaths.MigrateLegacyPortableData();
                var logPath = AppDataPaths.LogFile(logFileName);
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.Sink(new SensitiveFileLogSink(logPath))
                    .CreateLogger();

                LocalizationService.Current.Initialize();
                var result = new RuntimeProcessBootstrapResult(migration, DateTimeOffset.UtcNow, logPath);
                Log.Information(
                    "Runtime process bootstrap completed. Copied={Copied}; Skipped={Skipped}; Failed={Failed}",
                    migration.Copied,
                    migration.Skipped,
                    migration.Failed);
                _current = result;
                return result;
            }
            catch
            {
                _dataRootLease.Dispose();
                _dataRootLease = null;
                throw;
            }
        }
    }
}

namespace 币安量化机器人.Services.Backup;

public enum RuntimeStateRestorePhase
{
    Prepared,
    CurrentMovedToRollback,
    RestoredActivated,
    Committed
}

public sealed record RuntimeStateRestoreJournalV1(
    string Schema,
    string RestoreId,
    string BackupId,
    string SafetyBackupId,
    string ExpectedItemsSha256,
    DateTimeOffset StartedAtUtc,
    RuntimeStateRestorePhase Phase)
{
    public const string CurrentSchema = "wpe.runtime-state-restore-journal/1.0";
}

public sealed record RuntimeStateRestoreResult(
    string RestoreId,
    string BackupId,
    string SafetyBackupId,
    string SafetyBackupDirectory,
    DateTimeOffset CommittedAtUtc,
    int RestoredItemCount,
    string ItemsSha256);

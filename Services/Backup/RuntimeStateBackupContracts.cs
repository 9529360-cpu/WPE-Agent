using 币安量化机器人.Services.Security;

namespace 币安量化机器人.Services.Backup;

public static class RuntimeStateBackupInventoryV1
{
    public static readonly IReadOnlyList<string> AuthoritativeDatabases =
    [
        "agent.db",
        "trading.db",
        "notification-outbox.db",
        "security-storage.db"
    ];

    public static readonly IReadOnlyList<string> AuthoritativeFiles =
    [
        "agent-settings.json",
        "appsettings.json",
        "local-accounts.json",
        "device-license.dat",
        "ui-settings.json",
        "ui-preferences.json",
        "llm-calls.jsonl"
    ];

    public static RuntimeStateBackupItemKind? KindFor(string logicalName)
    {
        if (AuthoritativeDatabases.Contains(logicalName, StringComparer.Ordinal))
            return RuntimeStateBackupItemKind.SqliteDatabase;
        if (AuthoritativeFiles.Contains(logicalName, StringComparer.Ordinal))
            return RuntimeStateBackupItemKind.File;
        return null;
    }
}

public enum RuntimeStateBackupItemKind
{
    SqliteDatabase,
    File
}

public sealed record RuntimeStateBackupItemV1(
    string LogicalName,
    RuntimeStateBackupItemKind Kind,
    long PlaintextLength,
    string PlaintextSha256,
    IReadOnlyList<string> EncryptedChunks);

public sealed record RuntimeStateBackupAuthenticationV1(
    string FileName,
    long Length,
    string Sha256);

public sealed record RuntimeStateBackupDescriptorV1(
    string Schema,
    string ProductVersion,
    string DeviceCodeSha256,
    string ItemsSha256,
    EncryptedBackupManifest Manifest,
    IReadOnlyList<RuntimeStateBackupItemV1> Items,
    RuntimeStateBackupAuthenticationV1 Authentication)
{
    public const string CurrentSchema = "wpe.runtime-state-backup/1.0";
}

public sealed record RuntimeStateBackupResult(
    string BackupId,
    string Directory,
    RuntimeStateBackupDescriptorV1 Descriptor);

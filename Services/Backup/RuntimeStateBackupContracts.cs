using 币安量化机器人.Services.Security;

namespace 币安量化机器人.Services.Backup;

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

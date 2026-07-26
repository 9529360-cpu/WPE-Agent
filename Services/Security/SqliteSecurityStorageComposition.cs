using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace 币安量化机器人.Services.Security;

public sealed class WindowsCurrentUserKeyProtector : IPlatformKeyProtector
{
    public bool IsAvailable=>OperatingSystem.IsWindows();
    public string KeyId=>"windows-current-user-v1";
    public byte[] WrapKey(ReadOnlySpan<byte> dek,EnvelopeKeyContext context)=>ProtectedData.Protect(dek.ToArray(),context.AssociatedDataHash,DataProtectionScope.CurrentUser);
    public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedDek,EnvelopeKeyContext context)=>ProtectedData.Unprotect(wrappedDek.ToArray(),context.AssociatedDataHash,DataProtectionScope.CurrentUser);
}

public sealed class SqliteSecurityStorage : IPlatformStorageKeyStore,ISecurityStorageInventory,ISecurityStorageTransaction
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly Func<DateTimeOffset> _utcNow;
    public SqliteSecurityStorage(string databasePath):this(databasePath,null){}
    internal SqliteSecurityStorage(string databasePath,Func<DateTimeOffset>? utcNow)
    {
        if(string.IsNullOrWhiteSpace(databasePath))throw new ArgumentException("Security storage database path is required.",nameof(databasePath));
        _databasePath=Path.GetFullPath(databasePath);_connectionString=new SqliteConnectionStringBuilder{DataSource=_databasePath,Mode=SqliteOpenMode.ReadWriteCreate,Cache=SqliteCacheMode.Shared}.ToString();_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }
    public PlatformKeyStoreKind Kind=>PlatformKeyStoreKind.WindowsDpapiCurrentUser;

    public async Task<IReadOnlyList<TrustedStorageRecord>> EnumerateAsync(CancellationToken ct)
    {
        await using var connection=await OpenAsync(ct);await EnsureSchemaAsync(connection,ct);await using var command=connection.CreateCommand();command.CommandText="SELECT record_type,record_id,record_version,stored_value,expected_sha256,legacy_format FROM security_storage_records ORDER BY record_type,record_id";
        var records=new List<TrustedStorageRecord>();await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var raw=reader.GetInt32(5);if(!Enum.IsDefined(typeof(LegacyFormat),raw))throw new InvalidDataException("Trusted storage metadata contains an unsupported legacy format.");
            records.Add(new(new(reader.GetString(0),reader.GetString(1),reader.GetInt32(2),reader.GetString(4),(LegacyFormat)raw),(byte[])reader[3]));
        }
        return records;
    }

    public async Task CommitMigrationAsync(IReadOnlyList<RecordMigrationWrite> writes,RecordMigrationCheckpoint checkpoint,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writes);ArgumentNullException.ThrowIfNull(checkpoint);await using var connection=await OpenAsync(ct);await EnsureSchemaAsync(connection,ct);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach(var write in writes)
        {
            await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="UPDATE security_storage_records SET stored_value=$value,expected_sha256=$hash,legacy_format=$none WHERE record_id=$id";command.Parameters.AddWithValue("$value",write.EncodedEnvelope);command.Parameters.AddWithValue("$hash",write.EncodedSha256);command.Parameters.AddWithValue("$none",(int)LegacyFormat.None);command.Parameters.AddWithValue("$id",write.RecordId);if(await command.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Migration record metadata did not resolve uniquely.");
        }
        await using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="INSERT INTO security_storage_checkpoints(migration_id,target_version,checkpoint_json,committed_at) VALUES($id,$version,$json,$at) ON CONFLICT(migration_id) DO UPDATE SET target_version=excluded.target_version,checkpoint_json=excluded.checkpoint_json,committed_at=excluded.committed_at";command.Parameters.AddWithValue("$id",checkpoint.MigrationId);command.Parameters.AddWithValue("$version",checkpoint.TargetEnvelopeVersion);command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(checkpoint.CompletedRecordHashes));command.Parameters.AddWithValue("$at",Utc(_utcNow()));await command.ExecuteNonQueryAsync(ct);}
        await transaction.CommitAsync(ct);
    }

    public async Task CommitRestoreAsync(BackupRestoreDrillVerification verification,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verification);if(!verification.Allowed)throw new InvalidOperationException("Unverified restore cannot be committed.");await using var connection=await OpenAsync(ct);await EnsureSchemaAsync(connection,ct);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="INSERT INTO security_storage_restores(backup_id,plan_sha256,audit_correlation_id,committed_at) VALUES($backup,$hash,$audit,$at)";command.Parameters.AddWithValue("$backup",verification.BackupId);command.Parameters.AddWithValue("$hash",verification.PlanSha256);command.Parameters.AddWithValue("$audit",verification.AuditCorrelationId);command.Parameters.AddWithValue("$at",Utc(_utcNow()));await command.ExecuteNonQueryAsync(ct);await transaction.CommitAsync(ct);
    }

    public async Task CommitRotationAsync(KeyRotationArtifact artifact,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);if(!artifact.CanRetireOldKey)throw new InvalidOperationException("Rotation artifact is not committed.");await using var connection=await OpenAsync(ct);await EnsureSchemaAsync(connection,ct);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="INSERT INTO security_storage_rotations(rotation_id,artifact_json,inventory_sha256,committed_at) VALUES($id,$artifact,$hash,$at) ON CONFLICT(rotation_id) DO UPDATE SET artifact_json=excluded.artifact_json,inventory_sha256=excluded.inventory_sha256,committed_at=excluded.committed_at";command.Parameters.AddWithValue("$id",artifact.RotationId);command.Parameters.AddWithValue("$artifact",JsonSerializer.Serialize(artifact));command.Parameters.AddWithValue("$hash",artifact.InventorySha256);command.Parameters.AddWithValue("$at",Utc(_utcNow()));await command.ExecuteNonQueryAsync(ct);await transaction.CommitAsync(ct);
    }

    public async Task<StorageKeyDescriptor> CreateAsync(CancellationToken ct)
    {
        var reference=new StorageKeyReference(Guid.NewGuid().ToString("N"),1);var now=_utcNow().ToUniversalTime();var material=RandomNumberGenerator.GetBytes(32);try{var protectedMaterial=ProtectedData.Protect(material,null,DataProtectionScope.CurrentUser);await SaveKeyAsync(reference,StorageKeyState.Active,protectedMaterial,now,ct);return new(reference.KeyId,reference.Version,StorageKeyState.Active,Kind,now,now);}finally{CryptographicOperations.ZeroMemory(material);}
    }
    public Task<ProtectedStorageKey?> LoadAsync(StorageKeyReference reference,CancellationToken ct)=>LoadKeyAsync(reference,false,ct);
    public Task<ProtectedStorageKey?> LoadForRecoveryAsync(StorageKeyReference reference,CancellationToken ct)=>LoadKeyAsync(reference,true,ct);
    public async Task<StorageKeyDescriptor> BeginRotationAsync(StorageKeyReference current,CancellationToken ct)
    {
        var reference=new StorageKeyReference(Guid.NewGuid().ToString("N"),current.Version+1);var now=_utcNow().ToUniversalTime();var material=RandomNumberGenerator.GetBytes(32);try{var protectedMaterial=ProtectedData.Protect(material,null,DataProtectionScope.CurrentUser);await SaveKeyAsync(reference,StorageKeyState.RotationPending,protectedMaterial,now,ct);return new(reference.KeyId,reference.Version,StorageKeyState.RotationPending,Kind,now);}finally{CryptographicOperations.ZeroMemory(material);}
    }
    public Task RetireAsync(StorageKeyReference reference,CancellationToken ct)=>SetKeyStateAsync(reference,StorageKeyState.Retired,"security-storage.rotation-retired",ct);
    public Task RevokeAsync(StorageKeyReference reference,string reasonCode,CancellationToken ct)=>SetKeyStateAsync(reference,StorageKeyState.Revoked,reasonCode,ct);

    internal async Task AddTrustedRecordAsync(TrustedStorageRecord record,CancellationToken ct)
    {
        await using var connection=await OpenAsync(ct);await EnsureSchemaAsync(connection,ct);await using var command=connection.CreateCommand();command.CommandText="INSERT INTO security_storage_records(record_type,record_id,record_version,stored_value,expected_sha256,legacy_format) VALUES($type,$id,$version,$value,$hash,$legacy)";command.Parameters.AddWithValue("$type",record.Metadata.RecordType);command.Parameters.AddWithValue("$id",record.Metadata.RecordId);command.Parameters.AddWithValue("$version",record.Metadata.RecordVersion);command.Parameters.AddWithValue("$value",record.StoredValue);command.Parameters.AddWithValue("$hash",record.Metadata.ExpectedStoredSha256);command.Parameters.AddWithValue("$legacy",(int)record.Metadata.LegacyFormat);await command.ExecuteNonQueryAsync(ct);
    }

    private async Task SaveKeyAsync(StorageKeyReference reference,StorageKeyState state,byte[] material,DateTimeOffset now,CancellationToken ct){await using var connection=await OpenAsync(ct);await EnsureSchemaAsync(connection,ct);await using var command=connection.CreateCommand();command.CommandText="INSERT INTO security_storage_keys(key_id,key_version,key_state,protected_material,created_at,updated_at,reason_code) VALUES($id,$version,$state,$material,$at,$at,'security-storage.key-created')";command.Parameters.AddWithValue("$id",reference.KeyId);command.Parameters.AddWithValue("$version",reference.Version);command.Parameters.AddWithValue("$state",(int)state);command.Parameters.AddWithValue("$material",material);command.Parameters.AddWithValue("$at",Utc(now));await command.ExecuteNonQueryAsync(ct);}
    private async Task<ProtectedStorageKey?> LoadKeyAsync(StorageKeyReference reference,bool recovery,CancellationToken ct){await using var connection=await OpenAsync(ct);await EnsureSchemaAsync(connection,ct);await using var command=connection.CreateCommand();command.CommandText="SELECT key_state,protected_material FROM security_storage_keys WHERE key_id=$id AND key_version=$version";command.Parameters.AddWithValue("$id",reference.KeyId);command.Parameters.AddWithValue("$version",reference.Version);await using var reader=await command.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return null;var state=(StorageKeyState)reader.GetInt32(0);if(state==StorageKeyState.Revoked||!recovery&&state!=StorageKeyState.Active)return null;return new(reference,Kind,(byte[])reader[1]);}
    private async Task SetKeyStateAsync(StorageKeyReference reference,StorageKeyState state,string reason,CancellationToken ct){await using var connection=await OpenAsync(ct);await EnsureSchemaAsync(connection,ct);await using var command=connection.CreateCommand();command.CommandText="UPDATE security_storage_keys SET key_state=$state,updated_at=$at,reason_code=$reason WHERE key_id=$id AND key_version=$version";command.Parameters.AddWithValue("$state",(int)state);command.Parameters.AddWithValue("$at",Utc(_utcNow()));command.Parameters.AddWithValue("$reason",reason);command.Parameters.AddWithValue("$id",reference.KeyId);command.Parameters.AddWithValue("$version",reference.Version);if(await command.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Storage key was not found.");}
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct){Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);var connection=new SqliteConnection(_connectionString);await connection.OpenAsync(ct);return connection;}
    private static async Task EnsureSchemaAsync(SqliteConnection connection,CancellationToken ct){await using var command=connection.CreateCommand();command.CommandText="""
CREATE TABLE IF NOT EXISTS security_storage_records(record_type TEXT NOT NULL,record_id TEXT NOT NULL UNIQUE,record_version INTEGER NOT NULL,stored_value BLOB NOT NULL,expected_sha256 TEXT NOT NULL,legacy_format INTEGER NOT NULL CHECK(legacy_format IN (0,1)),PRIMARY KEY(record_type,record_id));
CREATE TABLE IF NOT EXISTS security_storage_checkpoints(migration_id TEXT PRIMARY KEY,target_version INTEGER NOT NULL,checkpoint_json TEXT NOT NULL,committed_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS security_storage_rotations(rotation_id TEXT PRIMARY KEY,artifact_json TEXT NOT NULL,inventory_sha256 TEXT NOT NULL,committed_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS security_storage_restores(id INTEGER PRIMARY KEY AUTOINCREMENT,backup_id TEXT NOT NULL,plan_sha256 TEXT NOT NULL,audit_correlation_id TEXT NOT NULL,committed_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS security_storage_keys(key_id TEXT NOT NULL,key_version INTEGER NOT NULL,key_state INTEGER NOT NULL,protected_material BLOB NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,reason_code TEXT NOT NULL,PRIMARY KEY(key_id,key_version));
""";await command.ExecuteNonQueryAsync(ct);}
    private static string Utc(DateTimeOffset value)=>value.ToUniversalTime().ToString("O");
}

public static class SecurityStorageComposition
{
    public static SecurityStorageRuntime Create(string databasePath)
    {
        var storage=new SqliteSecurityStorage(databasePath);return new(storage,new WindowsCurrentUserKeyProtector(),storage,storage);
    }
}

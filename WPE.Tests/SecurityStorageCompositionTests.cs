using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Security;

namespace WPE.Tests;

public sealed class SecurityStorageCompositionTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-security-storage-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"security.db");
    private SqliteSecurityStorage Storage()=>new(DatabasePath,()=>new DateTimeOffset(2026,7,22,12,0,0,TimeSpan.Zero));

    [Fact]
    public async Task MigrationWritesAndCheckpointRollbackAsOneTransaction()
    {
        var storage=Storage();var original=new byte[]{1,2,3};await storage.AddTrustedRecordAsync(Record("r1",original,LegacyFormat.WindowsDpapiCurrentUser),CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>storage.CommitMigrationAsync([new("r1",[4,5],Hash([4,5])),new("missing",[6],Hash([6]))],new("migration",1,new Dictionary<string,string>{{"r1",Hash([4,5])}}),CancellationToken.None));
        var row=Assert.Single(await storage.EnumerateAsync(CancellationToken.None));Assert.Equal(original,row.StoredValue);Assert.Equal(LegacyFormat.WindowsDpapiCurrentUser,row.Metadata.LegacyFormat);Assert.Equal(0,await ScalarAsync("SELECT COUNT(*) FROM security_storage_checkpoints"));
    }

    [Fact]
    public async Task SuccessfulMigrationCommitsEnvelopeMetadataAndCheckpointTogether()
    {
        var storage=Storage();await storage.AddTrustedRecordAsync(Record("r1",[1],LegacyFormat.WindowsDpapiCurrentUser),CancellationToken.None);var migrated=new byte[]{7,8,9};
        await storage.CommitMigrationAsync([new("r1",migrated,Hash(migrated))],new("migration",1,new Dictionary<string,string>{{"r1",Hash(migrated)}}),CancellationToken.None);
        var row=Assert.Single(await storage.EnumerateAsync(CancellationToken.None));Assert.Equal(migrated,row.StoredValue);Assert.Equal(LegacyFormat.None,row.Metadata.LegacyFormat);Assert.Equal(1,await ScalarAsync("SELECT COUNT(*) FROM security_storage_checkpoints"));
    }

    [Fact]
    public void RuntimeSnapshotExposesOnlySanitizedSecurityStorageContract()
    {
        var status=new SecurityStorageRuntimeStatus(SecurityStorageRuntimeState.Committed,"security-storage.restore-committed",1,3,new string('A',64));var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,securityStorageState:status);var value=Assert.IsType<RuntimeSecurityStorageV1>(snapshot.SecurityStorage.Value);
        Assert.Equal("Committed",value.State);Assert.Equal(3,value.RecordCount);var fields=value.GetType().GetProperties().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal);Assert.True(fields.SetEquals(["State","ReasonCode","EnvelopeVersion","RecordCount","EvidenceSha256"]));
        var json=System.Text.Json.JsonSerializer.Serialize(snapshot.SecurityStorage);foreach(var forbidden in new[]{"envelope\"","keyId","databasePath","innerException"})Assert.DoesNotContain(forbidden,json,StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> ScalarAsync(string sql){await using var connection=new SqliteConnection($"Data Source={DatabasePath}");await connection.OpenAsync();await using var command=connection.CreateCommand();command.CommandText=sql;return Convert.ToInt64(await command.ExecuteScalarAsync());}
    private static TrustedStorageRecord Record(string id,byte[] value,LegacyFormat format)=>new(new("test",id,1,Hash(value),format),value);
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value));
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

using System.Data;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace WpeAgent.ProductionLanding;

public enum PublicationStage { LeaseAcquired, WriteFlushed, PrivateRenameComplete, BeforeFinalSerialization, FinalValidationComplete }
public sealed record ProductionPublicationRequest(string PublicationId,string FenceVersion,string TokenId,string Owner,DateTimeOffset LeaseExpiresAtUtc,byte[] Bytes,string Sha256);
public sealed record ProductionPublicationResult(bool Published,string ReasonCode);

public sealed class ProductionLandingPublisher
{
    private readonly string _sealedRoot;
    private readonly string _connectionString;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<PublicationStage,CancellationToken,Task>? _stage;

    public ProductionLandingPublisher(string root,Func<DateTimeOffset>? utcNow=null,Func<PublicationStage,CancellationToken,Task>? stage=null)
    {
        if(string.IsNullOrWhiteSpace(root))throw new ArgumentException("Root is required.",nameof(root));
        root=Path.GetFullPath(root);Directory.CreateDirectory(root);_sealedRoot=Path.Combine(root,".sealed");Directory.CreateDirectory(_sealedRoot);
        _connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"publication-authority.sqlite"),Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString();
        _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);_stage=stage;Initialize();
    }

    public async Task RegisterLocalAuthorityAsync(string publicationId,string fenceVersion,string tokenId,string owner,CancellationToken ct=default)
    {
        ValidateIdentity(publicationId,fenceVersion,tokenId,owner);await using var c=await OpenAsync(ct);await using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT INTO publication_authority(publication_id,fence_version,token_id,owner,authority_status,token_status,lease_status) VALUES($p,$f,$t,$o,'valid','available','none')";
        q.Parameters.AddWithValue("$p",publicationId);q.Parameters.AddWithValue("$f",fenceVersion);q.Parameters.AddWithValue("$t",tokenId);q.Parameters.AddWithValue("$o",owner);await q.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
    }

    public async Task<ProductionPublicationResult> PublishAsync(ProductionPublicationRequest request,CancellationToken ct=default)
    {
        if(!Valid(request))return Reject("publication.request-invalid");
        var actualHash=Convert.ToHexString(SHA256.HashData(request.Bytes)).ToLowerInvariant();if(!FixedEquals(actualHash,request.Sha256))return Reject("publication.digest-mismatch");
        if(!await AcquireLeaseAsync(request,ct))return Reject("publication.lease-rejected");
        await Notify(PublicationStage.LeaseAcquired,ct);
        var opaque=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.PublicationId+"\0"+request.TokenId+"\0"+actualHash))).ToLowerInvariant();
        var temporary=Path.Combine(_sealedRoot,opaque+".tmp");var sealedPath=Path.Combine(_sealedRoot,opaque+".sealed");
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){await stream.WriteAsync(request.Bytes,ct);await stream.FlushAsync(ct);stream.Flush(true);}
            await Notify(PublicationStage.WriteFlushed,ct);File.Move(temporary,sealedPath,false);await Notify(PublicationStage.PrivateRenameComplete,ct);await Notify(PublicationStage.BeforeFinalSerialization,ct);
            return await FinalizeAsync(request,sealedPath,actualHash,ct);
        }
        catch(OperationCanceledException){throw;}
        catch{TryDelete(temporary);return Reject("publication.failed-closed");}
    }

    public async Task<bool> RevokeAsync(string publicationId,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(publicationId))return false;await using var c=await OpenAsync(ct);await using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="UPDATE publication_authority SET authority_status='revoked',lease_status=CASE WHEN lease_status='active' THEN 'revoked' ELSE lease_status END WHERE publication_id=$p AND authority_status='valid'";q.Parameters.AddWithValue("$p",publicationId);
        var changed=await q.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return changed==1;
    }

    public async Task RecoverFailClosedAsync(CancellationToken ct=default)
    {
        await using var c=await OpenAsync(ct);await using var tx=c.BeginTransaction(IsolationLevel.Serializable);await using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="UPDATE publication_authority SET lease_status='failed',lease_expires_at=NULL WHERE lease_status='active' AND publication_id NOT IN (SELECT publication_id FROM readable_publications)";await q.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
    }

    public async Task<byte[]?> ReadCommittedAsync(string publicationId,CancellationToken ct=default)
    {
        await using var c=await OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT sealed_path,sha256 FROM readable_publications WHERE publication_id=$p AND manifest_visible=1";q.Parameters.AddWithValue("$p",publicationId);
        await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;var path=r.GetString(0);var expected=r.GetString(1);if(!path.StartsWith(_sealedRoot+Path.DirectorySeparatorChar,StringComparison.Ordinal)||!File.Exists(path))return null;
        var bytes=await File.ReadAllBytesAsync(path,ct);return FixedEquals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),expected)?bytes:null;
    }

    public async Task<(bool ManifestVisible,string TokenStatus,string LeaseStatus)> InspectAsync(string publicationId,CancellationToken ct=default)
    {
        await using var c=await OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT EXISTS(SELECT 1 FROM readable_publications WHERE publication_id=$p AND manifest_visible=1),token_status,lease_status FROM publication_authority WHERE publication_id=$p";q.Parameters.AddWithValue("$p",publicationId);
        await using var r=await q.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?(r.GetBoolean(0),r.GetString(1),r.GetString(2)):(false,"unknown","unknown");
    }

    private async Task<bool> AcquireLeaseAsync(ProductionPublicationRequest x,CancellationToken ct)
    {
        await using var c=await OpenAsync(ct);await using var tx=c.BeginTransaction(IsolationLevel.Serializable);await using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="UPDATE publication_authority SET lease_status='active',lease_expires_at=$e,lease_digest=$d WHERE publication_id=$p AND fence_version=$f AND token_id=$t AND owner=$o AND authority_status='valid' AND token_status='available' AND lease_status IN ('none','failed') AND $e>$n";
        Bind(q,x);q.Parameters.AddWithValue("$e",x.LeaseExpiresAtUtc.ToString("O"));q.Parameters.AddWithValue("$d",x.Sha256);q.Parameters.AddWithValue("$n",_utcNow().ToString("O"));var changed=await q.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return changed==1;
    }

    private async Task<ProductionPublicationResult> FinalizeAsync(ProductionPublicationRequest x,string path,string hash,CancellationToken ct)
    {
        await using var c=await OpenAsync(ct);await using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        await using(var serialize=c.CreateCommand()){serialize.Transaction=tx;serialize.CommandText="UPDATE publication_authority SET publication_id=publication_id WHERE publication_id=$p";serialize.Parameters.AddWithValue("$p",x.PublicationId);if(await serialize.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Reject("publication.authority-missing");}}
        await using(var check=c.CreateCommand()){check.Transaction=tx;check.CommandText="SELECT COUNT(*) FROM publication_authority WHERE publication_id=$p AND fence_version=$f AND token_id=$t AND owner=$o AND authority_status='valid' AND token_status='available' AND lease_status='active' AND lease_expires_at>$n AND lease_digest=$d";Bind(check,x);check.Parameters.AddWithValue("$n",_utcNow().ToString("O"));check.Parameters.AddWithValue("$d",hash);if(Convert.ToInt64(await check.ExecuteScalarAsync(ct))!=1){await tx.RollbackAsync(ct);return Reject("publication.final-fence-rejected");}}
        if(!File.Exists(path)||!FixedEquals(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path,ct))).ToLowerInvariant(),hash)){await tx.RollbackAsync(ct);return Reject("publication.sealed-receipt-invalid");}
        await Notify(PublicationStage.FinalValidationComplete,ct);
        await using(var map=c.CreateCommand()){map.Transaction=tx;map.CommandText="INSERT INTO readable_publications(publication_id,sealed_path,sha256,manifest_visible) VALUES($p,$s,$h,1)";map.Parameters.AddWithValue("$p",x.PublicationId);map.Parameters.AddWithValue("$s",path);map.Parameters.AddWithValue("$h",hash);await map.ExecuteNonQueryAsync(ct);}
        await using(var consume=c.CreateCommand()){consume.Transaction=tx;consume.CommandText="UPDATE publication_authority SET token_status='consumed',lease_status='released',lease_expires_at=NULL WHERE publication_id=$p AND token_status='available' AND lease_status='active'";consume.Parameters.AddWithValue("$p",x.PublicationId);if(await consume.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Reject("publication.token-conflict");}}
        await tx.CommitAsync(ct);return new(true,"publication.committed");
    }

    private void Initialize(){using var c=new SqliteConnection(_connectionString);c.Open();using var q=c.CreateCommand();q.CommandText="""
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS publication_authority(publication_id TEXT PRIMARY KEY,fence_version TEXT NOT NULL,token_id TEXT NOT NULL UNIQUE,owner TEXT NOT NULL,authority_status TEXT NOT NULL CHECK(authority_status IN ('valid','revoked')),token_status TEXT NOT NULL CHECK(token_status IN ('available','consumed')),lease_status TEXT NOT NULL CHECK(lease_status IN ('none','active','revoked','failed','released')),lease_expires_at TEXT,lease_digest TEXT);
        CREATE TABLE IF NOT EXISTS readable_publications(publication_id TEXT PRIMARY KEY REFERENCES publication_authority(publication_id),sealed_path TEXT NOT NULL,sha256 TEXT NOT NULL,manifest_visible INTEGER NOT NULL CHECK(manifest_visible=1));
        """;q.ExecuteNonQuery();}
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct){var c=new SqliteConnection(_connectionString);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000";await q.ExecuteNonQueryAsync(ct);return c;}
    private Task Notify(PublicationStage s,CancellationToken ct)=>_stage?.Invoke(s,ct)??Task.CompletedTask;
    private static void Bind(SqliteCommand q,ProductionPublicationRequest x){q.Parameters.AddWithValue("$p",x.PublicationId);q.Parameters.AddWithValue("$f",x.FenceVersion);q.Parameters.AddWithValue("$t",x.TokenId);q.Parameters.AddWithValue("$o",x.Owner);}
    private static bool Valid(ProductionPublicationRequest? x)=>x is not null&&x.Bytes is{Length:>0}&&x.Sha256 is{Length:64}&&!string.IsNullOrWhiteSpace(x.PublicationId)&&!string.IsNullOrWhiteSpace(x.FenceVersion)&&!string.IsNullOrWhiteSpace(x.TokenId)&&!string.IsNullOrWhiteSpace(x.Owner);
    private static void ValidateIdentity(params string[] values){if(values.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("Authority identity fields are required.");}
    private static bool FixedEquals(string a,string b)=>a.Length==b.Length&&CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a),System.Text.Encoding.ASCII.GetBytes(b.ToLowerInvariant()));
    private static ProductionPublicationResult Reject(string code)=>new(false,code);
    private static void TryDelete(string path){try{File.Delete(path);}catch{}}
}

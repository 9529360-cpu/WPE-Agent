using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using WpeAgent.FinancialEvidence;

namespace WpeAgent.Knowledge;

public enum KnowledgeMutationKindV1 { Fact, Correction, Retraction }
public enum KnowledgeQueryOutcomeV1 { Current, Abstained }
public sealed record KnowledgeMutationResultV1(bool Accepted,bool Idempotent,string ReasonCode);
public sealed record KnowledgeChainEntryV1(long Sequence,KnowledgeMutationKindV1 Kind,string RecordId,string RecordVersion,string RecordHash,string? TargetRecordHash,DateTimeOffset EffectiveAt);
public sealed record KnowledgeQueryResultV1(KnowledgeQueryOutcomeV1 Outcome,FinancialEvidenceRecordV1? Record,IReadOnlyList<KnowledgeChainEntryV1> Chain,IReadOnlyList<string> ReasonCodes);

public sealed class DeterministicKnowledgeStore
{
    public const string SchemaVersion="wpe.model-off-knowledge/1.0";
    private readonly string _connectionString;
    private readonly FinancialEvidenceRetrievalGateV1 _gate=new();

    public DeterministicKnowledgeStore(string databasePath)
    {
        if(string.IsNullOrWhiteSpace(databasePath))throw new ArgumentException("Database path is required.",nameof(databasePath));
        var path=Path.GetFullPath(databasePath);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString();Initialize();
    }

    public async Task<KnowledgeMutationResultV1> IngestAsync(AuthorizedLocalCorpusResultV1 corpus,FinancialEvidenceRetrievalRequestV1 authorization,CancellationToken cancellationToken=default)
    {
        if(corpus is null||!corpus.Accepted||corpus.Records is null||corpus.Records.Count!=1)return Rejected("knowledge.authorization-required");
        var record=corpus.Records[0];
        try{record.VerifyIntegrity();}catch{return Rejected("knowledge.malformed-or-hash-mismatch");}
        var eligibility=_gate.Evaluate(record,authorization);
        if(!eligibility.Eligible)return Rejected(eligibility.ReasonCodes.FirstOrDefault()??"knowledge.authorization-required");
        var relation=Relation(record);
        if(relation is null)return Rejected("knowledge.chain-malformed");

        await using var connection=await OpenAsync(cancellationToken);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing=await FindIdentityAsync(connection,transaction,record.RecordId,record.RecordVersion,cancellationToken);
            if(existing is not null){await transaction.RollbackAsync(cancellationToken);return existing==record.RecordHash?new(true,true,"knowledge.idempotent"):Rejected("knowledge.identity-conflict");}
            var key=Key(record);var chain=await ReadChainAsync(connection,transaction,key,authorization.AsOf,cancellationToken);var current=Replay(chain);
            var target=relation.Value.Target;
            if(relation.Value.Kind==KnowledgeMutationKindV1.Fact&&current is not null)return RejectedAfterRollback("knowledge.current-conflict");
            if(relation.Value.Kind!=KnowledgeMutationKindV1.Fact&&(current is null||target is null||current.RecordHash!=target.RecordHash||current.RecordId!=target.RecordId||current.RecordVersion!=target.RecordVersion))return RejectedAfterRollback("knowledge.reference-not-current");
            if(target is not null&&!await ExactReferenceExistsAsync(connection,transaction,target,cancellationToken))return RejectedAfterRollback("knowledge.reference-invalid");

            await using(var insert=connection.CreateCommand())
            {
                insert.Transaction=transaction;insert.CommandText="INSERT INTO knowledge_records(record_hash,content_hash,record_id,record_version,knowledge_key,envelope,source_provider,source_dataset,observed_at,effective_at,expires_at,entitlement_ref,schema_version) VALUES($h,$ch,$id,$v,$k,$e,$p,$d,$o,$a,$x,$n,$s)";
                insert.Parameters.AddWithValue("$h",record.RecordHash);insert.Parameters.AddWithValue("$ch",record.ContentHash);insert.Parameters.AddWithValue("$id",record.RecordId);insert.Parameters.AddWithValue("$v",record.RecordVersion);insert.Parameters.AddWithValue("$k",key);insert.Parameters.AddWithValue("$e",FinancialEvidenceCanonicalizerV1.SerializeRecord(record));insert.Parameters.AddWithValue("$p",record.Draft.SourceProvider);insert.Parameters.AddWithValue("$d",record.Draft.SourceUriOrDatasetId);insert.Parameters.AddWithValue("$o",record.ObservedAt.ToString("O",CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$a",record.Draft.EffectiveAt.ToString("O",CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$x",record.Draft.ExpiresAt!.Value.ToString("O",CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$n",record.Draft.EntitlementReference);insert.Parameters.AddWithValue("$s",record.Draft.SchemaVersion);await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using(var append=connection.CreateCommand())
            {
                append.Transaction=transaction;append.CommandText="INSERT INTO knowledge_events(knowledge_key,kind,record_hash,target_record_hash,effective_at) VALUES($k,$t,$r,$x,$a)";append.Parameters.AddWithValue("$k",key);append.Parameters.AddWithValue("$t",relation.Value.Kind.ToString());append.Parameters.AddWithValue("$r",record.RecordHash);append.Parameters.AddWithValue("$x",(object?)target?.RecordHash??DBNull.Value);append.Parameters.AddWithValue("$a",record.Draft.EffectiveAt.ToString("O",CultureInfo.InvariantCulture));await append.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);return new(true,false,"knowledge.persisted");

            KnowledgeMutationResultV1 RejectedAfterRollback(string code){transaction.Rollback();return Rejected(code);}
        }
        catch(OperationCanceledException){try{await transaction.RollbackAsync(CancellationToken.None);}catch{}throw;}
        catch{try{await transaction.RollbackAsync(CancellationToken.None);}catch{}return Rejected("knowledge.persistence-failed");}
    }

    public async Task<KnowledgeQueryResultV1> QueryAsync(FinancialEvidenceRetrievalRequestV1 request,CancellationToken cancellationToken=default)
    {
        try{FinancialEvidenceRetrievalGateV1.ValidateRequest(request);}catch{return Abstain("knowledge.query-invalid");}
        if(request.CollectionTypes.Count!=1)return Abstain("knowledge.query-ambiguous");
        var key=Key(request.CollectionTypes.Single(),request.InstrumentId,request.Venue);
        await using var connection=await OpenAsync(cancellationToken);var chain=await ReadChainAsync(connection,null,key,request.AsOf,cancellationToken);var current=Replay(chain);
        if(current is null)return new(KnowledgeQueryOutcomeV1.Abstained,null,Project(chain),["knowledge.unknown"]);
        var eligibility=_gate.Evaluate(current,request);
        return eligibility.Eligible?new(KnowledgeQueryOutcomeV1.Current,current,Project(chain),[]):new(KnowledgeQueryOutcomeV1.Abstained,null,Project(chain),eligibility.ReasonCodes);
    }

    private void Initialize()
    {
        using var connection=new SqliteConnection(_connectionString);connection.Open();using var command=connection.CreateCommand();command.CommandText="""
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS knowledge_schema(version TEXT PRIMARY KEY CHECK(version='wpe.model-off-knowledge/1.0'));
        INSERT OR IGNORE INTO knowledge_schema(version) VALUES('wpe.model-off-knowledge/1.0');
        CREATE TABLE IF NOT EXISTS knowledge_records(record_hash TEXT PRIMARY KEY,content_hash TEXT NOT NULL,record_id TEXT NOT NULL,record_version TEXT NOT NULL,knowledge_key TEXT NOT NULL,envelope BLOB NOT NULL,source_provider TEXT NOT NULL,source_dataset TEXT NOT NULL,observed_at TEXT NOT NULL,effective_at TEXT NOT NULL,expires_at TEXT NOT NULL,entitlement_ref TEXT NOT NULL,schema_version TEXT NOT NULL,UNIQUE(record_id,record_version));
        CREATE TABLE IF NOT EXISTS knowledge_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,knowledge_key TEXT NOT NULL,kind TEXT NOT NULL CHECK(kind IN ('Fact','Correction','Retraction')),record_hash TEXT NOT NULL REFERENCES knowledge_records(record_hash),target_record_hash TEXT,effective_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_knowledge_events_query ON knowledge_events(knowledge_key,effective_at,sequence);
        CREATE TRIGGER IF NOT EXISTS knowledge_records_no_update BEFORE UPDATE ON knowledge_records BEGIN SELECT RAISE(ABORT,'append-only'); END;
        CREATE TRIGGER IF NOT EXISTS knowledge_records_no_delete BEFORE DELETE ON knowledge_records BEGIN SELECT RAISE(ABORT,'append-only'); END;
        CREATE TRIGGER IF NOT EXISTS knowledge_events_no_update BEFORE UPDATE ON knowledge_events BEGIN SELECT RAISE(ABORT,'append-only'); END;
        CREATE TRIGGER IF NOT EXISTS knowledge_events_no_delete BEFORE DELETE ON knowledge_events BEGIN SELECT RAISE(ABORT,'append-only'); END;
        """;command.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct){var connection=new SqliteConnection(_connectionString);await connection.OpenAsync(ct);await using var q=connection.CreateCommand();q.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000";await q.ExecuteNonQueryAsync(ct);return connection;}
    private static (KnowledgeMutationKindV1 Kind,FinancialEvidenceRecordRefV1? Target)? Relation(FinancialEvidenceRecordV1 record)
    {
        var supersedes=record.Draft.SupersedesRecordRefs;var withdrawal=record.Draft.WithdrawalRecordRef;
        if(withdrawal is not null&&supersedes.Count==0)return(KnowledgeMutationKindV1.Retraction,withdrawal);
        if(withdrawal is null&&supersedes.Count==1)return(KnowledgeMutationKindV1.Correction,supersedes[0]);
        if(withdrawal is null&&supersedes.Count==0)return(KnowledgeMutationKindV1.Fact,null);
        return null;
    }
    private static string Key(FinancialEvidenceRecordV1 record)=>Key(record.Draft.CollectionType,record.Draft.InstrumentId,record.Draft.Venue);
    private static string Key(FinancialEvidenceCollectionTypeV1 type,string instrument,string venue)=>$"{type}|{instrument}|{venue}";
    private static KnowledgeMutationResultV1 Rejected(string code)=>new(false,false,code);
    private static KnowledgeQueryResultV1 Abstain(string code)=>new(KnowledgeQueryOutcomeV1.Abstained,null,[],[code]);
    private static FinancialEvidenceRecordV1? Replay(IReadOnlyList<StoredEvent> chain){FinancialEvidenceRecordV1? current=null;foreach(var item in chain)current=item.Kind switch{KnowledgeMutationKindV1.Fact when current is null=>item.Record,KnowledgeMutationKindV1.Correction when current?.RecordHash==item.TargetRecordHash=>item.Record,KnowledgeMutationKindV1.Retraction when current?.RecordHash==item.TargetRecordHash=>null,_=>current};return current;}
    private static IReadOnlyList<KnowledgeChainEntryV1> Project(IReadOnlyList<StoredEvent> chain)=>chain.Select(x=>new KnowledgeChainEntryV1(x.Sequence,x.Kind,x.Record.RecordId,x.Record.RecordVersion,x.Record.RecordHash,x.TargetRecordHash,x.EffectiveAt)).ToArray();
    private async Task<IReadOnlyList<StoredEvent>> ReadChainAsync(SqliteConnection connection,SqliteTransaction? transaction,string key,DateTimeOffset asOf,CancellationToken ct)
    {
        await using var q=connection.CreateCommand();q.Transaction=transaction;q.CommandText="SELECT e.sequence,e.kind,e.target_record_hash,e.effective_at,r.envelope FROM knowledge_events e JOIN knowledge_records r ON r.record_hash=e.record_hash WHERE e.knowledge_key=$k AND e.effective_at<=$a ORDER BY e.effective_at,e.sequence";q.Parameters.AddWithValue("$k",key);q.Parameters.AddWithValue("$a",asOf.ToString("O",CultureInfo.InvariantCulture));await using var reader=await q.ExecuteReaderAsync(ct);var result=new List<StoredEvent>();while(await reader.ReadAsync(ct)){var record=FinancialEvidenceCanonicalizerV1.DeserializeRecord((byte[])reader[4]);record.VerifyIntegrity();result.Add(new(reader.GetInt64(0),Enum.Parse<KnowledgeMutationKindV1>(reader.GetString(1),false),reader.IsDBNull(2)?null:reader.GetString(2),DateTimeOffset.Parse(reader.GetString(3),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),record));}return result;
    }
    private static async Task<string?> FindIdentityAsync(SqliteConnection c,SqliteTransaction tx,string id,string version,CancellationToken ct){await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="SELECT record_hash FROM knowledge_records WHERE record_id=$i AND record_version=$v";q.Parameters.AddWithValue("$i",id);q.Parameters.AddWithValue("$v",version);return (string?)await q.ExecuteScalarAsync(ct);}
    private static async Task<bool> ExactReferenceExistsAsync(SqliteConnection c,SqliteTransaction tx,FinancialEvidenceRecordRefV1 reference,CancellationToken ct){await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="SELECT COUNT(*) FROM knowledge_records WHERE record_id=$i AND record_version=$v AND record_hash=$r AND content_hash=$h";q.Parameters.AddWithValue("$i",reference.RecordId);q.Parameters.AddWithValue("$v",reference.RecordVersion);q.Parameters.AddWithValue("$r",reference.RecordHash);q.Parameters.AddWithValue("$h",reference.ContentHash);return Convert.ToInt64(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;}
    private sealed record StoredEvent(long Sequence,KnowledgeMutationKindV1 Kind,string? TargetRecordHash,DateTimeOffset EffectiveAt,FinancialEvidenceRecordV1 Record);
}

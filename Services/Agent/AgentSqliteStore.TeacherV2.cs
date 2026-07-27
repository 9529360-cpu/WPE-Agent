using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    private void InitializeTeacherV2()
    {
        using var c=new SqliteConnection(_cs);c.Open();using var q=c.CreateCommand();q.CommandText="""
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS teacher_lessons(lesson_id TEXT PRIMARY KEY,schema TEXT NOT NULL,kind TEXT NOT NULL,timezone_id TEXT NOT NULL,scheduled_for_utc TEXT NOT NULL,generated_at_utc TEXT NOT NULL,language TEXT NOT NULL,teaching_level TEXT NOT NULL,persona_version TEXT NOT NULL,content_sha256 TEXT NOT NULL,execution_authority INTEGER NOT NULL CHECK(execution_authority=0),canonical_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS teacher_lesson_evidence(lesson_id TEXT NOT NULL REFERENCES teacher_lessons(lesson_id),agent TEXT NOT NULL,output_id TEXT NOT NULL,cycle_id TEXT NOT NULL,canonical_sha256 TEXT NOT NULL,as_of_utc TEXT NOT NULL,availability TEXT NOT NULL,PRIMARY KEY(lesson_id,agent));
        CREATE TABLE IF NOT EXISTS teacher_recommendations(recommendation_id TEXT NOT NULL,version INTEGER NOT NULL,state TEXT NOT NULL,issued_at_utc TEXT NOT NULL,expires_at_utc TEXT NOT NULL,execution_authority INTEGER NOT NULL CHECK(execution_authority=0),supersedes_id TEXT,canonical_json TEXT NOT NULL,PRIMARY KEY(recommendation_id,version));
        CREATE TABLE IF NOT EXISTS teacher_corrections(correction_id TEXT PRIMARY KEY,superseded_lesson_id TEXT NOT NULL REFERENCES teacher_lessons(lesson_id),issued_at_utc TEXT NOT NULL,reason_code TEXT NOT NULL,sha256 TEXT NOT NULL,canonical_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS teacher_delivery_events(id INTEGER PRIMARY KEY AUTOINCREMENT,delivery_id TEXT NOT NULL,lesson_id TEXT NOT NULL REFERENCES teacher_lessons(lesson_id),destination_kind TEXT NOT NULL,state TEXT NOT NULL,attempt INTEGER NOT NULL,recorded_at_utc TEXT NOT NULL,reason_code TEXT NOT NULL,UNIQUE(delivery_id,attempt));
        CREATE TABLE IF NOT EXISTS teacher_memories(memory_id TEXT PRIMARY KEY,tier TEXT NOT NULL,recorded_at_utc TEXT NOT NULL,expires_at_utc TEXT,kind TEXT NOT NULL,canonical_reference TEXT NOT NULL,summary_hash TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS teacher_public_evidence(evidence_hash TEXT PRIMARY KEY,schema TEXT NOT NULL,source_id TEXT NOT NULL,identity TEXT NOT NULL,observed_at_utc TEXT NOT NULL,retrieved_at_utc TEXT NOT NULL,status TEXT NOT NULL,canonical_json TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_teacher_public_evidence_latest ON teacher_public_evidence(source_id,identity,observed_at_utc DESC);
        CREATE TABLE IF NOT EXISTS teacher_source_health_events(id INTEGER PRIMARY KEY AUTOINCREMENT,source_id TEXT NOT NULL,checked_at_utc TEXT NOT NULL,status TEXT NOT NULL,diagnostic_code TEXT NOT NULL,evidence_hash TEXT,UNIQUE(source_id,checked_at_utc,diagnostic_code));
        CREATE TRIGGER IF NOT EXISTS teacher_lessons_no_update BEFORE UPDATE ON teacher_lessons BEGIN SELECT RAISE(ABORT,'teacher lessons are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_lessons_no_delete BEFORE DELETE ON teacher_lessons BEGIN SELECT RAISE(ABORT,'teacher lessons are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_lesson_evidence_no_update BEFORE UPDATE ON teacher_lesson_evidence BEGIN SELECT RAISE(ABORT,'teacher evidence is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_lesson_evidence_no_delete BEFORE DELETE ON teacher_lesson_evidence BEGIN SELECT RAISE(ABORT,'teacher evidence is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_recommendations_no_update BEFORE UPDATE ON teacher_recommendations BEGIN SELECT RAISE(ABORT,'teacher recommendations are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_recommendations_no_delete BEFORE DELETE ON teacher_recommendations BEGIN SELECT RAISE(ABORT,'teacher recommendations are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_corrections_no_update BEFORE UPDATE ON teacher_corrections BEGIN SELECT RAISE(ABORT,'teacher corrections are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_corrections_no_delete BEFORE DELETE ON teacher_corrections BEGIN SELECT RAISE(ABORT,'teacher corrections are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_delivery_events_no_update BEFORE UPDATE ON teacher_delivery_events BEGIN SELECT RAISE(ABORT,'teacher delivery events are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_delivery_events_no_delete BEFORE DELETE ON teacher_delivery_events BEGIN SELECT RAISE(ABORT,'teacher delivery events are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_memories_no_update BEFORE UPDATE ON teacher_memories BEGIN SELECT RAISE(ABORT,'teacher memories are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_memories_no_delete BEFORE DELETE ON teacher_memories BEGIN SELECT RAISE(ABORT,'teacher memories are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_public_evidence_no_update BEFORE UPDATE ON teacher_public_evidence BEGIN SELECT RAISE(ABORT,'teacher public evidence is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_public_evidence_no_delete BEFORE DELETE ON teacher_public_evidence BEGIN SELECT RAISE(ABORT,'teacher public evidence is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_source_health_no_update BEFORE UPDATE ON teacher_source_health_events BEGIN SELECT RAISE(ABORT,'teacher source health is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_source_health_no_delete BEFORE DELETE ON teacher_source_health_events BEGIN SELECT RAISE(ABORT,'teacher source health is append-only'); END;
        """;q.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<TeacherEvidenceReferenceV2>> GetTeacherEvidenceForCycleAsync(string cycleId,DateTimeOffset asOfUtc,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(cycleId))throw new ArgumentException("Cycle id is required.",nameof(cycleId));
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT output_kind,output_id,cycle_id,canonical_sha256,as_of_utc,canonical_bytes,status FROM model_off_canonical_audits WHERE cycle_id=$cycle ORDER BY output_kind";q.Parameters.AddWithValue("$cycle",cycleId);await using var r=await q.ExecuteReaderAsync(ct);var result=new List<TeacherEvidenceReferenceV2>();var evaluation=asOfUtc.ToUniversalTime();
        while(await r.ReadAsync(ct)){var hash=r.GetString(3);var bytes=(byte[])r[5];var observed=DateTimeOffset.Parse(r.GetString(4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();var computed=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();var available=string.Equals(hash,computed,StringComparison.Ordinal)&&string.Equals(r.GetString(6),"succeeded",StringComparison.Ordinal)&&observed<=evaluation&&evaluation-observed<=TimeSpan.FromMinutes(30);var availability=available?TeacherEvidenceAvailabilityV2.Available:TeacherEvidenceAvailabilityV2.Invalid;result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),hash,observed,availability));}
        return result;
    }

    public async Task<TeacherPersistenceResult> SaveTeacherLessonAsync(TeacherLessonV2 lesson,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lesson);if(lesson.ExecutionAuthority||lesson.Schema!="wpe.teacher-lesson/2.0")return new(false,false,"teacher.lesson-invalid");MarketTeacherComposerV2.ValidateEvidence(lesson.Evidence,lesson.ScheduledForUtc);
        var json=JsonSerializer.Serialize(lesson);await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);await using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT OR IGNORE INTO teacher_lessons VALUES($id,$schema,$kind,$zone,$scheduled,$generated,$language,$level,$persona,$hash,0,$json)";AddParameters(q,("$id",lesson.LessonId),("$schema",lesson.Schema),("$kind",lesson.Kind.ToString()),("$zone",lesson.TimeZoneId),("$scheduled",DbInstant(lesson.ScheduledForUtc)),("$generated",DbInstant(lesson.GeneratedAtUtc)),("$language",lesson.Language),("$level",lesson.TeachingLevel),("$persona",lesson.PersonaVersion),("$hash",lesson.ContentSha256),("$json",json));if(await q.ExecuteNonQueryAsync(ct)==0){await using var check=c.CreateCommand();check.Transaction=tx;check.CommandText="SELECT canonical_json FROM teacher_lessons WHERE lesson_id=$id";check.Parameters.AddWithValue("$id",lesson.LessonId);var existing=(string?)await check.ExecuteScalarAsync(ct);await tx.RollbackAsync(ct);return string.Equals(existing,json,StringComparison.Ordinal)?new(true,true,"teacher.lesson-idempotent"):new(false,false,"teacher.lesson-identity-conflict");}}
        foreach(var e in lesson.Evidence){await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT INTO teacher_lesson_evidence VALUES($lesson,$agent,$output,$cycle,$hash,$asof,$availability)";AddParameters(q,("$lesson",lesson.LessonId),("$agent",e.Agent),("$output",e.OutputId),("$cycle",e.CycleId),("$hash",e.CanonicalSha256),("$asof",DbInstant(e.AsOfUtc)),("$availability",e.Availability.ToString()));await q.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);return new(true,false,"teacher.lesson-persisted");
    }

    public async Task<TeacherLessonV2?> GetTeacherLessonAsync(string lessonId,CancellationToken ct){await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT canonical_json FROM teacher_lessons WHERE lesson_id=$id";q.Parameters.AddWithValue("$id",lessonId);return await q.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<TeacherLessonV2>(json):null;}

    public async Task<TeacherPersistenceResult> SaveTeacherRecommendationAsync(TeacherRecommendationV2 value,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);if(value.ExecutionAuthority||value.Schema!="wpe.teacher-recommendation/2.0"||value.Version<1||value.ExpiresAtUtc<value.IssuedAtUtc)return new(false,false,"teacher.recommendation-invalid");var json=JsonSerializer.Serialize(value);
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO teacher_recommendations VALUES($id,$version,$state,$issued,$expires,0,$supersedes,$json); SELECT changes();";AddParameters(q,("$id",value.RecommendationId),("$version",value.Version),("$state",value.State.ToString()),("$issued",DbInstant(value.IssuedAtUtc)),("$expires",DbInstant(value.ExpiresAtUtc)),("$supersedes",value.SupersedesId),("$json",json));if(Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1)return new(true,false,"teacher.recommendation-persisted");
        await using var check=c.CreateCommand();check.CommandText="SELECT canonical_json FROM teacher_recommendations WHERE recommendation_id=$id AND version=$version";AddParameters(check,("$id",value.RecommendationId),("$version",value.Version));return string.Equals((string?)await check.ExecuteScalarAsync(ct),json,StringComparison.Ordinal)?new(true,true,"teacher.recommendation-idempotent"):new(false,false,"teacher.recommendation-identity-conflict");
    }

    public async Task<TeacherPersistenceResult> SaveTeacherCorrectionAsync(TeacherCorrectionV2 value,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);var json=JsonSerializer.Serialize(value);await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO teacher_corrections VALUES($id,$lesson,$issued,$reason,$hash,$json); SELECT changes();";AddParameters(q,("$id",value.CorrectionId),("$lesson",value.SupersededLessonId),("$issued",DbInstant(value.IssuedAtUtc)),("$reason",value.ReasonCode),("$hash",value.Sha256),("$json",json));return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1?new(true,false,"teacher.correction-persisted"):new(true,true,"teacher.correction-idempotent");
    }

    public async Task<TeacherPersistenceResult> SaveTeacherCryptoEvidenceAsync(TeacherCryptoMarketFactV2 value,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);if(!TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(value))return new(false,false,"teacher.evidence-invalid");var json=JsonSerializer.Serialize(value);await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT OR IGNORE INTO teacher_public_evidence VALUES($hash,$schema,$source,$identity,$observed,$retrieved,$status,$json); SELECT changes();";AddParameters(q,("$hash",value.CanonicalSha256),("$schema",value.Schema),("$source",value.SourceId),("$identity",value.Symbol),("$observed",DbInstant(value.ObservedAtUtc)),("$retrieved",DbInstant(value.RetrievedAtUtc)),("$status",value.Status),("$json",json));var inserted=Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;if(!inserted){await tx.RollbackAsync(ct);return new(true,true,"teacher.evidence-idempotent");}}
        await using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT INTO teacher_source_health_events(source_id,checked_at_utc,status,diagnostic_code,evidence_hash) VALUES($source,$checked,'available',$code,$hash)";AddParameters(q,("$source",value.SourceId),("$checked",DbInstant(value.RetrievedAtUtc)),("$code",value.DiagnosticCode),("$hash",value.CanonicalSha256));await q.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);return new(true,false,"teacher.evidence-persisted");
    }
    public async Task<TeacherCryptoMarketFactV2?> GetLatestTeacherCryptoEvidenceAsync(string symbol,DateTimeOffset asOfUtc,TimeSpan maximumAge,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT canonical_json FROM teacher_public_evidence WHERE source_id='binance-futures-public' AND identity=$identity AND observed_at_utc<=$asof ORDER BY observed_at_utc DESC LIMIT 1";AddParameters(q,("$identity",symbol.ToUpperInvariant()),("$asof",DbInstant(asOfUtc)));if(await q.ExecuteScalarAsync(ct) is not string json)return null;var value=JsonSerializer.Deserialize<TeacherCryptoMarketFactV2>(json);return value is not null&&TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(value)&&asOfUtc.ToUniversalTime()-value.ObservedAtUtc<=maximumAge?value:null;
    }
    public async Task RecordTeacherSourceHealthAsync(string sourceId,DateTimeOffset checkedAtUtc,string status,string diagnosticCode,string? evidenceHash,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO teacher_source_health_events(source_id,checked_at_utc,status,diagnostic_code,evidence_hash) VALUES($source,$checked,$status,$code,$hash)";AddParameters(q,("$source",sourceId),("$checked",DbInstant(checkedAtUtc)),("$status",status),("$code",diagnosticCode),("$hash",evidenceHash));await q.ExecuteNonQueryAsync(ct);
    }
}

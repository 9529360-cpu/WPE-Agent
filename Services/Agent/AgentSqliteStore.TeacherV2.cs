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
        CREATE TABLE IF NOT EXISTS teacher_event_fingerprints(fingerprint TEXT PRIMARY KEY,event_id TEXT NOT NULL,first_seen_at_utc TEXT NOT NULL,expires_at_utc TEXT NOT NULL,evidence_hash TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS teacher_recommendation_outcomes(outcome_id TEXT PRIMARY KEY,recommendation_id TEXT NOT NULL,recommendation_version INTEGER NOT NULL,evaluated_at_utc TEXT NOT NULL,process_state TEXT NOT NULL,canonical_sha256 TEXT NOT NULL,canonical_json TEXT NOT NULL,UNIQUE(recommendation_id,recommendation_version,evaluated_at_utc));
        CREATE TABLE IF NOT EXISTS teacher_public_evidence_corrections(correction_id TEXT PRIMARY KEY,source_id TEXT NOT NULL,identity TEXT NOT NULL,superseded_hash TEXT NOT NULL UNIQUE,replacement_hash TEXT NOT NULL UNIQUE,issued_at_utc TEXT NOT NULL,reason_code TEXT NOT NULL,canonical_sha256 TEXT NOT NULL,FOREIGN KEY(superseded_hash) REFERENCES teacher_public_evidence(evidence_hash),FOREIGN KEY(replacement_hash) REFERENCES teacher_public_evidence(evidence_hash));
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
        CREATE TRIGGER IF NOT EXISTS teacher_event_fingerprints_no_update BEFORE UPDATE ON teacher_event_fingerprints BEGIN SELECT RAISE(ABORT,'teacher event fingerprints are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_event_fingerprints_no_delete BEFORE DELETE ON teacher_event_fingerprints BEGIN SELECT RAISE(ABORT,'teacher event fingerprints are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_recommendation_outcomes_no_update BEFORE UPDATE ON teacher_recommendation_outcomes BEGIN SELECT RAISE(ABORT,'teacher recommendation outcomes are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_recommendation_outcomes_no_delete BEFORE DELETE ON teacher_recommendation_outcomes BEGIN SELECT RAISE(ABORT,'teacher recommendation outcomes are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_public_evidence_corrections_no_update BEFORE UPDATE ON teacher_public_evidence_corrections BEGIN SELECT RAISE(ABORT,'teacher public evidence corrections are append-only'); END;
        CREATE TRIGGER IF NOT EXISTS teacher_public_evidence_corrections_no_delete BEFORE DELETE ON teacher_public_evidence_corrections BEGIN SELECT RAISE(ABORT,'teacher public evidence corrections are append-only'); END;
        """;q.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<TeacherEvidenceReferenceV2>> GetTeacherEvidenceForCycleAsync(string cycleId,DateTimeOffset asOfUtc,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(cycleId))throw new ArgumentException("Cycle id is required.",nameof(cycleId));
        var evaluation=asOfUtc.ToUniversalTime();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
        WITH ranked AS (
            SELECT output_kind,output_id,cycle_id,canonical_sha256,as_of_utc,canonical_bytes,status,
                   ROW_NUMBER() OVER(PARTITION BY output_kind ORDER BY as_of_utc DESC,recorded_at_utc DESC,output_id DESC) AS rn
            FROM model_off_canonical_audits
            WHERE cycle_id=$cycle AND as_of_utc<=$asof AND recorded_at_utc<=$asof
        )
        SELECT output_kind,output_id,cycle_id,canonical_sha256,as_of_utc,canonical_bytes,status
        FROM ranked WHERE rn=1 ORDER BY output_kind
        """;q.Parameters.AddWithValue("$cycle",cycleId);q.Parameters.AddWithValue("$asof",DbInstant(evaluation));await using var r=await q.ExecuteReaderAsync(ct);var result=new List<TeacherEvidenceReferenceV2>();
        while(await r.ReadAsync(ct)){var hash=r.GetString(3);var bytes=(byte[])r[5];var observed=DateTimeOffset.Parse(r.GetString(4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();var computed=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();var available=string.Equals(hash,computed,StringComparison.Ordinal)&&string.Equals(r.GetString(6),"succeeded",StringComparison.Ordinal)&&observed<=evaluation&&evaluation-observed<=TimeSpan.FromMinutes(30);var availability=available?TeacherEvidenceAvailabilityV2.Available:TeacherEvidenceAvailabilityV2.Invalid;result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),hash,observed,availability));}
        return result;
    }
    public async Task<IReadOnlyList<string>> GetRecentTeacherCandidateCycleIdsAsync(DateTimeOffset asOfUtc,int limit,CancellationToken ct)
    {
        var evaluation=asOfUtc.ToUniversalTime();var floor=evaluation-TimeSpan.FromMinutes(30);var result=new List<string>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
        SELECT cycle_id
        FROM model_off_canonical_audits
        WHERE as_of_utc<=$asof AND recorded_at_utc<=$asof
        GROUP BY cycle_id
        HAVING MAX(as_of_utc)>=$floor
        ORDER BY MAX(as_of_utc) DESC,MAX(recorded_at_utc) DESC,cycle_id DESC
        LIMIT $limit
        """;q.Parameters.AddWithValue("$asof",DbInstant(evaluation));q.Parameters.AddWithValue("$floor",DbInstant(floor));q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result.Add(r.GetString(0));return result;
    }

    public async Task<TeacherPersistenceResult> SaveTeacherLessonAsync(TeacherLessonV2 lesson,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lesson);if(lesson.ExecutionAuthority||lesson.Schema!="wpe.teacher-lesson/2.0")return new(false,false,"teacher.lesson-invalid");MarketTeacherComposerV2.ValidateEvidence(lesson.Evidence,lesson.ScheduledForUtc);
        var json=JsonSerializer.Serialize(lesson);await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);await using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT OR IGNORE INTO teacher_lessons VALUES($id,$schema,$kind,$zone,$scheduled,$generated,$language,$level,$persona,$hash,0,$json)";AddParameters(q,("$id",lesson.LessonId),("$schema",lesson.Schema),("$kind",lesson.Kind.ToString()),("$zone",lesson.TimeZoneId),("$scheduled",DbInstant(lesson.ScheduledForUtc)),("$generated",DbInstant(lesson.GeneratedAtUtc)),("$language",lesson.Language),("$level",lesson.TeachingLevel),("$persona",lesson.PersonaVersion),("$hash",lesson.ContentSha256),("$json",json));if(await q.ExecuteNonQueryAsync(ct)==0){await using var check=c.CreateCommand();check.Transaction=tx;check.CommandText="SELECT canonical_json FROM teacher_lessons WHERE lesson_id=$id";check.Parameters.AddWithValue("$id",lesson.LessonId);var existing=(string?)await check.ExecuteScalarAsync(ct);await tx.RollbackAsync(ct);return string.Equals(existing,json,StringComparison.Ordinal)?new(true,true,"teacher.lesson-idempotent"):new(false,false,"teacher.lesson-identity-conflict");}}
        foreach(var e in lesson.Evidence){await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT INTO teacher_lesson_evidence VALUES($lesson,$agent,$output,$cycle,$hash,$asof,$availability)";AddParameters(q,("$lesson",lesson.LessonId),("$agent",e.Agent),("$output",e.OutputId),("$cycle",e.CycleId),("$hash",e.CanonicalSha256),("$asof",DbInstant(e.AsOfUtc)),("$availability",e.Availability.ToString()));await q.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);return new(true,false,"teacher.lesson-persisted");
    }

    public async Task<TeacherLessonV2?> GetTeacherLessonAsync(string lessonId,CancellationToken ct){await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT canonical_json FROM teacher_lessons WHERE lesson_id=$id";q.Parameters.AddWithValue("$id",lessonId);return await q.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<TeacherLessonV2>(json):null;}

    public async Task<TeacherRuntimeContentV2> GetTeacherRuntimeContentAsync(int limit,CancellationToken ct)
    {
        if(limit is <1 or >100)throw new ArgumentOutOfRangeException(nameof(limit));
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);
        async Task<IReadOnlyList<T>> ReadAsync<T>(string sql)
        {
            await using var q=c.CreateCommand();q.CommandText=sql;q.Parameters.AddWithValue("$limit",limit);await using var r=await q.ExecuteReaderAsync(ct);var rows=new List<T>();
            while(await r.ReadAsync(ct)){var value=JsonSerializer.Deserialize<T>(r.GetString(0));if(value is not null)rows.Add(value);}
            return rows;
        }
        var lessons=await ReadAsync<TeacherLessonV2>("SELECT canonical_json FROM teacher_lessons WHERE execution_authority=0 ORDER BY generated_at_utc DESC LIMIT $limit");
        var recommendations=await ReadAsync<TeacherRecommendationV2>("SELECT r.canonical_json FROM teacher_recommendations r WHERE r.execution_authority=0 AND r.version=(SELECT MAX(x.version) FROM teacher_recommendations x WHERE x.recommendation_id=r.recommendation_id) ORDER BY r.issued_at_utc DESC LIMIT $limit");
        var corrections=await ReadAsync<TeacherCorrectionV2>("SELECT canonical_json FROM teacher_corrections ORDER BY issued_at_utc DESC LIMIT $limit");
        var outcomes=await ReadAsync<TeacherRecommendationOutcomeV2>("SELECT canonical_json FROM teacher_recommendation_outcomes ORDER BY evaluated_at_utc DESC LIMIT $limit");
        return new(lessons.Where(x=>!x.ExecutionAuthority).ToArray(),recommendations.Where(x=>!x.ExecutionAuthority).ToArray(),corrections,outcomes);
    }

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
        ArgumentNullException.ThrowIfNull(value);if(!TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(value))return new(false,false,"teacher.evidence-invalid");var json=JsonSerializer.Serialize(value);await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);await using(var conflict=c.CreateCommand()){conflict.Transaction=tx;conflict.CommandText="SELECT evidence_hash FROM teacher_public_evidence WHERE source_id=$source AND identity=$identity AND observed_at_utc=$observed LIMIT 1";AddParameters(conflict,("$source",value.SourceId),("$identity",value.Symbol),("$observed",DbInstant(value.ObservedAtUtc)));if(await conflict.ExecuteScalarAsync(ct) is string existing&&!string.Equals(existing,value.CanonicalSha256,StringComparison.Ordinal)){await tx.RollbackAsync(ct);return new(false,false,"teacher.evidence-conflict");}}
        await using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT OR IGNORE INTO teacher_public_evidence VALUES($hash,$schema,$source,$identity,$observed,$retrieved,$status,$json); SELECT changes();";AddParameters(q,("$hash",value.CanonicalSha256),("$schema",value.Schema),("$source",value.SourceId),("$identity",value.Symbol),("$observed",DbInstant(value.ObservedAtUtc)),("$retrieved",DbInstant(value.RetrievedAtUtc)),("$status",value.Status),("$json",json));var inserted=Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;if(!inserted){await tx.RollbackAsync(ct);return new(true,true,"teacher.evidence-idempotent");}}
        await using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT INTO teacher_source_health_events(source_id,checked_at_utc,status,diagnostic_code,evidence_hash) VALUES($source,$checked,'available',$code,$hash)";AddParameters(q,("$source",value.SourceId),("$checked",DbInstant(value.RetrievedAtUtc)),("$code",value.DiagnosticCode),("$hash",value.CanonicalSha256));await q.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);return new(true,false,"teacher.evidence-persisted");
    }
    public async Task<TeacherCryptoMarketFactV2?> GetLatestTeacherCryptoEvidenceAsync(string symbol,DateTimeOffset asOfUtc,TimeSpan maximumAge,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT e.canonical_json FROM teacher_public_evidence e WHERE e.source_id='binance-futures-public' AND e.identity=$identity AND e.observed_at_utc<=$asof AND e.retrieved_at_utc<=$asof AND NOT EXISTS(SELECT 1 FROM teacher_public_evidence_corrections c WHERE c.superseded_hash=e.evidence_hash AND c.issued_at_utc<=$asof) ORDER BY e.observed_at_utc DESC,e.retrieved_at_utc DESC LIMIT 1";AddParameters(q,("$identity",symbol.ToUpperInvariant()),("$asof",DbInstant(asOfUtc)));if(await q.ExecuteScalarAsync(ct) is not string json)return null;var value=JsonSerializer.Deserialize<TeacherCryptoMarketFactV2>(json);return value is not null&&TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(value)&&asOfUtc.ToUniversalTime()-value.ObservedAtUtc<=maximumAge?value:null;
    }
    public async Task RecordTeacherSourceHealthAsync(string sourceId,DateTimeOffset checkedAtUtc,string status,string diagnosticCode,string? evidenceHash,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO teacher_source_health_events(source_id,checked_at_utc,status,diagnostic_code,evidence_hash) VALUES($source,$checked,$status,$code,$hash)";AddParameters(q,("$source",sourceId),("$checked",DbInstant(checkedAtUtc)),("$status",status),("$code",diagnosticCode),("$hash",evidenceHash));await q.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> TryClaimTeacherEventAsync(TeacherMarketEventV2 value,TimeSpan deduplicationWindow,CancellationToken ct)
    {
        if(value.Schema!="wpe.teacher-market-event/2.0"||value.Fingerprint.Length!=64||deduplicationWindow<=TimeSpan.Zero)return false;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO teacher_event_fingerprints VALUES($fingerprint,$event,$first,$expires,$hash); SELECT changes();";AddParameters(q,("$fingerprint",value.Fingerprint),("$event",value.EventId),("$first",DbInstant(value.ObservedAtUtc)),("$expires",DbInstant(value.ObservedAtUtc.Add(deduplicationWindow))),("$hash",value.CurrentEvidenceHash));return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }
    public async Task<string?> GetLatestTeacherEligibleCycleIdAsync(CancellationToken ct)
    {
        var now=_utcNow().ToUniversalTime();foreach(var cycleId in await GetRecentTeacherCandidateCycleIdsAsync(now,32,ct))
        {
            var evidence=await GetTeacherEvidenceForCycleAsync(cycleId,now,ct);
            if(MarketTeacherRuntimeV2.IsEvidenceEligible(evidence,now))return cycleId;
        }
        return null;
    }
    public async Task<bool> TryQueueTeacherDeliveryAsync(string deliveryId,string lessonId,string destinationKind,DateTimeOffset recordedAtUtc,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO teacher_delivery_events(delivery_id,lesson_id,destination_kind,state,attempt,recorded_at_utc,reason_code) VALUES($delivery,$lesson,$destination,'queued',1,$recorded,'teacher.delivery.queued'); SELECT changes();";AddParameters(q,("$delivery",deliveryId),("$lesson",lessonId),("$destination",destinationKind),("$recorded",DbInstant(recordedAtUtc)));return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }
    public async Task<IReadOnlyList<TeacherCryptoMarketFactV2>> GetTeacherCryptoEvidenceRangeAsync(string symbol,DateTimeOffset fromUtc,DateTimeOffset asOfUtc,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT e.canonical_json FROM teacher_public_evidence e WHERE e.source_id='binance-futures-public' AND e.identity=$identity AND e.observed_at_utc>=$from AND e.observed_at_utc<=$asof AND e.retrieved_at_utc<=$asof AND NOT EXISTS(SELECT 1 FROM teacher_public_evidence_corrections c WHERE c.superseded_hash=e.evidence_hash AND c.issued_at_utc<=$asof) ORDER BY e.observed_at_utc,e.retrieved_at_utc";AddParameters(q,("$identity",symbol.ToUpperInvariant()),("$from",DbInstant(fromUtc)),("$asof",DbInstant(asOfUtc)));await using var r=await q.ExecuteReaderAsync(ct);var result=new List<TeacherCryptoMarketFactV2>();while(await r.ReadAsync(ct)){var value=JsonSerializer.Deserialize<TeacherCryptoMarketFactV2>(r.GetString(0));if(value is not null&&TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(value))result.Add(value);}return result;
    }
    public async Task<TeacherPersistenceResult> SaveTeacherRecommendationOutcomeAsync(TeacherRecommendationOutcomeV2 value,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);if(value.Schema!="wpe.teacher-recommendation-outcome/2.0"||value.CanonicalSha256.Length!=64||value.ProcessState is not("invalidated" or "conditions-followed"))return new(false,false,"teacher.outcome-invalid");var json=JsonSerializer.Serialize(value);await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO teacher_recommendation_outcomes VALUES($id,$recommendation,$version,$evaluated,$state,$hash,$json); SELECT changes();";AddParameters(q,("$id",value.OutcomeId),("$recommendation",value.RecommendationId),("$version",value.RecommendationVersion),("$evaluated",DbInstant(value.EvaluatedAtUtc)),("$state",value.ProcessState),("$hash",value.CanonicalSha256),("$json",json));return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1?new(true,false,"teacher.outcome-persisted"):new(true,true,"teacher.outcome-idempotent");
    }
    public async Task<TeacherCryptoMarketFactV2?> GetTeacherCryptoEvidenceByHashAsync(string hash,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT canonical_json FROM teacher_public_evidence WHERE evidence_hash=$hash";q.Parameters.AddWithValue("$hash",hash);if(await q.ExecuteScalarAsync(ct) is not string json)return null;var value=JsonSerializer.Deserialize<TeacherCryptoMarketFactV2>(json);return value is not null&&TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(value)?value:null;
    }
    public async Task<IReadOnlyList<TeacherRecommendationV2>> GetDueTeacherRecommendationsAsync(DateTimeOffset asOfUtc,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT r.canonical_json FROM teacher_recommendations r LEFT JOIN teacher_recommendation_outcomes o ON o.recommendation_id=r.recommendation_id AND o.recommendation_version=r.version WHERE r.expires_at_utc<=$asof AND r.state NOT IN ('Expired','Unavailable') AND o.outcome_id IS NULL ORDER BY r.expires_at_utc LIMIT 50";q.Parameters.AddWithValue("$asof",DbInstant(asOfUtc));await using var r=await q.ExecuteReaderAsync(ct);var result=new List<TeacherRecommendationV2>();while(await r.ReadAsync(ct)){var value=JsonSerializer.Deserialize<TeacherRecommendationV2>(r.GetString(0));if(value is not null&&!value.ExecutionAuthority)result.Add(value);}return result;
    }
    public async Task<TeacherPersistenceResult> SaveTeacherPublicEvidenceCorrectionAsync(TeacherPublicEvidenceCorrectionV2 correction,TeacherCryptoMarketFactV2 replacement,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correction);ArgumentNullException.ThrowIfNull(replacement);if(!TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(replacement)||correction.Schema!="wpe.teacher-public-evidence-correction/2.0"||correction.ReplacementHash!=replacement.CanonicalSha256)return new(false,false,"teacher.correction-invalid");await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);await using(var check=c.CreateCommand()){check.Transaction=tx;check.CommandText="SELECT canonical_json FROM teacher_public_evidence WHERE evidence_hash=$hash";check.Parameters.AddWithValue("$hash",correction.SupersededHash);if(await check.ExecuteScalarAsync(ct) is not string oldJson){await tx.RollbackAsync(ct);return new(false,false,"teacher.correction-superseded-missing");}var old=JsonSerializer.Deserialize<TeacherCryptoMarketFactV2>(oldJson);if(old is null){await tx.RollbackAsync(ct);return new(false,false,"teacher.correction-superseded-invalid");}var expected=TeacherPublicEvidenceCorrectionCanonicalizerV2.Create(old,replacement,correction.IssuedAtUtc,correction.ReasonCode);if(expected!=correction){await tx.RollbackAsync(ct);return new(false,false,"teacher.correction-invalid");}}
        await using(var insert=c.CreateCommand()){insert.Transaction=tx;insert.CommandText="INSERT OR IGNORE INTO teacher_public_evidence VALUES($hash,$schema,$source,$identity,$observed,$retrieved,$status,$json)";AddParameters(insert,("$hash",replacement.CanonicalSha256),("$schema",replacement.Schema),("$source",replacement.SourceId),("$identity",replacement.Symbol),("$observed",DbInstant(replacement.ObservedAtUtc)),("$retrieved",DbInstant(replacement.RetrievedAtUtc)),("$status",replacement.Status),("$json",JsonSerializer.Serialize(replacement)));await insert.ExecuteNonQueryAsync(ct);}await using(var insert=c.CreateCommand()){insert.Transaction=tx;insert.CommandText="INSERT OR IGNORE INTO teacher_public_evidence_corrections VALUES($id,$source,$identity,$old,$new,$issued,$reason,$hash); SELECT changes();";AddParameters(insert,("$id",correction.CorrectionId),("$source",correction.SourceId),("$identity",correction.Identity),("$old",correction.SupersededHash),("$new",correction.ReplacementHash),("$issued",DbInstant(correction.IssuedAtUtc)),("$reason",correction.ReasonCode),("$hash",correction.CanonicalSha256));var changed=Convert.ToInt32(await insert.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);await tx.CommitAsync(ct);return changed==1?new(true,false,"teacher.correction-persisted"):new(true,true,"teacher.correction-idempotent");}
    }
}

public sealed record TeacherRuntimeContentV2(IReadOnlyList<TeacherLessonV2> Lessons,IReadOnlyList<TeacherRecommendationV2> Recommendations,IReadOnlyList<TeacherCorrectionV2> Corrections,IReadOnlyList<TeacherRecommendationOutcomeV2> Outcomes);

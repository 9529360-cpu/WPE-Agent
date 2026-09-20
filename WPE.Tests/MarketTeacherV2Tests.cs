using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using 币安量化机器人.Services.Agent;
using WpeAgent.Notifications;

namespace WPE.Tests;

public sealed class MarketTeacherV2Tests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,27,6,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-teacher-v2-"+Guid.NewGuid().ToString("N"));
    private string Db=>Path.Combine(_directory,"agent.db");
    public MarketTeacherV2Tests()=>Directory.CreateDirectory(_directory);
    public void Dispose(){SqliteConnection.ClearAllPools();try{Directory.Delete(_directory,true);}catch(IOException){}}

    [Fact] public void PersonaAndLessonHaveNoTradingAuthority()
    {
        var lesson=MarketTeacherComposerV2.Compose(TeacherLessonKindV2.Afternoon,Now,Refs(),generatedAtUtc:Now);
        Assert.Equal(68,MarketTeacherPersonaV2.Default.PersonaAge);Assert.Equal(45,MarketTeacherPersonaV2.Default.ExperienceYears);Assert.False(lesson.ExecutionAuthority);Assert.Equal(7,lesson.Evidence.Count);Assert.Contains("没有下单",string.Join('\n',lesson.Blocks.Select(x=>x.Content)));Assert.All(lesson.Evidence,x=>Assert.Contains(x.CanonicalSha256,lesson.Blocks.SelectMany(b=>b.EvidenceHashes)));
    }

    [Fact] public void ScheduleUsesBeijingWindows()
    {
        var morning=MarketTeacherScheduleV2.Next(new DateTimeOffset(2026,7,26,23,0,0,TimeSpan.Zero));Assert.Equal(TeacherLessonKindV2.Morning,morning.Kind);Assert.Equal(new DateTimeOffset(2026,7,27,0,0,0,TimeSpan.Zero),morning.ScheduledForUtc);
        var afternoon=MarketTeacherScheduleV2.Next(new DateTimeOffset(2026,7,27,4,0,0,TimeSpan.Zero));Assert.Equal(TeacherLessonKindV2.Afternoon,afternoon.Kind);Assert.Equal(new DateTimeOffset(2026,7,27,6,0,0,TimeSpan.Zero),afternoon.ScheduledForUtc);
        var evening=MarketTeacherScheduleV2.Next(new DateTimeOffset(2026,7,27,11,0,0,TimeSpan.Zero));Assert.Equal(TeacherLessonKindV2.Evening,evening.Kind);
        var due=MarketTeacherScheduleV2.Due(new DateTimeOffset(2026,7,27,6,5,0,TimeSpan.Zero));Assert.NotNull(due);Assert.Equal(TeacherLessonKindV2.Afternoon,due.Value.Kind);
    }

    [Fact] public void MissingStaleOrMalformedEvidenceFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(()=>MarketTeacherComposerV2.Compose(TeacherLessonKindV2.Morning,Now,Refs()[..^1],generatedAtUtc:Now));
        Assert.Throws<InvalidOperationException>(()=>MarketTeacherComposerV2.Compose(TeacherLessonKindV2.Morning,Now,Refs().Select((x,i)=>i==0?x with{Availability=TeacherEvidenceAvailabilityV2.Stale}:x).ToArray(),generatedAtUtc:Now));
        Assert.Throws<InvalidOperationException>(()=>MarketTeacherComposerV2.Compose(TeacherLessonKindV2.Morning,Now,Refs().Select((x,i)=>i==0?x with{CanonicalSha256="bad"}:x).ToArray(),generatedAtUtc:Now));
    }

    [Fact] public async Task BlockedCurrentCycleIsAQuietNoLessonCondition()
    {
        var store=new AgentSqliteStore(Db);var scheduled=Now;var now=Now.AddMinutes(5);
        await SeedCycleAsync("blocked-current",scheduled.AddSeconds(-20),scheduled.AddSeconds(-10),role=>role=="market"?"succeeded":"blocked");

        var lesson=await MarketTeacherRuntimeV2.GenerateDueLocalLessonAsync(store,"blocked-current",now,default);

        Assert.Null(lesson);
        Assert.Null(await store.GetTeacherLessonAsync("teacher-202607270600-afternoon",default));
    }

    [Fact] public async Task DueLessonUsesLatestEligibleEvidenceAvailableAtScheduledTime()
    {
        var store=new AgentSqliteStore(Db);var scheduled=Now;var now=Now.AddMinutes(5);
        await SeedCycleAsync("eligible-prior",scheduled.AddMinutes(-2),scheduled.AddMinutes(-1),_=>"succeeded");
        await SeedCycleAsync("blocked-nearer",scheduled.AddSeconds(-30),scheduled.AddSeconds(-20),role=>role=="market"?"succeeded":"blocked");
        await SeedCycleAsync("future-cycle",scheduled.AddMinutes(1),scheduled.AddMinutes(1),_=>"succeeded");

        var lesson=await MarketTeacherRuntimeV2.GenerateDueLocalLessonAsync(store,"future-cycle",now,default);

        Assert.NotNull(lesson);
        Assert.Equal("teacher-202607270600-afternoon",lesson!.LessonId);
        Assert.Equal(scheduled,lesson.ScheduledForUtc);
        Assert.Equal(now,lesson.GeneratedAtUtc);
        Assert.All(lesson.Evidence,x=>{Assert.Equal("eligible-prior",x.CycleId);Assert.True(x.AsOfUtc<=scheduled);Assert.Equal(TeacherEvidenceAvailabilityV2.Available,x.Availability);});
        Assert.NotNull(await store.GetTeacherLessonAsync(lesson.LessonId,default));
    }

    [Fact] public async Task TeacherEvidenceSelectsLatestRoleReferenceWithoutPointInTimeLookahead()
    {
        var store=new AgentSqliteStore(Db);var scheduled=Now;
        await SeedCycleAsync("amended-cycle",scheduled.AddMinutes(-10),scheduled.AddMinutes(-9),_=>"succeeded");
        await SeedAuditRowAsync("amended-cycle","audit","audit-pre-cutoff",scheduled.AddMinutes(-5),scheduled.AddMinutes(-4),"succeeded");
        await SeedAuditRowAsync("amended-cycle","audit","audit-post-cutoff",scheduled.AddMinutes(1),scheduled.AddMinutes(1),"succeeded");

        var evidence=await store.GetTeacherEvidenceForCycleAsync("amended-cycle",scheduled,default);

        Assert.Equal(7,evidence.Count);
        Assert.Equal("audit-pre-cutoff",Assert.Single(evidence,x=>x.Agent=="audit").OutputId);
        Assert.True(MarketTeacherRuntimeV2.IsEvidenceEligible(evidence,scheduled));
    }

    [Fact] public async Task LatestEligibleCycleRequiresFreshCompleteSucceededEvidence()
    {
        var store=new AgentSqliteStore(Db,()=>Now);
        await SeedCycleAsync("stale-success",Now.AddMinutes(-31),Now.AddMinutes(-31),_=>"succeeded");
        await SeedCycleAsync("fresh-blocked",Now.AddMinutes(-2),Now.AddMinutes(-1),role=>role=="market"?"succeeded":"blocked");
        Assert.Null(await store.GetLatestTeacherEligibleCycleIdAsync(default));

        await SeedCycleAsync("fresh-success",Now.AddMinutes(-2),Now.AddMinutes(-1),_=>"succeeded");
        Assert.Equal("fresh-success",await store.GetLatestTeacherEligibleCycleIdAsync(default));
    }

    [Fact] public async Task LessonPersistenceIsAppendOnlyRestartSafeAndIdempotent()
    {
        var lesson=MarketTeacherComposerV2.Compose(TeacherLessonKindV2.Afternoon,Now,Refs(),generatedAtUtc:Now);var store=new AgentSqliteStore(Db);
        var first=await store.SaveTeacherLessonAsync(lesson,default);var second=await new AgentSqliteStore(Db).SaveTeacherLessonAsync(lesson,default);Assert.True(first.Succeeded);Assert.False(first.Idempotent);Assert.True(second.Idempotent);var loaded=await new AgentSqliteStore(Db).GetTeacherLessonAsync(lesson.LessonId,default);Assert.NotNull(loaded);Assert.Equal(lesson.ContentSha256,loaded.ContentSha256);Assert.Equal(lesson.Evidence,loaded.Evidence);Assert.Equal(lesson.Blocks.Select(x=>x.Sha256),loaded.Blocks.Select(x=>x.Sha256));Assert.Equal(lesson.Blocks.SelectMany(x=>x.EvidenceHashes),loaded.Blocks.SelectMany(x=>x.EvidenceHashes));
        await using var c=new SqliteConnection($"Data Source={Db}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="UPDATE teacher_lessons SET language='en_US' WHERE lesson_id=$id";q.Parameters.AddWithValue("$id",lesson.LessonId);await Assert.ThrowsAsync<SqliteException>(()=>q.ExecuteNonQueryAsync());
    }

    [Fact] public async Task RecommendationExpiresByRevisionAndCanNeverAuthorizeExecution()
    {
        var current=TeacherRecommendationEngineV2.CreateCrypto("rec-1","BTCUSDT",TeacherRecommendationStateV2.PriorityWatch,"1d",Now,TimeSpan.FromHours(4),"仅作研究候选",Refs(),["研究确认"],["结构失效"],["高波动"]);Assert.False(current.ExecutionAuthority);
        var expired=TeacherRecommendationEngineV2.Expire(current,Now.AddHours(5),"已过期");Assert.Equal(2,expired.Version);Assert.Equal(TeacherRecommendationStateV2.Expired,expired.State);Assert.False(expired.ExecutionAuthority);
        var store=new AgentSqliteStore(Db);Assert.True((await store.SaveTeacherRecommendationAsync(current,default)).Succeeded);Assert.True((await store.SaveTeacherRecommendationAsync(expired,default)).Succeeded);
        var equity=TeacherRecommendationEngineV2.CurrentEquityUnavailable("eq-1","AAPL",Now);Assert.Equal(TeacherRecommendationStateV2.Unavailable,equity.State);Assert.False(equity.ExecutionAuthority);
    }

    [Fact] public void NumericProvenanceRejectsChangedGeneratedNumbers(){Assert.True(TeacherNumericProvenanceGuardV2.PreservesDeterministicValues("BTC 123.45，评分 72%","结论：BTC 123.45，评分 72%"));Assert.False(TeacherNumericProvenanceGuardV2.PreservesDeterministicValues("BTC 123.45","BTC 124.00"));}

    [Fact] public async Task LessonKindsRequireIndependentExplicitConsentAndQueueOnce()
    {
        var lesson=MarketTeacherComposerV2.Compose(TeacherLessonKindV2.Morning,Now,Refs(),generatedAtUtc:Now);var store=new AgentSqliteStore(Db);Assert.True((await store.SaveTeacherLessonAsync(lesson,default)).Succeeded);var observer=new RecordingObserver();var denied=new TeacherLessonNotificationPublisherV2(store,observer,_=>false);Assert.False(await denied.PublishIfAllowedAsync(lesson,"binance","Testnet",default));Assert.Empty(observer.Events);var allowed=new TeacherLessonNotificationPublisherV2(store,observer,x=>x==NotificationEventKind.TeacherMorningLesson);Assert.True(await allowed.PublishIfAllowedAsync(lesson,"binance","Testnet",default));Assert.False(await allowed.PublishIfAllowedAsync(lesson,"binance","Testnet",default));Assert.Equal(NotificationEventKind.TeacherMorningLesson,Assert.Single(observer.Events).Kind);
        var recommendation=TeacherRecommendationEngineV2.CreateCrypto("rec-notify","BTCUSDT",TeacherRecommendationStateV2.WaitForConfirmation,"4h",Now,TimeSpan.FromHours(4),"研究候选",Refs(),["确认"],["失效"],["风险"]);var recPublisher=new TeacherLessonNotificationPublisherV2(store,observer,x=>x==NotificationEventKind.TeacherRecommendation);Assert.True(await recPublisher.PublishRecommendationIfAllowedAsync(lesson.LessonId,recommendation,"binance","Testnet",default));Assert.False(await recPublisher.PublishRecommendationIfAllowedAsync(lesson.LessonId,recommendation,"binance","Testnet",default));Assert.Contains(observer.Events,x=>x.Kind==NotificationEventKind.TeacherRecommendation);
    }
    [Fact] public void DesktopSettingsExposeIndependentTeacherConsentKinds(){var source=File.ReadAllText(Path.Combine(ProjectRoot(),"SetupWindow.xaml.cs"));foreach(var kind in new[]{"TeacherMorningLesson","TeacherAfternoonLesson","TeacherEveningLesson","TeacherEventLesson","TeacherRecommendation","TeacherCorrection"})Assert.Contains(kind,source,StringComparison.Ordinal);}

    private async Task SeedCycleAsync(string cycle,DateTimeOffset asOfUtc,DateTimeOffset recordedAtUtc,Func<string,string> status)
    {
        foreach(var role in new[]{"market","research","strategy","risk","execution","recovery","audit"})
            await SeedAuditRowAsync(cycle,role,$"{cycle}-{role}",asOfUtc,recordedAtUtc,status(role));
    }

    private async Task SeedAuditRowAsync(string cycle,string role,string outputId,DateTimeOffset asOfUtc,DateTimeOffset recordedAtUtc,string status)
    {
        var bytes=Encoding.UTF8.GetBytes($"{cycle}|{role}|{outputId}|{status}|{asOfUtc:O}");
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await using var connection=new SqliteConnection($"Data Source={Db}");await connection.OpenAsync();await using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO model_off_canonical_audits(output_id,cycle_id,schema,template_version,canonical_sha256,status,output_kind,sources_json,as_of_utc,recorded_at_utc,canonical_bytes) VALUES($id,$cycle,'wpe.model-off-agent-output/1.0','teacher-test',$hash,$status,$kind,'[]',$asof,$recorded,$bytes)";
        command.Parameters.AddWithValue("$id",outputId);command.Parameters.AddWithValue("$cycle",cycle);command.Parameters.AddWithValue("$hash",hash);command.Parameters.AddWithValue("$status",status);command.Parameters.AddWithValue("$kind",role);command.Parameters.AddWithValue("$asof",asOfUtc.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$recorded",recordedAtUtc.ToUniversalTime().ToString("O"));command.Parameters.Add("$bytes",SqliteType.Blob).Value=bytes;await command.ExecuteNonQueryAsync();
    }

    private static TeacherEvidenceReferenceV2[] Refs()=>new[]{"market","research","strategy","risk","execution","recovery","audit"}.Select((role,i)=>new TeacherEvidenceReferenceV2(role,role+"-output","cycle-1",new string("abcdef0"[i],64),Now.AddMinutes(-7+i),TeacherEvidenceAvailabilityV2.Available)).ToArray();private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
    private sealed class RecordingObserver:IConfirmedNotificationObserver{public List<ConfirmedNotificationEvent> Events{get;}=[];public Task ObserveAsync(ConfirmedNotificationEvent value,CancellationToken ct){Events.Add(value);return Task.CompletedTask;}}
}

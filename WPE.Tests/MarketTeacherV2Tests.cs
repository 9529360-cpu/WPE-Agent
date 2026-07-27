using Microsoft.Data.Sqlite;
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
    }
    [Fact] public void DesktopSettingsExposeIndependentTeacherConsentKinds(){var source=File.ReadAllText(Path.Combine(ProjectRoot(),"SetupWindow.xaml.cs"));foreach(var kind in new[]{"TeacherMorningLesson","TeacherAfternoonLesson","TeacherEveningLesson","TeacherEventLesson","TeacherRecommendation","TeacherCorrection"})Assert.Contains(kind,source,StringComparison.Ordinal);}

    private static TeacherEvidenceReferenceV2[] Refs()=>new[]{"market","research","strategy","risk","execution","recovery","audit"}.Select((role,i)=>new TeacherEvidenceReferenceV2(role,role+"-output","cycle-1",new string("abcdef0"[i],64),Now.AddMinutes(-7+i),TeacherEvidenceAvailabilityV2.Available)).ToArray();private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
    private sealed class RecordingObserver:IConfirmedNotificationObserver{public List<ConfirmedNotificationEvent> Events{get;}=[];public Task ObserveAsync(ConfirmedNotificationEvent value,CancellationToken ct){Events.Add(value);return Task.CompletedTask;}}
}

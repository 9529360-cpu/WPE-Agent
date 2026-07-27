using Microsoft.Data.Sqlite;
using System.Text.Json;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class RuntimeTeacherProjectionTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,27,6,0,0,TimeSpan.Zero);
    private readonly string directory=Path.Combine(Path.GetTempPath(),"wpe-runtime-teacher-"+Guid.NewGuid().ToString("N"));
    private string Db=>Path.Combine(directory,"agent.db");
    public RuntimeTeacherProjectionTests()=>Directory.CreateDirectory(directory);
    public void Dispose(){SqliteConnection.ClearAllPools();try{Directory.Delete(directory,true);}catch(IOException){}}

    [Fact] public async Task TeacherContentProjectsReadOnlyAndSurvivesRestart()
    {
        var store=new AgentSqliteStore(Db);var lesson=MarketTeacherComposerV2.Compose(TeacherLessonKindV2.Afternoon,Now,Refs(),generatedAtUtc:Now);Assert.True((await store.SaveTeacherLessonAsync(lesson,default)).Succeeded);
        var recommendation=TeacherRecommendationEngineV2.CreateCrypto("rec-runtime","BTCUSDT",TeacherRecommendationStateV2.PriorityWatch,"4h",Now,TimeSpan.FromHours(4),"research candidate",Refs(),["confirm"],["invalidate"],["risk"]);Assert.True((await store.SaveTeacherRecommendationAsync(recommendation,default)).Succeeded);
        var replacement=lesson.Blocks[0] with{BlockId="correction-block",Sha256=Hash("correction-block")};var correction=new TeacherCorrectionV2("correction-runtime","wpe.teacher-correction/2.0",lesson.LessonId,"official-source-correction",Now.AddMinutes(1),[replacement],Hash("correction"));Assert.True((await store.SaveTeacherCorrectionAsync(correction,default)).Succeeded);
        var outcome=new TeacherRecommendationOutcomeV2("outcome-runtime","wpe.teacher-recommendation-outcome/2.0",recommendation.RecommendationId,1,"BTCUSDT","BTCUSDT","4h",Now,Now.AddHours(4),100m,102m,2m,1m,1m,3m,-1m,false,Hash("entry"),Hash("exit"),Hash("benchmark-entry"),Hash("benchmark-exit"),"conditions-followed",Hash("outcome"));Assert.True((await store.SaveTeacherRecommendationOutcomeAsync(outcome,default)).Succeeded);
        var state=new RuntimeTeacherStateStore(new AgentSqliteStore(Db)).Read();var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=Now.UtcDateTime},Now.UtcDateTime,teacherState:state);
        Assert.Equal(RuntimeCollectionState.Available,snapshot.TeacherLessons.State);Assert.Single(snapshot.TeacherLessons.Items);Assert.False(snapshot.TeacherLessons.Items[0].ExecutionAuthority);Assert.Single(snapshot.TeacherRecommendations.Items);Assert.False(snapshot.TeacherRecommendations.Items[0].ExecutionAuthority);Assert.Single(snapshot.TeacherCorrections.Items);Assert.Single(snapshot.TeacherOutcomes.Items);
        var json=JsonSerializer.Serialize(snapshot);Assert.DoesNotContain("executionAuthority\":true",json,StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void UnsupportedStaleAndErrorStatesWithholdTeacherContent()
    {
        var state=new SystemState{LastUpdated=Now.UtcDateTime};var unsupported=RuntimeSnapshotFactory.Create(state,Now.UtcDateTime);Assert.Equal(RuntimeCollectionState.Unsupported,unsupported.TeacherLessons.State);
        var item=new RuntimeTeacherLessonV1("lesson","Morning","Asia/Shanghai",Now,Now,"zh_CN","intermediate","persona",[],Hash("lesson"),false);var staleState=new RuntimeTeacherState(RuntimeCollectionState.Available,[item],[],[],[],Now-RuntimeTeacherStateStore.StaleAfter-TimeSpan.FromSeconds(1),null);var stale=RuntimeSnapshotFactory.Create(state,Now.UtcDateTime,teacherState:staleState);Assert.Equal(RuntimeCollectionState.Stale,stale.TeacherLessons.State);Assert.Empty(stale.TeacherLessons.Items);
        var error=RuntimeSnapshotFactory.Create(state,Now.UtcDateTime,teacherState:RuntimeTeacherState.Error("database secret raw failure"));Assert.Equal(RuntimeCollectionState.Error,error.TeacherLessons.State);Assert.Empty(error.TeacherLessons.Items);Assert.DoesNotContain("database secret raw failure",JsonSerializer.Serialize(error));
    }

    private static TeacherEvidenceReferenceV2[] Refs()=>new[]{"market","research","strategy","risk","execution","recovery","audit"}.Select((role,i)=>new TeacherEvidenceReferenceV2(role,role+"-output","cycle-1",new string("abcdef0"[i],64),Now.AddMinutes(-7+i),TeacherEvidenceAvailabilityV2.Available)).ToArray();
    private static string Hash(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

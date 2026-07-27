using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TeacherEventLessonV2Tests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,27,6,0,0,TimeSpan.Zero);private readonly string _dir=Path.Combine(Path.GetTempPath(),"teacher-event-"+Guid.NewGuid().ToString("N"));private string Db=>Path.Combine(_dir,"agent.db");public TeacherEventLessonV2Tests()=>Directory.CreateDirectory(_dir);public void Dispose(){SqliteConnection.ClearAllPools();try{Directory.Delete(_dir,true);}catch(IOException){}}
    [Fact] public void MaterialMoveCreatesEvidenceBoundNonCausalLesson()
    {
        var previous=Fact(Now.AddMinutes(-4),100m,1000m,.0001m,"a");var current=Fact(Now.AddMinutes(-1),104m,1200m,.0012m,"b");var marketEvent=TeacherCryptoEventDetectorV2.Detect(previous,current);Assert.NotNull(marketEvent);var lesson=TeacherEventLessonComposerV2.Compose(marketEvent!,Agents(),Now);var text=string.Join('\n',lesson.Blocks.Select(x=>x.Content));Assert.Contains("变化 4%",text);Assert.Contains("不能单独证明因果",text);Assert.Contains("老师没有执行权",text);Assert.False(lesson.ExecutionAuthority);Assert.Contains(previous.CanonicalSha256,lesson.Blocks.SelectMany(x=>x.EvidenceHashes));Assert.Contains(current.CanonicalSha256,lesson.Blocks.SelectMany(x=>x.EvidenceHashes));
    }
    [Fact] public void SmallMoveProducesNoEventAndTamperedEvidenceIsRejected(){Assert.Null(TeacherCryptoEventDetectorV2.Detect(Fact(Now.AddMinutes(-4),100m,1000m,.0001m,"a"),Fact(Now.AddMinutes(-1),101m,1050m,.0002m,"b")));Assert.Null(TeacherCryptoEventDetectorV2.Detect(Fact(Now.AddMinutes(-4),100m,1000m,.0001m,"a") with{CanonicalSha256=new string('0',64)},Fact(Now.AddMinutes(-1),104m,1200m,.0012m,"b")));}
    [Fact] public async Task DuplicateEventIsClaimedOnceAcrossRestartAndCannotBeDeleted()
    {
        var marketEvent=TeacherCryptoEventDetectorV2.Detect(Fact(Now.AddMinutes(-4),100m,1000m,.0001m,"a"),Fact(Now.AddMinutes(-1),104m,1200m,.0012m,"b"))!;var store=new AgentSqliteStore(Db);Assert.True(await store.TryClaimTeacherEventAsync(marketEvent,TimeSpan.FromHours(1),default));Assert.False(await new AgentSqliteStore(Db).TryClaimTeacherEventAsync(marketEvent,TimeSpan.FromHours(1),default));await using var c=new SqliteConnection($"Data Source={Db}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="DELETE FROM teacher_event_fingerprints";await Assert.ThrowsAsync<SqliteException>(()=>q.ExecuteNonQueryAsync());
    }
    [Fact] public void ProductionRunsTeacherNetworkOutsideTradingCycleAuthority()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));Assert.Contains("TeacherCryptoEvidenceSchedulerV2",source,StringComparison.Ordinal);Assert.Contains("teacherCryptoSchedulerTask",source,StringComparison.Ordinal);var scheduler=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","Agent","TeacherCryptoEvidenceSchedulerV2.cs"));Assert.DoesNotContain("TradingExecutionGateway",scheduler,StringComparison.Ordinal);Assert.DoesNotContain("ReliableOrderExecutor",scheduler,StringComparison.Ordinal);Assert.DoesNotContain("ApiKey",scheduler,StringComparison.OrdinalIgnoreCase);
    }
    private static TeacherCryptoMarketFactV2 Fact(DateTimeOffset observed,decimal mark,decimal oi,decimal funding,string salt)=>TeacherCryptoMarketFactCanonicalizerV2.Create("binance-futures-public","BTCUSDT",observed,observed.AddSeconds(1),mark,mark-.1m,funding,observed.AddHours(2),oi,1m,1000m,[Hash(salt+"1"),Hash(salt+"2"),Hash(salt+"3")]);private static string Hash(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();private static TeacherEvidenceReferenceV2[] Agents()=>new[]{"market","research","strategy","risk","execution","recovery","audit"}.Select((x,i)=>new TeacherEvidenceReferenceV2(x,x,"cycle",new string("abcdef0"[i],64),Now.AddMinutes(-1),TeacherEvidenceAvailabilityV2.Available)).ToArray();private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

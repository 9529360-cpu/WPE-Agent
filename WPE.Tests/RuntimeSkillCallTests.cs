using System.Text.Json;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

public sealed class RuntimeSkillCallTests:IDisposable
{
 private readonly string dir=Path.Combine(Path.GetTempPath(),"wpe-skills-"+Guid.NewGuid().ToString("N"));private string Db=>Path.Combine(dir,"agent.db");

 [Fact]public async Task LocalRuntimeFieldsRoundTripWithoutSummariesOrRetiredLlmTelemetry()
 {
  const string secret="sk-super-secret-token-123456";var db=new AgentSqliteStore(Db);
  await db.RecordSkillCallAsync("MarketStructure","SUCCESS",42,"input="+secret,"output="+secret,secret,default,"LocalOnly");
  await using(var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Db}")){await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="SELECT input_summary||output_summary||COALESCE(error,'') FROM skill_calls";Assert.DoesNotContain(secret,(string)(await q.ExecuteScalarAsync())!);}
  var store=new RuntimeSkillCallStateStore(new AgentSqliteStore(Db));var snap=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,skillCallState:store.Read());var item=Assert.Single(snap.SkillCalls.Items);
  Assert.Equal("MarketStructure",item.Skill);Assert.Equal("LocalOnly",item.Mode);Assert.Equal(42,item.DurationMs);
  var json=JsonSerializer.Serialize(snap);Assert.DoesNotContain(secret,json);Assert.DoesNotContain("remoteLlmUsed",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("tokens",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("costUsd",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("llmOutcome",json,StringComparison.OrdinalIgnoreCase);
 }

 [Fact]public async Task LegacyRowsExposeOnlyConfirmedLocalFacts()
 {
  var db=new AgentSqliteStore(Db);await db.RecordSkillCallAsync("EvidenceCollector","SUCCESS",12,"sensitive input","sensitive output","sensitive error",default);
  var row=Assert.Single(await db.GetRecentRuntimeSkillCallsAsync(10,default));Assert.Equal("EvidenceCollector",row.Skill);Assert.Null(row.Mode);
 }

 [Fact]public async Task BothSkillTablesAndSnapshotRedactFreeTextMetadata()
 {
  const string secret="sk-runtime-secret-token-123456";const string bearer="Bearer abcdefghijklmnopqrstuvwxyz123456";var db=new AgentSqliteStore(Db);
  await db.RecordSkillCallAsync("MarketStructure?api_key="+secret,"FAILED",9,"query?token="+secret,"header="+bearer,secret,default,"LocalOnly token="+secret);
  await using(var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Db}")){await c.OpenAsync();foreach(var sql in new[]{"SELECT skill||status||input_summary||output_summary||COALESCE(error,'') FROM skill_calls","SELECT skill||status||COALESCE(mode,'') FROM runtime_skill_calls"}){await using var q=c.CreateCommand();q.CommandText=sql;var stored=(string)(await q.ExecuteScalarAsync())!;Assert.DoesNotContain(secret,stored);Assert.DoesNotContain(bearer,stored);}}
  var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,skillCallState:new RuntimeSkillCallStateStore(new AgentSqliteStore(Db)).Read());var json=JsonSerializer.Serialize(snapshot);Assert.DoesNotContain(secret,json);Assert.DoesNotContain(bearer,json);
 }

 [Fact]public void FourStatesWithholdNonAvailable()
 {
  var now=DateTime.UtcNow;var missing=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now);Assert.Equal(RuntimeCollectionState.Unsupported,missing.SkillCalls.State);
  var item=new RuntimeSkillCallV1("1",now,"x","SUCCESS",1,"LocalOnly");var stale=new RuntimeSkillCallState(RuntimeCollectionState.Available,[item],new DateTimeOffset(now-RuntimeSkillCallStateStore.StaleAfter-TimeSpan.FromSeconds(1)),null);Assert.Empty(RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now,skillCallState:stale).SkillCalls.Items);
 }

 public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(dir))Directory.Delete(dir,true);}
}

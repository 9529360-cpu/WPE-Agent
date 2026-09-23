using System.Text.Json;using Microsoft.Data.Sqlite;using WpeAgent.RuntimeContracts;using WpeAgent.RuntimeServices;using 币安量化机器人.Core.Models;using 币安量化机器人.Services.Agent;
namespace WPE.Tests;
public sealed class LocalMemoryTests:IDisposable
{
 private readonly string dir=Path.Combine(Path.GetTempPath(),"wpe-memory-"+Guid.NewGuid().ToString("N"));private string Db=>Path.Combine(dir,"agent.db");

 [Fact]public async Task SecretsNeverLandOnDiskAndExpiredWorkingMemoryIsRemoved(){const string secret="sk-memory-secret-token-123456";var db=new AgentSqliteStore(Db);Assert.True(await db.SaveMemoryAsync(new("working",DateTime.UtcNow,"SOLUSDT","okx",null,"SUCCESS","skill","Authorization: Bearer "+secret+" token="+secret),default));Assert.True(await db.SaveMemoryAsync(new("working",DateTime.UtcNow.AddDays(-2),"SOLUSDT","okx",null,"OLD","skill","old"),default));var rows=await db.SearchMemoriesAsync(new(Symbol:"SOLUSDT"),default);Assert.Single(rows);await using var c=new SqliteConnection($"Data Source={Db}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="SELECT GROUP_CONCAT(summary||result||source,'') FROM agent_memories";Assert.DoesNotContain(secret,(string)(await q.ExecuteScalarAsync())!);}

 [Fact]public async Task StructuredOutcomeMemory_RedactsSecretsAndBoundsDecisionSummary(){const string secret="sk-memory-outcome-secret-123456";var db=new AgentSqliteStore(Db);await db.StartCycleAsync("cycle-memory",new EvidencePack{Completeness=100},"remote-brain",default);await db.CompleteCycleAsync("cycle-memory",new DecisionPlan{Action=DecisionAction.Hold,Instrument="BTCUSDT",Reason="Authorization: Bearer "+secret+" token="+secret+new string('x',220)},"provider unavailable",null,null,default);var row=Assert.Single(await db.RecentOutcomeMemoriesAsync(default));Assert.DoesNotContain(secret,row.DecisionSummary);Assert.True(row.DecisionSummary.Length<=120);Assert.Equal("degraded",row.RiskResult);Assert.Equal("provider_unavailable",row.RiskReasonCode);Assert.Equal("retry_later",row.RecoveryHint);}

 public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(dir))Directory.Delete(dir,true);}
}

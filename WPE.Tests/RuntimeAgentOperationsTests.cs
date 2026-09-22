using System.Text.Json;
using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Runtime;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class RuntimeAgentOperationsTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"wpe-agent-operations-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(directory,"agent.db");
    public RuntimeAgentOperationsTests()=>Directory.CreateDirectory(directory);

    [Fact]
    public async Task ProjectsOnlyPersistedStatusActivityAndWorkflowHandoffs()
    {
        var db=new AgentSqliteStore(DatabasePath);
        await db.RecordSkillCallAsync("DataQuality","SUCCESS",8,"input","output",null,default,"LocalOnly",false);
        await db.RecordSkillCallAsync("IndependentRiskManager","BLOCKED",4,"input","output",null,default,"Hybrid",false);
        await db.BeginWorkflowRunAsync("run-1","cycle-1","{}",default);
        await db.SaveWorkflowCheckpointAsync(new("run-1","cycle-1",WorkflowNode.Execution,CheckpointPhase.Entered,"{}",DateTime.UtcNow),default);
        await db.RecordRuntimeEventAsync(AgentRuntimeEvent.Create("run-1","runtime.heartbeat","AgentRuntimeSupervisor",new{RunId="run-1",LeaseRenewed=true}),default);
        await db.RecordRuntimeEventAsync(AgentRuntimeEvent.Create("cycle-1","workflow.node.entered","AgentRuntimeSupervisor",new{RunId="run-1",Previous=WorkflowNode.Risk,Node=WorkflowNode.Execution}),default);

        var state=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath)).Read();
        Assert.Equal("stopped",state.Operations.Single(x=>x.RoleId=="market").Status);
        Assert.Equal("stopped",state.Operations.Single(x=>x.RoleId=="risk").Status);
        Assert.Equal("stopped",state.Operations.Single(x=>x.RoleId=="execution").Status);
        var handoff=Assert.Single(state.Handoffs);Assert.Equal("risk",handoff.SourceRoleId);Assert.Equal("execution",handoff.TargetRoleId);
    }

    [Fact]
    public async Task WorkflowProjectionDoesNotInventDecisionToExecutionBypass()
    {
        var db=new AgentSqliteStore(DatabasePath);
        await db.RecordRuntimeEventAsync(AgentRuntimeEvent.Create("run-roles","runtime.heartbeat","AgentRuntimeSupervisor",new{RunId="run-roles",LeaseRenewed=true}),default);
        await db.RecordRuntimeEventAsync(AgentRuntimeEvent.Create("cycle-roles","workflow.node.entered","AgentRuntimeSupervisor",new{RunId="run-roles",Previous=WorkflowNode.Research,Node=WorkflowNode.PositionManagement}),default);
        await db.RecordRuntimeEventAsync(AgentRuntimeEvent.Create("cycle-roles","workflow.node.entered","AgentRuntimeSupervisor",new{RunId="run-roles",Previous=WorkflowNode.Risk,Node=WorkflowNode.Reflection}),default);

        var state=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath),new AgentRoleRuntimeRegistry()).Read();

        Assert.Contains(state.Handoffs,x=>x.SourceRoleId=="decision"&&x.TargetRoleId=="risk");
        Assert.Contains(state.Handoffs,x=>x.SourceRoleId=="risk"&&x.TargetRoleId=="audit");
        Assert.DoesNotContain(state.Handoffs,x=>x.SourceRoleId=="decision"&&x.TargetRoleId=="execution");
    }

    [Fact]
    public async Task HistoricalHandoffsAreHiddenWithoutAFreshMatchingRuntimeLease()
    {
        var db=new AgentSqliteStore(DatabasePath);
        await db.RecordRuntimeEventAsync(AgentRuntimeEvent.Create("old-cycle","workflow.node.entered","AgentRuntimeSupervisor",new{RunId="old-run",Previous=WorkflowNode.Observation,Node=WorkflowNode.Research}) with { OccurredAtUtc=DateTime.UtcNow.AddDays(-8) },default);

        var state=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath),new AgentRoleRuntimeRegistry()).Read();

        Assert.Empty(state.Handoffs);
    }

    [Fact]
    public void EmptyAvailableAndFourStatesWithholdUnavailableEvidence()
    {
        var empty=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath)).Read();
        Assert.Equal(RuntimeCollectionState.Available,empty.State);Assert.Equal(6,empty.Operations.Count);Assert.Empty(empty.Handoffs);Assert.All(empty.Operations,x=>Assert.Equal("stopped",x.Status));
        var now=DateTime.UtcNow;var missing=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now);Assert.Equal(RuntimeCollectionState.Unsupported,missing.AgentOperations.State);Assert.Empty(missing.AgentHandoffs.Items);
        var staleState=new RuntimeAgentOperationsState(RuntimeCollectionState.Available,[new("risk","waiting",now-TimeSpan.FromMinutes(6),"Skill risk SUCCESS","Local Only")],[],new DateTimeOffset(now-TimeSpan.FromMinutes(6)),null);
        var stale=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now,agentOperationsState:staleState);Assert.Equal(RuntimeCollectionState.Stale,stale.AgentOperations.State);Assert.Empty(stale.AgentOperations.Items);
        var error=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now,agentOperationsState:RuntimeAgentOperationsState.Error("db failed"));Assert.Equal(RuntimeCollectionState.Error,error.AgentOperations.State);Assert.Empty(error.AgentHandoffs.Items);
    }

    [Fact]
    public async Task HistoricalCanonicalCycleDoesNotPretendWorkersAreLive()
    {
        _=new AgentSqliteStore(DatabasePath);
        var roles=new[]{"market","research","strategy","risk","execution","recovery","audit"};
        await using(var connection=new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync();
            for(var i=0;i<roles.Length;i++)
            {
                await using var command=connection.CreateCommand();
                command.CommandText="INSERT INTO model_off_canonical_audits(output_id,cycle_id,schema,template_version,canonical_sha256,status,output_kind,sources_json,as_of_utc,recorded_at_utc,canonical_bytes) VALUES($id,'cycle-live','schema','template',$hash,$status,$role,'[]',$at,$at,$bytes)";
                command.Parameters.AddWithValue("$id","output-"+roles[i]);command.Parameters.AddWithValue("$hash",new string((char)('a'+i),64));command.Parameters.AddWithValue("$status",i==0?"succeeded":"blocked");command.Parameters.AddWithValue("$role",roles[i]);command.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.AddMilliseconds(i).ToString("O"));command.Parameters.Add("$bytes",Microsoft.Data.Sqlite.SqliteType.Blob).Value=new byte[]{(byte)i};
                await command.ExecuteNonQueryAsync();
                if(i==roles.Length-1)continue;
                await using var handoff=connection.CreateCommand();handoff.CommandText="INSERT INTO model_off_canonical_handoffs(handoff_id,cycle_id,from_agent,to_agent,canonical_output_id,canonical_sha256,status,recorded_at_utc,handoff_sha256,canonical_bytes) VALUES($id,'cycle-live',$from,$to,$output,$hash,'blocked',$at,$handoffHash,$bytes)";
                handoff.Parameters.AddWithValue("$id",$"handoff-{i}");handoff.Parameters.AddWithValue("$from",roles[i]);handoff.Parameters.AddWithValue("$to",roles[i+1]);handoff.Parameters.AddWithValue("$output","output-"+roles[i]);handoff.Parameters.AddWithValue("$hash",new string((char)('a'+i),64));handoff.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.AddMilliseconds(i).ToString("O"));handoff.Parameters.AddWithValue("$handoffHash",new string((char)('k'+i),64));handoff.Parameters.Add("$bytes",Microsoft.Data.Sqlite.SqliteType.Blob).Value=new byte[]{(byte)i};await handoff.ExecuteNonQueryAsync();
            }
        }

        var state=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath)).Read();
        Assert.Equal(6,state.Operations.Count);Assert.Empty(state.Handoffs);
        Assert.All(state.Operations,x=>Assert.Equal("stopped",x.Status));
        Assert.All(state.Operations,x=>Assert.Null(x.Activity));
    }

    [Fact]
    public void LiveRuntimeHealthOverridesBusinessOutcomeWithoutLosingHandoffs()
    {
        var registry=new AgentRoleRuntimeRegistry();
        registry.Publish("market","monitoring","Market stream active.");
        registry.Publish("decision","running","Direct decision cycle active.");
        registry.Publish("risk","monitoring","Risk gate active.");
        registry.Publish("execution","waiting","No approved order pending.");
        registry.Publish("recovery","monitoring","No unresolved order.");
        registry.Publish("audit","monitoring","Append-only audit active.");

        var state=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath),registry).Read();

        Assert.Equal("monitoring",state.Operations.Single(x=>x.RoleId=="market").Status);
        Assert.Equal("running",state.Operations.Single(x=>x.RoleId=="decision").Status);
        Assert.Equal("waiting",state.Operations.Single(x=>x.RoleId=="execution").Status);
        Assert.Equal("monitoring",state.Operations.Single(x=>x.RoleId=="audit").Status);
    }

    [Fact]
    public void RuntimeWideFailureCannotLeaveAnyRoleLookingHealthy()
    {
        var registry=new AgentRoleRuntimeRegistry();
        foreach(var role in new[]{"market","decision","risk","execution","recovery","audit"})
            registry.Publish(role,"monitoring","healthy");

        registry.DegradeAll("runtime failed");

        var state=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath),registry).Read();
        Assert.All(state.Operations,x=>Assert.Equal("degraded",x.Status));
        Assert.All(state.Operations,x=>Assert.Equal("runtime failed",x.Activity));
    }

    [Fact]
    public async Task SnapshotNeverProjectsSecretBearingSkillMetadata()
    {
        const string secret="sk-agent-operations-secret-123456";var db=new AgentSqliteStore(DatabasePath);
        await db.RecordSkillCallAsync("DataQuality?api_key="+secret,"FAILED",3,"prompt="+secret,"response="+secret,secret,default,"Hybrid token="+secret,true);
        var state=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath)).Read();
        var snapshot=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=DateTime.UtcNow},DateTime.UtcNow,agentOperationsState:state);
        Assert.DoesNotContain(secret,JsonSerializer.Serialize(snapshot),StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose(){try{Directory.Delete(directory,true);}catch{}}
}

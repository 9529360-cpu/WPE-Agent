using System.Text.Json;
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
        await db.RecordRuntimeEventAsync(AgentRuntimeEvent.Create("cycle-1","workflow.node.entered","AgentRuntimeSupervisor",new{Previous=WorkflowNode.Risk,Node=WorkflowNode.Execution}),default);

        var state=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath)).Read();
        Assert.Equal("idle",state.Operations.Single(x=>x.RoleId=="data-quality").Status);
        Assert.Equal("blocked",state.Operations.Single(x=>x.RoleId=="risk").Status);
        Assert.Equal("running",state.Operations.Single(x=>x.RoleId=="execution").Status);
        var handoff=Assert.Single(state.Handoffs);Assert.Equal("risk",handoff.SourceRoleId);Assert.Equal("execution",handoff.TargetRoleId);
    }

    [Fact]
    public void EmptyAvailableAndFourStatesWithholdUnavailableEvidence()
    {
        var empty=new RuntimeAgentOperationsStateStore(new AgentSqliteStore(DatabasePath)).Read();
        Assert.Equal(RuntimeCollectionState.Available,empty.State);Assert.Equal(8,empty.Operations.Count);Assert.Empty(empty.Handoffs);Assert.All(empty.Operations,x=>Assert.Equal("idle",x.Status));
        var now=DateTime.UtcNow;var missing=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now);Assert.Equal(RuntimeCollectionState.Unsupported,missing.AgentOperations.State);Assert.Empty(missing.AgentHandoffs.Items);
        var staleState=new RuntimeAgentOperationsState(RuntimeCollectionState.Available,[new("risk","idle",now-TimeSpan.FromMinutes(6),"Skill risk SUCCESS","Local Only")],[],new DateTimeOffset(now-TimeSpan.FromMinutes(6)),null);
        var stale=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now,agentOperationsState:staleState);Assert.Equal(RuntimeCollectionState.Stale,stale.AgentOperations.State);Assert.Empty(stale.AgentOperations.Items);
        var error=RuntimeSnapshotFactory.Create(new SystemState{LastUpdated=now},now,agentOperationsState:RuntimeAgentOperationsState.Error("db failed"));Assert.Equal(RuntimeCollectionState.Error,error.AgentOperations.State);Assert.Empty(error.AgentHandoffs.Items);
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

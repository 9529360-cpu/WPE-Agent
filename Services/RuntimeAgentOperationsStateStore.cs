using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.RuntimeServices;

public sealed class RuntimeAgentOperationsStateStore
{
    public static readonly TimeSpan StaleAfter=TimeSpan.FromMinutes(5);
    private static readonly string[] Roles=["orchestrator","data-quality","news-ingest","strategy-research","backtest","risk","execution","order-recovery"];
    private readonly AgentSqliteStore db;private readonly object gate=new();private RuntimeAgentOperationsState current=RuntimeAgentOperationsState.Unsupported("Agent operations persistence is not connected.");
    public RuntimeAgentOperationsStateStore(AgentSqliteStore database){db=database;RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();}
    public RuntimeAgentOperationsState Read(){lock(gate)return current;}
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var evidence=await db.GetAgentOperationsEvidenceAsync(ct);var byRole=evidence.Activities.ToDictionary(x=>x.RoleId,StringComparer.OrdinalIgnoreCase);
            var items=Roles.Select(role=>byRole.TryGetValue(role,out var value)?new RuntimeAgentOperationV1(role,value.Status,value.OccurredAtUtc,value.Activity,value.Mode):new RuntimeAgentOperationV1(role,"idle",null,null,"Local Only")).ToArray();
            var handoffs=evidence.Handoffs.Select(x=>new RuntimeAgentHandoffV1(x.Id,x.OccurredAtUtc,x.SourceRoleId,x.TargetRoleId,x.Result)).ToArray();
            lock(gate)current=new(RuntimeCollectionState.Available,items,handoffs,new DateTimeOffset(evidence.UpdatedAtUtc),null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){lock(gate)current=RuntimeAgentOperationsState.Error($"Agent operations read failed: {ex.Message}");}
    }
}

public sealed record RuntimeAgentOperationsState(RuntimeCollectionState State,IReadOnlyList<RuntimeAgentOperationV1> Operations,IReadOnlyList<RuntimeAgentHandoffV1> Handoffs,DateTimeOffset? UpdatedAt,string? Message)
{
    public static RuntimeAgentOperationsState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,[],[],null,message);
    public static RuntimeAgentOperationsState Error(string message)=>new(RuntimeCollectionState.Error,[],[],DateTimeOffset.UtcNow,message);
}

using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;
namespace WpeAgent.RuntimeServices;
public sealed class RuntimeSkillCallStateStore
{
 public static readonly TimeSpan StaleAfter=TimeSpan.FromMinutes(5);private readonly AgentSqliteStore _db;private readonly object _gate=new();private RuntimeSkillCallState _current=RuntimeSkillCallState.Unsupported("Skill-call persistence is not connected.");
 public RuntimeSkillCallStateStore(AgentSqliteStore db){_db=db;RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();}public RuntimeSkillCallState Read(){lock(_gate)return _current;}
 public async Task RefreshAsync(CancellationToken ct){try{var rows=await _db.GetRecentRuntimeSkillCallsAsync(200,ct);var items=rows.Select(x=>new RuntimeSkillCallV1(x.Id,x.OccurredAtUtc,x.Skill,x.Status,x.DurationMs,x.Mode,x.RemoteLlmUsed,x.Tokens,x.CostUsd)).ToArray();lock(_gate)_current=new(RuntimeCollectionState.Available,items,DateTimeOffset.UtcNow,null);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception ex){lock(_gate)_current=RuntimeSkillCallState.Error($"Skill-call persistence read failed: {ex.Message}");}}
}
public sealed record RuntimeSkillCallState(RuntimeCollectionState State,IReadOnlyList<RuntimeSkillCallV1> Items,DateTimeOffset? UpdatedAt,string? Message){public static RuntimeSkillCallState Unsupported(string m)=>new(RuntimeCollectionState.Unsupported,[],null,m);public static RuntimeSkillCallState Error(string m)=>new(RuntimeCollectionState.Error,[],DateTimeOffset.UtcNow,m);}

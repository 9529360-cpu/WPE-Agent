using WpeAgent.RuntimeContracts;using 币安量化机器人.Services.Agent;
namespace WpeAgent.RuntimeServices;
public sealed class RuntimeMemoryStateStore
{
 public static readonly TimeSpan StaleAfter=TimeSpan.FromMinutes(5);private readonly AgentSqliteStore db;private readonly object gate=new();private RuntimeMemoryState current=RuntimeMemoryState.Unsupported("Memory persistence is not connected.");
 public RuntimeMemoryStateStore(AgentSqliteStore database){db=database;RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();}public RuntimeMemoryState Read(){lock(gate)return current;}
 public async Task RefreshAsync(CancellationToken ct){try{var x=await db.GetMemoryRuntimeSnapshotAsync(ct);var rows=x.RecentRetrievals.Select(r=>new RuntimeMemoryRetrievalV1(r.Id,r.Tier??"all",r.Symbol,r.ProviderId,r.StrategyId,r.RetrievedAtUtc,r.Result??"any","deterministic-sqlite-query",r.ResultCount)).ToArray();var status=new RuntimeMemoryStatusV1(x.WorkingCount,x.EpisodicCount,x.LongTermCount,x.RecentRetrievals.FirstOrDefault()?.RetrievedAtUtc);lock(gate)current=new(RuntimeCollectionState.Available,status,rows,DateTimeOffset.UtcNow,null);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception ex){lock(gate)current=RuntimeMemoryState.Error($"Memory projection failed: {ex.Message}");}}
}
public sealed record RuntimeMemoryState(RuntimeCollectionState State,RuntimeMemoryStatusV1? Status,IReadOnlyList<RuntimeMemoryRetrievalV1> Retrievals,DateTimeOffset? UpdatedAt,string? Message){public static RuntimeMemoryState Unsupported(string m)=>new(RuntimeCollectionState.Unsupported,null,[],null,m);public static RuntimeMemoryState Error(string m)=>new(RuntimeCollectionState.Error,null,[],DateTimeOffset.UtcNow,m);}

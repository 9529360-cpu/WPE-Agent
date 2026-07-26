namespace 币安量化机器人.Services.Agent;

public interface ILegacyIntentReadOnlyFacts
{
    Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct);
    Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct);
    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string symbol,CancellationToken ct);
}

public sealed record LegacyIntentIsolationRunResult(int Examined,int Quarantined,int Retained,int Unknown,string Code);

/// <summary>Classifies pre-journal INTENT rows from read-only facts. It has no submission or provider mutation surface.</summary>
public sealed class LegacyIntentIsolationService
{
    private readonly AgentSqliteStore _store;
    private readonly ILegacyIntentReadOnlyFacts _facts;
    public LegacyIntentIsolationService(AgentSqliteStore store,ILegacyIntentReadOnlyFacts facts){_store=store??throw new ArgumentNullException(nameof(store));_facts=facts??throw new ArgumentNullException(nameof(facts));}

    public async Task<LegacyIntentIsolationRunResult> RunAsync(CancellationToken ct)
    {
        var candidates=await _store.GetLegacyIntentIsolationCandidatesAsync(ct);var quarantined=0;var retained=0;var unknown=0;
        foreach(var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();var intent=candidate.Intent;
            ExchangeOrder? found;IReadOnlyList<ManagedPosition> positions;IReadOnlyList<ExchangeOrder> orders;bool journalExists;
            try
            {
                found=await _facts.FindOrderAsync(intent.Symbol,intent.ClientOrderId,ct);
                positions=await _facts.GetPositionsAsync(ct);
                orders=await _facts.GetOpenOrdersAsync(intent.Symbol,ct);
                journalExists=await _store.HasExecutionSubmissionJournalAsync(intent.ClientOrderId,ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch{unknown++;continue;}
            if(found is not null||journalExists||positions.Any(value=>value.Symbol.Equals(intent.Symbol,StringComparison.OrdinalIgnoreCase)&&value.Quantity>0)||orders.Any(value=>value.Symbol.Equals(intent.Symbol,StringComparison.OrdinalIgnoreCase))){retained++;continue;}
            var result=await _store.TryQuarantineLegacyIntentAsync(intent.ClientOrderId,"legacy.intent-unresolved",ct);if(result.Succeeded)quarantined++;else retained++;
        }
        return new(candidates.Count,quarantined,retained,unknown,unknown>0?"legacy-isolation.incomplete":"legacy-isolation.completed");
    }
}

/// <summary>Reserved read contract for a future provider submission journal. No mutation API is defined here.</summary>
public sealed record ExecutionSubmissionJournalV1(string SubmissionId,string ClientOrderId,string ProviderId,string Environment,DateTimeOffset SubmittedAtUtc,string ResultCode,string ResultHash);

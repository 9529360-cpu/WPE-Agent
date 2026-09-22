using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed class DeterministicBrainProvider : IAssistantProvider
{
    public string Name => "WPE Local Brain";
    public bool IsLocal => true;

    public Task<BrainHealth> HealthCheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new BrainHealth(true, "LOCAL_DETERMINISTIC"));
    }

    public Task<BrainDecisionResult> DecideAsync(EvidencePack evidence,AgentContext context,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var decision=DirectMarketStructureDecisionSkill.Decide(evidence,context.CircuitBreakerActive);
        var market=evidence.Markets.GetValueOrDefault(decision.Instrument);
        var structure=market is null?null:MarketStructureIntelligence.Analyze(market);
        var audit=JsonSerializer.Serialize(new
        {
            provider=Name,
            decisionPath=DirectMarketStructureDecisionSkill.DecisionContextKind,
            decision.Action,
            decision.Instrument,
            decision.DecisionContextId,
            decision.StrategyVersion,
            structure=structure is null?null:new
            {
                structure.HigherTimeframeBias,
                structure.Phase,
                structure.Scenario,
                structure.TriggerPresent,
                structure.ConfirmationPresent,
                structure.StructuralSupport,
                structure.StructuralResistance,
                event15m=structure.FifteenMinute.Event
            },
            decision.Reason
        });
        return Task.FromResult(new BrainDecisionResult(decision,audit,audit));
    }
}

public sealed record AssistantAdapterDescriptor(string Id,string DisplayName,bool Local,string Protocol);

public static class AssistantAdapterCatalog
{
    public static IReadOnlyList<AssistantAdapterDescriptor> All { get; } =
        [new("local-deterministic","WPE Local Deterministic",true,"native")];
}

/// <summary>Local-only assistant composition boundary.</summary>
public static class AssistantProviderFactory
{
    public static IAssistantProvider CreateLocal()=>new DeterministicBrainProvider();

    public static IAssistantProvider Create(BrainSlot? slot,string? secret,global::币安量化机器人.Core.Models.AiRuntimeMode mode)
    {
        _=slot;_=secret;_=mode;
        return CreateLocal();
    }

    public static IAssistantProvider Create(BrainSlot? slot,string? secret,bool allowRemote)
    {
        _=slot;_=secret;_=allowRemote;
        return CreateLocal();
    }
}

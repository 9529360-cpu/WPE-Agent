using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public interface ITradingDecisionAgent
{
    string Name { get; }
    string DecisionAuthority { get; }
    Task<BrainDecisionResult> DecideAsync(
        EvidencePack evidence,
        AgentContext context,
        CancellationToken ct);
}

public sealed class TechnicalDecisionAgent : ITradingDecisionAgent
{
    private readonly string _providerName;
    private readonly IMarketStructureAnalysisTool _marketStructure;

    public TechnicalDecisionAgent(string providerName="WPE Local Brain",IMarketStructureAnalysisTool? marketStructure=null)
    {
        _providerName=string.IsNullOrWhiteSpace(providerName)
            ?throw new ArgumentException("Provider name is required.",nameof(providerName))
            :providerName;
        _marketStructure=marketStructure??MarketStructureAnalysisTool.Shared;
    }

    public string Name => "Technical Market Decision Agent";
    public string DecisionAuthority => DirectMarketStructureDecisionSkill.DecisionContextKind;

    public Task<BrainDecisionResult> DecideAsync(
        EvidencePack evidence,
        AgentContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var decision=DirectMarketStructureDecisionSkill.Decide(
            evidence,
            context.CircuitBreakerActive,
            _marketStructure);
        var market=evidence.Markets.GetValueOrDefault(decision.Instrument);
        var structure=market is null
            ?null
            :_marketStructure.Analyze(market);

        var audit=JsonSerializer.Serialize(new
        {
            provider=_providerName,
            agent=Name,
            decisionAuthority=DecisionAuthority,
            tool=_marketStructure.Name,
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

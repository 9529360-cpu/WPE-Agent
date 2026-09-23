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
        decision=ApplyTradeMemoryConfirmation(decision,structure,context.RelevantMemories,out var memoryUsed);

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
                structure.ConfirmationSource,
                event15m=structure.FifteenMinute.Event
            },
            memoryAdaptation=memoryUsed is null?null:new
            {
                memoryUsed.StrategyId,
                memoryUsed.Result,
                memoryUsed.OccurredAtUtc,
                policy="same-setup-loss-requires-15m-confirmation"
            },
            decision.Reason
        });

        return Task.FromResult(new BrainDecisionResult(decision,audit,audit));
    }

    private static DecisionPlan ApplyTradeMemoryConfirmation(
        DecisionPlan decision,
        MarketStructureRead? structure,
        IReadOnlyList<PlannerMemoryFact>? memories,
        out PlannerMemoryFact? memoryUsed)
    {
        memoryUsed=null;
        if(structure is null||
           decision.Action is not (DecisionAction.OpenLong or DecisionAction.OpenShort)||
           !string.Equals(structure.ConfirmationSource,"1m-realtime-closed",StringComparison.Ordinal)||
           memories is null||memories.Count==0||
           structure.Scenario==MarketStructureScenario.None)
            return decision;

        var latest=memories
            .Where(x=>string.Equals(x.Source,"post-trade",StringComparison.OrdinalIgnoreCase)&&
                      SetupFamilyMatches(x.StrategyId,structure.Scenario))
            .OrderByDescending(x=>x.OccurredAtUtc)
            .FirstOrDefault();
        if(latest is null||!string.Equals(latest.Result,"loss",StringComparison.OrdinalIgnoreCase))
            return decision;

        memoryUsed=latest;
        return new DecisionPlan
        {
            Action=DecisionAction.Hold,
            Instrument=decision.Instrument,
            TargetTier=0,
            Confidence=0,
            Regime=decision.Regime,
            Reason=$"{decision.Instrument}: recent {structure.Scenario} loss requires a closed 15m confirmation before re-entry.",
            Invalidation="No risk-increasing decision exists until the setup confirms on a closed 15m candle.",
            EvidenceReferences=decision.EvidenceReferences
                .Append("trade_memory=recent-same-setup-loss")
                .Append("memory_confirmation=15m-closed-required")
                .ToList(),
            MissingConditions=["15m-closed confirmation after recent same-setup loss"],
            ConflictSummary=$"trade-memory-confirmation; scenario={structure.Scenario}; latest_result=loss",
            StrategyVersion=decision.StrategyVersion,
            DecisionContextKind="market-observation",
            RiskBudgetMultiplier=0
        };
    }

    private static bool SetupFamilyMatches(string? strategyId,MarketStructureScenario scenario)=>
        !string.IsNullOrWhiteSpace(strategyId)&&
        strategyId.Contains("-"+scenario+"-",StringComparison.Ordinal);
}

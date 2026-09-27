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

    public TechnicalDecisionAgent(string providerName="WPE Local Brain")
    {
        _providerName=string.IsNullOrWhiteSpace(providerName)
            ?throw new ArgumentException("Provider name is required.",nameof(providerName))
            :providerName;
    }

    public string Name => "Medium Horizon Trading Agent";
    public string DecisionAuthority => MediumHorizonDecisionSkill.DecisionContextKind;

    public Task<BrainDecisionResult> DecideAsync(
        EvidencePack evidence,
        AgentContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var decision=MediumHorizonDecisionSkill.Decide(evidence);
        var market=evidence.Markets.GetValueOrDefault(decision.Instrument);
        var structure=market is null
            ?null
            :MediumHorizonDecisionSkill.Analyze(market);

        var audit=JsonSerializer.Serialize(new
        {
            provider=_providerName,
            agent=Name,
            decisionAuthority=DecisionAuthority,
            tool="medium-horizon-d1-h4-v1",
            decision.Action,
            decision.Instrument,
            decision.DecisionContextId,
            decision.StrategyVersion,
            structure=structure is null?null:new
            {
                dailyBias=structure.Daily.Bias,
                dailyStrength=structure.Daily.Strength,
                fourHourBias=structure.FourHour.Bias,
                fourHourStrength=structure.FourHour.Strength,
                fourHourClosedAt=structure.FourHour.ClosedAtUtc
            },
            decision.Reason
        });

        return Task.FromResult(new BrainDecisionResult(decision,audit,audit));
    }
}

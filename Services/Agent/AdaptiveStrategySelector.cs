using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed record StrategyCycleSelection(
    StrategyProfile Profile,
    StrategySignal Signal,
    MarketRegime Regime,
    double SelectionScore,
    double StrategyAgreement = 1);

public static class AdaptiveStrategySelector
{
    public const double MinimumDirectionalConsensus = .55;

    public static async Task<IReadOnlyDictionary<string, StrategyCycleSelection>> SelectAsync(
        IReadOnlyList<StrategyProfile> profiles,
        EvidencePack evidence,
        StrategyResearchAgent research,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(research);

        var selected = new Dictionary<string, StrategyCycleSelection>(StringComparer.OrdinalIgnoreCase);
        foreach (var market in evidence.Markets.Values.Where(value => value is not null))
        {
            var candidates = new List<StrategyCycleSelection>();
            foreach (var profile in profiles.Where(value =>
                         value.Lifecycle == StrategyLifecycle.Active &&
                         value.Symbol.Equals(market.Symbol, StringComparison.OrdinalIgnoreCase)))
            {
                var signal = await research.GetAdaptiveSignalAsync(profile, market, evidence.News, ct);
                if (signal.Direction == 0 || signal.Confidence <= 0)
                    continue;

                var score = Math.Clamp(profile.QualityScore, 0, 1) * Math.Clamp(signal.Confidence, 0, 1);
                candidates.Add(new(profile, signal, MarketRegimeClassifier.Detect(market), score));
            }

            var consensus = SelectConsensus(candidates);
            if (consensus is not null)
                selected[market.Symbol] = consensus;
        }

        return selected;
    }

    public static StrategyCycleSelection? SelectBest(IEnumerable<StrategyCycleSelection> candidates)
        => candidates
            .Where(Eligible)
            .OrderByDescending(value => value.SelectionScore)
            .ThenByDescending(value => value.Profile.Expectancy)
            .ThenBy(value => value.Profile.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    public static StrategyCycleSelection? SelectConsensus(IEnumerable<StrategyCycleSelection> candidates)
    {
        var eligible = candidates.Where(Eligible).ToArray();
        var leader = SelectBest(eligible);
        if (leader is null)
            return null;

        var total = eligible.Sum(value => Math.Max(0, value.SelectionScore));
        if (total <= 0)
            return null;

        var sameDirection = eligible
            .Where(value => value.Signal.Direction == leader.Signal.Direction)
            .Sum(value => Math.Max(0, value.SelectionScore));
        var agreement = Math.Clamp(sameDirection / total, 0, 1);
        if (agreement < MinimumDirectionalConsensus)
            return null;

        var confidence = Math.Clamp(leader.Signal.Confidence * agreement, 0, leader.Signal.Confidence);
        if (confidence <= 0)
            return null;

        var signal = leader.Signal with
        {
            Confidence = confidence,
            Reason = $"{leader.Signal.Reason}; strategy_consensus={agreement:F2}"
        };
        return leader with
        {
            Signal = signal,
            SelectionScore = Math.Clamp(leader.Profile.QualityScore, 0, 1) * confidence,
            StrategyAgreement = agreement
        };
    }

    public static bool DirectionMatches(StrategyCycleSelection selection, DecisionAction action)
    {
        var requiredDirection = action switch
        {
            DecisionAction.OpenLong or DecisionAction.AddLong or DecisionAction.ReverseToLong => 1,
            DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReverseToShort => -1,
            _ => 0
        };
        return requiredDirection == 0 || selection.Signal.Direction == requiredDirection;
    }

    private static bool Eligible(StrategyCycleSelection value)
        => value.Profile.Lifecycle == StrategyLifecycle.Active &&
           value.Signal.Direction is -1 or 1 &&
           double.IsFinite(value.Signal.Confidence) &&
           value.Signal.Confidence is > 0 and <= 1 &&
           double.IsFinite(value.Profile.QualityScore) &&
           double.IsFinite(value.SelectionScore) &&
           value.SelectionScore >= 0;
}

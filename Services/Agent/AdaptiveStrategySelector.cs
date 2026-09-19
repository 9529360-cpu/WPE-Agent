using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed record StrategyCycleSelection(
    StrategyProfile Profile,
    StrategySignal Signal,
    MarketRegime Regime,
    double SelectionScore);

public static class AdaptiveStrategySelector
{
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

            var best = SelectBest(candidates);
            if (best is not null)
                selected[market.Symbol] = best;
        }

        return selected;
    }

    public static StrategyCycleSelection? SelectBest(IEnumerable<StrategyCycleSelection> candidates)
        => candidates
            .Where(value =>
                value.Profile.Lifecycle == StrategyLifecycle.Active &&
                value.Signal.Direction is -1 or 1 &&
                double.IsFinite(value.Signal.Confidence) &&
                value.Signal.Confidence is > 0 and <= 1 &&
                double.IsFinite(value.Profile.QualityScore))
            .OrderByDescending(value => value.SelectionScore)
            .ThenByDescending(value => value.Profile.Expectancy)
            .ThenBy(value => value.Profile.Id, StringComparer.Ordinal)
            .FirstOrDefault();

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
}

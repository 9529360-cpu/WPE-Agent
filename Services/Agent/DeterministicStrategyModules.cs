using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

/// <summary>
/// Strategy code owns deterministic signal/position logic only. It never owns order submission,
/// authorization, risk approval, provider mutation, recovery, or persistence.
/// </summary>
internal interface IDeterministicStrategyModule
{
    StrategyFamily Family { get; }
    string ImplementationVersion { get; }
    StrategySignal Signal(StrategyProfile profile, MarketEvidence market, IReadOnlyList<NewsEvidence> news);
    List<(double Return, bool Trade)> Simulate(
        StrategyProfile profile,
        IReadOnlyList<CandleEvidence> candles,
        IReadOnlyList<NewsFeature> news,
        ResearchRealityModel reality);
}

internal sealed class DeterministicStrategyRegistry
{
    private readonly IReadOnlyDictionary<StrategyFamily, IDeterministicStrategyModule> _modules;

    internal DeterministicStrategyRegistry(IEnumerable<IDeterministicStrategyModule>? modules = null)
    {
        var selected = (modules ?? BuiltIns()).ToArray();
        var duplicates = selected.GroupBy(x => x.Family).Where(x => x.Count() != 1).Select(x => x.Key).ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException($"Strategy registry contains duplicate families: {string.Join(',', duplicates)}");

        _modules = selected.ToDictionary(x => x.Family);
        var missing = Enum.GetValues<StrategyFamily>().Where(x => !_modules.ContainsKey(x)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Strategy registry is missing deterministic modules: {string.Join(',', missing)}");

        if (_modules.Values.Any(x => string.IsNullOrWhiteSpace(x.ImplementationVersion)))
            throw new InvalidOperationException("Every deterministic strategy module requires an implementation version.");
    }

    internal IReadOnlyList<StrategyFamily> Families => _modules.Keys.Order().ToArray();
    internal IDeterministicStrategyModule Resolve(StrategyFamily family) =>
        _modules.TryGetValue(family, out var module)
            ? module
            : throw new InvalidOperationException($"No deterministic strategy module is registered for {family}.");

    private static IEnumerable<IDeterministicStrategyModule> BuiltIns()
    {
        yield return new TrendBreakoutStrategyModule();
        yield return new MeanReversionStrategyModule();
        yield return new NewsMomentumStrategyModule();
    }
}

internal sealed class TrendBreakoutStrategyModule : IDeterministicStrategyModule
{
    public StrategyFamily Family => StrategyFamily.TrendBreakout;
    public string ImplementationVersion => "trend-breakout/1.0";

    public StrategySignal Signal(StrategyProfile profile, MarketEvidence market, IReadOnlyList<NewsEvidence> news)
    {
        var candles = market.Candles;
        if (candles.Count < profile.Parameters.SlowPeriod + 2)
            return Hold(profile, market.Symbol, "insufficient candles");

        var p = profile.Parameters;
        var fast = candles.TakeLast(p.FastPeriod).Average(x => x.Close);
        var slow = candles.TakeLast(p.SlowPeriod).Average(x => x.Close);
        var confidence = Math.Min(1, Math.Abs((double)(fast / slow - 1)) * 40);
        var direction = fast > slow && market.Price > candles.TakeLast(48).Max(x => x.High) * (decimal)(1 - p.BreakoutBuffer)
            ? 1
            : fast < slow && market.Price < candles.TakeLast(48).Min(x => x.Low) * (decimal)(1 + p.BreakoutBuffer)
                ? -1
                : 0;
        return new(profile.Id, market.Symbol, direction, direction == 0 ? 0 : confidence,
            $"strategy={ImplementationVersion}; family={Family}; local deterministic signal");
    }

    public List<(double Return, bool Trade)> Simulate(
        StrategyProfile profile,
        IReadOnlyList<CandleEvidence> candles,
        IReadOnlyList<NewsFeature> news,
        ResearchRealityModel reality)
        => StrategyModuleSimulation.SimulateDirectional(profile, candles, reality,
            (market, _) => Signal(profile, market, Array.Empty<NewsEvidence>()).Direction);

    private static StrategySignal Hold(StrategyProfile profile, string symbol, string reason) =>
        new(profile.Id, symbol, 0, 0, reason);
}

internal sealed class NewsMomentumStrategyModule : IDeterministicStrategyModule
{
    public StrategyFamily Family => StrategyFamily.NewsMomentum;
    public string ImplementationVersion => "news-momentum/1.0";

    public StrategySignal Signal(StrategyProfile profile, MarketEvidence market, IReadOnlyList<NewsEvidence> news)
    {
        if (market.Candles.Count < profile.Parameters.SlowPeriod + 2)
            return new(profile.Id, market.Symbol, 0, 0, "insufficient candles");

        var sentiment = news
            .Where(x => x.AffectedAssets.Any(a => a.Equals(market.Symbol, StringComparison.OrdinalIgnoreCase) || market.Symbol.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.PublishedAt)
            .Take(5)
            .Select(x => x.Sentiment * x.Confidence)
            .DefaultIfEmpty()
            .Average();
        var threshold = profile.Parameters.NewsSentimentThreshold;
        var direction = sentiment >= threshold ? 1 : sentiment <= -threshold ? -1 : 0;
        return new(profile.Id, market.Symbol, direction, direction == 0 ? 0 : Math.Min(1, Math.Abs(sentiment)),
            $"strategy={ImplementationVersion}; family={Family}; sentiment={sentiment:F3}");
    }

    public List<(double Return, bool Trade)> Simulate(
        StrategyProfile profile,
        IReadOnlyList<CandleEvidence> candles,
        IReadOnlyList<NewsFeature> news,
        ResearchRealityModel reality)
        => StrategyModuleSimulation.SimulateDirectional(profile, candles, reality, (_, asOf) =>
        {
            var weighted = news
                .Where(x => x.PublishedAtUtc.Kind == DateTimeKind.Utc && x.PublishedAtUtc <= asOf && x.PublishedAtUtc > asOf.AddHours(-48) &&
                            (x.Asset.Equals(profile.Symbol, StringComparison.OrdinalIgnoreCase) || profile.Symbol.StartsWith(x.Asset, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.PublishedAtUtc)
                .Take(5)
                .Select(x => x.Sentiment * x.Confidence)
                .ToArray();
            var sentiment = weighted.Length == 0 ? 0 : weighted.Average();
            return sentiment >= profile.Parameters.NewsSentimentThreshold ? 1 : sentiment <= -profile.Parameters.NewsSentimentThreshold ? -1 : 0;
        });
}

internal sealed class MeanReversionStrategyModule : IDeterministicStrategyModule
{
    public StrategyFamily Family => StrategyFamily.MeanReversion;
    public string ImplementationVersion => "mean-reversion/1.0";

    public StrategySignal Signal(StrategyProfile profile, MarketEvidence market, IReadOnlyList<NewsEvidence> news)
    {
        var candles = market.Candles;
        if (candles.Count < profile.Parameters.SlowPeriod + 2)
            return new(profile.Id, market.Symbol, 0, 0, "insufficient candles");

        var p = profile.Parameters;
        var state = MeanReversionRegimeAnalyzer.Analyze(candles, p);
        if (state.Regime != MeanReversionRegime.Range)
            return new(profile.Id, market.Symbol, 0, 0, $"strategy={ImplementationVersion}; gated: regime={state.Regime}; adx={state.Adx:F1}; atr={state.AtrRatio:P2}");
        if (state.VolumeRatio < p.VolumeMultiplier)
            return new(profile.Id, market.Symbol, 0, 0, $"strategy={ImplementationVersion}; gated: volume={state.VolumeRatio:F2}");
        if (Math.Abs(state.ZScore) >= p.MeanReversionStopZ || state.DistanceAtr >= p.AtrStopMultiple)
            return new(profile.Id, market.Symbol, 0, 0, $"strategy={ImplementationVersion}; invalidated: z={state.ZScore:F2}; distanceAtr={state.DistanceAtr:F2}");

        var direction = state.ZScore <= -p.MeanReversionZ && state.Rsi <= 40 ? 1 : state.ZScore >= p.MeanReversionZ && state.Rsi >= 60 ? -1 : 0;
        var confidence = direction == 0
            ? 0
            : Math.Clamp((Math.Abs(state.ZScore) - p.MeanReversionZ) / Math.Max(.1, p.MeanReversionStopZ - p.MeanReversionZ) * .65 + (1 - state.Adx / p.AdxCeiling) * .35, 0, 1);
        return new(profile.Id, market.Symbol, direction, confidence,
            $"strategy={ImplementationVersion}; family={Family}; regime={state.Regime}; z={state.ZScore:F2}; rsi={state.Rsi:F1}; adx={state.Adx:F1}; atr={state.AtrRatio:P2}; distanceAtr={state.DistanceAtr:F2}; volume={state.VolumeRatio:F2}");
    }

    public List<(double Return, bool Trade)> Simulate(
        StrategyProfile profile,
        IReadOnlyList<CandleEvidence> candles,
        IReadOnlyList<NewsFeature> news,
        ResearchRealityModel reality)
    {
        var result = new List<(double Return, bool Trade)>();
        var p = profile.Parameters;
        var position = 0;
        var held = 0;
        for (var i = Math.Max(p.SlowPeriod, 42); i < candles.Count; i++)
        {
            var prefix = candles.Take(i).ToArray();
            var state = MeanReversionRegimeAnalyzer.Analyze(prefix, p);
            var close = (double)candles[i].Close;
            var previous = (double)candles[i - 1].Close;
            var value = position * (close / previous - 1);
            var closed = false;
            if (position != 0)
            {
                held++;
                var exit = state.Regime != MeanReversionRegime.Range || Math.Abs(state.ZScore) <= p.MeanReversionExitZ ||
                           Math.Abs(state.ZScore) >= p.MeanReversionStopZ || held >= p.MaximumHoldingBars;
                if (exit)
                {
                    value -= reality.CloseCost(position);
                    position = 0;
                    held = 0;
                    closed = true;
                }
            }

            if (position == 0 && !closed && state.Regime == MeanReversionRegime.Range && state.VolumeRatio >= p.VolumeMultiplier &&
                Math.Abs(state.ZScore) < p.MeanReversionStopZ && state.DistanceAtr < p.AtrStopMultiple)
            {
                var next = state.ZScore <= -p.MeanReversionZ && state.Rsi <= 40 ? 1 : state.ZScore >= p.MeanReversionZ && state.Rsi >= 60 ? -1 : 0;
                if (next != 0)
                {
                    position = next;
                    held = 0;
                    value -= reality.CloseCost(position);
                }
            }
            result.Add((value, closed));
        }

        if (position != 0 && result.Count > 0)
        {
            var last = result[^1];
            result[^1] = (last.Return - reality.CloseCost(position), true);
        }
        return result;
    }
}

internal static class StrategyModuleSimulation
{
    internal static List<(double Return, bool Trade)> SimulateDirectional(
        StrategyProfile profile,
        IReadOnlyList<CandleEvidence> candles,
        ResearchRealityModel reality,
        Func<MarketEvidence, DateTime, int> desiredPosition)
    {
        var result = new List<(double Return, bool Trade)>();
        var position = 0;
        for (var i = profile.Parameters.SlowPeriod + 1; i < candles.Count; i++)
        {
            var prefix = candles.Take(i).ToArray();
            var asOf = prefix[^1].OpenTime.ToUniversalTime();
            var market = new MarketEvidence(profile.Symbol, prefix[^1].Close, prefix.TakeLast(48).Min(x => x.Low), prefix.TakeLast(48).Max(x => x.High), 50, 0, 0, 0, new(0, 0, 1, 1, 1, 1, 0), asOf)
            {
                Candles = prefix
            };
            var direction = desiredPosition(market, asOf);
            var grossReturn = (double)(candles[i].Close / candles[i - 1].Close - 1);
            var step = reality.Apply(position, direction, grossReturn);
            result.Add((step.NetReturn, step.CompletedTrade));
            position = step.Position;
        }

        if (position != 0 && result.Count > 0)
        {
            var last = result[^1];
            result[^1] = (last.Return - reality.CloseCost(position), true);
        }
        return result;
    }
}

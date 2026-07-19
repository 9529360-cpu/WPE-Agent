using System.Text.Json;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed class StrategyResearchAgent
{
    private readonly AgentSqliteStore _database;
    private readonly StrategyGovernor _governor;
    private readonly HistoricalResearchEngine _engine = new();

    public StrategyResearchAgent(AgentSqliteStore database, StrategyGovernor? governor = null)
    {
        _database = database;
        _governor = governor ?? new StrategyGovernor();
    }

    public async Task<StrategyResearchSnapshot> RunOnceAsync(IReadOnlyList<string> symbols, RiskLimits limits, CancellationToken ct)
    {
        var candidates = await EnsureCandidatesAsync(symbols, ct);
        var news = await _database.GetRecentNewsFeaturesAsync(48, ct);
        var validated = 0;
        foreach (var profile in candidates.Where(x => x.Lifecycle == StrategyLifecycle.Draft || (x.BuiltIn && x.ValidationTrades == 0)).ToArray())
        {
            var candles = await _database.LoadHistoricalCandlesAsync(profile.Symbol, "1h", 5000, ct);
            var validation = _engine.Validate(profile, candles, news, limits);
            await _database.SaveStrategyValidationAsync(validation, ct);
            var next = _governor.NextLifecycle(profile, validation);
            profile.QualityScore = validation.QualityScore; profile.Expectancy = validation.Expectancy;
            profile.MaxDrawdown = validation.MaxDrawdown; profile.Sharpe = validation.Sharpe; profile.ValidationTrades = validation.Trades;
            if (next != profile.Lifecycle) { profile.Lifecycle = next; profile.StateChangedAtUtc = DateTime.UtcNow; profile.LastReason = validation.Summary; }
            await _database.UpsertStrategyAsync(profile, ct);
            await _database.RecordStrategyLifecycleAsync(profile, validation.Summary, ct);
            validated++;
        }

        var snapshot = await _database.GetStrategySnapshotAsync(ct);
        await _database.SetStateAsync("strategy-research:last-run", JsonSerializer.Serialize(new { snapshot, NewsFeatures = news.Count, validated }), ct);
        return snapshot with { Status = "LOCAL_RESEARCH_COMPLETE", LastRunAtUtc = DateTime.UtcNow, LastMessage = $"validated={validated}; news_features={news.Count}" };
    }

    public async Task<StrategyResearchSnapshot> ObserveAsync(EvidencePack evidence, CancellationToken ct)
    {
        var profiles = await _database.GetStrategiesAsync(ct);
        foreach (var profile in profiles.Where(x => x.Lifecycle is StrategyLifecycle.Shadow or StrategyLifecycle.Active))
        {
            var market = evidence.Markets.GetValueOrDefault(profile.Symbol);
            if (market is null) continue;
            var signal = _engine.Signal(profile, market, evidence.News);
            await _database.RecordStrategyObservationAsync(profile.Id, profile.Symbol, signal.Direction, market.Price, signal.Confidence, ct);
            var performance = await _database.GetStrategyObservationPerformanceAsync(profile.Id, ct);
            profile.ShadowObservations = performance.Observations; profile.Expectancy = performance.Expectancy;
            profile.MaxDrawdown = performance.MaxDrawdown; profile.FailureStreak = performance.FailureStreak; profile.QualityScore = performance.QualityScore;
            var next = _governor.NextLifecycle(profile);
            if (next != profile.Lifecycle) { profile.Lifecycle = next; profile.StateChangedAtUtc = DateTime.UtcNow; profile.LastReason = $"local performance: {performance.Summary}"; }
            await _database.UpsertStrategyAsync(profile, ct);
            if (next != StrategyLifecycle.Active && profile.Lifecycle == StrategyLifecycle.Degraded)
                await _database.RecordStrategyLifecycleAsync(profile, profile.LastReason, ct);
        }
        // Deterministic failover: a degraded strategy never remains the selected
        // strategy when a validated Shadow challenger has passed the same gates.
        foreach (var symbol in profiles.Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hasActive = profiles.Any(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && x.Lifecycle == StrategyLifecycle.Active);
            if (hasActive) continue;
            var challenger = profiles.Where(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .Where(x => _governor.CanActivateFromShadow(x))
                .OrderByDescending(x => x.QualityScore)
                .ThenByDescending(x => x.Expectancy)
                .FirstOrDefault();
            if (challenger is null) continue;
            challenger.Lifecycle = StrategyLifecycle.Active;
            challenger.StateChangedAtUtc = DateTime.UtcNow;
            challenger.LastReason = "deterministic failover from degraded strategy";
            await _database.UpsertStrategyAsync(challenger, ct);
            await _database.RecordStrategyLifecycleAsync(challenger, challenger.LastReason, ct);
        }
        return await _database.GetStrategySnapshotAsync(ct);
    }

    public StrategySignal GetSignal(StrategyProfile profile, MarketEvidence market, IReadOnlyList<NewsEvidence> news)
        => _engine.Signal(profile, market, news);

    private async Task<IReadOnlyList<StrategyProfile>> EnsureCandidatesAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var existing = await _database.GetStrategiesAsync(ct);
        var result = new List<StrategyProfile>(existing);
        foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var family in Enum.GetValues<StrategyFamily>())
        for (var variant = 0; variant < 2; variant++)
        {
            var id = $"{symbol.ToUpperInvariant()}-{family}-{variant}";
            if (result.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            var profile = new StrategyProfile { Id = id, Version = $"{family.ToString().ToLowerInvariant()}-{variant + 1}", Symbol = symbol.ToUpperInvariant(), Family = family, Parameters = LocalStrategyParameters.For(family, variant), Lifecycle = family == StrategyFamily.TrendBreakout && variant == 0 ? StrategyLifecycle.Active : StrategyLifecycle.Draft, BuiltIn = family == StrategyFamily.TrendBreakout && variant == 0, LastReason = "deterministic local seed" };
            result.Add(profile); await _database.UpsertStrategyAsync(profile, ct);
        }
        return result;
    }
}

internal sealed class HistoricalResearchEngine
{
    public StrategyValidation Validate(StrategyProfile profile, IReadOnlyList<CandleEvidence> candles, IReadOnlyList<NewsFeature> news, RiskLimits limits)
    {
        if (candles.Count < 500) return new(profile.Id, candles.Count, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, false, "insufficient hourly history");
        var returns = Simulate(profile, candles, news); var split = Math.Clamp((int)(returns.Count * .65), 1, returns.Count); var train = returns.Take(split).ToArray(); var test = returns.Skip(split).ToArray();
        var all = Metrics(returns); var oos = Metrics(test); var walk = WalkForward(profile, candles, news); var mc = MonteCarlo(returns); var score = Math.Clamp(.20 * Math.Min(1, all.ProfitFactor / 1.5) + .20 * Math.Max(0, (oos.TotalReturn + .10) / .30) + .20 * (1 - Math.Min(1, all.MaxDrawdown / .25)) + .20 * walk + .20 * (1 - mc), 0, 1);
        var passed = returns.Count(x => x.Trade) >= Math.Max(StrategyGovernor.MinimumValidationTrades, limits.MinimumBacktestTrades) && oos.Expectancy > 0 && all.ProfitFactor >= 1.1 && all.MaxDrawdown <= .25 && walk >= .5 && mc <= .45;
        return new(profile.Id, candles.Count, returns.Count(x => x.Trade), all.WinRate, all.ProfitFactor, all.Expectancy, all.MaxDrawdown, all.Sharpe, oos.TotalReturn, walk, mc, score, passed, $"{profile.Id} trades={returns.Count(x => x.Trade)} OOS={oos.TotalReturn:P1} PF={all.ProfitFactor:F2} DD={all.MaxDrawdown:P1} WF={walk:F2} MC={mc:P0} passed={passed}");
    }

    public StrategySignal Signal(StrategyProfile profile, MarketEvidence market, IReadOnlyList<NewsEvidence> news)
    {
        var candles = market.Candles; if (candles.Count < profile.Parameters.SlowPeriod + 2) return new(profile.Id, market.Symbol, 0, 0, "insufficient candles");
        var p = profile.Parameters; var fast = candles.TakeLast(p.FastPeriod).Average(x => x.Close); var slow = candles.TakeLast(p.SlowPeriod).Average(x => x.Close); var direction = 0; var confidence = Math.Min(1, Math.Abs((double)(fast / slow - 1)) * 40);
        if (profile.Family == StrategyFamily.TrendBreakout) direction = fast > slow && market.Price > candles.TakeLast(48).Max(x => x.High) * (decimal)(1 - p.BreakoutBuffer) ? 1 : fast < slow && market.Price < candles.TakeLast(48).Min(x => x.Low) * (decimal)(1 + p.BreakoutBuffer) ? -1 : 0;
        if (profile.Family == StrategyFamily.MeanReversion) { var mean = candles.TakeLast(p.SlowPeriod).Average(x => x.Close); var deviation = candles.TakeLast(p.SlowPeriod).Select(x => (double)(x.Close / mean - 1)).ToArray(); var z = deviation.Length == 0 ? 0 : deviation[^1] / Math.Max(.0001, Math.Sqrt(deviation.Select(x => x * x).Average())); direction = z < -p.MeanReversionZ ? 1 : z > p.MeanReversionZ ? -1 : 0; confidence = Math.Min(1, Math.Abs(z) / 3); }
        if (profile.Family == StrategyFamily.NewsMomentum) { var sentiment = news.Where(x => x.AffectedAssets.Any(a => a.Equals(market.Symbol, StringComparison.OrdinalIgnoreCase) || market.Symbol.StartsWith(a, StringComparison.OrdinalIgnoreCase))).OrderByDescending(x => x.PublishedAt).Take(5).Select(x => x.Sentiment * x.Confidence).DefaultIfEmpty().Average(); direction = sentiment >= p.NewsSentimentThreshold ? 1 : sentiment <= -p.NewsSentimentThreshold ? -1 : 0; confidence = Math.Min(1, Math.Abs(sentiment)); }
        return new(profile.Id, market.Symbol, direction, confidence, $"family={profile.Family}; local deterministic signal");
    }

    private static List<(double Return, bool Trade)> Simulate(StrategyProfile profile, IReadOnlyList<CandleEvidence> candles, IReadOnlyList<NewsFeature> news)
    { var result = new List<(double, bool)>(); var engine = new HistoricalResearchEngine(); for (var i = profile.Parameters.SlowPeriod + 1; i < candles.Count; i++) { var prefix = candles.Take(i + 1).ToArray(); var market = new MarketEvidence(profile.Symbol, prefix[^1].Close, prefix.TakeLast(48).Min(x => x.Low), prefix.TakeLast(48).Max(x => x.High), 50, 0, 0, 0, new(0, 0, 1, 1, 1, 1, 0), DateTime.UtcNow) { Candles = prefix }; var signal = engine.Signal(profile, market, Array.Empty<NewsEvidence>()); var r = signal.Direction * (double)(candles[i].Close / candles[i - 1].Close - 1) - (signal.Direction != 0 ? .0014 : 0); result.Add((r, signal.Direction != 0)); } return result; }
    private static (double WinRate, double ProfitFactor, double Expectancy, double MaxDrawdown, double Sharpe, double TotalReturn) Metrics(IReadOnlyList<(double Return, bool Trade)> values) { var r = values.Select(x => x.Return).ToArray(); if (r.Length == 0) return (0, 0, 0, 1, 0, 0); var wins = r.Where(x => x > 0).Sum(); var losses = -r.Where(x => x < 0).Sum(); var equity = 1d; var high = 1d; var dd = 0d; foreach (var x in r) { equity *= Math.Max(.01, 1 + x); high = Math.Max(high, equity); dd = Math.Max(dd, (high - equity) / high); } var avg = r.Average(); var sd = Math.Sqrt(r.Select(x => (x - avg) * (x - avg)).Average()); return (r.Count(x => x > 0) / (double)r.Length, losses > 0 ? wins / losses : wins > 0 ? 9 : 0, avg, dd, sd > 0 ? avg / sd * Math.Sqrt(24 * 365) : 0, equity - 1); }
    private double WalkForward(StrategyProfile p, IReadOnlyList<CandleEvidence> c, IReadOnlyList<NewsFeature> n) { var scores = new List<double>(); for (var i = 0; i < 4; i++) { var start = i * c.Count / 8; var length = Math.Min(c.Count - start, c.Count / 2); scores.Add(Math.Clamp(.5 + Metrics(Simulate(p, c.Skip(start).Take(length).ToArray(), n)).Expectancy * 100 - Metrics(Simulate(p, c.Skip(start).Take(length).ToArray(), n)).MaxDrawdown, 0, 1)); } return scores.DefaultIfEmpty(0).Average(); }
    private static double MonteCarlo(IReadOnlyList<(double Return, bool Trade)> values) { var r = values.Select(x => x.Return).ToArray(); if (r.Length == 0) return 1; var random = new Random(73); var losses = 0; for (var n = 0; n < 250; n++) { var equity = 1d; for (var i = 0; i < Math.Min(r.Length, 2000); i++) equity *= Math.Max(.01, 1 + r[random.Next(r.Length)]); if (equity < 1) losses++; } return losses / 250d; }
}

using System.Text.Json.Serialization;

namespace 币安量化机器人.Services.Agent;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarketStructureBias { Bullish, Bearish, Ranging, Transition, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarketStructureEvent { None, BreakoutUp, BreakoutDown, RetestUp, RetestDown, FalseBreakoutUp, FalseBreakoutDown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarketHypothesisStatus { Rejected, Developing, Confirmed }

public sealed record MarketStructureSnapshot(
    string Symbol,
    bool Ready,
    MarketStructureBias Bias,
    MarketStructureEvent Event,
    decimal ReferenceSupport,
    decimal ReferenceResistance,
    decimal LastClose,
    double VolumeRatio,
    double RangePosition,
    bool HigherTimeframeBullish,
    bool HigherTimeframeBearish,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> MissingConditions);

public sealed record MarketHypothesis(
    string Id,
    PositionSide? Direction,
    MarketHypothesisStatus Status,
    string Thesis,
    string Trigger,
    string Invalidation,
    IReadOnlyList<string> SupportingEvidence,
    IReadOnlyList<string> CounterEvidence);

public sealed record NumericalMarketAnalysis(
    MarketStructureSnapshot Structure,
    IReadOnlyList<MarketHypothesis> Hypotheses);

public sealed class NumericalMarketStructureSkill
{
    public const int MinimumCandles = 32;

    public MarketStructureSnapshot Analyze(MarketEvidence? market)
    {
        if (market is null) return Invalid("UNKNOWN", "structure.market-missing");
        if (string.IsNullOrWhiteSpace(market.Symbol)) return Invalid("UNKNOWN", "structure.symbol-invalid");
        if (!MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market)) return Invalid(market.Symbol, "structure.provenance-invalid");

        var candles = market.Candles.OrderBy(x => x.OpenTime).ToArray();
        if (candles.Length < MinimumCandles) return Invalid(market.Symbol, "structure.candles-insufficient");
        if (candles.Any(x => !Valid(x)) || candles.GroupBy(x => x.OpenTime).Any(x => x.Count() != 1))
            return Invalid(market.Symbol, "structure.candles-invalid");

        var window = candles.TakeLast(Math.Min(96, candles.Length)).ToArray();
        var recentCount = Math.Min(6, Math.Max(3, window.Length / 8));
        var baseline = window.Take(window.Length - recentCount).TakeLast(48).ToArray();
        var recent = window.Skip(window.Length - recentCount).ToArray();
        if (baseline.Length < 20 || recent.Length < 3) return Invalid(market.Symbol, "structure.window-insufficient");

        var referenceResistance = baseline.Max(x => x.High);
        var referenceSupport = baseline.Min(x => x.Low);
        var last = recent[^1];
        if (referenceResistance <= referenceSupport || last.Close <= 0) return Invalid(market.Symbol, "structure.range-invalid");

        var breakoutUpIndex = Array.FindIndex(recent, x => x.Close > referenceResistance * 1.0005m);
        var breakoutDownIndex = Array.FindIndex(recent, x => x.Close < referenceSupport * .9995m);
        var retestUp = breakoutUpIndex >= 0 && recent.Skip(breakoutUpIndex + 1)
            .Any(x => x.Low <= referenceResistance * 1.0025m && x.Close >= referenceResistance);
        var retestDown = breakoutDownIndex >= 0 && recent.Skip(breakoutDownIndex + 1)
            .Any(x => x.High >= referenceSupport * .9975m && x.Close <= referenceSupport);
        var falseBreakoutUp = breakoutUpIndex >= 0 && last.Close < referenceResistance * .9995m;
        var falseBreakoutDown = breakoutDownIndex >= 0 && last.Close > referenceSupport * 1.0005m;

        var marketEvent = falseBreakoutUp ? MarketStructureEvent.FalseBreakoutUp
            : falseBreakoutDown ? MarketStructureEvent.FalseBreakoutDown
            : retestUp ? MarketStructureEvent.RetestUp
            : retestDown ? MarketStructureEvent.RetestDown
            : breakoutUpIndex >= 0 ? MarketStructureEvent.BreakoutUp
            : breakoutDownIndex >= 0 ? MarketStructureEvent.BreakoutDown
            : MarketStructureEvent.None;

        var (swingHighs, swingLows) = Swings(window);
        var higherBullish = market.Trend1h > .001 && market.Trend4h >= 0;
        var higherBearish = market.Trend1h < -.001 && market.Trend4h <= 0;
        MarketStructureBias bias;
        if (swingHighs.Count >= 2 && swingLows.Count >= 2 &&
            swingHighs[^1] > swingHighs[^2] && swingLows[^1] > swingLows[^2])
            bias = MarketStructureBias.Bullish;
        else if (swingHighs.Count >= 2 && swingLows.Count >= 2 &&
                 swingHighs[^1] < swingHighs[^2] && swingLows[^1] < swingLows[^2])
            bias = MarketStructureBias.Bearish;
        else if (market.Trend15m > 0 && !higherBearish)
            bias = MarketStructureBias.Bullish;
        else if (market.Trend15m < 0 && !higherBullish)
            bias = MarketStructureBias.Bearish;
        else if (Math.Abs(market.Trend15m) < .003 && Math.Abs(market.Trend1h) < .005)
            bias = MarketStructureBias.Ranging;
        else
            bias = MarketStructureBias.Transition;

        var recentVolume = recent.TakeLast(Math.Min(4, recent.Length)).Average(x => x.Volume);
        var baselineVolume = baseline.TakeLast(Math.Min(20, baseline.Length)).Average(x => x.Volume);
        var volumeRatio = baselineVolume > 0 ? (double)(recentVolume / baselineVolume) : 1d;
        var rangePosition = (double)Math.Clamp((last.Close - referenceSupport) / (referenceResistance - referenceSupport), 0m, 1m);
        var evidence = new List<string>
        {
            "market-structure:v1",
            "bias:" + bias.ToString().ToLowerInvariant(),
            "event:" + marketEvent.ToString().ToLowerInvariant()
        };
        if (higherBullish) evidence.Add("htf:bullish");
        if (higherBearish) evidence.Add("htf:bearish");
        if (volumeRatio >= 1.2) evidence.Add("volume:expanding");
        else if (volumeRatio <= .7) evidence.Add("volume:contracting");
        else evidence.Add("volume:normal");

        var body = Math.Abs(last.Close - last.Open);
        var wickFloor = Math.Max(body, last.Close * .0005m);
        var upperWick = last.High - Math.Max(last.Open, last.Close);
        var lowerWick = Math.Min(last.Open, last.Close) - last.Low;
        if (lowerWick > wickFloor * 2) evidence.Add("rejection:lower-wick");
        if (upperWick > wickFloor * 2) evidence.Add("rejection:upper-wick");

        return new(
            market.Symbol, true, bias, marketEvent, referenceSupport, referenceResistance, last.Close,
            volumeRatio, rangePosition, higherBullish, higherBearish, evidence, Array.Empty<string>());
    }

    private static MarketStructureSnapshot Invalid(string symbol, string reason) =>
        new(symbol, false, MarketStructureBias.Unknown, MarketStructureEvent.None, 0, 0, 0, 0, 0, false, false,
            Array.Empty<string>(), new[] { reason });

    private static bool Valid(CandleEvidence x) =>
        x.OpenTime.Kind == DateTimeKind.Utc && x.Open > 0 && x.High > 0 && x.Low > 0 && x.Close > 0 &&
        x.Low <= Math.Min(x.Open, x.Close) && x.High >= Math.Max(x.Open, x.Close) && x.High >= x.Low &&
        x.Volume >= 0 && x.QuoteVolume >= 0 && x.Trades >= 0 && x.TakerBuyVolume >= 0;

    private static (List<decimal> Highs, List<decimal> Lows) Swings(IReadOnlyList<CandleEvidence> candles)
    {
        var highs = new List<decimal>();
        var lows = new List<decimal>();
        for (var i = 2; i < candles.Count - 2; i++)
        {
            var value = candles[i];
            if (value.High >= candles[i - 1].High && value.High > candles[i - 2].High &&
                value.High >= candles[i + 1].High && value.High > candles[i + 2].High)
                highs.Add(value.High);
            if (value.Low <= candles[i - 1].Low && value.Low < candles[i - 2].Low &&
                value.Low <= candles[i + 1].Low && value.Low < candles[i + 2].Low)
                lows.Add(value.Low);
        }
        return (highs, lows);
    }
}

public sealed class MarketHypothesisSkill
{
    public IReadOnlyList<MarketHypothesis> Build(MarketEvidence market, MarketStructureSnapshot structure)
    {
        if (!structure.Ready)
            return new[]
            {
                new MarketHypothesis("wait-for-data", null, MarketHypothesisStatus.Developing,
                    "Market structure is not yet trustworthy.", "fresh canonical candles",
                    "structure data remains invalid", Array.Empty<string>(), structure.MissingConditions)
            };

        var longSupport = new List<string>();
        var longCounter = new List<string>();
        var shortSupport = new List<string>();
        var shortCounter = new List<string>();

        if (structure.Bias == MarketStructureBias.Bullish) longSupport.Add("higher-high/higher-low or aligned positive structure");
        if (structure.Bias == MarketStructureBias.Bearish) shortSupport.Add("lower-high/lower-low or aligned negative structure");
        if (structure.HigherTimeframeBullish) { longSupport.Add("1h/4h alignment is bullish"); shortCounter.Add("higher timeframes oppose short"); }
        if (structure.HigherTimeframeBearish) { shortSupport.Add("1h/4h alignment is bearish"); longCounter.Add("higher timeframes oppose long"); }

        if (structure.Event is MarketStructureEvent.BreakoutUp or MarketStructureEvent.RetestUp)
            longSupport.Add("upside breakout structure is active");
        if (structure.Event is MarketStructureEvent.BreakoutDown or MarketStructureEvent.RetestDown)
            shortSupport.Add("downside breakout structure is active");
        if (structure.Event == MarketStructureEvent.FalseBreakoutUp)
        {
            shortSupport.Add("upside breakout failed back into range");
            longCounter.Add("upside breakout failed");
        }
        if (structure.Event == MarketStructureEvent.FalseBreakoutDown)
        {
            longSupport.Add("downside breakout failed back into range");
            shortCounter.Add("downside breakout failed");
        }

        if (structure.VolumeRatio >= 1.2)
        {
            longSupport.Add("participation expanded with the move");
            shortSupport.Add("participation expanded with the move");
        }
        else if (structure.VolumeRatio <= .7)
        {
            longCounter.Add("participation is contracting");
            shortCounter.Add("participation is contracting");
        }

        if (market.Derivatives.FundingRate > .001m || market.Derivatives.LongShortRatio > 1.8m)
            longCounter.Add("long positioning is crowded");
        if (market.Derivatives.FundingRate < -.001m || (market.Derivatives.LongShortRatio > 0 && market.Derivatives.LongShortRatio < .55m))
            shortCounter.Add("short positioning is crowded");

        var longConfirmed = !structure.HigherTimeframeBearish &&
            (structure.Event == MarketStructureEvent.RetestUp ||
             structure.Event == MarketStructureEvent.FalseBreakoutDown ||
             structure.Event == MarketStructureEvent.BreakoutUp && structure.Bias == MarketStructureBias.Bullish && structure.VolumeRatio >= .85 ||
             structure.Event == MarketStructureEvent.None && structure.Bias == MarketStructureBias.Bullish && structure.HigherTimeframeBullish && structure.VolumeRatio >= 1.15 && structure.RangePosition >= .55);
        var shortConfirmed = !structure.HigherTimeframeBullish &&
            (structure.Event == MarketStructureEvent.RetestDown ||
             structure.Event == MarketStructureEvent.FalseBreakoutUp ||
             structure.Event == MarketStructureEvent.BreakoutDown && structure.Bias == MarketStructureBias.Bearish && structure.VolumeRatio >= .85 ||
             structure.Event == MarketStructureEvent.None && structure.Bias == MarketStructureBias.Bearish && structure.HigherTimeframeBearish && structure.VolumeRatio >= 1.15 && structure.RangePosition <= .45);

        var longDeveloping = !longConfirmed &&
            (structure.Bias == MarketStructureBias.Bullish || structure.Event is MarketStructureEvent.BreakoutUp or MarketStructureEvent.RetestUp or MarketStructureEvent.FalseBreakoutDown);
        var shortDeveloping = !shortConfirmed &&
            (structure.Bias == MarketStructureBias.Bearish || structure.Event is MarketStructureEvent.BreakoutDown or MarketStructureEvent.RetestDown or MarketStructureEvent.FalseBreakoutUp);

        var hypotheses = new List<MarketHypothesis>
        {
            new("bullish-continuation", PositionSide.Long,
                longConfirmed ? MarketHypothesisStatus.Confirmed : longDeveloping ? MarketHypothesisStatus.Developing : MarketHypothesisStatus.Rejected,
                "Bullish structure can continue if price holds the reclaimed structure.",
                "hold above " + structure.ReferenceResistance.ToString("0.########"),
                "close below " + structure.ReferenceSupport.ToString("0.########"),
                longSupport, longCounter),
            new("bearish-continuation", PositionSide.Short,
                shortConfirmed ? MarketHypothesisStatus.Confirmed : shortDeveloping ? MarketHypothesisStatus.Developing : MarketHypothesisStatus.Rejected,
                "Bearish structure can continue if price holds below the lost structure.",
                "hold below " + structure.ReferenceSupport.ToString("0.########"),
                "close above " + structure.ReferenceResistance.ToString("0.########"),
                shortSupport, shortCounter)
        };

        var rangeStatus = !longConfirmed && !shortConfirmed && structure.Bias == MarketStructureBias.Ranging
            ? MarketHypothesisStatus.Confirmed : MarketHypothesisStatus.Developing;
        hypotheses.Add(new("range-or-wait", null, rangeStatus,
            "No directional structure has earned an entry yet.", "directional structure confirmation",
            "confirmed directional structure", structure.Evidence, Array.Empty<string>()));
        return hypotheses;
    }
}

public sealed class NumericalStrategySkill
{
    public const string Basis = "market-structure-hypothesis-v1";
    public const string Version = "wpe-numerical-structure-v1";
    private readonly NumericalMarketStructureSkill _structure = new();
    private readonly MarketHypothesisSkill _hypothesis = new();

    public bool CanAnalyze(EvidencePack evidence) =>
        evidence.Markets.Values.Any(x => _structure.Analyze(x).Ready);

    public DecisionPlan Decide(EvidencePack evidence, AgentContext context)
    {
        var analyses = evidence.Markets.Values
            .Where(x => x is not null)
            .Select(x =>
            {
                var structure = _structure.Analyze(x);
                return new { Market = x, Analysis = new NumericalMarketAnalysis(structure, _hypothesis.Build(x, structure)) };
            })
            .Where(x => x.Analysis.Structure.Ready)
            .OrderBy(x => x.Market.Symbol, StringComparer.Ordinal)
            .ToArray();

        var defaultSymbol = context.ActiveSymbol ?? analyses.FirstOrDefault()?.Market.Symbol ?? evidence.Markets.Keys.FirstOrDefault() ?? "BTCUSDT";
        if (context.CircuitBreakerActive)
            return Hold(defaultSymbol, "local risk circuit breaker is active", analyses.SelectMany(x => x.Analysis.Structure.Evidence));

        var occupied = evidence.Positions.Select(x => x.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = analyses
            .Where(x => !occupied.Contains(x.Market.Symbol))
            .SelectMany(x => x.Analysis.Hypotheses
                .Where(h => h.Direction.HasValue && h.Status == MarketHypothesisStatus.Confirmed)
                .Select(h => new { x.Market, x.Analysis, Hypothesis = h }))
            .GroupBy(x => x.Market.Symbol, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Hypothesis.Direction).Distinct().Count() == 1)
            .SelectMany(g => g)
            .OrderByDescending(x => EventPriority(x.Analysis.Structure.Event))
            .ThenByDescending(x => DirectionAligned(x.Analysis.Structure, x.Hypothesis.Direction!.Value))
            .ThenByDescending(x => x.Market.Quality.QualityScore)
            .ThenBy(x => x.Market.Symbol, StringComparer.Ordinal)
            .ToArray();

        var selected = candidates.FirstOrDefault();
        if (selected is null)
        {
            var developing = analyses
                .SelectMany(x => x.Analysis.Hypotheses.Where(h => h.Status == MarketHypothesisStatus.Developing && h.Direction.HasValue)
                    .Select(h => new { x.Market, x.Analysis, Hypothesis = h }))
                .OrderByDescending(x => EventPriority(x.Analysis.Structure.Event))
                .ThenByDescending(x => x.Market.Quality.QualityScore)
                .ThenBy(x => x.Market.Symbol, StringComparer.Ordinal)
                .FirstOrDefault();
            var reason = developing is null ? "WAIT_FOR_CONFIRMATION: no confirmed directional market structure"
                : "WAIT_FOR_CONFIRMATION: " + developing.Hypothesis.Trigger;
            var evidenceRefs = developing is null ? analyses.SelectMany(x => x.Analysis.Structure.Evidence)
                : developing.Analysis.Structure.Evidence.Concat(developing.Hypothesis.SupportingEvidence);
            return Hold(developing?.Market.Symbol ?? defaultSymbol, reason, evidenceRefs,
                developing is null ? Array.Empty<string>() : new[] { developing.Hypothesis.Trigger });
        }

        var longSide = selected.Hypothesis.Direction == PositionSide.Long;
        var action = longSide ? DecisionAction.OpenLong : DecisionAction.OpenShort;
        return new DecisionPlan
        {
            Action = action,
            Instrument = selected.Market.Symbol,
            TargetTier = 1,
            Confidence = Confidence(selected.Analysis.Structure.Event),
            Regime = Regime(selected.Analysis.Structure.Bias).ToString(),
            Reason = selected.Hypothesis.Thesis,
            Invalidation = selected.Hypothesis.Invalidation,
            EvidenceReferences = selected.Analysis.Structure.Evidence
                .Concat(selected.Hypothesis.SupportingEvidence)
                .Distinct(StringComparer.Ordinal)
                .Take(12)
                .ToList(),
            MissingConditions = new List<string>(),
            ConflictSummary = selected.Hypothesis.CounterEvidence.Count == 0
                ? "no material counter-evidence"
                : string.Join("; ", selected.Hypothesis.CounterEvidence),
            StrategyVersion = Version,
            DecisionBasis = Basis
        };
    }

    public static bool IsNumerical(DecisionPlan? plan) =>
        plan is not null && string.Equals(plan.DecisionBasis, Basis, StringComparison.Ordinal);

    public static IReadOnlyList<string> ValidateDecision(DecisionPlan plan, EvidencePack evidence, DecisionPolicy? policy)
    {
        var blocks = new List<string>();
        if (!IsNumerical(plan)) return new[] { "structure.basis-invalid" };
        if (!DeterministicPlanSkill.IsRiskIncreasing(plan.Action)) return blocks;
        if (plan.Action is not DecisionAction.OpenLong and not DecisionAction.OpenShort)
            blocks.Add("structure.action-not-supported");
        if (!evidence.Markets.TryGetValue(plan.Instrument, out var market) || market is null)
            return blocks.Append("structure.market-missing").ToArray();
        if (!MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market))
            blocks.Add("structure.provenance-invalid");
        if (policy is not null && market.Quality.QualityScore < policy.MinimumMarketQuality)
            blocks.Add("structure.market-quality");

        var structure = new NumericalMarketStructureSkill().Analyze(market);
        if (!structure.Ready)
        {
            blocks.AddRange(structure.MissingConditions);
            return blocks.Distinct(StringComparer.Ordinal).ToArray();
        }
        var hypotheses = new MarketHypothesisSkill().Build(market, structure);
        var confirmed = hypotheses.Where(x => x.Status == MarketHypothesisStatus.Confirmed && x.Direction.HasValue).ToArray();
        if (confirmed.Select(x => x.Direction).Distinct().Count() > 1) blocks.Add("structure.direction-conflict");
        var expected = plan.Action == DecisionAction.OpenLong ? PositionSide.Long : PositionSide.Short;
        if (!confirmed.Any(x => x.Direction == expected)) blocks.Add("structure.hypothesis-not-confirmed");
        return blocks.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static DecisionPlan Hold(string symbol, string reason, IEnumerable<string> evidence, IEnumerable<string>? missing = null) =>
        new()
        {
            Action = DecisionAction.Hold,
            Instrument = symbol,
            TargetTier = 0,
            Confidence = 0,
            Regime = MarketRegime.Unknown.ToString(),
            Reason = reason,
            Invalidation = "wait until a directional structure is confirmed",
            EvidenceReferences = evidence.Distinct(StringComparer.Ordinal).Take(12).ToList(),
            MissingConditions = (missing ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList(),
            ConflictSummary = "no executable structure-confirmed hypothesis",
            StrategyVersion = Version,
            DecisionBasis = Basis
        };

    private static int EventPriority(MarketStructureEvent value) => value switch
    {
        MarketStructureEvent.RetestUp or MarketStructureEvent.RetestDown => 4,
        MarketStructureEvent.FalseBreakoutUp or MarketStructureEvent.FalseBreakoutDown => 3,
        MarketStructureEvent.BreakoutUp or MarketStructureEvent.BreakoutDown => 2,
        _ => 1
    };

    private static int DirectionAligned(MarketStructureSnapshot structure, PositionSide side) =>
        side == PositionSide.Long && structure.HigherTimeframeBullish ||
        side == PositionSide.Short && structure.HigherTimeframeBearish ? 1 : 0;

    private static double Confidence(MarketStructureEvent value) => value switch
    {
        MarketStructureEvent.RetestUp or MarketStructureEvent.RetestDown => .78,
        MarketStructureEvent.FalseBreakoutUp or MarketStructureEvent.FalseBreakoutDown => .76,
        MarketStructureEvent.BreakoutUp or MarketStructureEvent.BreakoutDown => .74,
        _ => .70
    };

    private static MarketRegime Regime(MarketStructureBias bias) => bias switch
    {
        MarketStructureBias.Bullish or MarketStructureBias.Bearish => MarketRegime.Trending,
        MarketStructureBias.Ranging => MarketRegime.Ranging,
        MarketStructureBias.Transition => MarketRegime.Transition,
        _ => MarketRegime.Unknown
    };
}

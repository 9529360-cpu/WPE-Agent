namespace 币安量化机器人.Services.Agent;

public enum PriceStructureState
{
    Unknown,
    Bullish,
    Bearish,
    Range,
    Transition
}

public enum MarketStructureEvent
{
    None,
    BullishBreak,
    BearishBreak,
    BullishRetest,
    BearishRetest,
    LiquiditySweepLowReclaim,
    LiquiditySweepHighReject,
    BullishRejection,
    BearishRejection,
    BullishConfirmation,
    BearishConfirmation,
    BullishDisplacement,
    BearishDisplacement,
    Compression
}

public enum MarketStructureBias
{
    Unknown,
    Bullish,
    Bearish,
    Range,
    Mixed
}

public enum MarketStructurePhase
{
    Unknown,
    BullishImpulse,
    BearishImpulse,
    BullishPullback,
    BearishPullback,
    Balance,
    Compression,
    BullishReversalAttempt,
    BearishReversalAttempt
}

public enum MarketStructureScenario
{
    None,
    TrendPullbackLong,
    TrendPullbackShort,
    RangeReversionLong,
    RangeReversionShort,
    BreakoutRetestLong,
    BreakoutRetestShort
}

public sealed record TimeframeStructureRead(
    string Interval,
    PriceStructureState State,
    MarketStructureEvent Event,
    decimal LastClose,
    decimal Demand,
    decimal Supply,
    decimal LastSwingHigh,
    decimal PreviousSwingHigh,
    decimal LastSwingLow,
    decimal PreviousSwingLow,
    decimal Atr,
    bool VolumeExpansion,
    bool RangeExpansion,
    bool Compression,
    string Narrative);

public sealed record MarketStructureRead(
    bool Available,
    MarketStructureBias HigherTimeframeBias,
    MarketStructurePhase Phase,
    MarketStructureScenario Scenario,
    bool TriggerPresent,
    bool ConfirmationPresent,
    decimal StructuralSupport,
    decimal StructuralResistance,
    TimeframeStructureRead FifteenMinute,
    TimeframeStructureRead OneHour,
    TimeframeStructureRead FourHour,
    string Narrative,
    IReadOnlyList<string> Evidence)
{
    public const string DecisionBasis = "candles-structure-v3";
}

public static class MarketStructureIntelligence
{
    public static MarketStructureRead Analyze(MarketEvidence market)
    {
        ArgumentNullException.ThrowIfNull(market);
        var m15 = AnalyzeFrame("15m", market.Candles, market.CollectedAt);
        var h1 = AnalyzeFrame("1h", market.Candles1h, market.CollectedAt);
        var h4 = AnalyzeFrame("4h", market.Candles4h, market.CollectedAt);
        var available = m15.State != PriceStructureState.Unknown &&
                        h1.State != PriceStructureState.Unknown &&
                        h4.State != PriceStructureState.Unknown;
        if (!available)
            return new(false, MarketStructureBias.Unknown, MarketStructurePhase.Unknown, MarketStructureScenario.None, false, false,
                market.Support, market.Resistance, m15, h1, h4,
                "Multi-timeframe candle structure is not yet complete.",
                ["structure_basis=unavailable"]);

        var bias = HigherTimeframeBias(h1.State, h4.State);
        var nearDemand = Near(m15.LastClose, m15.Demand, m15.Atr);
        var nearSupply = Near(m15.LastClose, m15.Supply, m15.Atr);
        var phase = DetectPhase(bias, m15);
        var scenario = DetectScenario(bias, phase, m15, nearDemand, nearSupply);
        var trigger = HasTrigger(scenario, m15);
        var confirmation = HasConfirmation(scenario, m15);
        var narrative = BuildNarrative(bias, phase, scenario, m15, h1, h4, nearDemand, nearSupply);
        var evidence = new List<string>
        {
            $"structure_basis={MarketStructureRead.DecisionBasis}",
            $"structure_bias={bias}",
            $"structure_phase={phase}",
            $"structure_scenario={scenario}",
            $"15m_structure={m15.State}",
            $"15m_event={m15.Event}",
            $"1h_structure={h1.State}",
            $"4h_structure={h4.State}",
            $"15m_demand={m15.Demand:F2}",
            $"15m_supply={m15.Supply:F2}",
            $"15m_volume={(m15.VolumeExpansion ? "expanding" : "normal")}",
            $"15m_range={(m15.Compression ? "compression" : m15.RangeExpansion ? "expansion" : "normal")}",
            $"structure_trigger={(trigger ? "present" : "waiting")}",
            $"structure_confirmation={(confirmation ? "present" : "waiting")}",
            $"structure_entry_ready={(trigger && confirmation ? "ready" : "waiting")}"
        };

        return new(true, bias, phase, scenario, trigger, confirmation,
            m15.Demand > 0 ? m15.Demand : market.Support,
            m15.Supply > 0 ? m15.Supply : market.Resistance,
            m15, h1, h4, narrative, evidence);
    }

    private static TimeframeStructureRead AnalyzeFrame(
        string interval,
        IReadOnlyList<CandleEvidence> source,
        DateTime observedAtUtc)
    {
        var candles = ConfirmedMarketCandlesV1.Select(source, interval, observedAtUtc)
            .TakeLast(160)
            .ToArray();
        if (candles.Length < 24)
            return Unknown(interval);

        var last = candles[^1];
        var atr = Atr(candles);
        var buffer = Math.Max(atr * .12m, last.Close * .0003m);
        var highs = Pivots(candles, high: true);
        var lows = Pivots(candles, high: false);

        var recentWindow = candles.TakeLast(Math.Min(12, candles.Length)).ToArray();
        var priorWindow = candles.Skip(Math.Max(0, candles.Length - 24)).Take(Math.Min(12, Math.Max(0, candles.Length - 12))).ToArray();
        if (priorWindow.Length < 6)
            priorWindow = candles.Take(Math.Max(1, candles.Length - recentWindow.Length)).TakeLast(12).ToArray();

        var recentHigh = recentWindow.Max(x => x.High);
        var recentLow = recentWindow.Min(x => x.Low);
        var priorHigh = priorWindow.Length > 0 ? priorWindow.Max(x => x.High) : recentHigh;
        var priorLow = priorWindow.Length > 0 ? priorWindow.Min(x => x.Low) : recentLow;

        var lastSwingHigh = highs.Count > 0 ? highs[^1].Price : recentHigh;
        var previousSwingHigh = highs.Count > 1 ? highs[^2].Price : priorHigh;
        var lastSwingLow = lows.Count > 0 ? lows[^1].Price : recentLow;
        var previousSwingLow = lows.Count > 1 ? lows[^2].Price : priorLow;

        var state = StructureState(
            lastSwingHigh, previousSwingHigh, lastSwingLow, previousSwingLow,
            recentHigh, priorHigh, recentLow, priorLow, buffer);

        var previous = candles[^2];
        var referenceHigh = highs.LastOrDefault(x => x.Index <= candles.Length - 3)?.Price ?? candles.TakeLast(20).SkipLast(1).Max(x => x.High);
        var referenceLow = lows.LastOrDefault(x => x.Index <= candles.Length - 3)?.Price ?? candles.TakeLast(20).SkipLast(1).Min(x => x.Low);

        var priorRanges = candles.Skip(Math.Max(0, candles.Length - 13)).Take(Math.Min(12, candles.Length - 1))
            .Select(x => x.High - x.Low).Where(x => x > 0).ToArray();
        var averageRange = priorRanges.Length == 0 ? atr : priorRanges.Average();
        var currentRange = last.High - last.Low;
        var rangeExpansion = averageRange > 0 && currentRange >= averageRange * 1.45m;

        var baselineVolume = candles.TakeLast(Math.Min(21, candles.Length)).SkipLast(1)
            .Select(x => x.QuoteVolume).Where(x => x > 0).ToArray();
        var volumeExpansion = baselineVolume.Length >= 8 &&
                              last.QuoteVolume > baselineVolume.Average() * 1.25m;

        var recentRanges = candles.TakeLast(5).Select(x => x.High - x.Low).Where(x => x > 0).ToArray();
        var earlierRanges = candles.Skip(Math.Max(0, candles.Length - 15)).Take(10)
            .Select(x => x.High - x.Low).Where(x => x > 0).ToArray();
        var compression = recentRanges.Length == 5 && earlierRanges.Length >= 6 &&
                          recentRanges.Average() < earlierRanges.Average() * .68m;

        var eventKind = DetectEvent(
            candles, last, previous, referenceHigh, referenceLow, buffer,
            volumeExpansion, rangeExpansion, compression);

        var demand = lastSwingLow > 0 ? lastSwingLow : recentLow;
        var supply = lastSwingHigh > 0 ? lastSwingHigh : recentHigh;
        var narrative =
            $"{interval}: {state}; last={last.Close:F2}; demand={demand:F2}; supply={supply:F2}; event={eventKind}; " +
            $"volume={(volumeExpansion ? "expansion" : "normal")}; range={(compression ? "compression" : rangeExpansion ? "expansion" : "normal")}.";

        return new(interval, state, eventKind, last.Close, demand, supply,
            lastSwingHigh, previousSwingHigh, lastSwingLow, previousSwingLow,
            atr, volumeExpansion, rangeExpansion, compression, narrative);
    }

    private static MarketStructureEvent DetectEvent(
        IReadOnlyList<CandleEvidence> candles,
        CandleEvidence last,
        CandleEvidence previous,
        decimal referenceHigh,
        decimal referenceLow,
        decimal buffer,
        bool volumeExpansion,
        bool rangeExpansion,
        bool compression)
    {
        var breakUp = previous.Close <= referenceHigh + buffer && last.Close > referenceHigh + buffer;
        var breakDown = previous.Close >= referenceLow - buffer && last.Close < referenceLow - buffer;
        var recent = candles.TakeLast(Math.Min(7, candles.Count)).SkipLast(1).ToArray();
        var brokeUpRecently = recent.Any(x => x.Close > referenceHigh + buffer);
        var brokeDownRecently = recent.Any(x => x.Close < referenceLow - buffer);
        var retestUp = brokeUpRecently && last.Low <= referenceHigh + buffer && last.Close >= referenceHigh && last.Close > last.Open;
        var retestDown = brokeDownRecently && last.High >= referenceLow - buffer && last.Close <= referenceLow && last.Close < last.Open;
        var sweepLow = last.Low < referenceLow - buffer && last.Close > referenceLow + buffer;
        var sweepHigh = last.High > referenceHigh + buffer && last.Close < referenceHigh - buffer;
        var priorCandles = candles.Take(Math.Max(0,candles.Count-1)).ToArray();
        var priorAtr = Atr(priorCandles);
        var priorBuffer = Math.Max(priorAtr * .12m, previous.Close * .0003m);
        var priorHighs = Pivots(priorCandles, high: true);
        var priorLows = Pivots(priorCandles, high: false);
        var priorReferenceHigh = priorHighs.LastOrDefault(x => x.Index <= priorCandles.Length - 3)?.Price ?? priorCandles.TakeLast(20).SkipLast(1).Max(x => x.High);
        var priorReferenceLow = priorLows.LastOrDefault(x => x.Index <= priorCandles.Length - 3)?.Price ?? priorCandles.TakeLast(20).SkipLast(1).Min(x => x.Low);
        var previousSweepLow = previous.Low < priorReferenceLow - priorBuffer && previous.Close > priorReferenceLow + priorBuffer;
        var previousSweepHigh = previous.High > priorReferenceHigh + priorBuffer && previous.Close < priorReferenceHigh - priorBuffer;
        var previousBody = Math.Abs(previous.Close - previous.Open);
        var previousRange = Math.Max(.00000001m, previous.High - previous.Low);
        var previousLowerWick = Math.Min(previous.Open, previous.Close) - previous.Low;
        var previousUpperWick = previous.High - Math.Max(previous.Open, previous.Close);
        var priorZoneTolerance = Math.Max(previous.Close * .0035m, priorAtr * 1.5m);
        var previousBullishRejection = previous.Close > previous.Open &&
                                       previousLowerWick >= Math.Max(previousBody * 1.25m, previousRange * .35m) &&
                                       Math.Abs(previous.Low - priorReferenceLow) <= priorZoneTolerance;
        var previousBearishRejection = previous.Close < previous.Open &&
                                       previousUpperWick >= Math.Max(previousBody * 1.25m, previousRange * .35m) &&
                                       Math.Abs(previous.High - priorReferenceHigh) <= priorZoneTolerance;

        var body = Math.Abs(last.Close - last.Open);
        var range = Math.Max(.00000001m, last.High - last.Low);
        var bodyRatio = body / range;
        var lowerWick = Math.Min(last.Open, last.Close) - last.Low;
        var upperWick = last.High - Math.Max(last.Open, last.Close);
        var bullishReject = last.Close > last.Open && lowerWick >= Math.Max(body * 1.25m, range * .35m);
        var bearishReject = last.Close < last.Open && upperWick >= Math.Max(body * 1.25m, range * .35m);
        var displacementBody = bodyRatio >= .68m;
        var bullishConfirmation = (previousSweepLow || previousBullishRejection) &&
                                  last.Close > previous.High + buffer * .15m &&
                                  last.Close > last.Open &&
                                  bodyRatio >= .50m;
        var bearishConfirmation = (previousSweepHigh || previousBearishRejection) &&
                                  last.Close < previous.Low - buffer * .15m &&
                                  last.Close < last.Open &&
                                  bodyRatio >= .50m;

        if (bullishConfirmation) return MarketStructureEvent.BullishConfirmation;
        if (bearishConfirmation) return MarketStructureEvent.BearishConfirmation;
        if (sweepLow) return MarketStructureEvent.LiquiditySweepLowReclaim;
        if (sweepHigh) return MarketStructureEvent.LiquiditySweepHighReject;
        if (retestUp) return MarketStructureEvent.BullishRetest;
        if (retestDown) return MarketStructureEvent.BearishRetest;
        if (breakUp) return MarketStructureEvent.BullishBreak;
        if (breakDown) return MarketStructureEvent.BearishBreak;
        if (rangeExpansion && displacementBody && last.Close > last.Open && volumeExpansion) return MarketStructureEvent.BullishDisplacement;
        if (rangeExpansion && displacementBody && last.Close < last.Open && volumeExpansion) return MarketStructureEvent.BearishDisplacement;
        if (bullishReject) return MarketStructureEvent.BullishRejection;
        if (bearishReject) return MarketStructureEvent.BearishRejection;
        if (compression) return MarketStructureEvent.Compression;
        return MarketStructureEvent.None;
    }

    private static MarketStructurePhase DetectPhase(
        MarketStructureBias bias,
        TimeframeStructureRead m15)
    {
        if (m15.Compression) return MarketStructurePhase.Compression;

        if (m15.Event is MarketStructureEvent.LiquiditySweepLowReclaim or MarketStructureEvent.BullishRejection)
            return MarketStructurePhase.BullishReversalAttempt;
        if (m15.Event is MarketStructureEvent.LiquiditySweepHighReject or MarketStructureEvent.BearishRejection)
            return MarketStructurePhase.BearishReversalAttempt;
        if (m15.Event == MarketStructureEvent.BullishConfirmation)
            return MarketStructurePhase.BullishImpulse;
        if (m15.Event == MarketStructureEvent.BearishConfirmation)
            return MarketStructurePhase.BearishImpulse;

        if (bias == MarketStructureBias.Bullish)
        {
            if (m15.Event is MarketStructureEvent.BullishBreak or MarketStructureEvent.BullishRetest or MarketStructureEvent.BullishDisplacement ||
                m15.State == PriceStructureState.Bullish)
                return MarketStructurePhase.BullishImpulse;
            if (m15.State is PriceStructureState.Bearish or PriceStructureState.Transition)
                return MarketStructurePhase.BullishPullback;
            if (m15.State == PriceStructureState.Range)
                return MarketStructurePhase.Balance;
        }

        if (bias == MarketStructureBias.Bearish)
        {
            if (m15.Event is MarketStructureEvent.BearishBreak or MarketStructureEvent.BearishRetest or MarketStructureEvent.BearishDisplacement ||
                m15.State == PriceStructureState.Bearish)
                return MarketStructurePhase.BearishImpulse;
            if (m15.State is PriceStructureState.Bullish or PriceStructureState.Transition)
                return MarketStructurePhase.BearishPullback;
            if (m15.State == PriceStructureState.Range)
                return MarketStructurePhase.Balance;
        }

        if (m15.State == PriceStructureState.Range)
            return MarketStructurePhase.Balance;
        if (m15.State == PriceStructureState.Bullish)
            return MarketStructurePhase.BullishImpulse;
        if (m15.State == PriceStructureState.Bearish)
            return MarketStructurePhase.BearishImpulse;
        return MarketStructurePhase.Unknown;
    }

    private static MarketStructureScenario DetectScenario(
        MarketStructureBias bias,
        MarketStructurePhase phase,
        TimeframeStructureRead m15,
        bool nearDemand,
        bool nearSupply)
    {
        if (bias == MarketStructureBias.Range)
        {
            if (m15.Event == MarketStructureEvent.BullishConfirmation)
                return MarketStructureScenario.RangeReversionLong;
            if (m15.Event == MarketStructureEvent.BearishConfirmation)
                return MarketStructureScenario.RangeReversionShort;
            if (phase == MarketStructurePhase.BullishReversalAttempt &&
                (nearDemand || m15.Event == MarketStructureEvent.LiquiditySweepLowReclaim))
                return MarketStructureScenario.RangeReversionLong;
            if (phase == MarketStructurePhase.BearishReversalAttempt &&
                (nearSupply || m15.Event == MarketStructureEvent.LiquiditySweepHighReject))
                return MarketStructureScenario.RangeReversionShort;
        }

        if (bias == MarketStructureBias.Bullish)
        {
            if (m15.Event == MarketStructureEvent.BullishConfirmation)
                return MarketStructureScenario.TrendPullbackLong;
            if (m15.Event is MarketStructureEvent.BullishBreak or MarketStructureEvent.BullishRetest)
                return MarketStructureScenario.BreakoutRetestLong;
            if (phase == MarketStructurePhase.BullishReversalAttempt &&
                (nearDemand || m15.Event == MarketStructureEvent.LiquiditySweepLowReclaim))
                return MarketStructureScenario.TrendPullbackLong;
            if (nearDemand && phase == MarketStructurePhase.BullishPullback)
                return MarketStructureScenario.TrendPullbackLong;
        }

        if (bias == MarketStructureBias.Bearish)
        {
            if (m15.Event == MarketStructureEvent.BearishConfirmation)
                return MarketStructureScenario.TrendPullbackShort;
            if (m15.Event is MarketStructureEvent.BearishBreak or MarketStructureEvent.BearishRetest)
                return MarketStructureScenario.BreakoutRetestShort;
            if (phase == MarketStructurePhase.BearishReversalAttempt &&
                (nearSupply || m15.Event == MarketStructureEvent.LiquiditySweepHighReject))
                return MarketStructureScenario.TrendPullbackShort;
            if (nearSupply && phase == MarketStructurePhase.BearishPullback)
                return MarketStructureScenario.TrendPullbackShort;
        }

        return MarketStructureScenario.None;
    }

    private static bool HasTrigger(MarketStructureScenario scenario, TimeframeStructureRead m15) => scenario switch
    {
        MarketStructureScenario.TrendPullbackLong =>
            m15.Event is MarketStructureEvent.LiquiditySweepLowReclaim or MarketStructureEvent.BullishRejection or MarketStructureEvent.BullishConfirmation or MarketStructureEvent.BullishRetest or MarketStructureEvent.BullishDisplacement,
        MarketStructureScenario.TrendPullbackShort =>
            m15.Event is MarketStructureEvent.LiquiditySweepHighReject or MarketStructureEvent.BearishRejection or MarketStructureEvent.BearishConfirmation or MarketStructureEvent.BearishRetest or MarketStructureEvent.BearishDisplacement,
        MarketStructureScenario.RangeReversionLong =>
            m15.Event is MarketStructureEvent.LiquiditySweepLowReclaim or MarketStructureEvent.BullishRejection or MarketStructureEvent.BullishConfirmation,
        MarketStructureScenario.RangeReversionShort =>
            m15.Event is MarketStructureEvent.LiquiditySweepHighReject or MarketStructureEvent.BearishRejection or MarketStructureEvent.BearishConfirmation,
        MarketStructureScenario.BreakoutRetestLong =>
            m15.Event == MarketStructureEvent.BullishRetest || (m15.Event == MarketStructureEvent.BullishBreak && m15.VolumeExpansion),
        MarketStructureScenario.BreakoutRetestShort =>
            m15.Event == MarketStructureEvent.BearishRetest || (m15.Event == MarketStructureEvent.BearishBreak && m15.VolumeExpansion),
        _ => false
    };

    private static bool HasConfirmation(MarketStructureScenario scenario, TimeframeStructureRead m15)
    {
        var longSide = scenario is MarketStructureScenario.TrendPullbackLong or MarketStructureScenario.RangeReversionLong or MarketStructureScenario.BreakoutRetestLong;
        var shortSide = scenario is MarketStructureScenario.TrendPullbackShort or MarketStructureScenario.RangeReversionShort or MarketStructureScenario.BreakoutRetestShort;
        if (longSide)
            return m15.Event is MarketStructureEvent.BullishConfirmation or MarketStructureEvent.BullishBreak or MarketStructureEvent.BullishRetest or MarketStructureEvent.BullishDisplacement ||
                   (m15.State == PriceStructureState.Bullish && m15.LastClose > m15.PreviousSwingHigh);
        if (shortSide)
            return m15.Event is MarketStructureEvent.BearishConfirmation or MarketStructureEvent.BearishBreak or MarketStructureEvent.BearishRetest or MarketStructureEvent.BearishDisplacement ||
                   (m15.State == PriceStructureState.Bearish && m15.LastClose < m15.PreviousSwingLow);
        return false;
    }

    private static MarketStructureBias HigherTimeframeBias(PriceStructureState h1, PriceStructureState h4)
    {
        if (h4 == PriceStructureState.Bullish && h1 != PriceStructureState.Bearish) return MarketStructureBias.Bullish;
        if (h4 == PriceStructureState.Bearish && h1 != PriceStructureState.Bullish) return MarketStructureBias.Bearish;
        if (h1 == PriceStructureState.Bullish && h4 is (PriceStructureState.Range or PriceStructureState.Transition)) return MarketStructureBias.Bullish;
        if (h1 == PriceStructureState.Bearish && h4 is (PriceStructureState.Range or PriceStructureState.Transition)) return MarketStructureBias.Bearish;
        if (h1 == PriceStructureState.Range && h4 == PriceStructureState.Range) return MarketStructureBias.Range;
        if (h1 == PriceStructureState.Unknown || h4 == PriceStructureState.Unknown) return MarketStructureBias.Unknown;
        return MarketStructureBias.Mixed;
    }

    private static PriceStructureState StructureState(
        decimal lastHigh, decimal previousHigh, decimal lastLow, decimal previousLow,
        decimal recentHigh, decimal priorHigh, decimal recentLow, decimal priorLow, decimal buffer)
    {
        var higherHigh = lastHigh > previousHigh + buffer || recentHigh > priorHigh + buffer;
        var higherLow = lastLow > previousLow + buffer || recentLow > priorLow + buffer;
        var lowerHigh = lastHigh < previousHigh - buffer || recentHigh < priorHigh - buffer;
        var lowerLow = lastLow < previousLow - buffer || recentLow < priorLow - buffer;
        if (higherHigh && higherLow) return PriceStructureState.Bullish;
        if (lowerHigh && lowerLow) return PriceStructureState.Bearish;
        var flatHigh = Math.Abs(lastHigh - previousHigh) <= buffer * 2 && Math.Abs(recentHigh - priorHigh) <= buffer * 2;
        var flatLow = Math.Abs(lastLow - previousLow) <= buffer * 2 && Math.Abs(recentLow - priorLow) <= buffer * 2;
        if (flatHigh && flatLow) return PriceStructureState.Range;
        return PriceStructureState.Transition;
    }

    private static List<SwingPoint> Pivots(IReadOnlyList<CandleEvidence> candles, bool high)
    {
        var result = new List<SwingPoint>();
        for (var i = 2; i < candles.Count - 2; i++)
        {
            var price = high ? candles[i].High : candles[i].Low;
            var pivot = true;
            for (var j = i - 2; j <= i + 2; j++)
            {
                if (j == i) continue;
                var other = high ? candles[j].High : candles[j].Low;
                if (high ? other >= price : other <= price)
                {
                    pivot = false;
                    break;
                }
            }
            if (pivot) result.Add(new(i, price));
        }
        return result;
    }

    private static decimal Atr(IReadOnlyList<CandleEvidence> candles)
    {
        var values = new List<decimal>();
        for (var i = Math.Max(1, candles.Count - 14); i < candles.Count; i++)
        {
            var current = candles[i];
            var previous = candles[i - 1];
            values.Add(Math.Max(current.High - current.Low,
                Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close))));
        }
        return values.Count == 0 ? 0 : values.Average();
    }

    private static bool Near(decimal price, decimal level, decimal atr)
    {
        if (price <= 0 || level <= 0) return false;
        var tolerance = Math.Max(price * .0035m, atr * 1.5m);
        return Math.Abs(price - level) <= tolerance;
    }

    private static string BuildNarrative(
        MarketStructureBias bias,
        MarketStructurePhase phase,
        MarketStructureScenario scenario,
        TimeframeStructureRead m15,
        TimeframeStructureRead h1,
        TimeframeStructureRead h4,
        bool nearDemand,
        bool nearSupply)
    {
        var location = nearDemand ? "near 15m demand" : nearSupply ? "near 15m supply" : "between structural zones";
        return $"4h={h4.State}, 1h={h1.State}, 15m={m15.State}; higher-timeframe bias={bias}; phase={phase}; " +
               $"price is {location}; latest 15m event={m15.Event}; scenario={scenario}.";
    }

    private static bool Valid(CandleEvidence candle) =>
        candle.Open > 0 && candle.High > 0 && candle.Low > 0 && candle.Close > 0 &&
        candle.Low <= Math.Min(candle.Open, candle.Close) &&
        candle.High >= Math.Max(candle.Open, candle.Close) &&
        candle.High >= candle.Low &&
        candle.Volume >= 0 && candle.QuoteVolume >= 0 && candle.Trades >= 0 && candle.TakerBuyVolume >= 0;

    private static TimeframeStructureRead Unknown(string interval) =>
        new(interval, PriceStructureState.Unknown, MarketStructureEvent.None, 0, 0, 0, 0, 0, 0, 0, 0, false, false, false,
            $"{interval}: insufficient confirmed candles for structure analysis.");

    private sealed record SwingPoint(int Index, decimal Price);
}

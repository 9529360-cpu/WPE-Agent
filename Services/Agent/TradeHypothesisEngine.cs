using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum TradeHypothesisKind
{
    None,
    TrendPullbackLong,
    TrendPullbackShort,
    RangeReversionLong,
    RangeReversionShort
}

public enum TradeHypothesisStage
{
    Observing,
    Watching,
    ScoutReady,
    Confirmed,
    Invalidated
}

public sealed record TradeHypothesis(
    string Id,
    string Version,
    string Symbol,
    TradeHypothesisKind Kind,
    TradeHypothesisStage Stage,
    int Direction,
    MarketRegime Regime,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Revision,
    decimal ReferencePrice,
    decimal Support,
    decimal Resistance,
    decimal InvalidationPrice,
    decimal TriggerPrice,
    double LastTrend15m,
    double LastTrend1h,
    double LastTrend4h,
    double LastRsi,
    double LastOrderBookImbalance,
    decimal LastTakerBuySellRatio,
    double RiskBudgetMultiplier,
    string Thesis,
    string Trigger,
    string Invalidation,
    IReadOnlyList<string> Evidence)
{
    public const string CurrentVersion = "hypothesis-v1";
    public bool Actionable => Stage is TradeHypothesisStage.ScoutReady or TradeHypothesisStage.Confirmed;
    public bool IsLive => Kind != TradeHypothesisKind.None && Stage != TradeHypothesisStage.Invalidated;
    public bool DirectionMatches(DecisionAction action) => action switch
    {
        DecisionAction.OpenLong or DecisionAction.AddLong or DecisionAction.ReverseToLong => Direction > 0,
        DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReverseToShort => Direction < 0,
        _ => true
    };
}

public sealed class TradeHypothesisEngine
{
    public const string DecisionContextKind = "market-hypothesis";
    private const string StatePrefix = "trade-hypothesis:";
    private static readonly TimeSpan MaximumHypothesisAge = TimeSpan.FromHours(6);
    internal static readonly TimeSpan InvalidatedHypothesisCooldown = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };
    private readonly AgentSqliteStore _store;

    public TradeHypothesisEngine(AgentSqliteStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<IReadOnlyDictionary<string,TradeHypothesis>> EvaluateAsync(
        EvidencePack evidence,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var now = DateTimeOffset.UtcNow;
        var result = new Dictionary<string,TradeHypothesis>(StringComparer.OrdinalIgnoreCase);

        foreach (var market in evidence.Markets.Values.OrderBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            TradeHypothesis? previous = null;
            var raw = await _store.GetStateAsync(StateKey(market.Symbol), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { previous = JsonSerializer.Deserialize<TradeHypothesis>(raw, Json); }
                catch (JsonException) { previous = null; }
            }

            var next = EvaluateMarket(market, previous, evidence.Positions, now);
            result[market.Symbol] = next;
            await _store.SetStateAsync(StateKey(market.Symbol), JsonSerializer.Serialize(next, Json), ct).ConfigureAwait(false);
        }

        await _store.SetStateAsync(
            "trade-hypothesis:last-plan",
            JsonSerializer.Serialize(new
            {
                evaluatedAtUtc = now,
                hypotheses = result.Values.OrderBy(x => x.Symbol, StringComparer.Ordinal).ToArray()
            }, Json),
            ct).ConfigureAwait(false);

        return result;
    }

    public static TradeHypothesis EvaluateMarket(
        MarketEvidence market,
        TradeHypothesis? previous,
        IReadOnlyList<ManagedPosition> positions,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(positions);
        if (now == default || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Hypothesis evaluation time must be an explicit UTC instant.", nameof(now));

        if (!Valid(market))
            return Observing(market, now, "market evidence is incomplete or invalid");

        var regime = MarketRegimeClassifier.Detect(market);
        if (previous is not null &&
            previous.Symbol.Equals(market.Symbol, StringComparison.OrdinalIgnoreCase) &&
            previous.Kind != TradeHypothesisKind.None &&
            now - previous.CreatedAtUtc <= MaximumHypothesisAge)
        {
            if(previous.Stage==TradeHypothesisStage.Invalidated)
            {
                if(now-previous.UpdatedAtUtc<InvalidatedHypothesisCooldown)return previous;
                return DetectNew(market,regime,now);
            }

            var evolved = EvolveExisting(market, previous, positions, regime, now);
            if (evolved.Stage != TradeHypothesisStage.Invalidated)
                return evolved;
            return evolved;
        }

        return DetectNew(market, regime, now);
    }

    private static TradeHypothesis EvolveExisting(
        MarketEvidence market,
        TradeHypothesis previous,
        IReadOnlyList<ManagedPosition> positions,
        MarketRegime regime,
        DateTimeOffset now)
    {
        var longSide = previous.Direction > 0;
        var invalidated = longSide
            ? market.Price < previous.InvalidationPrice || market.Trend4h <= 0
            : market.Price > previous.InvalidationPrice || market.Trend4h >= 0;

        if (invalidated)
            return Snapshot(
                previous,
                market,
                regime,
                TradeHypothesisStage.Invalidated,
                0,
                longSide
                    ? $"Long thesis invalidated: price={market.Price:F2}, invalidation={previous.InvalidationPrice:F2}, 4h={market.Trend4h:F2}%."
                    : $"Short thesis invalidated: price={market.Price:F2}, invalidation={previous.InvalidationPrice:F2}, 4h={market.Trend4h:F2}%.",
                previous.Trigger,
                previous.Invalidation,
                now);

        var sameSidePosition = positions.Any(x =>
            x.Symbol.Equals(market.Symbol, StringComparison.OrdinalIgnoreCase) &&
            ((longSide && x.Side == PositionSide.Long) || (!longSide && x.Side == PositionSide.Short)));

        var trendImproved = longSide
            ? market.Trend15m > previous.LastTrend15m + .08 || market.Rsi > previous.LastRsi + 4
            : market.Trend15m < previous.LastTrend15m - .08 || market.Rsi < previous.LastRsi - 4;

        var bookImproved = longSide
            ? market.Quality.OrderBookImbalance > previous.LastOrderBookImbalance + .20
            : market.Quality.OrderBookImbalance < previous.LastOrderBookImbalance - .20;
        var bookPersistentlySupportive = longSide
            ? previous.LastOrderBookImbalance >= .35 && market.Quality.OrderBookImbalance >= .35
            : previous.LastOrderBookImbalance <= -.35 && market.Quality.OrderBookImbalance <= -.35;

        var takerAvailable = market.Derivatives.TakerBuySellRatio > 0;
        var takerReclaimed = takerAvailable && (longSide
            ? market.Derivatives.TakerBuySellRatio >= 1m
            : market.Derivatives.TakerBuySellRatio <= 1m);

        var microstructureNoLongerHostile = longSide
            ? market.Quality.OrderBookImbalance > -.20 || takerReclaimed
            : market.Quality.OrderBookImbalance < .20 || takerReclaimed;

        var shortTermConfirmed = longSide
            ? market.Trend15m >= 0 && market.Rsi >= Math.Max(32, previous.LastRsi)
            : market.Trend15m <= 0 && market.Rsi <= Math.Min(68, previous.LastRsi);

        var nextStage = previous.Stage;
        if (!sameSidePosition)
        {
            if (previous.Stage == TradeHypothesisStage.Watching &&
                (trendImproved || bookImproved || bookPersistentlySupportive) &&
                microstructureNoLongerHostile)
            {
                nextStage = TradeHypothesisStage.ScoutReady;
            }
            else if (previous.Stage == TradeHypothesisStage.ScoutReady)
            {
                if (!microstructureNoLongerHostile)
                    nextStage = TradeHypothesisStage.Watching;
                else if (shortTermConfirmed)
                    nextStage = TradeHypothesisStage.Confirmed;
            }
            else if (previous.Stage == TradeHypothesisStage.Confirmed)
            {
                if (!microstructureNoLongerHostile)
                    nextStage = TradeHypothesisStage.Watching;
                else if (!shortTermConfirmed)
                    nextStage = TradeHypothesisStage.ScoutReady;
            }
        }

        var risk = nextStage switch
        {
            TradeHypothesisStage.ScoutReady => .18,
            TradeHypothesisStage.Confirmed => .45,
            _ => 0
        };

        var thesis = longSide
            ? $"4h structure remains upward while the short horizon is working through a pullback near support. Current stage={nextStage}."
            : $"4h structure remains downward while the short horizon is working through a rebound near resistance. Current stage={nextStage}.";
        var trigger = longSide
            ? "Support must hold and short-horizon momentum or microstructure must improve before risk is increased."
            : "Resistance must hold and short-horizon momentum or microstructure must weaken before risk is increased.";

        return Snapshot(previous, market, regime, nextStage, risk, thesis, trigger, previous.Invalidation, now);
    }

    private static TradeHypothesis DetectNew(MarketEvidence market, MarketRegime regime, DateTimeOffset now)
    {
        var price = market.Price;
        var supportDistance = price > 0 && market.Support > 0 ? (double)((price - market.Support) / price) : double.PositiveInfinity;
        var resistanceDistance = price > 0 && market.Resistance > price ? (double)((market.Resistance - price) / price) : double.PositiveInfinity;
        var proximity = Math.Max(.0035, Math.Clamp(market.Quality.AtrPercent * 3, .0035, .015));
        var nearSupport = supportDistance >= -.001 && supportDistance <= proximity;
        var nearResistance = resistanceDistance >= -.001 && resistanceDistance <= proximity;

        var dominantLongStructure = market.Trend4h >= .35 &&
                                    market.Trend4h > Math.Abs(market.Trend1h) * 1.35;
        var dominantShortStructure = market.Trend4h <= -.35 &&
                                     Math.Abs(market.Trend4h) > Math.Abs(market.Trend1h) * 1.35;
        var shortTermPullback = market.Trend15m < 0 || market.Trend1h < 0 || market.Rsi <= 42;
        var shortTermRebound = market.Trend15m > 0 || market.Trend1h > 0 || market.Rsi >= 58;

        if (dominantLongStructure && nearSupport && shortTermPullback)
            return NewHypothesis(
                market,
                regime,
                TradeHypothesisKind.TrendPullbackLong,
                1,
                now,
                $"Higher-timeframe structure is upward ({market.Trend4h:F2}%) while 15m/1h are pulling back near support {market.Support:F2}.",
                "Do not chase. Watch for support to hold and for short-horizon momentum or microstructure to improve.",
                InvalidationPrice(market, longSide: true),
                "The pullback thesis fails if support breaks beyond the volatility buffer or the 4h structure turns down.");

        if (dominantShortStructure && nearResistance && shortTermRebound)
            return NewHypothesis(
                market,
                regime,
                TradeHypothesisKind.TrendPullbackShort,
                -1,
                now,
                $"Higher-timeframe structure is downward ({market.Trend4h:F2}%) while 15m/1h are rebounding near resistance {market.Resistance:F2}.",
                "Do not chase. Watch for resistance to hold and for short-horizon momentum or microstructure to weaken.",
                InvalidationPrice(market, longSide: false),
                "The rebound-short thesis fails if resistance breaks beyond the volatility buffer or the 4h structure turns up.");

        if (regime == MarketRegime.Ranging && nearSupport && market.Rsi <= 30)
            return NewHypothesis(
                market,
                regime,
                TradeHypothesisKind.RangeReversionLong,
                1,
                now,
                $"Market is ranging, price is near support {market.Support:F2}, and RSI is stretched at {market.Rsi:F1}.",
                "Wait for support retention and a short-horizon/microstructure turn before a small reversion probe.",
                InvalidationPrice(market, longSide: true),
                "The range-reversion thesis fails on a volatility-adjusted support break.");

        if (regime == MarketRegime.Ranging && nearResistance && market.Rsi >= 70)
            return NewHypothesis(
                market,
                regime,
                TradeHypothesisKind.RangeReversionShort,
                -1,
                now,
                $"Market is ranging, price is near resistance {market.Resistance:F2}, and RSI is stretched at {market.Rsi:F1}.",
                "Wait for resistance retention and a short-horizon/microstructure turn before a small reversion probe.",
                InvalidationPrice(market, longSide: false),
                "The range-reversion thesis fails on a volatility-adjusted resistance break.");

        return Observing(market, now, "No coherent trade hypothesis is currently forming from market structure.");
    }

    private static TradeHypothesis NewHypothesis(
        MarketEvidence market,
        MarketRegime regime,
        TradeHypothesisKind kind,
        int direction,
        DateTimeOffset now,
        string thesis,
        string trigger,
        decimal invalidation,
        string invalidationText)
    {
        var id = $"HYP-{market.Symbol}-{kind}-{now:yyyyMMddHHmmss}";
        var triggerPrice = direction > 0 ? market.Support : market.Resistance;
        return new(
            id,
            TradeHypothesis.CurrentVersion,
            market.Symbol,
            kind,
            TradeHypothesisStage.Watching,
            direction,
            regime,
            now,
            now,
            1,
            market.Price,
            market.Support,
            market.Resistance,
            invalidation,
            triggerPrice,
            market.Trend15m,
            market.Trend1h,
            market.Trend4h,
            market.Rsi,
            market.Quality.OrderBookImbalance,
            market.Derivatives.TakerBuySellRatio,
            0,
            thesis,
            trigger,
            invalidationText,
            Evidence(market));
    }

    private static TradeHypothesis Snapshot(
        TradeHypothesis previous,
        MarketEvidence market,
        MarketRegime regime,
        TradeHypothesisStage stage,
        double riskBudgetMultiplier,
        string thesis,
        string trigger,
        string invalidation,
        DateTimeOffset now) =>
        previous with
        {
            Stage = stage,
            Regime = regime,
            UpdatedAtUtc = now,
            Revision = previous.Revision + 1,
            ReferencePrice = market.Price,
            Support = market.Support,
            Resistance = market.Resistance,
            LastTrend15m = market.Trend15m,
            LastTrend1h = market.Trend1h,
            LastTrend4h = market.Trend4h,
            LastRsi = market.Rsi,
            LastOrderBookImbalance = market.Quality.OrderBookImbalance,
            LastTakerBuySellRatio = market.Derivatives.TakerBuySellRatio,
            RiskBudgetMultiplier = riskBudgetMultiplier,
            Thesis = thesis,
            Trigger = trigger,
            Invalidation = invalidation,
            Evidence = Evidence(market)
        };

    private static TradeHypothesis Observing(MarketEvidence market, DateTimeOffset now, string thesis) =>
        new(
            $"OBS-{market.Symbol}",
            TradeHypothesis.CurrentVersion,
            market.Symbol,
            TradeHypothesisKind.None,
            TradeHypothesisStage.Observing,
            0,
            Valid(market) ? MarketRegimeClassifier.Detect(market) : MarketRegime.Unknown,
            now,
            now,
            1,
            market.Price,
            market.Support,
            market.Resistance,
            0,
            0,
            market.Trend15m,
            market.Trend1h,
            market.Trend4h,
            market.Rsi,
            market.Quality.OrderBookImbalance,
            market.Derivatives.TakerBuySellRatio,
            0,
            thesis,
            "Wait for a coherent market structure before considering risk.",
            "No active trade thesis exists.",
            Evidence(market));

    private static decimal InvalidationPrice(MarketEvidence market, bool longSide)
    {
        var volatility = (decimal)Math.Clamp(market.Quality.AtrPercent * 1.5, .0015, .02);
        return longSide
            ? market.Support * (1 - volatility)
            : market.Resistance * (1 + volatility);
    }

    private static bool Valid(MarketEvidence market) =>
        !string.IsNullOrWhiteSpace(market.Symbol) &&
        market.Price > 0 &&
        market.Support > 0 &&
        market.Resistance > market.Support &&
        market.Rsi is >= 0 and <= 100 &&
        double.IsFinite(market.Trend15m) &&
        double.IsFinite(market.Trend1h) &&
        double.IsFinite(market.Trend4h) &&
        double.IsFinite(market.Quality.AtrPercent) &&
        double.IsFinite(market.Quality.OrderBookImbalance);

    private static IReadOnlyList<string> Evidence(MarketEvidence market)
    {
        var taker = market.Derivatives.TakerBuySellRatio > 0
            ? $"taker_buy_sell={market.Derivatives.TakerBuySellRatio:F3}"
            : "taker_buy_sell=unavailable";
        return
        [
            $"price={market.Price:F2}",
            $"support={market.Support:F2}",
            $"resistance={market.Resistance:F2}",
            $"trend15m={market.Trend15m:F3}%",
            $"trend1h={market.Trend1h:F3}%",
            $"trend4h={market.Trend4h:F3}%",
            $"rsi={market.Rsi:F1}",
            $"order_book={market.Quality.OrderBookImbalance:F3}",
            taker,
            $"market_quality={market.Quality.QualityScore}"
        ];
    }

    private static string StateKey(string symbol) => StatePrefix + symbol.ToUpperInvariant();
}

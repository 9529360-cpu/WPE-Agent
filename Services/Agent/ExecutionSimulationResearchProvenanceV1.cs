using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionSimulationResearchProvenanceV1(
    string Schema,
    string CorrelationId,
    string ClientOrderId,
    string StrategyId,
    string StrategyVersion,
    string CostModelVersion,
    string SimulationModelVersion,
    string VenueRuleVersion,
    string Symbol,
    string SimulatedFillCanonicalSha256,
    string BacktestValidationSha256,
    string TimelineSha256,
    string ShadowObservationSha256,
    string MarketProvenanceSha256,
    DateTimeOffset TimelineLastTradableAtUtc,
    DateTimeOffset ValidationAtUtc,
    DateTimeOffset MarketCollectedAtUtc,
    DateTimeOffset ShadowObservedAtUtc,
    DateTimeOffset MarketAsOfUtc,
    DateTimeOffset SimulatedAtUtc,
    DateTimeOffset BoundAtUtc,
    byte[] BacktestValidationCanonicalBytes,
    byte[] TimelineCanonicalBytes,
    byte[] ShadowObservationCanonicalBytes,
    byte[] MarketProvenanceCanonicalBytes,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class ExecutionSimulationResearchProvenanceCanonicalizerV1
{
    public const string Schema = "wpe.execution-simulation-research-provenance/1.0";
    private const string BacktestSchema = "wpe.backtest-validation/1.1";
    private const string TimelineSchema = "wpe.strategy-exposure-timeline/1.0";
    private const string ShadowSchema = "wpe.strategy-shadow-observation/1.0";
    private const string MarketSchema = "wpe.market-evidence-provenance/1.0";
    private static readonly TimeSpan MaximumShadowMarketAge = TimeSpan.FromHours(1);

    public static ExecutionSimulationResearchProvenanceV1 Create(
        ExecutionSimulationFillV1 simulatedFill,
        byte[] backtestValidationCanonicalBytes,
        byte[] timelineCanonicalBytes,
        byte[] shadowObservationCanonicalBytes,
        byte[] marketProvenanceCanonicalBytes,
        DateTimeOffset boundAtUtc)
    {
        ArgumentNullException.ThrowIfNull(simulatedFill);
        if (!ExecutionSimulationFillCanonicalizerV1.IsCanonical(simulatedFill))
            throw new InvalidOperationException("Simulated fill must be canonical before research provenance can be bound.");

        var sources = ParseAndValidateSources(
            backtestValidationCanonicalBytes,
            timelineCanonicalBytes,
            shadowObservationCanonicalBytes,
            marketProvenanceCanonicalBytes);

        ValidateBinding(simulatedFill, sources, boundAtUtc);

        var draft = new ExecutionSimulationResearchProvenanceV1(
            Schema,
            simulatedFill.CorrelationId,
            simulatedFill.ClientOrderId,
            simulatedFill.StrategyId,
            simulatedFill.StrategyVersion,
            simulatedFill.CostModelVersion,
            simulatedFill.SimulationModelVersion,
            simulatedFill.VenueRuleVersion,
            simulatedFill.Symbol,
            simulatedFill.CanonicalSha256,
            sources.BacktestValidationSha256,
            sources.TimelineSha256,
            sources.ShadowObservationSha256,
            sources.MarketProvenanceSha256,
            sources.TimelineLastTradableAtUtc,
            sources.ValidationAtUtc,
            sources.MarketCollectedAtUtc,
            sources.ShadowObservedAtUtc,
            simulatedFill.MarketAsOfUtc,
            simulatedFill.SimulatedAtUtc,
            boundAtUtc.ToUniversalTime(),
            backtestValidationCanonicalBytes.ToArray(),
            timelineCanonicalBytes.ToArray(),
            shadowObservationCanonicalBytes.ToArray(),
            marketProvenanceCanonicalBytes.ToArray(),
            Array.Empty<byte>(),
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(ExecutionSimulationResearchProvenanceV1 value)
    {
        if (value is null
            || value.Schema != Schema
            || value.CanonicalBytes.Length == 0
            || !IsLowerSha256(value.CanonicalSha256)
            || !IsLowerSha256(value.SimulatedFillCanonicalSha256))
            return false;

        try
        {
            var sources = ParseAndValidateSources(
                value.BacktestValidationCanonicalBytes,
                value.TimelineCanonicalBytes,
                value.ShadowObservationCanonicalBytes,
                value.MarketProvenanceCanonicalBytes);

            if (!string.Equals(value.BacktestValidationSha256, sources.BacktestValidationSha256, StringComparison.Ordinal)
                || !string.Equals(value.TimelineSha256, sources.TimelineSha256, StringComparison.Ordinal)
                || !string.Equals(value.ShadowObservationSha256, sources.ShadowObservationSha256, StringComparison.Ordinal)
                || !string.Equals(value.MarketProvenanceSha256, sources.MarketProvenanceSha256, StringComparison.Ordinal)
                || value.TimelineLastTradableAtUtc != sources.TimelineLastTradableAtUtc
                || value.ValidationAtUtc != sources.ValidationAtUtc
                || value.MarketCollectedAtUtc != sources.MarketCollectedAtUtc
                || value.ShadowObservedAtUtc != sources.ShadowObservedAtUtc)
                return false;

            ValidateIdentityAndChronology(value, sources);
        }
        catch
        {
            return false;
        }

        var bytes = Serialize(value);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(hash, value.CanonicalSha256, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes);
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> canonicalBytes,
        string canonicalSha256,
        out ExecutionSimulationResearchProvenanceV1? value)
    {
        value = null;
        if (canonicalBytes.Length == 0 || !IsLowerSha256(canonicalSha256))
            return false;

        try
        {
            var bytes = canonicalBytes.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, canonicalSha256, StringComparison.Ordinal))
                return false;

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            value = new(
                root.GetProperty("schema").GetString() ?? string.Empty,
                root.GetProperty("correlation_id").GetString() ?? string.Empty,
                root.GetProperty("client_order_id").GetString() ?? string.Empty,
                root.GetProperty("strategy_id").GetString() ?? string.Empty,
                root.GetProperty("strategy_version").GetString() ?? string.Empty,
                root.GetProperty("cost_model_version").GetString() ?? string.Empty,
                root.GetProperty("simulation_model_version").GetString() ?? string.Empty,
                root.GetProperty("venue_rule_version").GetString() ?? string.Empty,
                root.GetProperty("symbol").GetString() ?? string.Empty,
                root.GetProperty("simulated_fill_sha256").GetString() ?? string.Empty,
                root.GetProperty("backtest_validation_sha256").GetString() ?? string.Empty,
                root.GetProperty("timeline_sha256").GetString() ?? string.Empty,
                root.GetProperty("shadow_observation_sha256").GetString() ?? string.Empty,
                root.GetProperty("market_provenance_sha256").GetString() ?? string.Empty,
                ParseUtc(root, "timeline_last_tradable_at_utc"),
                ParseUtc(root, "validation_at_utc"),
                ParseUtc(root, "market_collected_at_utc"),
                ParseUtc(root, "shadow_observed_at_utc"),
                ParseUtc(root, "market_as_of_utc"),
                ParseUtc(root, "simulated_at_utc"),
                ParseUtc(root, "bound_at_utc"),
                root.GetProperty("backtest_validation_canonical_bytes").GetBytesFromBase64(),
                root.GetProperty("timeline_canonical_bytes").GetBytesFromBase64(),
                root.GetProperty("shadow_observation_canonical_bytes").GetBytesFromBase64(),
                root.GetProperty("market_provenance_canonical_bytes").GetBytesFromBase64(),
                bytes,
                canonicalSha256);

            if (IsCanonical(value))
                return true;

            value = null;
            return false;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static void ValidateBinding(
        ExecutionSimulationFillV1 fill,
        SourceEvidence sources,
        DateTimeOffset boundAtUtc)
    {
        if (boundAtUtc == default || boundAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Research provenance bind time must be UTC.");
        if (!string.Equals(fill.StrategyId, sources.StrategyId, StringComparison.Ordinal)
            || !string.Equals(fill.StrategyVersion, sources.StrategyVersion, StringComparison.Ordinal)
            || !string.Equals(fill.Symbol, sources.Symbol, StringComparison.Ordinal))
            throw new InvalidOperationException("Simulated fill identity does not match research provenance.");
        if (fill.MarketAsOfUtc != sources.MarketCollectedAtUtc)
            throw new InvalidOperationException("Simulated fill must use the exact market fact bound by the Shadow observation.");
        if (sources.ShadowObservedAtUtc > fill.SimulatedAtUtc)
            throw new InvalidOperationException("Simulated fill cannot predate its Shadow observation.");
        if (fill.SimulatedAtUtc > boundAtUtc)
            throw new InvalidOperationException("Research provenance cannot be bound before the simulated fill exists.");
    }

    private static void ValidateIdentityAndChronology(
        ExecutionSimulationResearchProvenanceV1 value,
        SourceEvidence sources)
    {
        if (string.IsNullOrWhiteSpace(value.CorrelationId)
            || string.IsNullOrWhiteSpace(value.ClientOrderId)
            || string.IsNullOrWhiteSpace(value.CostModelVersion)
            || string.IsNullOrWhiteSpace(value.SimulationModelVersion)
            || string.IsNullOrWhiteSpace(value.VenueRuleVersion))
            throw new InvalidOperationException("Simulation provenance identity is incomplete.");

        if (!string.Equals(value.StrategyId, sources.StrategyId, StringComparison.Ordinal)
            || !string.Equals(value.StrategyVersion, sources.StrategyVersion, StringComparison.Ordinal)
            || !string.Equals(value.Symbol, sources.Symbol, StringComparison.Ordinal))
            throw new InvalidOperationException("Simulation provenance source identity is inconsistent.");
        if (value.MarketAsOfUtc != sources.MarketCollectedAtUtc)
            throw new InvalidOperationException("Simulation provenance market time is not bound to the exact market source.");
        if (value.TimelineLastTradableAtUtc > value.ValidationAtUtc
            || value.ValidationAtUtc > value.MarketCollectedAtUtc
            || value.MarketCollectedAtUtc > value.ShadowObservedAtUtc
            || value.ShadowObservedAtUtc > value.SimulatedAtUtc
            || value.SimulatedAtUtc > value.BoundAtUtc)
            throw new InvalidOperationException("Simulation provenance chronology is invalid.");

        foreach (var time in new[]
        {
            value.TimelineLastTradableAtUtc,
            value.ValidationAtUtc,
            value.MarketCollectedAtUtc,
            value.ShadowObservedAtUtc,
            value.MarketAsOfUtc,
            value.SimulatedAtUtc,
            value.BoundAtUtc
        })
        {
            if (time == default || time.Offset != TimeSpan.Zero)
                throw new InvalidOperationException("Simulation provenance timestamps must be UTC.");
        }
    }

    private static SourceEvidence ParseAndValidateSources(
        byte[] backtestBytes,
        byte[] timelineBytes,
        byte[] shadowBytes,
        byte[] marketBytes)
    {
        if (backtestBytes is null || backtestBytes.Length == 0
            || timelineBytes is null || timelineBytes.Length == 0
            || shadowBytes is null || shadowBytes.Length == 0
            || marketBytes is null || marketBytes.Length == 0)
            throw new InvalidOperationException("Simulation research provenance requires all canonical source bytes.");

        var backtestHash = Hash(backtestBytes);
        var timelineHash = Hash(timelineBytes);
        var shadowHash = Hash(shadowBytes);
        var marketHash = Hash(marketBytes);

        var validation = ParseBacktest(backtestBytes);
        var timeline = ParseTimeline(timelineBytes);
        var market = ParseMarket(marketBytes);
        var shadow = ParseShadow(shadowBytes);

        if (!string.Equals(validation.StrategyId, timeline.StrategyId, StringComparison.Ordinal)
            || !string.Equals(validation.StrategyId, shadow.StrategyId, StringComparison.Ordinal)
            || !string.Equals(validation.StrategyVersion, timeline.StrategyVersion, StringComparison.Ordinal)
            || !string.Equals(validation.StrategyVersion, shadow.StrategyVersion, StringComparison.Ordinal)
            || !string.Equals(validation.Symbol, timeline.Symbol, StringComparison.Ordinal)
            || !string.Equals(validation.Symbol, shadow.Symbol, StringComparison.Ordinal)
            || !string.Equals(validation.Symbol, market.Symbol, StringComparison.Ordinal))
            throw new InvalidOperationException("Research provenance source identities do not agree.");

        if (!string.Equals(shadow.BacktestValidationSha256, backtestHash, StringComparison.Ordinal)
            || !string.Equals(shadow.TimelineSha256, timelineHash, StringComparison.Ordinal)
            || !string.Equals(shadow.MarketProvenanceSha256, marketHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Shadow observation does not reference the supplied research source bytes.");

        if (shadow.ValidationAtUtc != validation.ValidatedAtUtc
            || shadow.MarketCollectedAtUtc != market.CollectedAtUtc
            || !string.Equals(shadow.MarketProviderId, market.ProviderId, StringComparison.Ordinal)
            || shadow.MarketPrice != market.Price)
            throw new InvalidOperationException("Shadow observation source market identity does not match supplied evidence.");

        if (timeline.LastTradableAtUtc > validation.ValidatedAtUtc
            || validation.ValidatedAtUtc > market.CollectedAtUtc
            || market.CollectedAtUtc > shadow.ObservedAtUtc
            || shadow.ObservedAtUtc - market.CollectedAtUtc > MaximumShadowMarketAge)
            throw new InvalidOperationException("Research provenance source chronology is invalid.");

        return new(
            validation.StrategyId,
            validation.StrategyVersion,
            validation.Symbol,
            timeline.LastTradableAtUtc,
            validation.ValidatedAtUtc,
            market.CollectedAtUtc,
            shadow.ObservedAtUtc,
            backtestHash,
            timelineHash,
            shadowHash,
            marketHash);
    }

    private static BacktestSource ParseBacktest(byte[] bytes)
    {
        var parts = Encoding.UTF8.GetString(bytes).Split('|');
        if (parts.Length != 20
            || !string.Equals(parts[0], BacktestSchema, StringComparison.Ordinal)
            || !string.Equals(parts[18], "true", StringComparison.Ordinal)
            || !string.Equals(parts[19], "false", StringComparison.Ordinal))
            throw new InvalidOperationException("Backtest validation source is not an approved unpromoted canonical v1.1 fact.");

        var validatedAt = DateTimeOffset.Parse(parts[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var sampleSize = int.Parse(parts[5], CultureInfo.InvariantCulture);
        var trades = int.Parse(parts[6], CultureInfo.InvariantCulture);
        var outOfSampleTrades = int.Parse(parts[7], CultureInfo.InvariantCulture);
        var coverageDays = int.Parse(parts[8], CultureInfo.InvariantCulture);
        var winRate = double.Parse(parts[9], CultureInfo.InvariantCulture);
        var profitFactor = double.Parse(parts[10], CultureInfo.InvariantCulture);
        var maxDrawdown = double.Parse(parts[12], CultureInfo.InvariantCulture);
        var walkForward = double.Parse(parts[15], CultureInfo.InvariantCulture);
        var monteCarloLoss = double.Parse(parts[16], CultureInfo.InvariantCulture);
        var quality = double.Parse(parts[17], CultureInfo.InvariantCulture);

        if (validatedAt.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(parts[1])
            || string.IsNullOrWhiteSpace(parts[2])
            || string.IsNullOrWhiteSpace(parts[3])
            || sampleSize < 1
            || trades < 0 || trades > sampleSize
            || outOfSampleTrades < 0 || outOfSampleTrades > trades
            || coverageDays < 0
            || !double.IsFinite(winRate) || winRate is < 0 or > 1
            || !double.IsFinite(profitFactor) || profitFactor < 0
            || !double.IsFinite(maxDrawdown) || maxDrawdown is < 0 or > 1
            || !double.IsFinite(walkForward) || walkForward is < 0 or > 1
            || !double.IsFinite(monteCarloLoss) || monteCarloLoss is < 0 or > 1
            || !double.IsFinite(quality) || quality is < 0 or > 1)
            throw new InvalidOperationException("Backtest validation source identity or metrics are invalid.");

        return new(parts[2], parts[3], parts[1], validatedAt);
    }

    private static TimelineSource ParseTimeline(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("schema").GetString(), TimelineSchema, StringComparison.Ordinal))
            throw new InvalidOperationException("Strategy timeline source schema is invalid.");

        var strategyId = Required(root, "strategy_id");
        var strategyVersion = Required(root, "strategy_version");
        var symbol = Required(root, "symbol");
        var count = root.GetProperty("decision_count").GetInt32();
        var decisions = root.GetProperty("decisions");
        var last = ParseUtc(root, "last_tradable_at_utc");
        var first = ParseUtc(root, "first_tradable_at_utc");
        if (count <= 0 || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != count || first > last)
            throw new InvalidOperationException("Strategy timeline source structure is invalid.");

        DateTimeOffset? previousTradable = null;
        var index = 0;
        foreach (var decision in decisions.EnumerateArray())
        {
            if (decision.GetProperty("sequence").GetInt32() != index
                || !string.Equals(Required(decision, "strategy_id"), strategyId, StringComparison.Ordinal)
                || !string.Equals(Required(decision, "strategy_version"), strategyVersion, StringComparison.Ordinal)
                || !string.Equals(Required(decision, "symbol"), symbol, StringComparison.Ordinal))
                throw new InvalidOperationException("Strategy timeline decision identity is invalid.");

            var source = ParseUtc(decision, "source_candle_open_time_utc");
            var evidence = ParseUtc(decision, "evidence_available_at_utc");
            var signal = ParseUtc(decision, "signal_generated_at_utc");
            var tradable = ParseUtc(decision, "tradable_at_utc");
            var exposure = decision.GetProperty("target_exposure").GetInt32();
            var open = decision.GetProperty("execution_open_price").GetDecimal();
            var close = decision.GetProperty("execution_close_price").GetDecimal();
            var volume = decision.GetProperty("execution_volume").GetDecimal();

            if (source >= evidence || evidence > signal || signal > tradable
                || previousTradable is not null && tradable <= previousTradable.Value
                || exposure is < -1 or > 1
                || open <= 0 || close <= 0 || volume < 0)
                throw new InvalidOperationException("Strategy timeline decision semantics are invalid.");

            previousTradable = tradable;
            index++;
        }

        if (previousTradable is null || first != ParseUtc(decisions[0], "tradable_at_utc") || last != previousTradable.Value)
            throw new InvalidOperationException("Strategy timeline first/last tradable bounds are invalid.");

        return new(strategyId, strategyVersion, symbol, last);
    }

    private static MarketSource ParseMarket(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("schema").GetString(), MarketSchema, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("environment").GetString(), "Testnet", StringComparison.Ordinal))
            throw new InvalidOperationException("Market provenance source must be canonical Testnet evidence.");

        var symbol = Required(root, "symbol");
        var provider = Required(root, "provider_id");
        var collectedAt = ParseUtc(root, "collected_at_utc");
        var price = root.GetProperty("price").GetDecimal();
        _ = root.GetProperty("resistance").GetDecimal();
        _ = root.GetProperty("rsi").GetDouble();
        _ = root.GetProperty("support").GetDecimal();
        _ = root.GetProperty("trend_15m").GetDouble();
        _ = root.GetProperty("trend_1h").GetDouble();
        _ = root.GetProperty("trend_4h").GetDouble();
        var candleCount = root.GetProperty("candle_count").GetInt32();
        var candles = root.GetProperty("candles");
        if (price <= 0 || candleCount < 0 || candles.ValueKind != JsonValueKind.Array || candles.GetArrayLength() != candleCount)
            throw new InvalidOperationException("Market provenance source structure is invalid.");

        DateTimeOffset? previous = null;
        foreach (var candle in candles.EnumerateArray())
        {
            if (candle.ValueKind != JsonValueKind.Array || candle.GetArrayLength() != 9)
                throw new InvalidOperationException("Market provenance candle structure is invalid.");
            var time = DateTimeOffset.Parse(candle[0].GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (time.Offset != TimeSpan.Zero || previous is not null && time <= previous.Value)
                throw new InvalidOperationException("Market provenance candle ordering is invalid.");
            for (var i = 1; i < 9; i++)
                _ = candle[i].GetDecimal();
            previous = time;
        }

        return new(symbol, provider, collectedAt, price);
    }

    private static ShadowSource ParseShadow(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("schema").GetString(), ShadowSchema, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("lifecycle").GetString(), "Shadow", StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("environment").GetString(), "Testnet", StringComparison.Ordinal))
            throw new InvalidOperationException("Shadow observation source must be qualification-only Testnet evidence.");

        var confidence = root.GetProperty("confidence").GetDouble();
        var direction = root.GetProperty("direction").GetInt32();
        var marketPrice = root.GetProperty("market_price").GetDecimal();
        if (!double.IsFinite(confidence) || confidence < 0 || confidence > 1 || direction is < -1 or > 1 || marketPrice <= 0)
            throw new InvalidOperationException("Shadow observation source metrics are invalid.");

        return new(
            Required(root, "strategy_id"),
            Required(root, "strategy_version"),
            Required(root, "symbol"),
            Required(root, "backtest_validation_sha256"),
            Required(root, "timeline_sha256"),
            Required(root, "market_provenance_sha256"),
            Required(root, "market_provider_id"),
            marketPrice,
            ParseUtc(root, "validation_at_utc"),
            ParseUtc(root, "market_collected_at_utc"),
            ParseUtc(root, "observed_at_utc"));
    }

    private static string Required(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Research provenance source field '{name}' is required.");
        return value;
    }

    private static DateTimeOffset ParseUtc(JsonElement root, string name)
    {
        var value = DateTimeOffset.Parse(
            root.GetProperty(name).GetString() ?? string.Empty,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new InvalidOperationException($"Research provenance source time '{name}' must be UTC.");
        return value;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] Serialize(ExecutionSimulationResearchProvenanceV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("backtest_validation_sha256", value.BacktestValidationSha256);
            writer.WriteBase64String("backtest_validation_canonical_bytes", value.BacktestValidationCanonicalBytes);
            writer.WriteString("bound_at_utc", value.BoundAtUtc.ToUniversalTime());
            writer.WriteString("client_order_id", value.ClientOrderId);
            writer.WriteString("correlation_id", value.CorrelationId);
            writer.WriteString("cost_model_version", value.CostModelVersion);
            writer.WriteString("market_as_of_utc", value.MarketAsOfUtc.ToUniversalTime());
            writer.WriteString("market_collected_at_utc", value.MarketCollectedAtUtc.ToUniversalTime());
            writer.WriteString("market_provenance_sha256", value.MarketProvenanceSha256);
            writer.WriteBase64String("market_provenance_canonical_bytes", value.MarketProvenanceCanonicalBytes);
            writer.WriteString("schema", value.Schema);
            writer.WriteString("shadow_observation_sha256", value.ShadowObservationSha256);
            writer.WriteBase64String("shadow_observation_canonical_bytes", value.ShadowObservationCanonicalBytes);
            writer.WriteString("shadow_observed_at_utc", value.ShadowObservedAtUtc.ToUniversalTime());
            writer.WriteString("simulated_at_utc", value.SimulatedAtUtc.ToUniversalTime());
            writer.WriteString("simulated_fill_sha256", value.SimulatedFillCanonicalSha256);
            writer.WriteString("simulation_model_version", value.SimulationModelVersion);
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteString("symbol", value.Symbol);
            writer.WriteString("timeline_last_tradable_at_utc", value.TimelineLastTradableAtUtc.ToUniversalTime());
            writer.WriteString("timeline_sha256", value.TimelineSha256);
            writer.WriteBase64String("timeline_canonical_bytes", value.TimelineCanonicalBytes);
            writer.WriteString("validation_at_utc", value.ValidationAtUtc.ToUniversalTime());
            writer.WriteString("venue_rule_version", value.VenueRuleVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private sealed record BacktestSource(string StrategyId, string StrategyVersion, string Symbol, DateTimeOffset ValidatedAtUtc);
    private sealed record TimelineSource(string StrategyId, string StrategyVersion, string Symbol, DateTimeOffset LastTradableAtUtc);
    private sealed record MarketSource(string Symbol, string ProviderId, DateTimeOffset CollectedAtUtc, decimal Price);
    private sealed record ShadowSource(
        string StrategyId,
        string StrategyVersion,
        string Symbol,
        string BacktestValidationSha256,
        string TimelineSha256,
        string MarketProvenanceSha256,
        string MarketProviderId,
        decimal MarketPrice,
        DateTimeOffset ValidationAtUtc,
        DateTimeOffset MarketCollectedAtUtc,
        DateTimeOffset ObservedAtUtc);
    private sealed record SourceEvidence(
        string StrategyId,
        string StrategyVersion,
        string Symbol,
        DateTimeOffset TimelineLastTradableAtUtc,
        DateTimeOffset ValidationAtUtc,
        DateTimeOffset MarketCollectedAtUtc,
        DateTimeOffset ShadowObservedAtUtc,
        string BacktestValidationSha256,
        string TimelineSha256,
        string ShadowObservationSha256,
        string MarketProvenanceSha256);
}

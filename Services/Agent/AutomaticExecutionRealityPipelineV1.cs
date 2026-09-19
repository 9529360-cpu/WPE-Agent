using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpeAgent.TradingAuthorization;

namespace 币安量化机器人.Services.Agent;

public sealed record AutomaticExecutionSimulationObservationV1(
    bool Available,
    string Code,
    string ProviderId,
    string Environment,
    MarketEvidence? Market,
    TradingRule? Rule,
    DateTimeOffset ObservedAtUtc);

public interface IAutomaticExecutionRealityEvidenceReader
{
    Task<AutomaticExecutionSimulationObservationV1> ObserveSimulationInputAsync(
        DurableExecutionArtifactV2 artifact,
        DurableExecutionIntentSnapshotV1 intent,
        CancellationToken ct);

    Task<ExchangeOrder?> ObserveOrderAsync(
        DurableExecutionArtifactV2 artifact,
        DurableExecutionIntentSnapshotV1 intent,
        CancellationToken ct);
}

internal enum ExecutionSimulationSourceStateV1
{
    Available,
    Unavailable
}

internal sealed record ExecutionSimulationSourceV1(
    string Schema,
    string CorrelationId,
    string ClientOrderId,
    string ArtifactSha256,
    string IntentSha256,
    string ProviderId,
    string Environment,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    PositionSide Side,
    bool ReduceOnly,
    ExecutionOrderType OrderType,
    decimal IntendedQuantity,
    int Leverage,
    DateTimeOffset ArtifactMarketCollectedAtUtc,
    string ArtifactMarketDataVersion,
    ExecutionSimulationSourceStateV1 State,
    DateTimeOffset SourceObservedAtUtc,
    DateTimeOffset MarketCollectedAtUtc,
    decimal MarketPrice,
    string MarketProvenanceSha256,
    byte[] MarketProvenanceCanonicalBytes,
    decimal StepSize,
    decimal TickSize,
    decimal MinQuantity,
    decimal MinNotional,
    int MaxLeverage,
    string VenueRuleSha256,
    string ReasonCode,
    byte[] CanonicalBytes,
    string CanonicalSha256);

internal static class ExecutionRealityCostAuthorityV1
{
    internal const string Schema = "wpe.execution-cost-authority/1.0";
    private static readonly decimal Commission = ResearchRealityModel.DefaultCosts.CommissionRate;
    private static readonly decimal Slippage = ResearchRealityModel.DefaultCosts.SlippageRate;
    internal static readonly string Version = CreateVersion();

    internal static ExecutionRealityCostAssumptionV1 Current =>
        new(Version, Commission, Slippage);

    private static string CreateVersion()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("commission_rate", Commission);
            writer.WriteString("schema", Schema);
            writer.WriteNumber("slippage_rate", Slippage);
            writer.WriteEndObject();
        }
        return Schema + ":" + Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

internal static class ExecutionSimulationSourceCanonicalizerV1
{
    internal const string Schema = "wpe.execution-simulation-source/1.0";
    internal static readonly TimeSpan MaximumMarketAge = TimeSpan.FromSeconds(30);

    internal static ExecutionSimulationSourceV1 Create(
        DurableExecutionArtifactV2 artifact,
        DurableExecutionIntentSnapshotV1 intent,
        AutomaticExecutionSimulationObservationV1 observation,
        DateTimeOffset fallbackObservedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(observation);
        if (!DurableExecutionArtifactCanonicalizerV2.Validate(artifact).Valid)
            throw new InvalidOperationException("Simulation source durable artifact is invalid.");

        var hashes = DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        var observedAt = observation.ObservedAtUtc == default
            ? fallbackObservedAtUtc.ToUniversalTime()
            : observation.ObservedAtUtc.ToUniversalTime();
        if (observedAt == default || observedAt.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Simulation source observation time must be UTC.");
        if (!string.Equals(artifact.Environment, "Testnet", StringComparison.Ordinal))
            throw new InvalidOperationException("Simulation source is Testnet-only.");
        if (!Enum.TryParse<PositionSide>(intent.Side, false, out var side)
            || !Enum.TryParse<ExecutionOrderType>(intent.OrderType, false, out var orderType))
            throw new InvalidOperationException("Simulation source intent enum identity is invalid.");

        var providerId = string.IsNullOrWhiteSpace(observation.ProviderId)
            ? artifact.ProviderId
            : observation.ProviderId;
        var environment = string.IsNullOrWhiteSpace(observation.Environment)
            ? artifact.Environment
            : observation.Environment;

        ExecutionSimulationSourceStateV1 state;
        DateTimeOffset marketAt;
        decimal marketPrice;
        string marketHash;
        byte[] marketBytes;
        decimal step;
        decimal tick;
        decimal minQty;
        decimal minNotional;
        int maxLeverage;
        string venueRuleHash;
        string reason;

        if (observation.Available
            && observation.Market is not null
            && observation.Rule is not null
            && string.Equals(providerId, artifact.ProviderId, StringComparison.Ordinal)
            && string.Equals(environment, "Testnet", StringComparison.Ordinal)
            && string.Equals(observation.Market.Symbol, intent.Symbol, StringComparison.Ordinal)
            && string.Equals(observation.Rule.Symbol, intent.Symbol, StringComparison.Ordinal)
            && MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(observation.Market)
            && observation.Market.Provenance is not null
            && string.Equals(observation.Market.Provenance.ProviderId, artifact.ProviderId, StringComparison.Ordinal)
            && string.Equals(observation.Market.Provenance.Environment, "Testnet", StringComparison.Ordinal)
            && observation.Market.CollectedAt.Kind == DateTimeKind.Utc)
        {
            marketAt = new DateTimeOffset(observation.Market.CollectedAt);
            var rule = observation.Rule;
            if (marketAt > observedAt
                || observedAt - marketAt > MaximumMarketAge
                || observation.Market.Price <= 0
                || rule.StepSize <= 0
                || rule.TickSize <= 0
                || rule.MinQuantity <= 0
                || rule.MinNotional <= 0
                || rule.MaxLeverage <= 0)
            {
                state = ExecutionSimulationSourceStateV1.Unavailable;
                marketAt = artifact.MarketCollectedAtUtc;
                marketPrice = 0;
                marketHash = string.Empty;
                marketBytes = [];
                step = tick = minQty = minNotional = 0;
                maxLeverage = 0;
                venueRuleHash = string.Empty;
                reason = "simulation-source-invalid-or-stale";
            }
            else
            {
                state = ExecutionSimulationSourceStateV1.Available;
                marketPrice = observation.Market.Price;
                marketHash = observation.Market.Provenance.CanonicalSha256;
                marketBytes = observation.Market.Provenance.CanonicalBytes;
                step = rule.StepSize;
                tick = rule.TickSize;
                minQty = rule.MinQuantity;
                minNotional = rule.MinNotional;
                maxLeverage = rule.MaxLeverage;
                venueRuleHash = VenueRuleHash(rule);
                reason = "simulation-source-available";
            }
        }
        else
        {
            state = ExecutionSimulationSourceStateV1.Unavailable;
            marketAt = artifact.MarketCollectedAtUtc;
            marketPrice = 0;
            marketHash = string.Empty;
            marketBytes = [];
            step = tick = minQty = minNotional = 0;
            maxLeverage = 0;
            venueRuleHash = string.Empty;
            reason = string.IsNullOrWhiteSpace(observation.Code)
                ? "simulation-source-unavailable"
                : observation.Code;
        }

        var draft = new ExecutionSimulationSourceV1(
            Schema,
            artifact.CorrelationId,
            intent.ClientOrderId,
            hashes.ArtifactHash,
            hashes.IntentHash,
            artifact.ProviderId,
            artifact.Environment,
            artifact.StrategyId,
            artifact.StrategyVersion,
            intent.Symbol,
            side,
            intent.ReduceOnly,
            orderType,
            intent.Quantity,
            artifact.Leverage,
            artifact.MarketCollectedAtUtc,
            artifact.MarketDataVersion,
            state,
            observedAt,
            marketAt,
            marketPrice,
            marketHash,
            marketBytes,
            step,
            tick,
            minQty,
            minNotional,
            maxLeverage,
            venueRuleHash,
            reason,
            [],
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    internal static bool IsCanonical(ExecutionSimulationSourceV1? value)
    {
        if (value is null
            || value.Schema != Schema
            || !LowerSha(value.ArtifactSha256)
            || !LowerSha(value.IntentSha256)
            || !LowerSha(value.CanonicalSha256)
            || value.CanonicalBytes.Length == 0
            || value.Environment != "Testnet"
            || string.IsNullOrWhiteSpace(value.ProviderId)
            || string.IsNullOrWhiteSpace(value.CorrelationId)
            || string.IsNullOrWhiteSpace(value.ClientOrderId)
            || string.IsNullOrWhiteSpace(value.StrategyId)
            || string.IsNullOrWhiteSpace(value.StrategyVersion)
            || string.IsNullOrWhiteSpace(value.Symbol)
            || value.IntendedQuantity <= 0
            || value.Leverage <= 0
            || value.ArtifactMarketCollectedAtUtc.Offset != TimeSpan.Zero
            || value.SourceObservedAtUtc.Offset != TimeSpan.Zero
            || value.MarketCollectedAtUtc.Offset != TimeSpan.Zero
            || value.MarketCollectedAtUtc > value.SourceObservedAtUtc
            || string.IsNullOrWhiteSpace(value.ArtifactMarketDataVersion)
            || string.IsNullOrWhiteSpace(value.ReasonCode))
            return false;

        if (value.State == ExecutionSimulationSourceStateV1.Available)
        {
            if (value.SourceObservedAtUtc - value.MarketCollectedAtUtc > MaximumMarketAge
                || value.MarketPrice <= 0
                || !LowerSha(value.MarketProvenanceSha256)
                || value.MarketProvenanceCanonicalBytes.Length == 0
                || !LowerSha(value.VenueRuleSha256)
                || value.StepSize <= 0
                || value.TickSize <= 0
                || value.MinQuantity <= 0
                || value.MinNotional <= 0
                || value.MaxLeverage <= 0)
                return false;
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(value.MarketProvenanceCanonicalBytes),
                        Convert.FromHexString(value.MarketProvenanceSha256)))
                    return false;
            }
            catch
            {
                return false;
            }
        }
        else if (value.MarketPrice != 0
                 || value.MarketProvenanceSha256.Length != 0
                 || value.MarketProvenanceCanonicalBytes.Length != 0
                 || value.StepSize != 0
                 || value.TickSize != 0
                 || value.MinQuantity != 0
                 || value.MinNotional != 0
                 || value.MaxLeverage != 0
                 || value.VenueRuleSha256.Length != 0)
            return false;

        try
        {
            var bytes = Serialize(value);
            return CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes)
                && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes),
                    Convert.FromHexString(value.CanonicalSha256));
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryDeserialize(
        ReadOnlySpan<byte> bytes,
        string canonicalSha256,
        out ExecutionSimulationSourceV1? value)
    {
        value = null;
        if (bytes.IsEmpty || !LowerSha(canonicalSha256))
            return false;
        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, canonicalSha256, StringComparison.Ordinal))
                return false;

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            value = new(
                root.GetProperty("schema").GetString() ?? string.Empty,
                root.GetProperty("correlation_id").GetString() ?? string.Empty,
                root.GetProperty("client_order_id").GetString() ?? string.Empty,
                root.GetProperty("artifact_sha256").GetString() ?? string.Empty,
                root.GetProperty("intent_sha256").GetString() ?? string.Empty,
                root.GetProperty("provider_id").GetString() ?? string.Empty,
                root.GetProperty("environment").GetString() ?? string.Empty,
                root.GetProperty("strategy_id").GetString() ?? string.Empty,
                root.GetProperty("strategy_version").GetString() ?? string.Empty,
                root.GetProperty("symbol").GetString() ?? string.Empty,
                Enum.Parse<PositionSide>(root.GetProperty("side").GetString() ?? string.Empty, false),
                root.GetProperty("reduce_only").GetBoolean(),
                Enum.Parse<ExecutionOrderType>(root.GetProperty("order_type").GetString() ?? string.Empty, false),
                root.GetProperty("intended_quantity").GetDecimal(),
                root.GetProperty("leverage").GetInt32(),
                DateTimeOffset.Parse(root.GetProperty("artifact_market_collected_at_utc").GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                root.GetProperty("artifact_market_data_version").GetString() ?? string.Empty,
                Enum.Parse<ExecutionSimulationSourceStateV1>(root.GetProperty("state").GetString() ?? string.Empty, false),
                DateTimeOffset.Parse(root.GetProperty("source_observed_at_utc").GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(root.GetProperty("market_collected_at_utc").GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                root.GetProperty("market_price").GetDecimal(),
                root.GetProperty("market_provenance_sha256").GetString() ?? string.Empty,
                root.GetProperty("market_provenance_canonical_bytes").GetBytesFromBase64(),
                root.GetProperty("step_size").GetDecimal(),
                root.GetProperty("tick_size").GetDecimal(),
                root.GetProperty("min_quantity").GetDecimal(),
                root.GetProperty("min_notional").GetDecimal(),
                root.GetProperty("max_leverage").GetInt32(),
                root.GetProperty("venue_rule_sha256").GetString() ?? string.Empty,
                root.GetProperty("reason_code").GetString() ?? string.Empty,
                bytes.ToArray(),
                canonicalSha256);
            return IsCanonical(value);
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static string VenueRuleHash(TradingRule rule)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("max_leverage", rule.MaxLeverage);
            writer.WriteNumber("min_notional", rule.MinNotional);
            writer.WriteNumber("min_quantity", rule.MinQuantity);
            writer.WriteNumber("step_size", rule.StepSize);
            writer.WriteString("symbol", rule.Symbol);
            writer.WriteNumber("tick_size", rule.TickSize);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static byte[] Serialize(ExecutionSimulationSourceV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("artifact_market_collected_at_utc", value.ArtifactMarketCollectedAtUtc.ToUniversalTime());
            writer.WriteString("artifact_market_data_version", value.ArtifactMarketDataVersion);
            writer.WriteString("artifact_sha256", value.ArtifactSha256);
            writer.WriteString("client_order_id", value.ClientOrderId);
            writer.WriteString("correlation_id", value.CorrelationId);
            writer.WriteString("environment", value.Environment);
            writer.WriteString("intent_sha256", value.IntentSha256);
            writer.WriteNumber("intended_quantity", value.IntendedQuantity);
            writer.WriteNumber("leverage", value.Leverage);
            writer.WriteNumber("market_price", value.MarketPrice);
            writer.WriteString("market_collected_at_utc", value.MarketCollectedAtUtc.ToUniversalTime());
            writer.WriteBase64String("market_provenance_canonical_bytes", value.MarketProvenanceCanonicalBytes);
            writer.WriteString("market_provenance_sha256", value.MarketProvenanceSha256);
            writer.WriteNumber("max_leverage", value.MaxLeverage);
            writer.WriteNumber("min_notional", value.MinNotional);
            writer.WriteNumber("min_quantity", value.MinQuantity);
            writer.WriteString("order_type", value.OrderType.ToString());
            writer.WriteString("provider_id", value.ProviderId);
            writer.WriteBoolean("reduce_only", value.ReduceOnly);
            writer.WriteString("reason_code", value.ReasonCode);
            writer.WriteString("schema", value.Schema);
            writer.WriteString("side", value.Side.ToString());
            writer.WriteString("source_observed_at_utc", value.SourceObservedAtUtc.ToUniversalTime());
            writer.WriteString("state", value.State.ToString());
            writer.WriteNumber("step_size", value.StepSize);
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteString("symbol", value.Symbol);
            writer.WriteNumber("tick_size", value.TickSize);
            writer.WriteString("venue_rule_sha256", value.VenueRuleSha256);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool LowerSha(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class AutomaticExecutionSimulationModelV1
{
    internal const string Version = "wpe.execution-simulation-model/1.0";

    internal static ExecutionSimulationFillV1 CreateFill(ExecutionSimulationSourceV1 source)
    {
        if (!ExecutionSimulationSourceCanonicalizerV1.IsCanonical(source))
            throw new InvalidOperationException("Execution simulation source is not canonical.");

        var costs = ExecutionRealityCostAuthorityV1.Current;
        var venueVersion = source.State == ExecutionSimulationSourceStateV1.Available
            ? "wpe.venue-rule/1.0:" + source.VenueRuleSha256
            : "wpe.venue-rule/1.0:unavailable";

        if (source.State != ExecutionSimulationSourceStateV1.Available)
            return Unsupported(source, costs.Version, venueVersion, source.ReasonCode);

        if (source.OrderType != ExecutionOrderType.Market)
            return Unsupported(source, costs.Version, venueVersion, "simulation-order-type-unsupported");

        if (source.Leverage > source.MaxLeverage
            || source.IntendedQuantity < source.MinQuantity
            || source.IntendedQuantity != RoundQuantity(source.IntendedQuantity, source.StepSize)
            || source.MarketPrice * source.IntendedQuantity < source.MinNotional)
            return Unsupported(source, costs.Version, venueVersion, "simulation-venue-rule-unsupported");

        var buy = source.Side == PositionSide.Long
            ? !source.ReduceOnly
            : source.ReduceOnly;
        var adverseMultiplier = buy
            ? 1m + costs.SlippageRate
            : 1m - costs.SlippageRate;
        var averagePrice = source.MarketPrice * adverseMultiplier;
        if (averagePrice <= 0)
            return Unsupported(source, costs.Version, venueVersion, "simulation-price-invalid");

        var fee = averagePrice * source.IntendedQuantity * costs.CommissionRate;
        return ExecutionSimulationFillCanonicalizerV1.Create(
            source.CorrelationId,
            source.ClientOrderId,
            source.StrategyId,
            source.StrategyVersion,
            costs.Version,
            Version,
            venueVersion,
            source.Symbol,
            source.Side,
            source.ReduceOnly,
            source.OrderType,
            source.IntendedQuantity,
            ExecutionSimulationFillStateV1.Filled,
            source.IntendedQuantity,
            averagePrice,
            fee,
            ExecutionSimulationFeeRoleV1.Taker,
            latencyModeled:false,
            simulatedLatencyMs:0,
            source.MarketCollectedAtUtc,
            source.SourceObservedAtUtc,
            "market-full-taker-cost-authority");
    }

    private static ExecutionSimulationFillV1 Unsupported(
        ExecutionSimulationSourceV1 source,
        string costVersion,
        string venueVersion,
        string reason) =>
        ExecutionSimulationFillCanonicalizerV1.Create(
            source.CorrelationId,
            source.ClientOrderId,
            source.StrategyId,
            source.StrategyVersion,
            costVersion,
            Version,
            venueVersion,
            source.Symbol,
            source.Side,
            source.ReduceOnly,
            source.OrderType,
            source.IntendedQuantity,
            ExecutionSimulationFillStateV1.Unsupported,
            0,
            0,
            0,
            ExecutionSimulationFeeRoleV1.Unavailable,
            latencyModeled:false,
            simulatedLatencyMs:0,
            source.MarketCollectedAtUtc,
            source.SourceObservedAtUtc,
            reason);

    private static decimal RoundQuantity(decimal value, decimal step) =>
        step <= 0 ? value : Math.Floor(value / step) * step;
}

internal sealed record AutomaticExecutionRealityPipelineResultV1(
    int Examined,
    int Simulated,
    int Observed,
    int Compared,
    int Skipped,
    string Code);

internal sealed class AutomaticExecutionRealityPipelineV1
{
    private readonly AgentSqliteStore _store;
    private readonly IAutomaticExecutionRealityEvidenceReader? _reader;
    private readonly Func<DateTimeOffset> _utcNow;

    internal AutomaticExecutionRealityPipelineV1(
        AgentSqliteStore store,
        IAutomaticExecutionGateway gateway,
        Func<DateTimeOffset>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reader = gateway as IAutomaticExecutionRealityEvidenceReader;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<AutomaticExecutionRealityPipelineResultV1> CaptureBeforeMutationAsync(
        PersistedAutomaticExecution item,
        CancellationToken ct)
    {
        if (!ValidateDurableIdentity(item, requireCurrentValidity:true, out var artifact, out _))
            return new(0,0,0,0,0,"execution-reality.artifact-invalid");

        var simulated = 0;
        var skipped = 0;
        foreach (var intent in artifact.Intents.OrderBy(x => x.Sequence))
        {
            ct.ThrowIfCancellationRequested();
            var existing = await _store.GetExecutionSimulationSourceAsync(
                artifact.CorrelationId,
                intent.ClientOrderId,
                ct);
            ExecutionSimulationSourceV1 source;
            if (existing is not null)
            {
                source = existing;
                skipped++;
            }
            else
            {
                AutomaticExecutionSimulationObservationV1 observation;
                var now = _utcNow().ToUniversalTime();
                if (_reader is null)
                    observation = new(false,"simulation-reader-unavailable",artifact.ProviderId,artifact.Environment,null,null,now);
                else
                {
                    try
                    {
                        observation = await _reader.ObserveSimulationInputAsync(artifact,intent,ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        observation = new(false,"simulation-source-query-failed",artifact.ProviderId,artifact.Environment,null,null,now);
                    }
                }
                source = ExecutionSimulationSourceCanonicalizerV1.Create(artifact,intent,observation,now);
                await _store.SaveExecutionSimulationSourceAsync(source,ct);
            }

            var fill = AutomaticExecutionSimulationModelV1.CreateFill(source);
            await _store.SaveExecutionSimulationFillAsync(fill,ct);
            simulated++;
        }

        return new(artifact.Intents.Count,simulated,0,0,skipped,"execution-reality.simulation-captured");
    }

    internal async Task<AutomaticExecutionRealityPipelineResultV1> CompareTerminalAsync(
        PersistedAutomaticExecution item,
        IReadOnlyList<PersistedAutomaticExecutionEvent> events,
        CancellationToken ct)
    {
        if (!ValidateDurableIdentity(item, requireCurrentValidity:false, out var artifact, out var receipt)
            || _reader is null
            || item.Status is not (AutomaticExecutionQueueStatus.Succeeded or AutomaticExecutionQueueStatus.FailedTerminal))
            return new(0,0,0,0,0,"execution-reality.not-comparable");

        var executing = events
            .Where(x => x.ToStatus == AutomaticExecutionQueueStatus.Executing)
            .OrderBy(x => x.Sequence)
            .ToArray();
        if (executing.Length != 1
            || executing[0].OccurredAtUtc < artifact.CreatedAtUtc
            || executing[0].OccurredAtUtc < receipt.IssuedAtUtc
            || executing[0].OccurredAtUtc >= receipt.ExpiresAtUtc)
            return new(0,0,0,0,0,"execution-reality.executing-evidence-invalid");

        var examined = 0;
        var observedCount = 0;
        var compared = 0;
        var skipped = 0;
        foreach (var intentSnapshot in artifact.Intents.OrderBy(x => x.Sequence))
        {
            examined++;
            ct.ThrowIfCancellationRequested();
            var fill = await _store.GetExecutionSimulationFillAsync(
                artifact.CorrelationId,
                intentSnapshot.ClientOrderId,
                ct);
            var source = await _store.GetExecutionSimulationSourceAsync(
                artifact.CorrelationId,
                intentSnapshot.ClientOrderId,
                ct);
            if (fill is null || source is null
                || !string.Equals(fill.StrategyId,artifact.StrategyId,StringComparison.Ordinal)
                || !string.Equals(fill.StrategyVersion,artifact.StrategyVersion,StringComparison.Ordinal)
                || fill.SimulatedAtUtc != source.SourceObservedAtUtc)
            {
                skipped++;
                continue;
            }

            ExchangeOrder? order;
            try
            {
                order = await _reader.ObserveOrderAsync(artifact,intentSnapshot,ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                skipped++;
                continue;
            }
            if (order is null
                || order.UpdatedAt.Kind != DateTimeKind.Utc
                || !string.Equals(order.Symbol,intentSnapshot.Symbol,StringComparison.Ordinal)
                || !string.Equals(order.ClientOrderId,intentSnapshot.ClientOrderId,StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            var intent = RestoreIntent(intentSnapshot);
            var costs = ExecutionRealityCostAuthorityV1.Current;
            var fee = await _store.GetExecutionRealityFeeObservationAsync(
                order.ClientOrderId,
                order.ExecutedQuantity,
                ct);
            var now = _utcNow().ToUniversalTime();
            var expectation = new ExecutionRealityExpectationV1(
                artifact.CorrelationId,
                intent.ClientOrderId,
                artifact.StrategyId,
                artifact.StrategyVersion,
                costs.Version,
                intent.Symbol,
                intent.Side,
                intent.ReduceOnly,
                intent.OrderType,
                intent.Quantity,
                intent.ExpectedPrice,
                costs.CommissionRate,
                costs.SlippageRate,
                executing[0].OccurredAtUtc);
            var observation = new ExecutionRealityObservationV1(
                order.ClientOrderId,
                order.Status,
                order.ExecutedQuantity,
                order.ExecutedQuantity > 0 ? order.AvgPrice : 0,
                fee.Available ? fee.FeeAmount : 0,
                fee.Available ? ExecutionRealityDriftV1.ExchangeReportedFeeBasis : ExecutionRealityDriftV1.UnavailableFeeBasis,
                new DateTimeOffset(order.UpdatedAt),
                now);
            var fact = ExecutionRealityDriftV1.Analyze(expectation,observation);
            await _store.SaveExecutionRealityDriftAsync(fact,ct);
            observedCount++;

            var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(fill,fact,now);
            await _store.SaveExecutionSimulationComparisonAsync(comparison,ct);
            compared++;
        }

        return new(examined,0,observedCount,compared,skipped,
            compared>0?"execution-reality.comparison-stored":"execution-reality.comparison-unavailable");
    }

    private static bool ValidateDurableIdentity(
        PersistedAutomaticExecution item,
        bool requireCurrentValidity,
        out DurableExecutionArtifactV2 artifact,
        out DeterministicRiskReceipt receipt)
    {
        artifact = null!;
        receipt = null!;
        if (item is null
            || !item.ArtifactValid
            || !item.RiskReceiptValid
            || item.Artifact is null
            || item.RiskReceipt is null)
            return false;

        artifact = item.Artifact;
        receipt = item.RiskReceipt;
        if (!DurableExecutionArtifactCanonicalizerV2.Validate(artifact).Valid
            || artifact.Environment != "Testnet")
            return false;

        var hashes = DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        if (!string.Equals(item.ArtifactHash,hashes.ArtifactHash,StringComparison.Ordinal)
            || !string.Equals(item.IntentHash,hashes.IntentHash,StringComparison.Ordinal)
            || !receipt.Approved
            || receipt.RevokedAtUtc is not null
            || !string.Equals(receipt.CorrelationId,artifact.CorrelationId,StringComparison.Ordinal)
            || !string.Equals(receipt.IntentHash,hashes.IntentHash,StringComparison.Ordinal)
            || !string.Equals(receipt.ArtifactHash,hashes.ArtifactHash,StringComparison.Ordinal)
            || receipt.IssuedAtUtc.Offset != TimeSpan.Zero
            || receipt.ExpiresAtUtc.Offset != TimeSpan.Zero
            || receipt.ExpiresAtUtc <= receipt.IssuedAtUtc)
            return false;

        if (requireCurrentValidity)
        {
            var now = DateTimeOffset.UtcNow;
            if (artifact.CreatedAtUtc > now
                || artifact.ExpiresAtUtc <= now
                || receipt.IssuedAtUtc > now
                || receipt.ExpiresAtUtc <= now)
                return false;
        }
        return true;
    }

    private static ExecutionIntent RestoreIntent(DurableExecutionIntentSnapshotV1 value) =>
        new(
            value.Symbol,
            Enum.Parse<PositionSide>(value.Side,false),
            value.Quantity,
            value.ReduceOnly,
            value.StopLoss,
            value.TakeProfit,
            value.ClientOrderId,
            value.ReasonCode,
            Enum.Parse<DecisionAction>(value.Action,false),
            Enum.Parse<ExecutionOrderType>(value.OrderType,false),
            value.LimitPrice,
            value.ExpectedPrice);
}

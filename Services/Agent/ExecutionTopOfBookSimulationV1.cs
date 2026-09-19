using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WpeAgent.TradingAuthorization;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionSimulationSourceV1(
    string Schema,
    string CorrelationId,
    string ClientOrderId,
    int IntentSequence,
    string StrategyId,
    string StrategyVersion,
    string CostModelVersion,
    string SimulationModelVersion,
    string VenueRuleVersion,
    string ArtifactSha256,
    string IntentSha256,
    string ProviderId,
    string Environment,
    string Symbol,
    string Side,
    bool ReduceOnly,
    string OrderType,
    decimal IntendedQuantity,
    decimal ExpectedPrice,
    bool RuleAvailable,
    decimal RuleStepSize,
    decimal RuleTickSize,
    decimal RuleMinQuantity,
    decimal RuleMinNotional,
    int RuleMaxLeverage,
    bool SnapshotAvailable,
    DateTimeOffset? MarketUpdatedAtUtc,
    decimal LastPrice,
    decimal BestBid,
    decimal BestAsk,
    decimal BidQuantity,
    decimal AskQuantity,
    long MarketMessages,
    bool MarketConnected,
    DateTimeOffset SimulatedAtUtc,
    string SimulatedFillSha256,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public sealed record ExecutionSimulationBundleV1(
    ExecutionSimulationSourceV1 Source,
    ExecutionSimulationFillV1 Fill);

public sealed record ExecutionSimulationProducerResultV1(
    bool Recorded,
    int FillCount,
    int SupportedFillCount,
    string Code,
    IReadOnlyList<string> FillCanonicalSha256s);

public interface IAutomaticExecutionSimulationProducerV1
{
    Task<ExecutionSimulationProducerResultV1> CaptureAsync(
        DurableExecutionArtifactV2 artifact,
        CancellationToken ct);
}

public static class ExecutionTopOfBookSimulationV1
{
    public const string SourceSchema = "wpe.execution-simulation-source/1.0";
    public const string SimulationModelVersion = "wpe.top-of-book-market/1.0";
    public const string DefaultCostModelVersion = "research-cost-v1";
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(15);

    public static ExecutionSimulationBundleV1 Create(
        DurableExecutionArtifactV2 artifact,
        DurableExecutionIntentSnapshotV1 intent,
        string providerId,
        ExchangeEnvironment environment,
        RealtimeMarketSnapshot? snapshot,
        TradingRule? rule,
        string costModelVersion,
        DateTimeOffset simulatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(intent);

        var validation = DurableExecutionArtifactCanonicalizerV2.Validate(artifact);
        if (!validation.Valid)
            throw new InvalidOperationException("Simulation source requires a valid durable execution artifact.");
        if (environment != ExchangeEnvironment.Testnet
            || !string.Equals(artifact.Environment, "Testnet", StringComparison.Ordinal))
            throw new InvalidOperationException("Execution simulation evidence is Testnet-only.");
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        if (string.IsNullOrWhiteSpace(costModelVersion))
            throw new ArgumentException("Cost-model version is required.", nameof(costModelVersion));
        if (simulatedAtUtc == default || simulatedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Simulation time must be UTC.", nameof(simulatedAtUtc));

        var matching = artifact.Intents
            .Where(x => x.Sequence == intent.Sequence
                && string.Equals(x.ClientOrderId, intent.ClientOrderId, StringComparison.Ordinal))
            .ToArray();
        if (matching.Length != 1 || !Equals(matching[0], intent))
            throw new InvalidOperationException("Simulation intent is not the exact durable artifact intent.");

        var hashes = DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);
        var ruleAvailable = RuleUsable(rule, intent.Symbol);
        var venueRuleVersion = ruleAvailable
            ? providerId + ":rules:" + RuleHash(rule!)
            : providerId + ":rules-unavailable";

        var snapshotAvailable = SnapshotUsable(snapshot, intent.Symbol);
        var marketUpdatedAt = snapshotAvailable
            ? new DateTimeOffset(snapshot!.UpdatedAt)
            : (DateTimeOffset?)null;

        var sourceDraft = new ExecutionSimulationSourceV1(
            SourceSchema,
            artifact.CorrelationId,
            intent.ClientOrderId,
            intent.Sequence,
            artifact.StrategyId,
            artifact.StrategyVersion,
            costModelVersion,
            SimulationModelVersion,
            venueRuleVersion,
            hashes.ArtifactHash,
            hashes.IntentHash,
            providerId,
            environment.ToString(),
            intent.Symbol,
            intent.Side,
            intent.ReduceOnly,
            intent.OrderType,
            intent.Quantity,
            intent.ExpectedPrice,
            ruleAvailable,
            ruleAvailable ? rule!.StepSize : 0m,
            ruleAvailable ? rule!.TickSize : 0m,
            ruleAvailable ? rule!.MinQuantity : 0m,
            ruleAvailable ? rule!.MinNotional : 0m,
            ruleAvailable ? rule!.MaxLeverage : 0,
            snapshotAvailable,
            marketUpdatedAt,
            snapshotAvailable ? snapshot!.LastPrice : 0m,
            snapshotAvailable ? snapshot!.BestBid : 0m,
            snapshotAvailable ? snapshot!.BestAsk : 0m,
            snapshotAvailable ? snapshot!.BidQuantity : 0m,
            snapshotAvailable ? snapshot!.AskQuantity : 0m,
            snapshotAvailable ? snapshot!.Messages : 0,
            snapshotAvailable && snapshot!.Connected,
            simulatedAtUtc,
            string.Empty,
            Array.Empty<byte>(),
            string.Empty);

        var fill = ReplayFill(sourceDraft);
        var withFill = sourceDraft with { SimulatedFillSha256 = fill.CanonicalSha256 };
        var sourceBytes = SerializeSource(withFill);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var source = withFill with { CanonicalBytes = sourceBytes, CanonicalSha256 = sourceHash };

        if (!IsCanonical(source))
            throw new InvalidOperationException("Execution simulation source canonicalization failed.");
        return new(source, fill);
    }

    public static ExecutionSimulationFillV1 ReplayFill(ExecutionSimulationSourceV1 source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!SourceFieldsStructurallyValid(source, requireCanonicalIdentity:false))
            throw new InvalidOperationException("Execution simulation source is structurally invalid.");

        if (!Enum.TryParse<PositionSide>(source.Side, false, out var side))
            throw new InvalidOperationException("Simulation side is invalid.");
        if (!Enum.TryParse<ExecutionOrderType>(source.OrderType, false, out var orderType))
            throw new InvalidOperationException("Simulation order type is invalid.");

        ExecutionSimulationFillStateV1 state;
        decimal executed;
        decimal averagePrice;
        string reason;

        if (!source.RuleAvailable)
        {
            state = ExecutionSimulationFillStateV1.Unsupported;
            executed = 0m;
            averagePrice = 0m;
            reason = "venue-rule-unavailable";
        }
        else if (!IntentPassesRule(source))
        {
            state = ExecutionSimulationFillStateV1.Unsupported;
            executed = 0m;
            averagePrice = 0m;
            reason = "venue-rule-rejected";
        }
        else if (orderType != ExecutionOrderType.Market)
        {
            state = ExecutionSimulationFillStateV1.Unsupported;
            executed = 0m;
            averagePrice = 0m;
            reason = "order-type-unsupported";
        }
        else if (!source.SnapshotAvailable || source.MarketUpdatedAtUtc is null)
        {
            state = ExecutionSimulationFillStateV1.Unsupported;
            executed = 0m;
            averagePrice = 0m;
            reason = "top-of-book-unavailable";
        }
        else if (source.MarketUpdatedAtUtc.Value > source.SimulatedAtUtc
            || source.SimulatedAtUtc - source.MarketUpdatedAtUtc.Value > MaximumSnapshotAge)
        {
            state = ExecutionSimulationFillStateV1.Unsupported;
            executed = 0m;
            averagePrice = 0m;
            reason = "top-of-book-stale";
        }
        else
        {
            var buy = (!source.ReduceOnly && side == PositionSide.Long)
                || (source.ReduceOnly && side == PositionSide.Short);
            var available = buy ? source.AskQuantity : source.BidQuantity;
            var price = buy ? source.BestAsk : source.BestBid;
            if (available <= 0)
            {
                state = ExecutionSimulationFillStateV1.NotFilled;
                executed = 0m;
                averagePrice = 0m;
                reason = "top-of-book-zero-quantity";
            }
            else
            {
                executed = Math.Min(source.IntendedQuantity, available);
                averagePrice = price;
                state = executed == source.IntendedQuantity
                    ? ExecutionSimulationFillStateV1.Filled
                    : ExecutionSimulationFillStateV1.Partial;
                reason = state == ExecutionSimulationFillStateV1.Filled
                    ? "top-of-book-full"
                    : "top-of-book-partial";
            }
        }

        return ExecutionSimulationFillCanonicalizerV1.Create(
            source.CorrelationId,
            source.ClientOrderId,
            source.StrategyId,
            source.StrategyVersion,
            source.CostModelVersion,
            source.SimulationModelVersion,
            source.VenueRuleVersion,
            source.Symbol,
            side,
            source.ReduceOnly,
            orderType,
            source.IntendedQuantity,
            state,
            executed,
            averagePrice,
            feeAmount:0m,
            feeRole:ExecutionSimulationFeeRoleV1.Unavailable,
            latencyModeled:false,
            simulatedLatencyMs:0,
            marketAsOfUtc:source.MarketUpdatedAtUtc ?? source.SimulatedAtUtc,
            simulatedAtUtc:source.SimulatedAtUtc,
            reasonCode:reason);
    }

    public static bool IsCanonical(ExecutionSimulationSourceV1 source)
    {
        if (!SourceFieldsStructurallyValid(source, requireCanonicalIdentity:true))
            return false;
        try
        {
            var replay = ReplayFill(source);
            if (!string.Equals(replay.CanonicalSha256, source.SimulatedFillSha256, StringComparison.Ordinal))
                return false;
            var bytes = SerializeSource(source);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return string.Equals(hash, source.CanonicalSha256, StringComparison.Ordinal)
                && CryptographicOperations.FixedTimeEquals(bytes, source.CanonicalBytes);
        }
        catch
        {
            return false;
        }
    }

    private static bool SourceFieldsStructurallyValid(
        ExecutionSimulationSourceV1 source,
        bool requireCanonicalIdentity)
    {
        if (source.Schema != SourceSchema
            || string.IsNullOrWhiteSpace(source.CorrelationId)
            || string.IsNullOrWhiteSpace(source.ClientOrderId)
            || source.IntentSequence < 0
            || string.IsNullOrWhiteSpace(source.StrategyId)
            || string.IsNullOrWhiteSpace(source.StrategyVersion)
            || string.IsNullOrWhiteSpace(source.CostModelVersion)
            || source.SimulationModelVersion != SimulationModelVersion
            || string.IsNullOrWhiteSpace(source.VenueRuleVersion)
            || !LowerSha(source.ArtifactSha256)
            || !LowerSha(source.IntentSha256)
            || string.IsNullOrWhiteSpace(source.ProviderId)
            || source.Environment != "Testnet"
            || string.IsNullOrWhiteSpace(source.Symbol)
            || string.IsNullOrWhiteSpace(source.Side)
            || string.IsNullOrWhiteSpace(source.OrderType)
            || source.IntendedQuantity <= 0
            || source.ExpectedPrice <= 0
            || source.SimulatedAtUtc == default
            || source.SimulatedAtUtc.Offset != TimeSpan.Zero
            || !LowerSha(source.SimulatedFillSha256))
            return false;

        if (source.RuleAvailable)
        {
            if (source.RuleStepSize <= 0
                || source.RuleTickSize <= 0
                || source.RuleMinQuantity <= 0
                || source.RuleMinNotional < 0
                || source.RuleMaxLeverage <= 0
                || !source.VenueRuleVersion.EndsWith(RuleHash(source), StringComparison.Ordinal))
                return false;
        }
        else if (source.RuleStepSize != 0
            || source.RuleTickSize != 0
            || source.RuleMinQuantity != 0
            || source.RuleMinNotional != 0
            || source.RuleMaxLeverage != 0)
            return false;

        if (source.SnapshotAvailable)
        {
            if (source.MarketUpdatedAtUtc is null
                || source.MarketUpdatedAtUtc.Value.Offset != TimeSpan.Zero
                || source.LastPrice <= 0
                || source.BestBid <= 0
                || source.BestAsk < source.BestBid
                || source.BidQuantity < 0
                || source.AskQuantity < 0
                || source.MarketMessages <= 0
                || !source.MarketConnected)
                return false;
        }
        else if (source.MarketUpdatedAtUtc is not null
            || source.LastPrice != 0
            || source.BestBid != 0
            || source.BestAsk != 0
            || source.BidQuantity != 0
            || source.AskQuantity != 0
            || source.MarketMessages != 0
            || source.MarketConnected)
            return false;

        if (!requireCanonicalIdentity)
            return true;

        return source.CanonicalBytes.Length > 0
            && LowerSha(source.CanonicalSha256);
    }

    private static bool IntentPassesRule(ExecutionSimulationSourceV1 source)
    {
        if (!source.RuleAvailable)
            return false;
        if (source.IntendedQuantity < source.RuleMinQuantity)
            return false;
        var rounded = Math.Floor(source.IntendedQuantity / source.RuleStepSize) * source.RuleStepSize;
        if (rounded != source.IntendedQuantity)
            return false;
        var referencePrice = source.ExpectedPrice;
        if (referencePrice * source.IntendedQuantity < source.RuleMinNotional)
            return false;
        return true;
    }

    private static bool RuleUsable(TradingRule? rule, string symbol) =>
        rule is not null
        && string.Equals(rule.Symbol, symbol, StringComparison.Ordinal)
        && rule.StepSize > 0
        && rule.TickSize > 0
        && rule.MinQuantity > 0
        && rule.MinNotional >= 0
        && rule.MaxLeverage > 0;

    private static bool SnapshotUsable(RealtimeMarketSnapshot? snapshot, string symbol) =>
        snapshot is not null
        && string.Equals(snapshot.Symbol, symbol, StringComparison.Ordinal)
        && snapshot.UpdatedAt != default
        && snapshot.UpdatedAt.Kind == DateTimeKind.Utc
        && snapshot.LastPrice > 0
        && snapshot.BestBid > 0
        && snapshot.BestAsk >= snapshot.BestBid
        && snapshot.BidQuantity >= 0
        && snapshot.AskQuantity >= 0
        && snapshot.Messages > 0
        && snapshot.Connected;

    private static string RuleHash(TradingRule rule) =>
        Convert.ToHexString(SHA256.HashData(SerializeRule(
            rule.StepSize,
            rule.TickSize,
            rule.MinQuantity,
            rule.MinNotional,
            rule.MaxLeverage))).ToLowerInvariant();

    private static string RuleHash(ExecutionSimulationSourceV1 source) =>
        Convert.ToHexString(SHA256.HashData(SerializeRule(
            source.RuleStepSize,
            source.RuleTickSize,
            source.RuleMinQuantity,
            source.RuleMinNotional,
            source.RuleMaxLeverage))).ToLowerInvariant();

    private static byte[] SerializeRule(
        decimal stepSize,
        decimal tickSize,
        decimal minQuantity,
        decimal minNotional,
        int maxLeverage)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("max_leverage", maxLeverage);
            writer.WriteNumber("min_notional", minNotional);
            writer.WriteNumber("min_quantity", minQuantity);
            writer.WriteNumber("step_size", stepSize);
            writer.WriteNumber("tick_size", tickSize);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] SerializeSource(ExecutionSimulationSourceV1 source)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("artifact_sha256", source.ArtifactSha256);
            writer.WriteNumber("ask_quantity", source.AskQuantity);
            writer.WriteNumber("best_ask", source.BestAsk);
            writer.WriteNumber("best_bid", source.BestBid);
            writer.WriteNumber("bid_quantity", source.BidQuantity);
            writer.WriteString("client_order_id", source.ClientOrderId);
            writer.WriteString("correlation_id", source.CorrelationId);
            writer.WriteString("cost_model_version", source.CostModelVersion);
            writer.WriteString("environment", source.Environment);
            writer.WriteNumber("expected_price", source.ExpectedPrice);
            writer.WriteString("intent_sha256", source.IntentSha256);
            writer.WriteNumber("intent_sequence", source.IntentSequence);
            writer.WriteNumber("intended_quantity", source.IntendedQuantity);
            writer.WriteNumber("last_price", source.LastPrice);
            writer.WriteBoolean("market_connected", source.MarketConnected);
            writer.WriteNumber("market_messages", source.MarketMessages);
            if (source.MarketUpdatedAtUtc is null) writer.WriteNull("market_updated_at_utc");
            else writer.WriteString("market_updated_at_utc", source.MarketUpdatedAtUtc.Value.ToUniversalTime());
            writer.WriteString("order_type", source.OrderType);
            writer.WriteString("provider_id", source.ProviderId);
            writer.WriteBoolean("reduce_only", source.ReduceOnly);
            writer.WriteBoolean("rule_available", source.RuleAvailable);
            writer.WriteNumber("rule_max_leverage", source.RuleMaxLeverage);
            writer.WriteNumber("rule_min_notional", source.RuleMinNotional);
            writer.WriteNumber("rule_min_quantity", source.RuleMinQuantity);
            writer.WriteNumber("rule_step_size", source.RuleStepSize);
            writer.WriteNumber("rule_tick_size", source.RuleTickSize);
            writer.WriteString("schema", source.Schema);
            writer.WriteString("side", source.Side);
            writer.WriteString("simulated_at_utc", source.SimulatedAtUtc.ToUniversalTime());
            writer.WriteString("simulated_fill_sha256", source.SimulatedFillSha256);
            writer.WriteString("simulation_model_version", source.SimulationModelVersion);
            writer.WriteBoolean("snapshot_available", source.SnapshotAvailable);
            writer.WriteString("strategy_id", source.StrategyId);
            writer.WriteString("strategy_version", source.StrategyVersion);
            writer.WriteString("symbol", source.Symbol);
            writer.WriteString("venue_rule_version", source.VenueRuleVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool LowerSha(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class ExecutionTopOfBookSimulationProducerV1 : IAutomaticExecutionSimulationProducerV1
{
    private readonly string _providerId;
    private readonly IExchangeAdapter _exchange;
    private readonly IRealtimeMarketFeed _realtime;
    private readonly AgentSqliteStore _store;
    private readonly string _costModelVersion;
    private readonly Func<DateTimeOffset> _utcNow;

    public ExecutionTopOfBookSimulationProducerV1(
        string providerId,
        IExchangeAdapter exchange,
        IRealtimeMarketFeed realtime,
        AgentSqliteStore store,
        string costModelVersion = ExecutionTopOfBookSimulationV1.DefaultCostModelVersion,
        Func<DateTimeOffset>? utcNow = null)
    {
        _providerId = string.IsNullOrWhiteSpace(providerId)
            ? throw new ArgumentException("Provider id is required.", nameof(providerId))
            : providerId;
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
        _realtime = realtime ?? throw new ArgumentNullException(nameof(realtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _costModelVersion = string.IsNullOrWhiteSpace(costModelVersion)
            ? throw new ArgumentException("Cost-model version is required.", nameof(costModelVersion))
            : costModelVersion;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ExecutionSimulationProducerResultV1> CaptureAsync(
        DurableExecutionArtifactV2 artifact,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (_exchange.Environment != ExchangeEnvironment.Testnet
            || !DurableExecutionArtifactCanonicalizerV2.Validate(artifact).Valid)
            return new(false, 0, 0, "simulation-source-invalid", Array.Empty<string>());

        var hashes = new List<string>(artifact.Intents.Count);
        var supported = 0;
        foreach (var intent in artifact.Intents.OrderBy(x => x.Sequence))
        {
            ct.ThrowIfCancellationRequested();
            TradingRule? rule = null;
            try
            {
                rule = await _exchange.GetRulesAsync(intent.Symbol, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                rule = null;
            }

            RealtimeMarketSnapshot? snapshot;
            try
            {
                snapshot = _realtime.GetSnapshot(intent.Symbol);
            }
            catch
            {
                snapshot = null;
            }

            var now = _utcNow().ToUniversalTime();
            var bundle = ExecutionTopOfBookSimulationV1.Create(
                artifact,
                intent,
                _providerId,
                _exchange.Environment,
                snapshot,
                rule,
                _costModelVersion,
                now);

            var persisted = await _store.SaveExecutionSimulationBundleAsync(
                bundle.Source,
                bundle.Fill,
                ct);
            if (!persisted.Succeeded)
                return new(false, hashes.Count, supported, persisted.Code, hashes);

            hashes.Add(bundle.Fill.CanonicalSha256);
            if (bundle.Fill.State != ExecutionSimulationFillStateV1.Unsupported)
                supported++;
        }

        return new(true, hashes.Count, supported, "simulation-evidence-stored", hashes);
    }
}

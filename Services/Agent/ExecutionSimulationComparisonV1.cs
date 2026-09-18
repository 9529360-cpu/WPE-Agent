using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum ExecutionSimulationFillStateV1
{
    Filled,
    Partial,
    NotFilled,
    Unsupported
}

public enum ExecutionSimulationFeeRoleV1
{
    Maker,
    Taker,
    Unavailable
}

public sealed record ExecutionSimulationFillV1(
    string Schema,
    string CorrelationId,
    string ClientOrderId,
    string StrategyId,
    string StrategyVersion,
    string CostModelVersion,
    string SimulationModelVersion,
    string VenueRuleVersion,
    string Symbol,
    PositionSide Side,
    bool ReduceOnly,
    ExecutionOrderType OrderType,
    decimal IntendedQuantity,
    ExecutionSimulationFillStateV1 State,
    decimal ExecutedQuantity,
    decimal AveragePrice,
    decimal FeeAmount,
    ExecutionSimulationFeeRoleV1 FeeRole,
    bool LatencyModeled,
    long SimulatedLatencyMs,
    DateTimeOffset MarketAsOfUtc,
    DateTimeOffset SimulatedAtUtc,
    string ReasonCode,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class ExecutionSimulationFillCanonicalizerV1
{
    public const string Schema = "wpe.execution-simulated-fill/1.0";

    public static ExecutionSimulationFillV1 Create(
        string correlationId,
        string clientOrderId,
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string venueRuleVersion,
        string symbol,
        PositionSide side,
        bool reduceOnly,
        ExecutionOrderType orderType,
        decimal intendedQuantity,
        ExecutionSimulationFillStateV1 state,
        decimal executedQuantity,
        decimal averagePrice,
        decimal feeAmount,
        ExecutionSimulationFeeRoleV1 feeRole,
        bool latencyModeled,
        long simulatedLatencyMs,
        DateTimeOffset marketAsOfUtc,
        DateTimeOffset simulatedAtUtc,
        string reasonCode)
    {
        Validate(
            correlationId, clientOrderId, strategyId, strategyVersion, costModelVersion,
            simulationModelVersion, venueRuleVersion, symbol, intendedQuantity, state,
            executedQuantity, averagePrice, feeAmount, feeRole, latencyModeled,
            simulatedLatencyMs, marketAsOfUtc, simulatedAtUtc, reasonCode);

        var draft = new ExecutionSimulationFillV1(
            Schema,
            correlationId,
            clientOrderId,
            strategyId,
            strategyVersion,
            costModelVersion,
            simulationModelVersion,
            venueRuleVersion,
            symbol,
            side,
            reduceOnly,
            orderType,
            intendedQuantity,
            state,
            executedQuantity,
            averagePrice,
            feeAmount,
            feeRole,
            latencyModeled,
            simulatedLatencyMs,
            marketAsOfUtc.ToUniversalTime(),
            simulatedAtUtc.ToUniversalTime(),
            reasonCode,
            Array.Empty<byte>(),
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(ExecutionSimulationFillV1 value)
    {
        if (value.Schema != Schema || value.CanonicalBytes.Length == 0 || value.CanonicalSha256.Length != 64)
            return false;
        try
        {
            Validate(
                value.CorrelationId, value.ClientOrderId, value.StrategyId, value.StrategyVersion,
                value.CostModelVersion, value.SimulationModelVersion, value.VenueRuleVersion,
                value.Symbol, value.IntendedQuantity, value.State, value.ExecutedQuantity,
                value.AveragePrice, value.FeeAmount, value.FeeRole, value.LatencyModeled,
                value.SimulatedLatencyMs, value.MarketAsOfUtc, value.SimulatedAtUtc, value.ReasonCode);
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

    private static void Validate(
        string correlationId,
        string clientOrderId,
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string venueRuleVersion,
        string symbol,
        decimal intendedQuantity,
        ExecutionSimulationFillStateV1 state,
        decimal executedQuantity,
        decimal averagePrice,
        decimal feeAmount,
        ExecutionSimulationFeeRoleV1 feeRole,
        bool latencyModeled,
        long simulatedLatencyMs,
        DateTimeOffset marketAsOfUtc,
        DateTimeOffset simulatedAtUtc,
        string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("Correlation id is required.");
        if (string.IsNullOrWhiteSpace(clientOrderId)) throw new ArgumentException("Client order id is required.");
        if (string.IsNullOrWhiteSpace(strategyId)) throw new ArgumentException("Strategy id is required.");
        if (string.IsNullOrWhiteSpace(strategyVersion)) throw new ArgumentException("Strategy version is required.");
        if (string.IsNullOrWhiteSpace(costModelVersion)) throw new ArgumentException("Cost model version is required.");
        if (string.IsNullOrWhiteSpace(simulationModelVersion)) throw new ArgumentException("Simulation model version is required.");
        if (string.IsNullOrWhiteSpace(venueRuleVersion)) throw new ArgumentException("Venue rule version is required.");
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol is required.");
        if (intendedQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(intendedQuantity));
        if (executedQuantity < 0 || executedQuantity > intendedQuantity)
            throw new InvalidOperationException("Simulated executed quantity is outside the intended quantity.");
        if (feeAmount < 0) throw new InvalidOperationException("Simulated fee cannot be negative.");
        if (simulatedLatencyMs < 0) throw new InvalidOperationException("Simulated latency cannot be negative.");
        if (marketAsOfUtc == default || simulatedAtUtc == default)
            throw new ArgumentException("Simulation timestamps are required.");
        if (marketAsOfUtc.Offset != TimeSpan.Zero || simulatedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Simulation timestamps must be UTC.");
        if (marketAsOfUtc > simulatedAtUtc)
            throw new InvalidOperationException("Simulation cannot use market evidence from the future.");
        if (string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("Simulation reason code is required.");

        if (!latencyModeled && simulatedLatencyMs != 0)
            throw new InvalidOperationException("Unavailable latency modeling cannot carry a latency value.");

        switch (state)
        {
            case ExecutionSimulationFillStateV1.Filled:
                if (executedQuantity != intendedQuantity)
                    throw new InvalidOperationException("Filled simulation must execute the full intended quantity.");
                if (averagePrice <= 0)
                    throw new InvalidOperationException("Filled simulation requires a positive average price.");
                break;
            case ExecutionSimulationFillStateV1.Partial:
                if (executedQuantity <= 0 || executedQuantity >= intendedQuantity)
                    throw new InvalidOperationException("Partial simulation requires a strict partial quantity.");
                if (averagePrice <= 0)
                    throw new InvalidOperationException("Partial simulation requires a positive average price.");
                break;
            case ExecutionSimulationFillStateV1.NotFilled:
                if (executedQuantity != 0 || averagePrice != 0 || feeAmount != 0)
                    throw new InvalidOperationException("Not-filled simulation cannot carry fill economics.");
                if (feeRole != ExecutionSimulationFeeRoleV1.Unavailable)
                    throw new InvalidOperationException("Not-filled simulation cannot claim fee-role evidence.");
                break;
            case ExecutionSimulationFillStateV1.Unsupported:
                if (executedQuantity != 0 || averagePrice != 0 || feeAmount != 0 || simulatedLatencyMs != 0)
                    throw new InvalidOperationException("Unsupported simulation cannot invent fill or latency economics.");
                if (feeRole != ExecutionSimulationFeeRoleV1.Unavailable || latencyModeled)
                    throw new InvalidOperationException("Unsupported simulation cannot claim fee or latency modeling.");
                break;
            default:
                throw new InvalidOperationException("Unsupported simulation state.");
        }

        if (executedQuantity == 0 && feeRole != ExecutionSimulationFeeRoleV1.Unavailable)
            throw new InvalidOperationException("Unfilled simulation cannot claim maker/taker fee role.");
        if (feeRole == ExecutionSimulationFeeRoleV1.Unavailable && feeAmount != 0)
            throw new InvalidOperationException("Unavailable simulated fee evidence cannot carry a fee.");
    }

    private static byte[] Serialize(ExecutionSimulationFillV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("average_price", value.AveragePrice);
            writer.WriteString("client_order_id", value.ClientOrderId);
            writer.WriteString("correlation_id", value.CorrelationId);
            writer.WriteString("cost_model_version", value.CostModelVersion);
            writer.WriteNumber("executed_quantity", value.ExecutedQuantity);
            writer.WriteNumber("fee_amount", value.FeeAmount);
            writer.WriteString("fee_role", value.FeeRole.ToString());
            writer.WriteNumber("intended_quantity", value.IntendedQuantity);
            writer.WriteBoolean("latency_modeled", value.LatencyModeled);
            writer.WriteString("market_as_of_utc", value.MarketAsOfUtc.ToUniversalTime());
            writer.WriteString("order_type", value.OrderType.ToString());
            writer.WriteBoolean("reduce_only", value.ReduceOnly);
            writer.WriteString("reason_code", value.ReasonCode);
            writer.WriteString("schema", value.Schema);
            writer.WriteNumber("simulated_latency_ms", value.SimulatedLatencyMs);
            writer.WriteString("simulated_at_utc", value.SimulatedAtUtc.ToUniversalTime());
            writer.WriteString("simulation_model_version", value.SimulationModelVersion);
            writer.WriteString("side", value.Side.ToString());
            writer.WriteString("state", value.State.ToString());
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteString("symbol", value.Symbol);
            writer.WriteString("venue_rule_version", value.VenueRuleVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

public sealed record ExecutionSimulationComparisonV1(
    string Schema,
    string CorrelationId,
    string ClientOrderId,
    string StrategyId,
    string StrategyVersion,
    string CostModelVersion,
    string SimulationModelVersion,
    string VenueRuleVersion,
    string Symbol,
    PositionSide Side,
    bool ReduceOnly,
    ExecutionOrderType OrderType,
    ExecutionSimulationFillStateV1 SimulatedState,
    ExecutionRealityStateV1 ObservedState,
    bool StateMatch,
    decimal SimulatedFillRatio,
    decimal ObservedFillRatio,
    decimal FillRatioDelta,
    bool PriceComparable,
    decimal? PriceDriftBps,
    bool FeeComparable,
    decimal? FeeDriftBps,
    bool LatencyComparable,
    long? LatencyDriftMs,
    bool TotalComparable,
    decimal? TotalExecutionDriftBps,
    string ReasonCode,
    string SimulatedCanonicalSha256,
    string ObservedCanonicalSha256,
    DateTimeOffset ComparedAtUtc,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class ExecutionSimulationComparisonCanonicalizerV1
{
    public const string Schema = "wpe.execution-simulation-comparison/1.0";

    public static ExecutionSimulationComparisonV1 Create(
        ExecutionSimulationFillV1 simulated,
        ExecutionRealityDriftFactV1 observed,
        DateTimeOffset comparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(simulated);
        ArgumentNullException.ThrowIfNull(observed);
        if (!ExecutionSimulationFillCanonicalizerV1.IsCanonical(simulated))
            throw new InvalidOperationException("Simulation evidence is not canonical.");
        if (!ExecutionRealityDriftV1.IsCanonical(observed))
            throw new InvalidOperationException("Observed execution reality evidence is not canonical.");
        if (comparedAtUtc == default || comparedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Comparison time must be UTC.", nameof(comparedAtUtc));
        if (comparedAtUtc < simulated.SimulatedAtUtc || comparedAtUtc < observed.ObservedAtUtc)
            throw new InvalidOperationException("Comparison cannot predate its evidence.");

        RequireIdentity(simulated, observed);

        var simulatedFillRatio = simulated.ExecutedQuantity / simulated.IntendedQuantity;
        var observedFillRatio = observed.FillRatio;
        var priceComparable = simulated.State is ExecutionSimulationFillStateV1.Filled or ExecutionSimulationFillStateV1.Partial
            && observed.Comparable;
        var simulatedFeeComparable = priceComparable && simulated.FeeRole != ExecutionSimulationFeeRoleV1.Unavailable;
        var feeComparable = simulatedFeeComparable && observed.FeeComparable;
        var latencyComparable = simulated.LatencyModeled;
        var totalComparable = priceComparable && feeComparable;

        decimal? priceDrift = null;
        if (priceComparable)
            priceDrift = DirectionalPriceDriftBps(simulated, observed.AveragePrice);

        decimal? feeDrift = null;
        if (feeComparable)
        {
            var simulatedRate = simulated.FeeAmount / (simulated.AveragePrice * simulated.ExecutedQuantity) * 10000m;
            feeDrift = observed.ObservedFeeRateBps - simulatedRate;
        }

        var stateMatch = StateMatches(simulated.State, observed.State, observed.Terminal);
        var reason = simulated.State == ExecutionSimulationFillStateV1.Unsupported
            ? "simulation-unsupported"
            : !priceComparable
                ? "price-not-comparable"
                : !feeComparable
                    ? "fee-not-comparable"
                    : stateMatch
                        ? "comparable"
                        : "fill-state-drift";

        var draft = new ExecutionSimulationComparisonV1(
            Schema,
            simulated.CorrelationId,
            simulated.ClientOrderId,
            simulated.StrategyId,
            simulated.StrategyVersion,
            simulated.CostModelVersion,
            simulated.SimulationModelVersion,
            simulated.VenueRuleVersion,
            simulated.Symbol,
            simulated.Side,
            simulated.ReduceOnly,
            simulated.OrderType,
            simulated.State,
            observed.State,
            stateMatch,
            simulatedFillRatio,
            observedFillRatio,
            observedFillRatio - simulatedFillRatio,
            priceComparable,
            priceDrift,
            feeComparable,
            feeDrift,
            latencyComparable,
            latencyComparable ? observed.ObservationLatencyMs - simulated.SimulatedLatencyMs : null,
            totalComparable,
            totalComparable ? priceDrift + feeDrift : null,
            reason,
            simulated.CanonicalSha256,
            observed.CanonicalSha256,
            comparedAtUtc.ToUniversalTime(),
            Array.Empty<byte>(),
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(ExecutionSimulationComparisonV1 value)
    {
        if (value.Schema != Schema || value.CanonicalBytes.Length == 0 || value.CanonicalSha256.Length != 64)
            return false;
        var bytes = Serialize(value);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(hash, value.CanonicalSha256, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes);
    }

    private static void RequireIdentity(ExecutionSimulationFillV1 simulated, ExecutionRealityDriftFactV1 observed)
    {
        if (!string.Equals(simulated.CorrelationId, observed.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(simulated.ClientOrderId, observed.ClientOrderId, StringComparison.Ordinal)
            || !string.Equals(simulated.StrategyId, observed.StrategyId, StringComparison.Ordinal)
            || !string.Equals(simulated.StrategyVersion, observed.StrategyVersion, StringComparison.Ordinal)
            || !string.Equals(simulated.CostModelVersion, observed.CostModelVersion, StringComparison.Ordinal)
            || !string.Equals(simulated.Symbol, observed.Symbol, StringComparison.Ordinal)
            || simulated.Side != observed.Side
            || simulated.ReduceOnly != observed.ReduceOnly
            || simulated.OrderType != observed.OrderType
            || simulated.IntendedQuantity != observed.IntendedQuantity)
            throw new InvalidOperationException("Simulation and observed execution identities do not match.");
    }

    private static decimal DirectionalPriceDriftBps(ExecutionSimulationFillV1 simulated, decimal observedPrice)
    {
        var buy = (!simulated.ReduceOnly && simulated.Side == PositionSide.Long)
            || (simulated.ReduceOnly && simulated.Side == PositionSide.Short);
        var delta = buy ? observedPrice - simulated.AveragePrice : simulated.AveragePrice - observedPrice;
        return delta / simulated.AveragePrice * 10000m;
    }

    private static bool StateMatches(
        ExecutionSimulationFillStateV1 simulated,
        ExecutionRealityStateV1 observed,
        bool observedTerminal) => simulated switch
    {
        ExecutionSimulationFillStateV1.Filled => observed == ExecutionRealityStateV1.Filled,
        ExecutionSimulationFillStateV1.Partial => observed == ExecutionRealityStateV1.Partial,
        ExecutionSimulationFillStateV1.NotFilled => observed is ExecutionRealityStateV1.NotFilled
            || (observed == ExecutionRealityStateV1.Open && !observedTerminal),
        ExecutionSimulationFillStateV1.Unsupported => false,
        _ => false
    };

    private static byte[] Serialize(ExecutionSimulationComparisonV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("client_order_id", value.ClientOrderId);
            writer.WriteString("compared_at_utc", value.ComparedAtUtc.ToUniversalTime());
            writer.WriteString("correlation_id", value.CorrelationId);
            writer.WriteString("cost_model_version", value.CostModelVersion);
            WriteDecimalOrNull(writer, "fee_drift_bps", value.FeeDriftBps);
            writer.WriteBoolean("fee_comparable", value.FeeComparable);
            writer.WriteNumber("fill_ratio_delta", value.FillRatioDelta);
            WriteLongOrNull(writer, "latency_drift_ms", value.LatencyDriftMs);
            writer.WriteBoolean("latency_comparable", value.LatencyComparable);
            writer.WriteString("observed_canonical_sha256", value.ObservedCanonicalSha256);
            writer.WriteNumber("observed_fill_ratio", value.ObservedFillRatio);
            writer.WriteString("observed_state", value.ObservedState.ToString());
            writer.WriteString("order_type", value.OrderType.ToString());
            WriteDecimalOrNull(writer, "price_drift_bps", value.PriceDriftBps);
            writer.WriteBoolean("price_comparable", value.PriceComparable);
            writer.WriteBoolean("reduce_only", value.ReduceOnly);
            writer.WriteString("reason_code", value.ReasonCode);
            writer.WriteString("schema", value.Schema);
            writer.WriteString("side", value.Side.ToString());
            writer.WriteString("simulated_canonical_sha256", value.SimulatedCanonicalSha256);
            writer.WriteNumber("simulated_fill_ratio", value.SimulatedFillRatio);
            writer.WriteString("simulated_state", value.SimulatedState.ToString());
            writer.WriteString("simulation_model_version", value.SimulationModelVersion);
            writer.WriteBoolean("state_match", value.StateMatch);
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteString("symbol", value.Symbol);
            writer.WriteBoolean("total_comparable", value.TotalComparable);
            WriteDecimalOrNull(writer, "total_execution_drift_bps", value.TotalExecutionDriftBps);
            writer.WriteString("venue_rule_version", value.VenueRuleVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteDecimalOrNull(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value);
    }

    private static void WriteLongOrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value);
    }
}

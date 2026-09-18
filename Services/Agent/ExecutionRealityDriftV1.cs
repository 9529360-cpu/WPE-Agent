using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum ExecutionRealityStateV1 { Filled, Partial, Open, NotFilled, Unknown }

public sealed record ExecutionRealityExpectationV1(
    string CorrelationId,
    string ClientOrderId,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    PositionSide Side,
    bool ReduceOnly,
    ExecutionOrderType OrderType,
    decimal Quantity,
    decimal ExpectedPrice,
    decimal ExpectedCommissionRate,
    decimal ExpectedSlippageRate,
    DateTimeOffset IntendedAtUtc);

public sealed record ExecutionRealityObservationV1(
    string ClientOrderId,
    string Status,
    decimal ExecutedQuantity,
    decimal AveragePrice,
    decimal ObservedFee,
    string FeeBasis,
    DateTimeOffset ExchangeUpdatedAtUtc,
    DateTimeOffset ObservedAtUtc);

public sealed record ExecutionRealityDriftFactV1(
    string Schema,
    string CorrelationId,
    string ClientOrderId,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    PositionSide Side,
    bool ReduceOnly,
    ExecutionOrderType OrderType,
    decimal IntendedQuantity,
    decimal ExecutedQuantity,
    decimal ExpectedPrice,
    decimal AveragePrice,
    string ExchangeStatus,
    ExecutionRealityStateV1 State,
    bool Terminal,
    bool Comparable,
    decimal FillRatio,
    decimal AdverseSlippageBps,
    decimal ExpectedSlippageBps,
    decimal SlippageDriftBps,
    decimal ObservedFee,
    string FeeBasis,
    bool FeeComparable,
    decimal ObservedFeeRateBps,
    decimal ExpectedCommissionBps,
    decimal FeeDriftBps,
    bool TotalComparable,
    decimal TotalExecutionDriftBps,
    long ObservationLatencyMs,
    DateTimeOffset ExchangeUpdatedAtUtc,
    DateTimeOffset ObservedAtUtc,
    string ReasonCode,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class ExecutionRealityDriftV1
{
    public const string Schema = "wpe.execution-reality-drift/1.0";
    public const string ExchangeReportedFeeBasis = "exchange-reported";
    public const string UnavailableFeeBasis = "unavailable";

    private static readonly HashSet<string> OpenStatuses = new(StringComparer.Ordinal)
    {
        "NEW", "PENDING_NEW", "ACCEPTED", "PENDING", "OPEN"
    };

    private static readonly HashSet<string> TerminalNoFillStatuses = new(StringComparer.Ordinal)
    {
        "CANCELED", "CANCELLED", "REJECTED", "EXPIRED", "EXPIRED_IN_MATCH"
    };

    public static ExecutionRealityDriftFactV1 Analyze(ExecutionRealityExpectationV1 expected, ExecutionRealityObservationV1 observed)
    {
        Validate(expected, observed);
        var status = observed.Status.Trim().ToUpperInvariant();
        var terminal = status == "FILLED" || TerminalNoFillStatuses.Contains(status);
        var state = Classify(status, observed.ExecutedQuantity, expected.Quantity);
        var comparable = state is ExecutionRealityStateV1.Filled or ExecutionRealityStateV1.Partial;
        var feeComparable = comparable && observed.FeeBasis == ExchangeReportedFeeBasis;
        var totalComparable = comparable && feeComparable;
        var fillRatio = observed.ExecutedQuantity / expected.Quantity;
        var expectedSlippageBps = expected.ExpectedSlippageRate * 10000m;
        var expectedCommissionBps = expected.ExpectedCommissionRate * 10000m;
        var adverseSlippageBps = comparable ? AdverseSlippageBps(expected, observed.AveragePrice) : 0m;
        var observedFeeRateBps = feeComparable
            ? observed.ObservedFee / (observed.AveragePrice * observed.ExecutedQuantity) * 10000m
            : 0m;
        var slippageDriftBps = comparable ? adverseSlippageBps - expectedSlippageBps : 0m;
        var feeDriftBps = feeComparable ? observedFeeRateBps - expectedCommissionBps : 0m;
        var latencyMs = checked((long)Math.Round(
            (observed.ObservedAtUtc - expected.IntendedAtUtc).TotalMilliseconds,
            MidpointRounding.AwayFromZero));

        var draft = new ExecutionRealityDriftFactV1(
            Schema,
            expected.CorrelationId,
            expected.ClientOrderId,
            expected.StrategyId,
            expected.StrategyVersion,
            expected.Symbol,
            expected.Side,
            expected.ReduceOnly,
            expected.OrderType,
            expected.Quantity,
            observed.ExecutedQuantity,
            expected.ExpectedPrice,
            observed.AveragePrice,
            status,
            state,
            terminal,
            comparable,
            fillRatio,
            adverseSlippageBps,
            expectedSlippageBps,
            slippageDriftBps,
            observed.ObservedFee,
            observed.FeeBasis,
            feeComparable,
            observedFeeRateBps,
            expectedCommissionBps,
            feeDriftBps,
            totalComparable,
            totalComparable ? slippageDriftBps + feeDriftBps : 0m,
            latencyMs,
            observed.ExchangeUpdatedAtUtc.ToUniversalTime(),
            observed.ObservedAtUtc.ToUniversalTime(),
            Reason(state, terminal, feeComparable),
            Array.Empty<byte>(),
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(ExecutionRealityDriftFactV1 value)
    {
        if (value.Schema != Schema || value.CanonicalBytes.Length == 0 || value.CanonicalSha256.Length != 64)
            return false;

        var bytes = Serialize(value);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(hash, value.CanonicalSha256, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes);
    }

    private static void Validate(ExecutionRealityExpectationV1 expected, ExecutionRealityObservationV1 observed)
    {
        if (string.IsNullOrWhiteSpace(expected.CorrelationId)) throw new ArgumentException("Correlation id is required.", nameof(expected));
        if (string.IsNullOrWhiteSpace(expected.ClientOrderId)) throw new ArgumentException("Client order id is required.", nameof(expected));
        if (string.IsNullOrWhiteSpace(expected.StrategyId)) throw new ArgumentException("Strategy id is required.", nameof(expected));
        if (string.IsNullOrWhiteSpace(expected.StrategyVersion)) throw new ArgumentException("Strategy version is required.", nameof(expected));
        if (string.IsNullOrWhiteSpace(expected.Symbol)) throw new ArgumentException("Symbol is required.", nameof(expected));
        if (expected.Quantity <= 0) throw new ArgumentOutOfRangeException(nameof(expected), "Intended quantity must be positive.");
        if (expected.ExpectedPrice <= 0) throw new ArgumentOutOfRangeException(nameof(expected), "Expected price must be positive.");
        if (expected.ExpectedCommissionRate < 0 || expected.ExpectedCommissionRate >= 1) throw new ArgumentOutOfRangeException(nameof(expected), "Expected commission rate is invalid.");
        if (expected.ExpectedSlippageRate < 0 || expected.ExpectedSlippageRate >= 1) throw new ArgumentOutOfRangeException(nameof(expected), "Expected slippage rate is invalid.");
        if (expected.IntendedAtUtc == default) throw new ArgumentException("Intent time is required.", nameof(expected));
        if (string.IsNullOrWhiteSpace(observed.ClientOrderId)) throw new ArgumentException("Observed client order id is required.", nameof(observed));
        if (!string.Equals(expected.ClientOrderId, observed.ClientOrderId, StringComparison.Ordinal))
            throw new InvalidOperationException("Execution observation does not match the intended client order id.");
        if (string.IsNullOrWhiteSpace(observed.Status)) throw new ArgumentException("Exchange status is required.", nameof(observed));
        if (observed.FeeBasis is not ExchangeReportedFeeBasis and not UnavailableFeeBasis)
            throw new InvalidOperationException("Execution fee basis is unsupported.");
        if (observed.FeeBasis == UnavailableFeeBasis && observed.ObservedFee != 0)
            throw new InvalidOperationException("Unavailable fee evidence cannot carry an observed fee.");
        if (observed.ExecutedQuantity < 0 || observed.ExecutedQuantity > expected.Quantity)
            throw new InvalidOperationException("Executed quantity is outside the intended quantity.");
        if (observed.ExecutedQuantity == 0 && observed.AveragePrice != 0)
            throw new InvalidOperationException("Unfilled execution cannot have an average fill price.");
        if (observed.ExecutedQuantity > 0 && observed.AveragePrice <= 0)
            throw new InvalidOperationException("Filled execution requires a positive average fill price.");
        if (observed.ObservedFee < 0)
            throw new InvalidOperationException("Observed fee cannot be negative in the current exchange fee evidence contract.");
        if (observed.FeeBasis == ExchangeReportedFeeBasis && observed.ExecutedQuantity <= 0)
            throw new InvalidOperationException("Exchange-reported fee evidence requires an executed quantity.");
        if (observed.ExchangeUpdatedAtUtc == default || observed.ObservedAtUtc == default)
            throw new ArgumentException("Execution timestamps are required.", nameof(observed));
        if (observed.ObservedAtUtc < expected.IntendedAtUtc)
            throw new InvalidOperationException("Execution observation predates the intended order.");
        var status = observed.Status.Trim().ToUpperInvariant();
        if (status == "FILLED" && observed.ExecutedQuantity != expected.Quantity)
            throw new InvalidOperationException("FILLED status must match the full intended quantity.");
        if (status == "PARTIALLY_FILLED" && (observed.ExecutedQuantity <= 0 || observed.ExecutedQuantity >= expected.Quantity))
            throw new InvalidOperationException("PARTIALLY_FILLED status requires a strict partial quantity.");
        if (status == "REJECTED" && observed.ExecutedQuantity > 0)
            throw new InvalidOperationException("REJECTED status cannot carry executed quantity.");
    }

    private static ExecutionRealityStateV1 Classify(string status, decimal executed, decimal intended)
    {
        if (status == "FILLED") return ExecutionRealityStateV1.Filled;
        if (status == "PARTIALLY_FILLED") return ExecutionRealityStateV1.Partial;
        if (TerminalNoFillStatuses.Contains(status))
            return executed > 0 && executed < intended ? ExecutionRealityStateV1.Partial : ExecutionRealityStateV1.NotFilled;
        if (OpenStatuses.Contains(status))
            return executed > 0 && executed < intended ? ExecutionRealityStateV1.Partial : ExecutionRealityStateV1.Open;
        return ExecutionRealityStateV1.Unknown;
    }

    private static decimal AdverseSlippageBps(ExecutionRealityExpectationV1 expected, decimal actualPrice)
    {
        var buy = (!expected.ReduceOnly && expected.Side == PositionSide.Long)
            || (expected.ReduceOnly && expected.Side == PositionSide.Short);
        var delta = buy ? actualPrice - expected.ExpectedPrice : expected.ExpectedPrice - actualPrice;
        return delta / expected.ExpectedPrice * 10000m;
    }

    private static string Reason(ExecutionRealityStateV1 state, bool terminal, bool feeComparable)
    {
        var reason = state switch
        {
            ExecutionRealityStateV1.Filled => "filled",
            ExecutionRealityStateV1.Partial when terminal => "terminal-partial-fill",
            ExecutionRealityStateV1.Partial => "partial-fill",
            ExecutionRealityStateV1.Open => "open-unfilled",
            ExecutionRealityStateV1.NotFilled => "terminal-no-fill",
            _ => "unknown-status"
        };
        return state is ExecutionRealityStateV1.Filled or ExecutionRealityStateV1.Partial && !feeComparable
            ? reason + "-fee-unavailable"
            : reason;
    }

    private static byte[] Serialize(ExecutionRealityDriftFactV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("adverse_slippage_bps", value.AdverseSlippageBps);
            writer.WriteNumber("average_price", value.AveragePrice);
            writer.WriteBoolean("comparable", value.Comparable);
            writer.WriteString("correlation_id", value.CorrelationId);
            writer.WriteString("exchange_status", value.ExchangeStatus);
            writer.WriteString("exchange_updated_at_utc", value.ExchangeUpdatedAtUtc.ToUniversalTime());
            writer.WriteNumber("executed_quantity", value.ExecutedQuantity);
            writer.WriteNumber("expected_commission_bps", value.ExpectedCommissionBps);
            writer.WriteNumber("expected_price", value.ExpectedPrice);
            writer.WriteNumber("expected_slippage_bps", value.ExpectedSlippageBps);
            writer.WriteString("fee_basis", value.FeeBasis);
            writer.WriteBoolean("fee_comparable", value.FeeComparable);
            writer.WriteNumber("fee_drift_bps", value.FeeDriftBps);
            writer.WriteNumber("fill_ratio", value.FillRatio);
            writer.WriteNumber("intended_quantity", value.IntendedQuantity);
            writer.WriteString("client_order_id", value.ClientOrderId);
            writer.WriteNumber("observation_latency_ms", value.ObservationLatencyMs);
            writer.WriteString("observed_at_utc", value.ObservedAtUtc.ToUniversalTime());
            writer.WriteNumber("observed_fee", value.ObservedFee);
            writer.WriteNumber("observed_fee_rate_bps", value.ObservedFeeRateBps);
            writer.WriteString("order_type", value.OrderType.ToString());
            writer.WriteBoolean("reduce_only", value.ReduceOnly);
            writer.WriteString("reason_code", value.ReasonCode);
            writer.WriteString("schema", value.Schema);
            writer.WriteString("side", value.Side.ToString());
            writer.WriteNumber("slippage_drift_bps", value.SlippageDriftBps);
            writer.WriteString("state", value.State.ToString());
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteString("symbol", value.Symbol);
            writer.WriteBoolean("terminal", value.Terminal);
            writer.WriteBoolean("total_comparable", value.TotalComparable);
            writer.WriteNumber("total_execution_drift_bps", value.TotalExecutionDriftBps);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

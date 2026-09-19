using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum PostTradePnlDriftStateV1
{
    GrossComparable,
    FeeAdjustedComparable
}

public sealed record PostTradePnlSimulationSourceV1(
    int Sequence,
    string CorrelationId,
    string ClientOrderId,
    bool ReduceOnly,
    string SimulationSourceSha256,
    string SimulatedFillSha256);

public sealed record PostTradePnlDriftV1(
    string Schema,
    string CloseClientOrderId,
    string CloseCycleId,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    PositionSide Side,
    decimal ClosingQuantity,
    string EntryLedgerSha256,
    IReadOnlyList<PostTradePnlSimulationSourceV1> EntrySimulationSources,
    string CloseSimulationSourceSha256,
    string CloseSimulationSha256,
    decimal ActualEntryPrice,
    decimal ActualExitPrice,
    decimal ActualGrossPnl,
    decimal ActualFees,
    string ActualFeeBasis,
    decimal ActualFundingAmount,
    string ActualFundingBasis,
    decimal ActualNetPnl,
    decimal SimulatedEntryPrice,
    decimal SimulatedExitPrice,
    bool SimulatedFeeEvidenceComplete,
    decimal SimulatedFees,
    decimal SimulatedGrossPnl,
    decimal SimulatedPnlBeforeFunding,
    decimal ObservedMinusSimulatedGrossPnl,
    bool FeeAdjustedPnlComparable,
    decimal? ObservedMinusSimulatedFeeAdjustedPnl,
    bool NetPnlComparable,
    decimal? ObservedMinusSimulatedNetPnl,
    PostTradePnlDriftStateV1 State,
    string ReasonCode,
    DateTimeOffset ComparedAtUtc,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class PostTradePnlDriftCanonicalizerV1
{
    public const string Schema = "wpe.post-trade-pnl-drift/1.1";

    public static PostTradePnlDriftV1 Create(
        string closeClientOrderId,
        string closeCycleId,
        string strategyId,
        string strategyVersion,
        string symbol,
        PositionSide side,
        decimal closingQuantity,
        string entryLedgerSha256,
        IReadOnlyList<PostTradePnlSimulationSourceV1> entrySimulationSources,
        string closeSimulationSourceSha256,
        string closeSimulationSha256,
        decimal actualEntryPrice,
        decimal actualExitPrice,
        decimal actualGrossPnl,
        decimal actualFees,
        string actualFeeBasis,
        decimal actualFundingAmount,
        string actualFundingBasis,
        decimal actualNetPnl,
        decimal simulatedEntryPrice,
        decimal simulatedExitPrice,
        bool simulatedFeeEvidenceComplete,
        decimal simulatedFees,
        DateTimeOffset comparedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(closeClientOrderId)
            || string.IsNullOrWhiteSpace(closeCycleId)
            || string.IsNullOrWhiteSpace(strategyId)
            || string.IsNullOrWhiteSpace(strategyVersion)
            || string.IsNullOrWhiteSpace(symbol)
            || string.IsNullOrWhiteSpace(actualFeeBasis)
            || string.IsNullOrWhiteSpace(actualFundingBasis))
            throw new ArgumentException("Post-trade PnL drift identity is incomplete.");
        if (closingQuantity <= 0
            || actualEntryPrice <= 0
            || actualExitPrice <= 0
            || simulatedEntryPrice <= 0
            || simulatedExitPrice <= 0
            || actualFees < 0
            || simulatedFees < 0)
            throw new InvalidOperationException("Post-trade PnL drift economics are invalid.");
        if (!LowerSha(entryLedgerSha256)
            || !LowerSha(closeSimulationSourceSha256)
            || !LowerSha(closeSimulationSha256))
            throw new InvalidOperationException("Post-trade PnL drift source hash is invalid.");
        if (comparedAtUtc == default || comparedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Post-trade PnL drift comparison time must be UTC.");

        ArgumentNullException.ThrowIfNull(entrySimulationSources);
        var sources = entrySimulationSources.OrderBy(x => x.Sequence).ToArray();
        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            if (source.Sequence != i
                || string.IsNullOrWhiteSpace(source.CorrelationId)
                || string.IsNullOrWhiteSpace(source.ClientOrderId)
                || !LowerSha(source.SimulationSourceSha256)
                || !LowerSha(source.SimulatedFillSha256))
                throw new InvalidOperationException("Post-trade PnL simulation source identity is invalid.");
        }

        var expectedActualGross = side == PositionSide.Long
            ? (actualExitPrice - actualEntryPrice) * closingQuantity
            : (actualEntryPrice - actualExitPrice) * closingQuantity;
        if (expectedActualGross != actualGrossPnl)
            throw new InvalidOperationException("Actual gross PnL does not replay from the persisted trade economics.");

        var expectedActualNet = actualGrossPnl - actualFees + actualFundingAmount;
        if (expectedActualNet != actualNetPnl)
            throw new InvalidOperationException("Actual net PnL does not replay from the persisted trade economics.");

        var simulatedGross = side == PositionSide.Long
            ? (simulatedExitPrice - simulatedEntryPrice) * closingQuantity
            : (simulatedEntryPrice - simulatedExitPrice) * closingQuantity;
        var simulatedBeforeFunding = simulatedGross - simulatedFees;
        var grossDelta = actualGrossPnl - simulatedGross;
        var feeComparable = simulatedFeeEvidenceComplete
            && string.Equals(actualFeeBasis, "exchange-reported-usdt", StringComparison.Ordinal);
        var feeAdjustedDelta = feeComparable
            ? (actualGrossPnl - actualFees) - simulatedBeforeFunding
            : (decimal?)null;

        // Funding is intentionally not modeled by execution simulation yet. Do not invent zero funding.
        const bool netComparable = false;
        decimal? netDelta = null;
        var state = feeComparable
            ? PostTradePnlDriftStateV1.FeeAdjustedComparable
            : PostTradePnlDriftStateV1.GrossComparable;
        var reason = feeComparable
            ? "fee-adjusted-pnl-comparable-funding-unmodeled"
            : "gross-pnl-comparable-fee-or-funding-unavailable";

        var draft = new PostTradePnlDriftV1(
            Schema,
            closeClientOrderId,
            closeCycleId,
            strategyId,
            strategyVersion,
            symbol,
            side,
            closingQuantity,
            entryLedgerSha256,
            sources,
            closeSimulationSourceSha256,
            closeSimulationSha256,
            actualEntryPrice,
            actualExitPrice,
            actualGrossPnl,
            actualFees,
            actualFeeBasis,
            actualFundingAmount,
            actualFundingBasis,
            actualNetPnl,
            simulatedEntryPrice,
            simulatedExitPrice,
            simulatedFeeEvidenceComplete,
            simulatedFees,
            simulatedGross,
            simulatedBeforeFunding,
            grossDelta,
            feeComparable,
            feeAdjustedDelta,
            netComparable,
            netDelta,
            state,
            reason,
            comparedAtUtc,
            Array.Empty<byte>(),
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(PostTradePnlDriftV1? value)
    {
        if (value is null
            || value.Schema != Schema
            || value.CanonicalBytes.Length == 0
            || !LowerSha(value.CanonicalSha256))
            return false;
        try
        {
            var replay = Create(
                value.CloseClientOrderId,
                value.CloseCycleId,
                value.StrategyId,
                value.StrategyVersion,
                value.Symbol,
                value.Side,
                value.ClosingQuantity,
                value.EntryLedgerSha256,
                value.EntrySimulationSources,
                value.CloseSimulationSourceSha256,
                value.CloseSimulationSha256,
                value.ActualEntryPrice,
                value.ActualExitPrice,
                value.ActualGrossPnl,
                value.ActualFees,
                value.ActualFeeBasis,
                value.ActualFundingAmount,
                value.ActualFundingBasis,
                value.ActualNetPnl,
                value.SimulatedEntryPrice,
                value.SimulatedExitPrice,
                value.SimulatedFeeEvidenceComplete,
                value.SimulatedFees,
                value.ComparedAtUtc);
            return replay.State == value.State
                && replay.SimulatedGrossPnl == value.SimulatedGrossPnl
                && replay.SimulatedPnlBeforeFunding == value.SimulatedPnlBeforeFunding
                && replay.ObservedMinusSimulatedGrossPnl == value.ObservedMinusSimulatedGrossPnl
                && replay.FeeAdjustedPnlComparable == value.FeeAdjustedPnlComparable
                && replay.ObservedMinusSimulatedFeeAdjustedPnl == value.ObservedMinusSimulatedFeeAdjustedPnl
                && replay.NetPnlComparable == value.NetPnlComparable
                && replay.ObservedMinusSimulatedNetPnl == value.ObservedMinusSimulatedNetPnl
                && replay.ReasonCode == value.ReasonCode
                && string.Equals(replay.CanonicalSha256, value.CanonicalSha256, StringComparison.Ordinal)
                && CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes, value.CanonicalBytes);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> canonicalBytes,
        string canonicalSha256,
        out PostTradePnlDriftV1? value)
    {
        value = null;
        if (canonicalBytes.IsEmpty || !LowerSha(canonicalSha256))
            return false;
        try
        {
            var bytes = canonicalBytes.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, canonicalSha256, StringComparison.Ordinal))
                return false;

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var sources = root.GetProperty("entry_simulation_sources")
                .EnumerateArray()
                .Select(item => new PostTradePnlSimulationSourceV1(
                    item.GetProperty("sequence").GetInt32(),
                    Required(item, "correlation_id"),
                    Required(item, "client_order_id"),
                    item.GetProperty("reduce_only").GetBoolean(),
                    Required(item, "simulation_source_sha256"),
                    Required(item, "simulated_fill_sha256")))
                .ToArray();

            value = new(
                Required(root, "schema"),
                Required(root, "close_client_order_id"),
                Required(root, "close_cycle_id"),
                Required(root, "strategy_id"),
                Required(root, "strategy_version"),
                Required(root, "symbol"),
                Enum.Parse<PositionSide>(Required(root, "side"), false),
                root.GetProperty("closing_quantity").GetDecimal(),
                Required(root, "entry_ledger_sha256"),
                sources,
                Required(root, "close_simulation_source_sha256"),
                Required(root, "close_simulation_sha256"),
                root.GetProperty("actual_entry_price").GetDecimal(),
                root.GetProperty("actual_exit_price").GetDecimal(),
                root.GetProperty("actual_gross_pnl").GetDecimal(),
                root.GetProperty("actual_fees").GetDecimal(),
                Required(root, "actual_fee_basis"),
                root.GetProperty("actual_funding_amount").GetDecimal(),
                Required(root, "actual_funding_basis"),
                root.GetProperty("actual_net_pnl").GetDecimal(),
                root.GetProperty("simulated_entry_price").GetDecimal(),
                root.GetProperty("simulated_exit_price").GetDecimal(),
                root.GetProperty("simulated_fee_evidence_complete").GetBoolean(),
                root.GetProperty("simulated_fees").GetDecimal(),
                root.GetProperty("simulated_gross_pnl").GetDecimal(),
                root.GetProperty("simulated_pnl_before_funding").GetDecimal(),
                root.GetProperty("observed_minus_simulated_gross_pnl").GetDecimal(),
                root.GetProperty("fee_adjusted_pnl_comparable").GetBoolean(),
                NullableDecimal(root, "observed_minus_simulated_fee_adjusted_pnl"),
                root.GetProperty("net_pnl_comparable").GetBoolean(),
                NullableDecimal(root, "observed_minus_simulated_net_pnl"),
                Enum.Parse<PostTradePnlDriftStateV1>(Required(root, "state"), false),
                Required(root, "reason_code"),
                root.GetProperty("compared_at_utc").GetDateTimeOffset(),
                bytes,
                canonicalSha256);

            return IsCanonical(value);
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static byte[] Serialize(PostTradePnlDriftV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("actual_entry_price", value.ActualEntryPrice);
            writer.WriteString("actual_fee_basis", value.ActualFeeBasis);
            writer.WriteNumber("actual_fees", value.ActualFees);
            writer.WriteNumber("actual_funding_amount", value.ActualFundingAmount);
            writer.WriteString("actual_funding_basis", value.ActualFundingBasis);
            writer.WriteNumber("actual_gross_pnl", value.ActualGrossPnl);
            writer.WriteNumber("actual_net_pnl", value.ActualNetPnl);
            writer.WriteNumber("actual_exit_price", value.ActualExitPrice);
            writer.WriteString("close_client_order_id", value.CloseClientOrderId);
            writer.WriteString("close_cycle_id", value.CloseCycleId);
            writer.WriteString("close_simulation_source_sha256", value.CloseSimulationSourceSha256);
            writer.WriteString("close_simulation_sha256", value.CloseSimulationSha256);
            writer.WriteNumber("closing_quantity", value.ClosingQuantity);
            writer.WriteString("compared_at_utc", value.ComparedAtUtc.ToUniversalTime());
            writer.WriteString("entry_ledger_sha256", value.EntryLedgerSha256);
            writer.WritePropertyName("entry_simulation_sources");
            writer.WriteStartArray();
            foreach (var source in value.EntrySimulationSources.OrderBy(x => x.Sequence))
            {
                writer.WriteStartObject();
                writer.WriteString("client_order_id", source.ClientOrderId);
                writer.WriteString("correlation_id", source.CorrelationId);
                writer.WriteBoolean("reduce_only", source.ReduceOnly);
                writer.WriteNumber("sequence", source.Sequence);
                writer.WriteString("simulated_fill_sha256", source.SimulatedFillSha256);
                writer.WriteString("simulation_source_sha256", source.SimulationSourceSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("fee_adjusted_pnl_comparable", value.FeeAdjustedPnlComparable);
            writer.WriteBoolean("net_pnl_comparable", value.NetPnlComparable);
            if (value.ObservedMinusSimulatedFeeAdjustedPnl is null)
                writer.WriteNull("observed_minus_simulated_fee_adjusted_pnl");
            else
                writer.WriteNumber("observed_minus_simulated_fee_adjusted_pnl", value.ObservedMinusSimulatedFeeAdjustedPnl.Value);
            writer.WriteNumber("observed_minus_simulated_gross_pnl", value.ObservedMinusSimulatedGrossPnl);
            if (value.ObservedMinusSimulatedNetPnl is null)
                writer.WriteNull("observed_minus_simulated_net_pnl");
            else
                writer.WriteNumber("observed_minus_simulated_net_pnl", value.ObservedMinusSimulatedNetPnl.Value);
            writer.WriteString("reason_code", value.ReasonCode);
            writer.WriteString("schema", value.Schema);
            writer.WriteString("side", value.Side.ToString());
            writer.WriteBoolean("simulated_fee_evidence_complete", value.SimulatedFeeEvidenceComplete);
            writer.WriteNumber("simulated_fees", value.SimulatedFees);
            writer.WriteNumber("simulated_gross_pnl", value.SimulatedGrossPnl);
            writer.WriteNumber("simulated_entry_price", value.SimulatedEntryPrice);
            writer.WriteNumber("simulated_exit_price", value.SimulatedExitPrice);
            writer.WriteNumber("simulated_pnl_before_funding", value.SimulatedPnlBeforeFunding);
            writer.WriteString("state", value.State.ToString());
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteString("symbol", value.Symbol);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Required(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Post-trade PnL drift field '{name}' is required.");
        return value;
    }

    private static decimal? NullableDecimal(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();
    }

    private static bool LowerSha(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}

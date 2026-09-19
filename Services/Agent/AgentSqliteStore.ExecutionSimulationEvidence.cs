using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    public async Task<IReadOnlyList<ExecutionSimulationComparisonV1>> GetRecentExecutionSimulationComparisonFactsAsync(
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string venueRuleVersion,
        string symbol,
        int limit,
        CancellationToken ct)
    {
        ValidateExecutionSimulationEvidenceIdentity(
            strategyId,
            strategyVersion,
            costModelVersion,
            simulationModelVersion,
            venueRuleVersion,
            symbol);

        limit = Math.Clamp(limit, 1, 1000);

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection, ct);

        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT canonical_sha256,canonical_bytes,correlation_id,client_order_id,strategy_id,strategy_version,
                   cost_model_version,simulation_model_version,venue_rule_version,symbol,simulated_sha256,
                   observed_sha256,compared_at
            FROM execution_simulation_comparisons
            WHERE strategy_id=$strategy
              AND strategy_version=$version
              AND cost_model_version=$costModel
              AND simulation_model_version=$simulationModel
              AND venue_rule_version=$venueRule
              AND symbol=$symbol
            ORDER BY compared_at DESC,rowid DESC
            LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$strategy", strategyId);
        query.Parameters.AddWithValue("$version", strategyVersion);
        query.Parameters.AddWithValue("$costModel", costModelVersion);
        query.Parameters.AddWithValue("$simulationModel", simulationModelVersion);
        query.Parameters.AddWithValue("$venueRule", venueRuleVersion);
        query.Parameters.AddWithValue("$symbol", symbol);
        query.Parameters.AddWithValue("$limit", limit);

        var result = new List<ExecutionSimulationComparisonV1>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var canonicalSha256 = reader.GetString(0);
            var canonicalBytes = (byte[])reader[1];
            var comparison = ParseCanonicalExecutionSimulationComparison(
                canonicalSha256,
                canonicalBytes);

            if (!ExecutionSimulationComparisonCanonicalizerV1.IsCanonical(comparison))
                throw new InvalidOperationException("Persisted execution simulation comparison failed canonical verification.");

            if (!string.Equals(comparison.CorrelationId, reader.GetString(2), StringComparison.Ordinal)
                || !string.Equals(comparison.ClientOrderId, reader.GetString(3), StringComparison.Ordinal)
                || !string.Equals(comparison.StrategyId, reader.GetString(4), StringComparison.Ordinal)
                || !string.Equals(comparison.StrategyVersion, reader.GetString(5), StringComparison.Ordinal)
                || !string.Equals(comparison.CostModelVersion, reader.GetString(6), StringComparison.Ordinal)
                || !string.Equals(comparison.SimulationModelVersion, reader.GetString(7), StringComparison.Ordinal)
                || !string.Equals(comparison.VenueRuleVersion, reader.GetString(8), StringComparison.Ordinal)
                || !string.Equals(comparison.Symbol, reader.GetString(9), StringComparison.Ordinal)
                || !string.Equals(comparison.SimulatedCanonicalSha256, reader.GetString(10), StringComparison.Ordinal)
                || !string.Equals(comparison.ObservedCanonicalSha256, reader.GetString(11), StringComparison.Ordinal)
                || comparison.ComparedAtUtc != DateTimeOffset.Parse(
                    reader.GetString(12),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind))
                throw new InvalidOperationException("Persisted execution simulation comparison metadata does not match canonical evidence.");

            result.Add(comparison);
        }

        return result;
    }

    public async Task<ExecutionSimulationEvidenceDecisionV1> EvaluateRecentExecutionSimulationEvidenceAsync(
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string venueRuleVersion,
        string symbol,
        ExecutionSimulationEvidencePolicyV1 policy,
        int limit,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken ct)
    {
        var comparisons = await GetRecentExecutionSimulationComparisonFactsAsync(
            strategyId,
            strategyVersion,
            costModelVersion,
            simulationModelVersion,
            venueRuleVersion,
            symbol,
            limit,
            ct);

        return ExecutionSimulationEvidenceGateV1.Evaluate(
            strategyId,
            strategyVersion,
            costModelVersion,
            simulationModelVersion,
            venueRuleVersion,
            symbol,
            comparisons,
            policy,
            evaluatedAtUtc);
    }

    private static ExecutionSimulationComparisonV1 ParseCanonicalExecutionSimulationComparison(
        string canonicalSha256,
        byte[] canonicalBytes)
    {
        using var document = JsonDocument.Parse(canonicalBytes);
        var root = document.RootElement;
        if (!string.Equals(
                root.GetProperty("schema").GetString(),
                ExecutionSimulationComparisonCanonicalizerV1.Schema,
                StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted execution simulation comparison schema is invalid.");

        return new ExecutionSimulationComparisonV1(
            root.GetProperty("schema").GetString() ?? throw new InvalidOperationException("Missing comparison schema."),
            root.GetProperty("correlation_id").GetString() ?? throw new InvalidOperationException("Missing correlation id."),
            root.GetProperty("client_order_id").GetString() ?? throw new InvalidOperationException("Missing client order id."),
            root.GetProperty("strategy_id").GetString() ?? throw new InvalidOperationException("Missing strategy id."),
            root.GetProperty("strategy_version").GetString() ?? throw new InvalidOperationException("Missing strategy version."),
            root.GetProperty("cost_model_version").GetString() ?? throw new InvalidOperationException("Missing cost-model version."),
            root.GetProperty("simulation_model_version").GetString() ?? throw new InvalidOperationException("Missing simulation-model version."),
            root.GetProperty("venue_rule_version").GetString() ?? throw new InvalidOperationException("Missing venue-rule version."),
            root.GetProperty("symbol").GetString() ?? throw new InvalidOperationException("Missing symbol."),
            Enum.Parse<PositionSide>(root.GetProperty("side").GetString() ?? string.Empty, false),
            root.GetProperty("reduce_only").GetBoolean(),
            Enum.Parse<ExecutionOrderType>(root.GetProperty("order_type").GetString() ?? string.Empty, false),
            Enum.Parse<ExecutionSimulationFillStateV1>(root.GetProperty("simulated_state").GetString() ?? string.Empty, false),
            Enum.Parse<ExecutionRealityStateV1>(root.GetProperty("observed_state").GetString() ?? string.Empty, false),
            root.GetProperty("state_match").GetBoolean(),
            root.GetProperty("simulated_fill_ratio").GetDecimal(),
            root.GetProperty("observed_fill_ratio").GetDecimal(),
            root.GetProperty("fill_ratio_delta").GetDecimal(),
            root.GetProperty("price_comparable").GetBoolean(),
            NullableDecimal(root.GetProperty("price_drift_bps")),
            root.GetProperty("fee_comparable").GetBoolean(),
            NullableDecimal(root.GetProperty("fee_drift_bps")),
            root.GetProperty("latency_comparable").GetBoolean(),
            NullableLong(root.GetProperty("latency_drift_ms")),
            root.GetProperty("total_comparable").GetBoolean(),
            NullableDecimal(root.GetProperty("total_execution_drift_bps")),
            root.GetProperty("reason_code").GetString() ?? throw new InvalidOperationException("Missing reason code."),
            root.GetProperty("simulated_canonical_sha256").GetString() ?? throw new InvalidOperationException("Missing simulated source hash."),
            root.GetProperty("observed_canonical_sha256").GetString() ?? throw new InvalidOperationException("Missing observed source hash."),
            DateTimeOffset.Parse(
                root.GetProperty("compared_at_utc").GetString() ?? throw new InvalidOperationException("Missing comparison time."),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            canonicalBytes,
            canonicalSha256);
    }

    private static void ValidateExecutionSimulationEvidenceIdentity(
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string venueRuleVersion,
        string symbol)
    {
        if (string.IsNullOrWhiteSpace(strategyId)) throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        if (string.IsNullOrWhiteSpace(strategyVersion)) throw new ArgumentException("Strategy version is required.", nameof(strategyVersion));
        if (string.IsNullOrWhiteSpace(costModelVersion)) throw new ArgumentException("Cost-model version is required.", nameof(costModelVersion));
        if (string.IsNullOrWhiteSpace(simulationModelVersion)) throw new ArgumentException("Simulation-model version is required.", nameof(simulationModelVersion));
        if (string.IsNullOrWhiteSpace(venueRuleVersion)) throw new ArgumentException("Venue-rule version is required.", nameof(venueRuleVersion));
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol is required.", nameof(symbol));
    }

    private static decimal? NullableDecimal(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();

    private static long? NullableLong(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
}

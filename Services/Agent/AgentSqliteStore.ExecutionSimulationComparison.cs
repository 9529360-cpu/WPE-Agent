using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionSimulationPersistenceResultV1(bool Succeeded, bool Idempotent, string Code);

public sealed record ExecutionSimulationComparisonSummaryV1(
    string CorrelationId,
    string ClientOrderId,
    string StrategyId,
    string StrategyVersion,
    string CostModelVersion,
    string SimulationModelVersion,
    string VenueRuleVersion,
    string Symbol,
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
    string CanonicalSha256);

public sealed partial class AgentSqliteStore
{
    public async Task<ExecutionSimulationPersistenceResultV1> SaveExecutionSimulationFillAsync(
        ExecutionSimulationFillV1 fill,
        CancellationToken ct)
    {
        if (!ExecutionSimulationFillCanonicalizerV1.IsCanonical(fill))
            throw new InvalidOperationException("Execution simulation fill is not canonical.");

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection, ct);
        await EnsureExecutionRealityDriftStorageAsync(connection, ct);
        await VerifySimulationComparisonSourcesAsync(connection, comparison, ct);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO execution_simulated_fills(
                canonical_sha256,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,
                simulation_model_version,venue_rule_version,symbol,simulated_at,canonical_bytes)
            VALUES($hash,$correlation,$client,$strategy,$version,$costModel,$simulationModel,$venueRule,$symbol,$simulated,$bytes)
            ON CONFLICT(canonical_sha256) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$hash", fill.CanonicalSha256);
        insert.Parameters.AddWithValue("$correlation", fill.CorrelationId);
        insert.Parameters.AddWithValue("$client", fill.ClientOrderId);
        insert.Parameters.AddWithValue("$strategy", fill.StrategyId);
        insert.Parameters.AddWithValue("$version", fill.StrategyVersion);
        insert.Parameters.AddWithValue("$costModel", fill.CostModelVersion);
        insert.Parameters.AddWithValue("$simulationModel", fill.SimulationModelVersion);
        insert.Parameters.AddWithValue("$venueRule", fill.VenueRuleVersion);
        insert.Parameters.AddWithValue("$symbol", fill.Symbol);
        insert.Parameters.AddWithValue("$simulated", fill.SimulatedAtUtc.ToUniversalTime().ToString("O"));
        insert.Parameters.AddWithValue("$bytes", fill.CanonicalBytes);
        var affected = await insert.ExecuteNonQueryAsync(ct);
        if (affected == 1)
            return new(true, false, "stored");

        await VerifySimulationReplayAsync(
            connection,
            "execution_simulated_fills",
            fill.CanonicalSha256,
            fill.CanonicalBytes,
            ct);
        return new(true, true, "idempotent");
    }

    public async Task<ExecutionSimulationPersistenceResultV1> SaveExecutionSimulationComparisonAsync(
        ExecutionSimulationComparisonV1 comparison,
        CancellationToken ct)
    {
        if (!ExecutionSimulationComparisonCanonicalizerV1.IsCanonical(comparison))
            throw new InvalidOperationException("Execution simulation comparison is not canonical.");

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection, ct);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO execution_simulation_comparisons(
                canonical_sha256,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,
                simulation_model_version,venue_rule_version,symbol,simulated_sha256,observed_sha256,compared_at,canonical_bytes)
            VALUES($hash,$correlation,$client,$strategy,$version,$costModel,$simulationModel,$venueRule,$symbol,
                $simulatedHash,$observedHash,$compared,$bytes)
            ON CONFLICT(canonical_sha256) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$hash", comparison.CanonicalSha256);
        insert.Parameters.AddWithValue("$correlation", comparison.CorrelationId);
        insert.Parameters.AddWithValue("$client", comparison.ClientOrderId);
        insert.Parameters.AddWithValue("$strategy", comparison.StrategyId);
        insert.Parameters.AddWithValue("$version", comparison.StrategyVersion);
        insert.Parameters.AddWithValue("$costModel", comparison.CostModelVersion);
        insert.Parameters.AddWithValue("$simulationModel", comparison.SimulationModelVersion);
        insert.Parameters.AddWithValue("$venueRule", comparison.VenueRuleVersion);
        insert.Parameters.AddWithValue("$symbol", comparison.Symbol);
        insert.Parameters.AddWithValue("$simulatedHash", comparison.SimulatedCanonicalSha256);
        insert.Parameters.AddWithValue("$observedHash", comparison.ObservedCanonicalSha256);
        insert.Parameters.AddWithValue("$compared", comparison.ComparedAtUtc.ToUniversalTime().ToString("O"));
        insert.Parameters.AddWithValue("$bytes", comparison.CanonicalBytes);
        var affected = await insert.ExecuteNonQueryAsync(ct);
        if (affected == 1)
            return new(true, false, "stored");

        await VerifySimulationReplayAsync(
            connection,
            "execution_simulation_comparisons",
            comparison.CanonicalSha256,
            comparison.CanonicalBytes,
            ct);
        return new(true, true, "idempotent");
    }

    public async Task<IReadOnlyList<ExecutionSimulationComparisonSummaryV1>> GetRecentExecutionSimulationComparisonsAsync(
        int limit,
        CancellationToken ct,
        string? strategyId = null,
        string? strategyVersion = null,
        string? costModelVersion = null)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection, ct);

        var where = new List<string>();
        await using var query = connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(strategyId))
        {
            where.Add("strategy_id=$strategy");
            query.Parameters.AddWithValue("$strategy", strategyId);
        }
        if (!string.IsNullOrWhiteSpace(strategyVersion))
        {
            where.Add("strategy_version=$version");
            query.Parameters.AddWithValue("$version", strategyVersion);
        }
        if (!string.IsNullOrWhiteSpace(costModelVersion))
        {
            where.Add("cost_model_version=$costModel");
            query.Parameters.AddWithValue("$costModel", costModelVersion);
        }

        query.CommandText = $"""
            SELECT canonical_sha256,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,
                   simulation_model_version,venue_rule_version,symbol,simulated_sha256,observed_sha256,compared_at,canonical_bytes
            FROM execution_simulation_comparisons
            {(where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where))}
            ORDER BY compared_at DESC,rowid DESC
            LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$limit", limit);

        var result = new List<ExecutionSimulationComparisonSummaryV1>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var hash = reader.GetString(0);
            var bytes = (byte[])reader[12];
            VerifySimulationBytes(hash, bytes);
            var parsed = ParseComparisonSummary(hash, bytes);

            if (!string.Equals(parsed.CorrelationId, reader.GetString(1), StringComparison.Ordinal)
                || !string.Equals(parsed.ClientOrderId, reader.GetString(2), StringComparison.Ordinal)
                || !string.Equals(parsed.StrategyId, reader.GetString(3), StringComparison.Ordinal)
                || !string.Equals(parsed.StrategyVersion, reader.GetString(4), StringComparison.Ordinal)
                || !string.Equals(parsed.CostModelVersion, reader.GetString(5), StringComparison.Ordinal)
                || !string.Equals(parsed.SimulationModelVersion, reader.GetString(6), StringComparison.Ordinal)
                || !string.Equals(parsed.VenueRuleVersion, reader.GetString(7), StringComparison.Ordinal)
                || !string.Equals(parsed.Symbol, reader.GetString(8), StringComparison.Ordinal)
                || !string.Equals(parsed.SimulatedCanonicalSha256, reader.GetString(9), StringComparison.Ordinal)
                || !string.Equals(parsed.ObservedCanonicalSha256, reader.GetString(10), StringComparison.Ordinal)
                || parsed.ComparedAtUtc != DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
                throw new InvalidOperationException("Persisted execution simulation comparison metadata does not match canonical bytes.");

            result.Add(parsed);
        }
        return result;
    }

    private static async Task EnsureExecutionSimulationStorageAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS execution_simulated_fills(
                canonical_sha256 TEXT PRIMARY KEY,
                correlation_id TEXT NOT NULL,
                client_order_id TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                cost_model_version TEXT NOT NULL,
                simulation_model_version TEXT NOT NULL,
                venue_rule_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                simulated_at TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_execution_simulated_fills_identity
                ON execution_simulated_fills(strategy_id,strategy_version,cost_model_version,simulated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_execution_simulated_fills_client
                ON execution_simulated_fills(client_order_id,simulated_at DESC);
            CREATE TRIGGER IF NOT EXISTS execution_simulated_fills_no_update
                BEFORE UPDATE ON execution_simulated_fills
                BEGIN SELECT RAISE(ABORT,'execution simulated fills are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_simulated_fills_no_delete
                BEFORE DELETE ON execution_simulated_fills
                BEGIN SELECT RAISE(ABORT,'execution simulated fills are append-only'); END;

            CREATE TABLE IF NOT EXISTS execution_simulation_comparisons(
                canonical_sha256 TEXT PRIMARY KEY,
                correlation_id TEXT NOT NULL,
                client_order_id TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                cost_model_version TEXT NOT NULL,
                simulation_model_version TEXT NOT NULL,
                venue_rule_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                simulated_sha256 TEXT NOT NULL,
                observed_sha256 TEXT NOT NULL,
                compared_at TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_execution_simulation_comparisons_identity
                ON execution_simulation_comparisons(strategy_id,strategy_version,cost_model_version,compared_at DESC);
            CREATE INDEX IF NOT EXISTS ix_execution_simulation_comparisons_client
                ON execution_simulation_comparisons(client_order_id,compared_at DESC);
            CREATE TRIGGER IF NOT EXISTS execution_simulation_comparisons_no_update
                BEFORE UPDATE ON execution_simulation_comparisons
                BEGIN SELECT RAISE(ABORT,'execution simulation comparisons are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_simulation_comparisons_no_delete
                BEFORE DELETE ON execution_simulation_comparisons
                BEGIN SELECT RAISE(ABORT,'execution simulation comparisons are append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task VerifySimulationComparisonSourcesAsync(
        SqliteConnection connection,
        ExecutionSimulationComparisonV1 comparison,
        CancellationToken ct)
    {
        var simulated = await ReadPersistedSimulationFillAsync(
            connection,
            comparison.SimulatedCanonicalSha256,
            ct);
        if (simulated is null)
            throw new InvalidOperationException("Persisted simulated fill source is missing.");
        if (!ExecutionSimulationFillCanonicalizerV1.IsCanonical(simulated))
            throw new InvalidOperationException("Persisted simulated fill source failed canonical verification.");

        var observed = await ReadPersistedObservedDriftAsync(
            connection,
            comparison.ObservedCanonicalSha256,
            ct);
        if (observed is null)
            throw new InvalidOperationException("Persisted observed execution source is missing.");
        if (!ExecutionRealityDriftV1.IsCanonical(observed))
            throw new InvalidOperationException("Persisted observed execution source failed canonical verification.");

        var replay = ExecutionSimulationComparisonCanonicalizerV1.Create(
            simulated,
            observed,
            comparison.ComparedAtUtc);
        if (!string.Equals(replay.CanonicalSha256, comparison.CanonicalSha256, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes, comparison.CanonicalBytes))
            throw new InvalidOperationException("Execution simulation comparison does not replay from persisted source evidence.");
    }

    private static async Task<ExecutionSimulationFillV1?> ReadPersistedSimulationFillAsync(
        SqliteConnection connection,
        string canonicalSha256,
        CancellationToken ct)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT canonical_bytes FROM execution_simulated_fills WHERE canonical_sha256=$hash LIMIT 1";
        query.Parameters.AddWithValue("$hash", canonicalSha256);
        var value = await query.ExecuteScalarAsync(ct);
        if (value is not byte[] bytes)
            return null;

        VerifySimulationBytes(canonicalSha256, bytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("schema").GetString(), ExecutionSimulationFillCanonicalizerV1.Schema, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted simulated fill source schema is invalid.");

        return new ExecutionSimulationFillV1(
            root.GetProperty("schema").GetString() ?? throw new InvalidOperationException("Missing simulation schema."),
            root.GetProperty("correlation_id").GetString() ?? throw new InvalidOperationException("Missing simulation correlation id."),
            root.GetProperty("client_order_id").GetString() ?? throw new InvalidOperationException("Missing simulation client order id."),
            root.GetProperty("strategy_id").GetString() ?? throw new InvalidOperationException("Missing simulation strategy id."),
            root.GetProperty("strategy_version").GetString() ?? throw new InvalidOperationException("Missing simulation strategy version."),
            root.GetProperty("cost_model_version").GetString() ?? throw new InvalidOperationException("Missing simulation cost-model version."),
            root.GetProperty("simulation_model_version").GetString() ?? throw new InvalidOperationException("Missing simulation model version."),
            root.GetProperty("venue_rule_version").GetString() ?? throw new InvalidOperationException("Missing simulation venue-rule version."),
            root.GetProperty("symbol").GetString() ?? throw new InvalidOperationException("Missing simulation symbol."),
            Enum.Parse<PositionSide>(root.GetProperty("side").GetString() ?? string.Empty, false),
            root.GetProperty("reduce_only").GetBoolean(),
            Enum.Parse<ExecutionOrderType>(root.GetProperty("order_type").GetString() ?? string.Empty, false),
            root.GetProperty("intended_quantity").GetDecimal(),
            Enum.Parse<ExecutionSimulationFillStateV1>(root.GetProperty("state").GetString() ?? string.Empty, false),
            root.GetProperty("executed_quantity").GetDecimal(),
            root.GetProperty("average_price").GetDecimal(),
            root.GetProperty("fee_amount").GetDecimal(),
            Enum.Parse<ExecutionSimulationFeeRoleV1>(root.GetProperty("fee_role").GetString() ?? string.Empty, false),
            root.GetProperty("latency_modeled").GetBoolean(),
            root.GetProperty("simulated_latency_ms").GetInt64(),
            DateTimeOffset.Parse(root.GetProperty("market_as_of_utc").GetString() ?? throw new InvalidOperationException("Missing simulation market time."), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(root.GetProperty("simulated_at_utc").GetString() ?? throw new InvalidOperationException("Missing simulation time."), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            root.GetProperty("reason_code").GetString() ?? throw new InvalidOperationException("Missing simulation reason code."),
            bytes,
            canonicalSha256);
    }

    private static async Task<ExecutionRealityDriftFactV1?> ReadPersistedObservedDriftAsync(
        SqliteConnection connection,
        string canonicalSha256,
        CancellationToken ct)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT schema,correlation_id,client_order_id,strategy_id,strategy_version,cost_model_version,symbol,side,
                   reduce_only,order_type,intended_quantity,executed_quantity,expected_price,average_price,
                   exchange_status,state,terminal,comparable,fill_ratio,adverse_slippage_bps,expected_slippage_bps,
                   slippage_drift_bps,observed_fee,fee_basis,fee_comparable,observed_fee_rate_bps,
                   expected_commission_bps,fee_drift_bps,total_comparable,total_execution_drift_bps,
                   observation_latency_ms,exchange_updated_at,observed_at,reason_code,canonical_bytes,canonical_sha256
            FROM execution_reality_drift
            WHERE canonical_sha256=$hash
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$hash", canonicalSha256);
        await using var reader = await query.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRealityFact(reader) : null;
    }

    private static async Task VerifySimulationReplayAsync(
        SqliteConnection connection,
        string table,
        string canonicalSha256,
        byte[] canonicalBytes,
        CancellationToken ct)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"SELECT canonical_bytes FROM {table} WHERE canonical_sha256=$hash";
        query.Parameters.AddWithValue("$hash", canonicalSha256);
        var value = await query.ExecuteScalarAsync(ct);
        if (value is not byte[] bytes || !CryptographicOperations.FixedTimeEquals(bytes, canonicalBytes))
            throw new InvalidOperationException("Execution simulation persistence hash collision or replay conflict.");
    }

    private static void VerifySimulationBytes(string canonicalSha256, byte[] canonicalBytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        if (!string.Equals(hash, canonicalSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted execution simulation canonical bytes failed hash verification.");
    }

    private static ExecutionSimulationComparisonSummaryV1 ParseComparisonSummary(string canonicalSha256, byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("schema").GetString(), ExecutionSimulationComparisonCanonicalizerV1.Schema, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted execution simulation comparison schema is invalid.");

        return new(
            root.GetProperty("correlation_id").GetString() ?? throw new InvalidOperationException("Missing correlation id."),
            root.GetProperty("client_order_id").GetString() ?? throw new InvalidOperationException("Missing client order id."),
            root.GetProperty("strategy_id").GetString() ?? throw new InvalidOperationException("Missing strategy id."),
            root.GetProperty("strategy_version").GetString() ?? throw new InvalidOperationException("Missing strategy version."),
            root.GetProperty("cost_model_version").GetString() ?? throw new InvalidOperationException("Missing cost model version."),
            root.GetProperty("simulation_model_version").GetString() ?? throw new InvalidOperationException("Missing simulation model version."),
            root.GetProperty("venue_rule_version").GetString() ?? throw new InvalidOperationException("Missing venue rule version."),
            root.GetProperty("symbol").GetString() ?? throw new InvalidOperationException("Missing symbol."),
            root.GetProperty("state_match").GetBoolean(),
            root.GetProperty("simulated_fill_ratio").GetDecimal(),
            root.GetProperty("observed_fill_ratio").GetDecimal(),
            root.GetProperty("fill_ratio_delta").GetDecimal(),
            root.GetProperty("price_comparable").GetBoolean(),
            SimulationNullableDecimal(root.GetProperty("price_drift_bps")),
            root.GetProperty("fee_comparable").GetBoolean(),
            SimulationNullableDecimal(root.GetProperty("fee_drift_bps")),
            root.GetProperty("latency_comparable").GetBoolean(),
            SimulationNullableLong(root.GetProperty("latency_drift_ms")),
            root.GetProperty("total_comparable").GetBoolean(),
            SimulationNullableDecimal(root.GetProperty("total_execution_drift_bps")),
            root.GetProperty("reason_code").GetString() ?? throw new InvalidOperationException("Missing reason code."),
            root.GetProperty("simulated_canonical_sha256").GetString() ?? throw new InvalidOperationException("Missing simulated hash."),
            root.GetProperty("observed_canonical_sha256").GetString() ?? throw new InvalidOperationException("Missing observed hash."),
            DateTimeOffset.Parse(root.GetProperty("compared_at_utc").GetString() ?? throw new InvalidOperationException("Missing comparison time."), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            canonicalSha256);
    }

    private static decimal? SimulationNullableDecimal(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();

    private static long? SimulationNullableLong(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
}

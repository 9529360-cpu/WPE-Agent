using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    public async Task<ExecutionSimulationPersistenceResultV1> SaveExecutionSimulationResearchProvenanceAsync(
        ExecutionSimulationResearchProvenanceV1 provenance,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (!ExecutionSimulationResearchProvenanceCanonicalizerV1.IsCanonical(provenance))
            throw new InvalidOperationException("Execution simulation research provenance is not canonical.");

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection, ct);
        await EnsureExecutionSimulationResearchProvenanceStorageAsync(connection, ct);

        var fill = await ReadPersistedSimulationFillAsync(
            connection,
            provenance.SimulatedFillCanonicalSha256,
            ct);
        VerifyResearchProvenanceAgainstFill(provenance, fill);

        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText = """
                SELECT canonical_sha256,canonical_bytes
                FROM execution_simulation_research_provenance
                WHERE simulated_fill_sha256=$fillHash
                LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$fillHash", provenance.SimulatedFillCanonicalSha256);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var existingHash = reader.GetString(0);
                var existingBytes = (byte[])reader[1];
                if (!string.Equals(existingHash, provenance.CanonicalSha256, StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(existingBytes, provenance.CanonicalBytes))
                    throw new InvalidOperationException("Simulated fill already has conflicting research provenance.");
                return new(true, true, "idempotent");
            }
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO execution_simulation_research_provenance(
                canonical_sha256,simulated_fill_sha256,correlation_id,client_order_id,
                strategy_id,strategy_version,cost_model_version,simulation_model_version,
                venue_rule_version,symbol,backtest_validation_sha256,timeline_sha256,
                shadow_observation_sha256,market_provenance_sha256,bound_at,canonical_bytes)
            VALUES(
                $hash,$fillHash,$correlation,$client,$strategy,$version,$costModel,$simulationModel,
                $venueRule,$symbol,$validationHash,$timelineHash,$shadowHash,$marketHash,$bound,$bytes);
            """;
        insert.Parameters.AddWithValue("$hash", provenance.CanonicalSha256);
        insert.Parameters.AddWithValue("$fillHash", provenance.SimulatedFillCanonicalSha256);
        insert.Parameters.AddWithValue("$correlation", provenance.CorrelationId);
        insert.Parameters.AddWithValue("$client", provenance.ClientOrderId);
        insert.Parameters.AddWithValue("$strategy", provenance.StrategyId);
        insert.Parameters.AddWithValue("$version", provenance.StrategyVersion);
        insert.Parameters.AddWithValue("$costModel", provenance.CostModelVersion);
        insert.Parameters.AddWithValue("$simulationModel", provenance.SimulationModelVersion);
        insert.Parameters.AddWithValue("$venueRule", provenance.VenueRuleVersion);
        insert.Parameters.AddWithValue("$symbol", provenance.Symbol);
        insert.Parameters.AddWithValue("$validationHash", provenance.BacktestValidationSha256);
        insert.Parameters.AddWithValue("$timelineHash", provenance.TimelineSha256);
        insert.Parameters.AddWithValue("$shadowHash", provenance.ShadowObservationSha256);
        insert.Parameters.AddWithValue("$marketHash", provenance.MarketProvenanceSha256);
        insert.Parameters.AddWithValue("$bound", provenance.BoundAtUtc.ToString("O"));
        insert.Parameters.Add("$bytes", SqliteType.Blob).Value = provenance.CanonicalBytes;
        await insert.ExecuteNonQueryAsync(ct);

        return new(true, false, "stored");
    }

    public async Task<ExecutionSimulationResearchProvenanceV1?> GetExecutionSimulationResearchProvenanceAsync(
        string simulatedFillCanonicalSha256,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(simulatedFillCanonicalSha256))
            throw new ArgumentException("Simulated fill hash is required.", nameof(simulatedFillCanonicalSha256));

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection, ct);
        await EnsureExecutionSimulationResearchProvenanceStorageAsync(connection, ct);

        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT canonical_sha256,simulated_fill_sha256,correlation_id,client_order_id,
                   strategy_id,strategy_version,cost_model_version,simulation_model_version,
                   venue_rule_version,symbol,backtest_validation_sha256,timeline_sha256,
                   shadow_observation_sha256,market_provenance_sha256,bound_at,canonical_bytes
            FROM execution_simulation_research_provenance
            WHERE simulated_fill_sha256=$fillHash
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$fillHash", simulatedFillCanonicalSha256);

        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var hash = reader.GetString(0);
        var bytes = (byte[])reader[15];
        if (!ExecutionSimulationResearchProvenanceCanonicalizerV1.TryDeserialize(bytes, hash, out var provenance)
            || provenance is null)
            throw new InvalidOperationException("Persisted execution simulation research provenance failed canonical verification.");

        if (!string.Equals(reader.GetString(1), provenance.SimulatedFillCanonicalSha256, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), provenance.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3), provenance.ClientOrderId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(4), provenance.StrategyId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(5), provenance.StrategyVersion, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(6), provenance.CostModelVersion, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(7), provenance.SimulationModelVersion, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(8), provenance.VenueRuleVersion, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(9), provenance.Symbol, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(10), provenance.BacktestValidationSha256, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(11), provenance.TimelineSha256, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(12), provenance.ShadowObservationSha256, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(13), provenance.MarketProvenanceSha256, StringComparison.Ordinal)
            || DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) != provenance.BoundAtUtc)
            throw new InvalidOperationException("Persisted execution simulation research provenance metadata does not match canonical bytes.");

        var fill = await ReadPersistedSimulationFillAsync(
            connection,
            provenance.SimulatedFillCanonicalSha256,
            ct);
        VerifyResearchProvenanceAgainstFill(provenance, fill);

        return provenance;
    }

    private static void VerifyResearchProvenanceAgainstFill(
        ExecutionSimulationResearchProvenanceV1 provenance,
        ExecutionSimulationFillV1? fill)
    {
        if (fill is null)
            throw new InvalidOperationException("Persisted simulated fill source is missing.");
        if (!ExecutionSimulationFillCanonicalizerV1.IsCanonical(fill))
            throw new InvalidOperationException("Persisted simulated fill source failed canonical verification.");

        if (!string.Equals(fill.CanonicalSha256, provenance.SimulatedFillCanonicalSha256, StringComparison.Ordinal)
            || !string.Equals(fill.CorrelationId, provenance.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(fill.ClientOrderId, provenance.ClientOrderId, StringComparison.Ordinal)
            || !string.Equals(fill.StrategyId, provenance.StrategyId, StringComparison.Ordinal)
            || !string.Equals(fill.StrategyVersion, provenance.StrategyVersion, StringComparison.Ordinal)
            || !string.Equals(fill.CostModelVersion, provenance.CostModelVersion, StringComparison.Ordinal)
            || !string.Equals(fill.SimulationModelVersion, provenance.SimulationModelVersion, StringComparison.Ordinal)
            || !string.Equals(fill.VenueRuleVersion, provenance.VenueRuleVersion, StringComparison.Ordinal)
            || !string.Equals(fill.Symbol, provenance.Symbol, StringComparison.Ordinal)
            || fill.MarketAsOfUtc != provenance.MarketAsOfUtc
            || fill.SimulatedAtUtc != provenance.SimulatedAtUtc)
            throw new InvalidOperationException("Execution simulation research provenance does not match the persisted simulated fill.");
    }

    private static async Task EnsureExecutionSimulationResearchProvenanceStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS execution_simulation_research_provenance(
                canonical_sha256 TEXT PRIMARY KEY,
                simulated_fill_sha256 TEXT NOT NULL UNIQUE,
                correlation_id TEXT NOT NULL,
                client_order_id TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                cost_model_version TEXT NOT NULL,
                simulation_model_version TEXT NOT NULL,
                venue_rule_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                backtest_validation_sha256 TEXT NOT NULL,
                timeline_sha256 TEXT NOT NULL,
                shadow_observation_sha256 TEXT NOT NULL,
                market_provenance_sha256 TEXT NOT NULL,
                bound_at TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_execution_simulation_research_provenance_identity
                ON execution_simulation_research_provenance(
                    strategy_id,strategy_version,cost_model_version,simulation_model_version,venue_rule_version,bound_at DESC);
            CREATE TRIGGER IF NOT EXISTS execution_simulation_research_provenance_no_update
                BEFORE UPDATE ON execution_simulation_research_provenance
                BEGIN SELECT RAISE(ABORT,'execution simulation research provenance is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_simulation_research_provenance_no_delete
                BEFORE DELETE ON execution_simulation_research_provenance
                BEGIN SELECT RAISE(ABORT,'execution simulation research provenance is append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}

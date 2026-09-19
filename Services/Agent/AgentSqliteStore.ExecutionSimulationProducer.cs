using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionSimulationBundlePersistenceResultV1(
    bool Succeeded,
    bool Idempotent,
    string Code);

public sealed partial class AgentSqliteStore
{
    public async Task<ExecutionSimulationBundlePersistenceResultV1> SaveExecutionSimulationBundleAsync(
        ExecutionSimulationSourceV1 source,
        ExecutionSimulationFillV1 fill,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fill);
        if (!ExecutionTopOfBookSimulationV1.IsCanonical(source))
            throw new InvalidOperationException("Execution simulation source is not canonical.");
        if (!ExecutionSimulationFillCanonicalizerV1.IsCanonical(fill))
            throw new InvalidOperationException("Execution simulated fill is not canonical.");

        var replay = ExecutionTopOfBookSimulationV1.ReplayFill(source);
        if (!string.Equals(replay.CanonicalSha256, fill.CanonicalSha256, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes, fill.CanonicalBytes)
            || !string.Equals(source.SimulatedFillSha256, fill.CanonicalSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Execution simulation source does not replay to the supplied fill.");

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection, ct);
        await EnsureExecutionSimulationSourceStorageAsync(connection, ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var fillInserted = await InsertSimulationFillAsync(connection, transaction, fill, ct);
        if (!fillInserted)
            await VerifySimulationBundleReplayAsync(
                connection,
                transaction,
                "execution_simulated_fills",
                "canonical_sha256",
                fill.CanonicalSha256,
                fill.CanonicalBytes,
                ct);

        var sourceInserted = await InsertSimulationSourceAsync(connection, transaction, source, ct);
        if (!sourceInserted)
        {
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = """
                SELECT canonical_sha256,simulated_fill_sha256,canonical_bytes
                FROM execution_simulation_sources
                WHERE artifact_sha256=$artifact AND client_order_id=$client
                LIMIT 1;
                """;
            query.Parameters.AddWithValue("$artifact", source.ArtifactSha256);
            query.Parameters.AddWithValue("$client", source.ClientOrderId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Execution simulation source replay lost the existing row.");
            var existingHash = reader.GetString(0);
            var existingFill = reader.GetString(1);
            var existingBytes = (byte[])reader[2];
            if (!string.Equals(existingHash, source.CanonicalSha256, StringComparison.Ordinal)
                || !string.Equals(existingFill, fill.CanonicalSha256, StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(existingBytes, source.CanonicalBytes))
                throw new InvalidOperationException("Execution simulation source conflicts with the durable artifact/client-order identity.");
        }

        await transaction.CommitAsync(ct);
        return new(true, !fillInserted && !sourceInserted, sourceInserted ? "stored" : "idempotent");
    }

    private static async Task<bool> InsertSimulationFillAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExecutionSimulationFillV1 fill,
        CancellationToken ct)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
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
        insert.Parameters.AddWithValue("$simulated", fill.SimulatedAtUtc.ToString("O"));
        insert.Parameters.AddWithValue("$bytes", fill.CanonicalBytes);
        return await insert.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<bool> InsertSimulationSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExecutionSimulationSourceV1 source,
        CancellationToken ct)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO execution_simulation_sources(
                canonical_sha256,artifact_sha256,intent_sha256,correlation_id,client_order_id,intent_sequence,
                strategy_id,strategy_version,cost_model_version,simulation_model_version,venue_rule_version,
                provider_id,environment,symbol,simulated_fill_sha256,market_updated_at,simulated_at,canonical_bytes)
            VALUES(
                $hash,$artifact,$intentHash,$correlation,$client,$sequence,
                $strategy,$version,$costModel,$simulationModel,$venueRule,
                $provider,$environment,$symbol,$fillHash,$marketAt,$simulated,$bytes)
            ON CONFLICT(artifact_sha256,client_order_id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$hash", source.CanonicalSha256);
        insert.Parameters.AddWithValue("$artifact", source.ArtifactSha256);
        insert.Parameters.AddWithValue("$intentHash", source.IntentSha256);
        insert.Parameters.AddWithValue("$correlation", source.CorrelationId);
        insert.Parameters.AddWithValue("$client", source.ClientOrderId);
        insert.Parameters.AddWithValue("$sequence", source.IntentSequence);
        insert.Parameters.AddWithValue("$strategy", source.StrategyId);
        insert.Parameters.AddWithValue("$version", source.StrategyVersion);
        insert.Parameters.AddWithValue("$costModel", source.CostModelVersion);
        insert.Parameters.AddWithValue("$simulationModel", source.SimulationModelVersion);
        insert.Parameters.AddWithValue("$venueRule", source.VenueRuleVersion);
        insert.Parameters.AddWithValue("$provider", source.ProviderId);
        insert.Parameters.AddWithValue("$environment", source.Environment);
        insert.Parameters.AddWithValue("$symbol", source.Symbol);
        insert.Parameters.AddWithValue("$fillHash", source.SimulatedFillSha256);
        insert.Parameters.AddWithValue("$marketAt", source.MarketUpdatedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        insert.Parameters.AddWithValue("$simulated", source.SimulatedAtUtc.ToString("O"));
        insert.Parameters.AddWithValue("$bytes", source.CanonicalBytes);
        return await insert.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task EnsureExecutionSimulationSourceStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS execution_simulation_sources(
                canonical_sha256 TEXT PRIMARY KEY,
                artifact_sha256 TEXT NOT NULL,
                intent_sha256 TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                client_order_id TEXT NOT NULL,
                intent_sequence INTEGER NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                cost_model_version TEXT NOT NULL,
                simulation_model_version TEXT NOT NULL,
                venue_rule_version TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                environment TEXT NOT NULL CHECK(environment='Testnet'),
                symbol TEXT NOT NULL,
                simulated_fill_sha256 TEXT NOT NULL,
                market_updated_at TEXT NULL,
                simulated_at TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(artifact_sha256,client_order_id));
            CREATE INDEX IF NOT EXISTS ix_execution_simulation_sources_strategy
                ON execution_simulation_sources(
                    strategy_id,strategy_version,cost_model_version,simulated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_execution_simulation_sources_fill
                ON execution_simulation_sources(simulated_fill_sha256);
            CREATE TRIGGER IF NOT EXISTS execution_simulation_sources_no_update
                BEFORE UPDATE ON execution_simulation_sources
                BEGIN SELECT RAISE(ABORT,'execution simulation sources are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_simulation_sources_no_delete
                BEFORE DELETE ON execution_simulation_sources
                BEGIN SELECT RAISE(ABORT,'execution simulation sources are append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task VerifySimulationBundleReplayAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string keyColumn,
        string key,
        byte[] canonicalBytes,
        CancellationToken ct)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"SELECT canonical_bytes FROM {table} WHERE {keyColumn}=$key LIMIT 1";
        query.Parameters.AddWithValue("$key", key);
        var value = await query.ExecuteScalarAsync(ct);
        if (value is not byte[] bytes
            || !CryptographicOperations.FixedTimeEquals(bytes, canonicalBytes))
            throw new InvalidOperationException("Execution simulation bundle replay conflict.");
    }
}

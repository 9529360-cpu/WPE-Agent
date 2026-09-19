using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionSimulationIntentEvidenceV1(
    ExecutionSimulationSourceV1 Source,
    ExecutionSimulationFillV1 Fill);

public sealed partial class AgentSqliteStore
{
    public async Task<ExecutionSimulationIntentEvidenceV1?> GetExecutionSimulationIntentEvidenceAsync(
        string correlationId,
        string clientOrderId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        if (string.IsNullOrWhiteSpace(clientOrderId))
            throw new ArgumentException("Client order id is required.", nameof(clientOrderId));

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection, ct);
        await EnsureExecutionSimulationSourceStorageAsync(connection, ct);

        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT canonical_sha256,artifact_sha256,intent_sha256,correlation_id,client_order_id,intent_sequence,
                   strategy_id,strategy_version,cost_model_version,simulation_model_version,venue_rule_version,
                   provider_id,environment,symbol,simulated_fill_sha256,canonical_bytes
            FROM execution_simulation_sources
            WHERE correlation_id=$correlation AND client_order_id=$client
            ORDER BY simulated_at DESC,rowid DESC
            LIMIT 2;
            """;
        query.Parameters.AddWithValue("$correlation", correlationId);
        query.Parameters.AddWithValue("$client", clientOrderId);

        string? sourceHash = null;
        string? artifactHash = null;
        string? intentHash = null;
        string? strategyId = null;
        string? strategyVersion = null;
        string? costModelVersion = null;
        string? simulationModelVersion = null;
        string? venueRuleVersion = null;
        string? providerId = null;
        string? environment = null;
        string? symbol = null;
        string? fillHash = null;
        byte[]? sourceBytes = null;
        var rows = 0;

        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows++;
                if (rows > 1)
                    throw new InvalidOperationException("Multiple execution simulation sources match one durable intent.");
                sourceHash = reader.GetString(0);
                artifactHash = reader.GetString(1);
                intentHash = reader.GetString(2);
                if (!string.Equals(reader.GetString(3), correlationId, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(4), clientOrderId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Execution simulation source identity metadata is inconsistent.");
                strategyId = reader.GetString(6);
                strategyVersion = reader.GetString(7);
                costModelVersion = reader.GetString(8);
                simulationModelVersion = reader.GetString(9);
                venueRuleVersion = reader.GetString(10);
                providerId = reader.GetString(11);
                environment = reader.GetString(12);
                symbol = reader.GetString(13);
                fillHash = reader.GetString(14);
                sourceBytes = (byte[])reader[15];
            }
        }

        if (rows == 0)
            return null;
        if (sourceHash is null || sourceBytes is null || fillHash is null)
            throw new InvalidOperationException("Execution simulation source row is incomplete.");

        var computedHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        if (!string.Equals(computedHash, sourceHash, StringComparison.Ordinal)
            || !ExecutionTopOfBookSimulationV1.TryDeserializeSource(sourceBytes, sourceHash, out var source)
            || source is null)
            throw new InvalidOperationException("Persisted execution simulation source failed canonical verification.");

        if (!string.Equals(source.ArtifactSha256, artifactHash, StringComparison.Ordinal)
            || !string.Equals(source.IntentSha256, intentHash, StringComparison.Ordinal)
            || !string.Equals(source.StrategyId, strategyId, StringComparison.Ordinal)
            || !string.Equals(source.StrategyVersion, strategyVersion, StringComparison.Ordinal)
            || !string.Equals(source.CostModelVersion, costModelVersion, StringComparison.Ordinal)
            || !string.Equals(source.SimulationModelVersion, simulationModelVersion, StringComparison.Ordinal)
            || !string.Equals(source.VenueRuleVersion, venueRuleVersion, StringComparison.Ordinal)
            || !string.Equals(source.ProviderId, providerId, StringComparison.Ordinal)
            || !string.Equals(source.Environment, environment, StringComparison.Ordinal)
            || !string.Equals(source.Symbol, symbol, StringComparison.Ordinal)
            || !string.Equals(source.SimulatedFillSha256, fillHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted execution simulation source metadata does not match canonical bytes.");

        var fill = await ReadPersistedSimulationFillAsync(connection, fillHash, ct);
        if (fill is null || !ExecutionSimulationFillCanonicalizerV1.IsCanonical(fill))
            throw new InvalidOperationException("Persisted execution simulated fill source is missing or invalid.");
        if (!string.Equals(fill.CorrelationId, correlationId, StringComparison.Ordinal)
            || !string.Equals(fill.ClientOrderId, clientOrderId, StringComparison.Ordinal)
            || !string.Equals(fill.StrategyId, source.StrategyId, StringComparison.Ordinal)
            || !string.Equals(fill.StrategyVersion, source.StrategyVersion, StringComparison.Ordinal)
            || !string.Equals(fill.CostModelVersion, source.CostModelVersion, StringComparison.Ordinal)
            || !string.Equals(fill.SimulationModelVersion, source.SimulationModelVersion, StringComparison.Ordinal)
            || !string.Equals(fill.VenueRuleVersion, source.VenueRuleVersion, StringComparison.Ordinal)
            || !string.Equals(fill.Symbol, source.Symbol, StringComparison.Ordinal))
            throw new InvalidOperationException("Execution simulated fill identity does not match its canonical source.");

        var replay = ExecutionTopOfBookSimulationV1.ReplayFill(source);
        if (!string.Equals(replay.CanonicalSha256, fill.CanonicalSha256, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes, fill.CanonicalBytes))
            throw new InvalidOperationException("Execution simulated fill does not replay from its persisted source.");

        return new(source, fill);
    }
}

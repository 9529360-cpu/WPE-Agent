using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    internal async Task<bool> SaveExecutionSimulationSourceAsync(
        ExecutionSimulationSourceV1 source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ExecutionSimulationSourceCanonicalizerV1.IsCanonical(source))
            throw new InvalidOperationException("Execution simulation source is not canonical.");

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationSourceStorageAsync(connection,ct);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO execution_simulation_sources(
                    canonical_sha256,correlation_id,client_order_id,artifact_sha256,intent_sha256,
                    provider_id,environment,strategy_id,strategy_version,symbol,state,source_observed_at,
                    market_provenance_sha256,venue_rule_sha256,canonical_bytes)
                VALUES(
                    $hash,$correlation,$client,$artifact,$intent,$provider,$environment,$strategy,$version,
                    $symbol,$state,$observed,$marketHash,$ruleHash,$bytes);
                SELECT changes();
                """;
            insert.Parameters.AddWithValue("$hash",source.CanonicalSha256);
            insert.Parameters.AddWithValue("$correlation",source.CorrelationId);
            insert.Parameters.AddWithValue("$client",source.ClientOrderId);
            insert.Parameters.AddWithValue("$artifact",source.ArtifactSha256);
            insert.Parameters.AddWithValue("$intent",source.IntentSha256);
            insert.Parameters.AddWithValue("$provider",source.ProviderId);
            insert.Parameters.AddWithValue("$environment",source.Environment);
            insert.Parameters.AddWithValue("$strategy",source.StrategyId);
            insert.Parameters.AddWithValue("$version",source.StrategyVersion);
            insert.Parameters.AddWithValue("$symbol",source.Symbol);
            insert.Parameters.AddWithValue("$state",source.State.ToString());
            insert.Parameters.AddWithValue("$observed",source.SourceObservedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$marketHash",source.MarketProvenanceSha256);
            insert.Parameters.AddWithValue("$ruleHash",source.VenueRuleSha256);
            insert.Parameters.Add("$bytes",SqliteType.Blob).Value=source.CanonicalBytes;
            if (Convert.ToInt32(await insert.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1)
                return true;
        }

        var replay = await GetExecutionSimulationSourceAsync(
            source.CorrelationId,
            source.ClientOrderId,
            ct);
        if (replay is null
            || !string.Equals(replay.CanonicalSha256,source.CanonicalSha256,StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes,source.CanonicalBytes))
            throw new InvalidOperationException("Execution simulation source identity conflicts with persisted evidence.");
        return false;
    }

    internal async Task<ExecutionSimulationSourceV1?> GetExecutionSimulationSourceAsync(
        string correlationId,
        string clientOrderId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.",nameof(correlationId));
        if (string.IsNullOrWhiteSpace(clientOrderId))
            throw new ArgumentException("Client order id is required.",nameof(clientOrderId));

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationSourceStorageAsync(connection,ct);

        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT canonical_sha256,artifact_sha256,intent_sha256,provider_id,environment,
                   strategy_id,strategy_version,symbol,state,source_observed_at,
                   market_provenance_sha256,venue_rule_sha256,canonical_bytes
            FROM execution_simulation_sources
            WHERE correlation_id=$correlation AND client_order_id=$client
            LIMIT 2;
            """;
        query.Parameters.AddWithValue("$correlation",correlationId);
        query.Parameters.AddWithValue("$client",clientOrderId);

        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var hash=reader.GetString(0);
        var artifactHash=reader.GetString(1);
        var intentHash=reader.GetString(2);
        var provider=reader.GetString(3);
        var environment=reader.GetString(4);
        var strategy=reader.GetString(5);
        var version=reader.GetString(6);
        var symbol=reader.GetString(7);
        var state=reader.GetString(8);
        var observed=DateTimeOffset.Parse(reader.GetString(9),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
        var marketHash=reader.GetString(10);
        var ruleHash=reader.GetString(11);
        var bytes=(byte[])reader[12];
        if (await reader.ReadAsync(ct))
            throw new InvalidOperationException("Multiple execution simulation sources exist for one execution intent.");

        if (!ExecutionSimulationSourceCanonicalizerV1.TryDeserialize(bytes,hash,out var source)
            || source is null
            || !string.Equals(source.CorrelationId,correlationId,StringComparison.Ordinal)
            || !string.Equals(source.ClientOrderId,clientOrderId,StringComparison.Ordinal)
            || !string.Equals(source.ArtifactSha256,artifactHash,StringComparison.Ordinal)
            || !string.Equals(source.IntentSha256,intentHash,StringComparison.Ordinal)
            || !string.Equals(source.ProviderId,provider,StringComparison.Ordinal)
            || !string.Equals(source.Environment,environment,StringComparison.Ordinal)
            || !string.Equals(source.StrategyId,strategy,StringComparison.Ordinal)
            || !string.Equals(source.StrategyVersion,version,StringComparison.Ordinal)
            || !string.Equals(source.Symbol,symbol,StringComparison.Ordinal)
            || !string.Equals(source.State.ToString(),state,StringComparison.Ordinal)
            || source.SourceObservedAtUtc!=observed
            || !string.Equals(source.MarketProvenanceSha256,marketHash,StringComparison.Ordinal)
            || !string.Equals(source.VenueRuleSha256,ruleHash,StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted execution simulation source failed canonical verification.");

        return source;
    }

    internal async Task<ExecutionSimulationFillV1?> GetExecutionSimulationFillAsync(
        string correlationId,
        string clientOrderId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.",nameof(correlationId));
        if (string.IsNullOrWhiteSpace(clientOrderId))
            throw new ArgumentException("Client order id is required.",nameof(clientOrderId));

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionSimulationStorageAsync(connection,ct);

        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT canonical_sha256
            FROM execution_simulated_fills
            WHERE correlation_id=$correlation AND client_order_id=$client
            ORDER BY simulated_at ASC,rowid ASC
            LIMIT 2;
            """;
        query.Parameters.AddWithValue("$correlation",correlationId);
        query.Parameters.AddWithValue("$client",clientOrderId);
        var hashes=new List<string>();
        await using(var reader=await query.ExecuteReaderAsync(ct))
            while(await reader.ReadAsync(ct))hashes.Add(reader.GetString(0));

        if(hashes.Count==0)return null;
        if(hashes.Count>1)
            throw new InvalidOperationException("Multiple simulated fills exist for one execution intent.");

        var fill=await ReadPersistedSimulationFillAsync(connection,hashes[0],ct);
        if(fill is null||!ExecutionSimulationFillCanonicalizerV1.IsCanonical(fill))
            throw new InvalidOperationException("Persisted simulated fill failed canonical verification.");
        if(!string.Equals(fill.CorrelationId,correlationId,StringComparison.Ordinal)
           ||!string.Equals(fill.ClientOrderId,clientOrderId,StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted simulated fill identity is inconsistent.");
        return fill;
    }

    private static async Task EnsureExecutionSimulationSourceStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS execution_simulation_sources(
                canonical_sha256 TEXT PRIMARY KEY,
                correlation_id TEXT NOT NULL,
                client_order_id TEXT NOT NULL,
                artifact_sha256 TEXT NOT NULL,
                intent_sha256 TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                environment TEXT NOT NULL CHECK(environment='Testnet'),
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                state TEXT NOT NULL,
                source_observed_at TEXT NOT NULL,
                market_provenance_sha256 TEXT NOT NULL,
                venue_rule_sha256 TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(correlation_id,client_order_id));
            CREATE INDEX IF NOT EXISTS ix_execution_simulation_source_identity
                ON execution_simulation_sources(strategy_id,strategy_version,source_observed_at DESC);
            CREATE TRIGGER IF NOT EXISTS execution_simulation_sources_no_update
                BEFORE UPDATE ON execution_simulation_sources
                BEGIN SELECT RAISE(ABORT,'execution simulation sources are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_simulation_sources_no_delete
                BEFORE DELETE ON execution_simulation_sources
                BEGIN SELECT RAISE(ABORT,'execution simulation sources are append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}

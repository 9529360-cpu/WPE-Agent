using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    internal async Task<bool> SaveStrategyExposureTimelineAsync(
        StrategyExposureTimelineArtifactV1 artifact,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if(!StrategyExposureTimelineV1.IsCanonical(artifact))
            throw new InvalidOperationException("Strategy exposure timeline canonical identity is invalid.");

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureStrategyExposureTimelineStorageAsync(connection,ct);

        await using(var insert=connection.CreateCommand())
        {
            insert.CommandText="""
                INSERT OR IGNORE INTO strategy_exposure_timeline_artifacts(
                    canonical_sha256,schema,strategy_id,strategy_version,symbol,decision_count,
                    first_tradable_at,last_tradable_at,canonical_bytes)
                VALUES($hash,$schema,$strategy,$version,$symbol,$count,$first,$last,$bytes);
                SELECT changes();
                """;
            insert.Parameters.AddWithValue("$hash",artifact.CanonicalSha256);
            insert.Parameters.AddWithValue("$schema",artifact.Schema);
            insert.Parameters.AddWithValue("$strategy",artifact.StrategyId);
            insert.Parameters.AddWithValue("$version",artifact.StrategyVersion);
            insert.Parameters.AddWithValue("$symbol",artifact.Symbol);
            insert.Parameters.AddWithValue("$count",artifact.DecisionCount);
            insert.Parameters.AddWithValue("$first",artifact.FirstTradableAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$last",artifact.LastTradableAtUtc.ToString("O"));
            insert.Parameters.Add("$bytes",SqliteType.Blob).Value=artifact.CanonicalBytes;
            if(Convert.ToInt32(await insert.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1)
                return true;
        }

        await using var query=connection.CreateCommand();
        query.CommandText="""
            SELECT schema,strategy_id,strategy_version,symbol,decision_count,
                   first_tradable_at,last_tradable_at,canonical_bytes
            FROM strategy_exposure_timeline_artifacts
            WHERE canonical_sha256=$hash
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$hash",artifact.CanonicalSha256);
        await using var reader=await query.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Strategy exposure timeline idempotent replay lost persisted evidence.");

        var bytes=(byte[])reader[7];
        if(!string.Equals(reader.GetString(0),artifact.Schema,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(1),artifact.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(2),artifact.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(3),artifact.Symbol,StringComparison.Ordinal)
           ||reader.GetInt32(4)!=artifact.DecisionCount
           ||DateTimeOffset.Parse(reader.GetString(5),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=artifact.FirstTradableAtUtc
           ||DateTimeOffset.Parse(reader.GetString(6),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=artifact.LastTradableAtUtc
           ||!CryptographicOperations.FixedTimeEquals(bytes,artifact.CanonicalBytes))
            throw new InvalidOperationException("Persisted strategy exposure timeline conflicts with canonical replay.");

        return false;
    }

    internal async Task<StrategyExposureTimelineArtifactV1?> GetStrategyExposureTimelineAsync(
        string canonicalSha256,
        string strategyId,
        string strategyVersion,
        string symbol,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(canonicalSha256)
           ||string.IsNullOrWhiteSpace(strategyId)
           ||string.IsNullOrWhiteSpace(strategyVersion)
           ||string.IsNullOrWhiteSpace(symbol))
            return null;

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureStrategyExposureTimelineStorageAsync(connection,ct);

        await using var query=connection.CreateCommand();
        query.CommandText="""
            SELECT schema,strategy_id,strategy_version,symbol,decision_count,
                   first_tradable_at,last_tradable_at,canonical_bytes
            FROM strategy_exposure_timeline_artifacts
            WHERE canonical_sha256=$hash
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$hash",canonicalSha256);
        await using var reader=await query.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return null;

        var bytes=(byte[])reader[7];
        if(!StrategyExposureTimelineV1.TryDeserializeArtifact(bytes,canonicalSha256,out var artifact)
           ||artifact is null
           ||!string.Equals(reader.GetString(0),artifact.Schema,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(1),strategyId,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(1),artifact.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(2),strategyVersion,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(2),artifact.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(3),symbol,StringComparison.Ordinal)
           ||!string.Equals(reader.GetString(3),artifact.Symbol,StringComparison.Ordinal)
           ||reader.GetInt32(4)!=artifact.DecisionCount
           ||DateTimeOffset.Parse(reader.GetString(5),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=artifact.FirstTradableAtUtc
           ||DateTimeOffset.Parse(reader.GetString(6),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=artifact.LastTradableAtUtc)
            throw new InvalidOperationException("Persisted strategy exposure timeline failed canonical read verification.");

        return artifact;
    }

    private static async Task EnsureStrategyExposureTimelineStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS strategy_exposure_timeline_artifacts(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                decision_count INTEGER NOT NULL CHECK(decision_count>0),
                first_tradable_at TEXT NOT NULL,
                last_tradable_at TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_strategy_exposure_timeline_identity
                ON strategy_exposure_timeline_artifacts(
                    strategy_id,strategy_version,symbol,last_tradable_at DESC);
            CREATE TRIGGER IF NOT EXISTS strategy_exposure_timeline_no_update
                BEFORE UPDATE ON strategy_exposure_timeline_artifacts
                BEGIN SELECT RAISE(ABORT,'strategy exposure timelines are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS strategy_exposure_timeline_no_delete
                BEFORE DELETE ON strategy_exposure_timeline_artifacts
                BEGIN SELECT RAISE(ABORT,'strategy exposure timelines are append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}

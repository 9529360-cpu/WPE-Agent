using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    internal async Task<bool> SaveStrategyQualificationAsync(
        StrategyQualificationArtifactV1 artifact,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if(!StrategyQualificationArtifactCanonicalizerV1.IsCanonical(artifact))
            throw new InvalidOperationException("Strategy qualification artifact is not canonical.");

        var rows=await GetCanonicalStrategyShadowObservationsAsync(
            artifact.StrategyId,
            artifact.StrategyVersion,
            ct);
        var replay=StrategyQualificationArtifactCanonicalizerV1.Create(rows,artifact.QualifiedAtUtc);
        if(!string.Equals(replay.CanonicalSha256,artifact.CanonicalSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes,artifact.CanonicalBytes))
            throw new InvalidOperationException("Strategy qualification artifact does not replay from persisted Shadow evidence.");

        await using var connection=new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureStrategyQualificationStorageAsync(connection,ct);

        await using(var existing=connection.CreateCommand())
        {
            existing.CommandText="""
                SELECT canonical_sha256,canonical_bytes
                FROM strategy_qualification_artifacts
                WHERE strategy_id=$strategy AND strategy_version=$version
                LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$strategy",artifact.StrategyId);
            existing.Parameters.AddWithValue("$version",artifact.StrategyVersion);
            await using var reader=await existing.ExecuteReaderAsync(ct);
            if(await reader.ReadAsync(ct))
            {
                var hash=reader.GetString(0);
                var bytes=(byte[])reader[1];
                if(!string.Equals(hash,artifact.CanonicalSha256,StringComparison.Ordinal)
                   ||!CryptographicOperations.FixedTimeEquals(bytes,artifact.CanonicalBytes))
                    throw new InvalidOperationException("Strategy version already has conflicting qualification evidence.");
                return false;
            }
        }

        await using var insert=connection.CreateCommand();
        insert.CommandText="""
            INSERT INTO strategy_qualification_artifacts(
                canonical_sha256,schema,policy_sha256,strategy_id,strategy_version,symbol,
                backtest_validation_sha256,timeline_sha256,shadow_evidence_set_sha256,
                shadow_observation_count,qualified_at,canonical_bytes)
            VALUES(
                $hash,$schema,$policy,$strategy,$version,$symbol,
                $validation,$timeline,$evidence,$count,$qualified,$bytes);
            """;
        insert.Parameters.AddWithValue("$hash",artifact.CanonicalSha256);
        insert.Parameters.AddWithValue("$schema",artifact.Schema);
        insert.Parameters.AddWithValue("$policy",artifact.PolicySha256);
        insert.Parameters.AddWithValue("$strategy",artifact.StrategyId);
        insert.Parameters.AddWithValue("$version",artifact.StrategyVersion);
        insert.Parameters.AddWithValue("$symbol",artifact.Symbol);
        insert.Parameters.AddWithValue("$validation",artifact.BacktestValidationSha256);
        insert.Parameters.AddWithValue("$timeline",artifact.TimelineSha256);
        insert.Parameters.AddWithValue("$evidence",artifact.ShadowEvidenceSetSha256);
        insert.Parameters.AddWithValue("$count",artifact.ShadowObservationCount);
        insert.Parameters.AddWithValue("$qualified",artifact.QualifiedAtUtc.ToString("O"));
        insert.Parameters.Add("$bytes",SqliteType.Blob).Value=artifact.CanonicalBytes;
        await insert.ExecuteNonQueryAsync(ct);
        return true;
    }

    internal async Task<StrategyQualificationArtifactV1?> GetStrategyQualificationAsync(
        string strategyId,
        string strategyVersion,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(strategyId)||string.IsNullOrWhiteSpace(strategyVersion))
            return null;

        StrategyQualificationArtifactV1? artifact;
        await using(var connection=new SqliteConnection(_cs))
        {
            await connection.OpenAsync(ct);
            await EnsureStrategyQualificationStorageAsync(connection,ct);

            await using var query=connection.CreateCommand();
            query.CommandText="""
                SELECT canonical_sha256,schema,policy_sha256,strategy_id,strategy_version,symbol,
                       backtest_validation_sha256,timeline_sha256,shadow_evidence_set_sha256,
                       shadow_observation_count,qualified_at,canonical_bytes
                FROM strategy_qualification_artifacts
                WHERE strategy_id=$strategy AND strategy_version=$version
                LIMIT 1;
                """;
            query.Parameters.AddWithValue("$strategy",strategyId);
            query.Parameters.AddWithValue("$version",strategyVersion);
            await using var reader=await query.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))return null;

            var hash=reader.GetString(0);
            var bytes=(byte[])reader[11];
            if(!StrategyQualificationArtifactCanonicalizerV1.TryDeserialize(bytes,hash,out artifact)
               ||artifact is null
               ||!string.Equals(reader.GetString(1),artifact.Schema,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(2),artifact.PolicySha256,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(3),artifact.StrategyId,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(4),artifact.StrategyVersion,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(5),artifact.Symbol,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(6),artifact.BacktestValidationSha256,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(7),artifact.TimelineSha256,StringComparison.Ordinal)
               ||!string.Equals(reader.GetString(8),artifact.ShadowEvidenceSetSha256,StringComparison.Ordinal)
               ||reader.GetInt32(9)!=artifact.ShadowObservationCount
               ||DateTimeOffset.Parse(reader.GetString(10),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)!=artifact.QualifiedAtUtc)
                throw new InvalidOperationException("Persisted strategy qualification metadata does not match canonical bytes.");
        }

        var rows=await GetCanonicalStrategyShadowObservationsAsync(strategyId,strategyVersion,ct);
        var replay=StrategyQualificationArtifactCanonicalizerV1.Create(rows,artifact.QualifiedAtUtc);
        if(!string.Equals(replay.CanonicalSha256,artifact.CanonicalSha256,StringComparison.Ordinal)
           ||!CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes,artifact.CanonicalBytes))
            throw new InvalidOperationException("Persisted strategy qualification no longer replays from canonical Shadow evidence.");

        return artifact;
    }

    private static async Task EnsureStrategyQualificationStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS strategy_qualification_artifacts(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                policy_sha256 TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                backtest_validation_sha256 TEXT NOT NULL,
                timeline_sha256 TEXT NOT NULL,
                shadow_evidence_set_sha256 TEXT NOT NULL,
                shadow_observation_count INTEGER NOT NULL CHECK(shadow_observation_count>0),
                qualified_at TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(strategy_id,strategy_version));
            CREATE INDEX IF NOT EXISTS ix_strategy_qualification_time
                ON strategy_qualification_artifacts(qualified_at DESC);
            CREATE TRIGGER IF NOT EXISTS strategy_qualification_no_update
                BEFORE UPDATE ON strategy_qualification_artifacts
                BEGIN SELECT RAISE(ABORT,'strategy qualification artifacts are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS strategy_qualification_no_delete
                BEFORE DELETE ON strategy_qualification_artifacts
                BEGIN SELECT RAISE(ABORT,'strategy qualification artifacts are append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}

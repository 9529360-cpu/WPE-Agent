using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionRealityDriftPersistenceResultV1(bool Succeeded, bool Idempotent, string Code);

public sealed record ExecutionRealityDriftSummaryV1(
    string StrategyId,
    string StrategyVersion,
    int ObservationCount,
    int ComparableCount,
    int TerminalCount,
    decimal AverageFillRatio,
    decimal AverageSlippageDriftBps,
    decimal AverageFeeDriftBps,
    decimal AverageTotalExecutionDriftBps,
    long MaximumObservationLatencyMs,
    DateTimeOffset? LatestObservedAtUtc);

public sealed partial class AgentSqliteStore
{
    public async Task<ExecutionRealityDriftPersistenceResultV1> SaveExecutionRealityDriftAsync(
        ExecutionRealityDriftFactV1 fact,
        CancellationToken ct)
    {
        if (!ExecutionRealityDriftV1.IsCanonical(fact))
            throw new InvalidOperationException("Execution reality drift fact is not canonical.");

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionRealityDriftStorageAsync(connection, ct);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO execution_reality_drift(
                canonical_sha256,schema,correlation_id,client_order_id,strategy_id,strategy_version,symbol,side,
                reduce_only,order_type,intended_quantity,executed_quantity,expected_price,average_price,
                exchange_status,state,terminal,comparable,fill_ratio,adverse_slippage_bps,expected_slippage_bps,
                slippage_drift_bps,observed_fee,observed_fee_rate_bps,expected_commission_bps,fee_drift_bps,
                total_execution_drift_bps,observation_latency_ms,exchange_updated_at,observed_at,reason_code,canonical_bytes)
            VALUES(
                $hash,$schema,$correlation,$client,$strategy,$version,$symbol,$side,
                $reduce,$type,$intended,$executed,$expectedPrice,$averagePrice,
                $status,$state,$terminal,$comparable,$fillRatio,$adverse,$expectedSlippage,
                $slippageDrift,$fee,$feeRate,$expectedCommission,$feeDrift,
                $totalDrift,$latency,$exchangeUpdated,$observed,$reason,$bytes)
            ON CONFLICT(canonical_sha256) DO NOTHING;
            """;
        BindFact(insert, fact);
        var affected = await insert.ExecuteNonQueryAsync(ct);
        if (affected == 1)
            return new(true, false, "stored");

        await using var existing = connection.CreateCommand();
        existing.CommandText = "SELECT canonical_bytes FROM execution_reality_drift WHERE canonical_sha256=$hash";
        existing.Parameters.AddWithValue("$hash", fact.CanonicalSha256);
        var value = await existing.ExecuteScalarAsync(ct);
        if (value is not byte[] bytes || !CryptographicOperations.FixedTimeEquals(bytes, fact.CanonicalBytes))
            throw new InvalidOperationException("Execution reality drift hash collision or persistence conflict.");

        return new(true, true, "idempotent");
    }

    public async Task<IReadOnlyList<ExecutionRealityDriftFactV1>> GetRecentExecutionRealityDriftAsync(
        int limit,
        CancellationToken ct,
        string? strategyId = null,
        string? strategyVersion = null)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsureExecutionRealityDriftStorageAsync(connection, ct);

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

        query.CommandText = $"""
            SELECT schema,correlation_id,client_order_id,strategy_id,strategy_version,symbol,side,reduce_only,order_type,
                   intended_quantity,executed_quantity,expected_price,average_price,exchange_status,state,terminal,
                   comparable,fill_ratio,adverse_slippage_bps,expected_slippage_bps,slippage_drift_bps,observed_fee,
                   observed_fee_rate_bps,expected_commission_bps,fee_drift_bps,total_execution_drift_bps,
                   observation_latency_ms,exchange_updated_at,observed_at,reason_code,canonical_bytes,canonical_sha256
            FROM execution_reality_drift
            {(where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where))}
            ORDER BY observed_at DESC, rowid DESC
            LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$limit", limit);

        var result = new List<ExecutionRealityDriftFactV1>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fact = ReadFact(reader);
            if (!ExecutionRealityDriftV1.IsCanonical(fact))
                throw new InvalidOperationException("Persisted execution reality drift fact failed canonical verification.");
            result.Add(fact);
        }
        return result;
    }

    public async Task<ExecutionRealityDriftSummaryV1?> GetExecutionRealityDriftSummaryAsync(
        string strategyId,
        string strategyVersion,
        int limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(strategyId)) throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        if (string.IsNullOrWhiteSpace(strategyVersion)) throw new ArgumentException("Strategy version is required.", nameof(strategyVersion));

        var values = await GetRecentExecutionRealityDriftAsync(limit, ct, strategyId, strategyVersion);
        if (values.Count == 0) return null;

        var comparable = values.Where(x => x.Comparable).ToArray();
        return new(
            strategyId,
            strategyVersion,
            values.Count,
            comparable.Length,
            values.Count(x => x.Terminal),
            comparable.Length == 0 ? 0 : comparable.Average(x => x.FillRatio),
            comparable.Length == 0 ? 0 : comparable.Average(x => x.SlippageDriftBps),
            comparable.Length == 0 ? 0 : comparable.Average(x => x.FeeDriftBps),
            comparable.Length == 0 ? 0 : comparable.Average(x => x.TotalExecutionDriftBps),
            values.Max(x => x.ObservationLatencyMs),
            values.Max(x => x.ObservedAtUtc));
    }

    private static async Task EnsureExecutionRealityDriftStorageAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS execution_reality_drift(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                client_order_id TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                side TEXT NOT NULL,
                reduce_only INTEGER NOT NULL CHECK(reduce_only IN (0,1)),
                order_type TEXT NOT NULL,
                intended_quantity TEXT NOT NULL,
                executed_quantity TEXT NOT NULL,
                expected_price TEXT NOT NULL,
                average_price TEXT NOT NULL,
                exchange_status TEXT NOT NULL,
                state TEXT NOT NULL,
                terminal INTEGER NOT NULL CHECK(terminal IN (0,1)),
                comparable INTEGER NOT NULL CHECK(comparable IN (0,1)),
                fill_ratio TEXT NOT NULL,
                adverse_slippage_bps TEXT NOT NULL,
                expected_slippage_bps TEXT NOT NULL,
                slippage_drift_bps TEXT NOT NULL,
                observed_fee TEXT NOT NULL,
                observed_fee_rate_bps TEXT NOT NULL,
                expected_commission_bps TEXT NOT NULL,
                fee_drift_bps TEXT NOT NULL,
                total_execution_drift_bps TEXT NOT NULL,
                observation_latency_ms INTEGER NOT NULL,
                exchange_updated_at TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                reason_code TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_execution_reality_drift_observed
                ON execution_reality_drift(observed_at DESC);
            CREATE INDEX IF NOT EXISTS ix_execution_reality_drift_strategy
                ON execution_reality_drift(strategy_id,strategy_version,observed_at DESC);
            CREATE INDEX IF NOT EXISTS ix_execution_reality_drift_client
                ON execution_reality_drift(client_order_id,observed_at DESC);
            CREATE TRIGGER IF NOT EXISTS execution_reality_drift_no_update
                BEFORE UPDATE ON execution_reality_drift
                BEGIN SELECT RAISE(ABORT,'execution reality drift is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_reality_drift_no_delete
                BEFORE DELETE ON execution_reality_drift
                BEGIN SELECT RAISE(ABORT,'execution reality drift is append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void BindFact(SqliteCommand command, ExecutionRealityDriftFactV1 fact)
    {
        command.Parameters.AddWithValue("$hash", fact.CanonicalSha256);
        command.Parameters.AddWithValue("$schema", fact.Schema);
        command.Parameters.AddWithValue("$correlation", fact.CorrelationId);
        command.Parameters.AddWithValue("$client", fact.ClientOrderId);
        command.Parameters.AddWithValue("$strategy", fact.StrategyId);
        command.Parameters.AddWithValue("$version", fact.StrategyVersion);
        command.Parameters.AddWithValue("$symbol", fact.Symbol);
        command.Parameters.AddWithValue("$side", fact.Side.ToString());
        command.Parameters.AddWithValue("$reduce", fact.ReduceOnly ? 1 : 0);
        command.Parameters.AddWithValue("$type", fact.OrderType.ToString());
        command.Parameters.AddWithValue("$intended", Text(fact.IntendedQuantity));
        command.Parameters.AddWithValue("$executed", Text(fact.ExecutedQuantity));
        command.Parameters.AddWithValue("$expectedPrice", Text(fact.ExpectedPrice));
        command.Parameters.AddWithValue("$averagePrice", Text(fact.AveragePrice));
        command.Parameters.AddWithValue("$status", fact.ExchangeStatus);
        command.Parameters.AddWithValue("$state", fact.State.ToString());
        command.Parameters.AddWithValue("$terminal", fact.Terminal ? 1 : 0);
        command.Parameters.AddWithValue("$comparable", fact.Comparable ? 1 : 0);
        command.Parameters.AddWithValue("$fillRatio", Text(fact.FillRatio));
        command.Parameters.AddWithValue("$adverse", Text(fact.AdverseSlippageBps));
        command.Parameters.AddWithValue("$expectedSlippage", Text(fact.ExpectedSlippageBps));
        command.Parameters.AddWithValue("$slippageDrift", Text(fact.SlippageDriftBps));
        command.Parameters.AddWithValue("$fee", Text(fact.ObservedFee));
        command.Parameters.AddWithValue("$feeRate", Text(fact.ObservedFeeRateBps));
        command.Parameters.AddWithValue("$expectedCommission", Text(fact.ExpectedCommissionBps));
        command.Parameters.AddWithValue("$feeDrift", Text(fact.FeeDriftBps));
        command.Parameters.AddWithValue("$totalDrift", Text(fact.TotalExecutionDriftBps));
        command.Parameters.AddWithValue("$latency", fact.ObservationLatencyMs);
        command.Parameters.AddWithValue("$exchangeUpdated", fact.ExchangeUpdatedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$observed", fact.ObservedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$reason", fact.ReasonCode);
        command.Parameters.AddWithValue("$bytes", fact.CanonicalBytes);
    }

    private static ExecutionRealityDriftFactV1 ReadFact(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        Enum.Parse<PositionSide>(reader.GetString(6), false),
        reader.GetInt32(7) == 1,
        Enum.Parse<ExecutionOrderType>(reader.GetString(8), false),
        Decimal(reader.GetString(9)),
        Decimal(reader.GetString(10)),
        Decimal(reader.GetString(11)),
        Decimal(reader.GetString(12)),
        reader.GetString(13),
        Enum.Parse<ExecutionRealityStateV1>(reader.GetString(14), false),
        reader.GetInt32(15) == 1,
        reader.GetInt32(16) == 1,
        Decimal(reader.GetString(17)),
        Decimal(reader.GetString(18)),
        Decimal(reader.GetString(19)),
        Decimal(reader.GetString(20)),
        Decimal(reader.GetString(21)),
        Decimal(reader.GetString(22)),
        Decimal(reader.GetString(23)),
        Decimal(reader.GetString(24)),
        Decimal(reader.GetString(25)),
        reader.GetInt64(26),
        DateTimeOffset.Parse(reader.GetString(27), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(28), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetString(29),
        (byte[])reader[30],
        reader.GetString(31));

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal Decimal(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}

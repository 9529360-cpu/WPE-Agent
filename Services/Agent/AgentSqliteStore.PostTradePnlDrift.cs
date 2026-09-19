using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    private sealed record PostTradeActualOutcomeSourceV1(
        string CloseClientOrderId,
        string CloseCycleId,
        string Symbol,
        string Side,
        decimal EntryPrice,
        decimal ExitPrice,
        decimal Quantity,
        decimal GrossPnl,
        decimal Fees,
        string FeeBasis,
        decimal FundingAmount,
        string FundingBasis,
        decimal NetPnl,
        DateTimeOffset ClosedAtUtc,
        string StrategyId,
        string StrategyVersion,
        string AttributionBasis);

    internal async Task RecordPostTradePnlDriftBestEffortAsync(
        string closeClientOrderId,
        CancellationToken ct)
    {
        try
        {
            await TryBuildAndSavePostTradePnlDriftAsync(closeClientOrderId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Evidence-only projection. Never alter an already-recorded execution outcome.
        }
    }

    internal async Task<PostTradePnlDriftV1?> TryBuildAndSavePostTradePnlDriftAsync(
        string closeClientOrderId,
        CancellationToken ct)
    {
        var fact = await BuildPostTradePnlDriftAsync(closeClientOrderId, ct);
        if (fact is null)
            return null;

        await SavePostTradePnlDriftAsync(fact, ct);
        return fact;
    }

    internal async Task<int> BackfillMissingPostTradePnlDriftAsync(
        int limit,
        CancellationToken ct)
    {
        var boundedLimit=Math.Clamp(limit,1,100);
        var closeIds=new List<string>();
        await using(var connection=new SqliteConnection(_cs))
        {
            await connection.OpenAsync(ct);
            await EnsurePostTradePnlDriftStorageAsync(connection,ct);
            await using var query=connection.CreateCommand();
            query.CommandText="""
                SELECT outcome.client_order_id
                FROM trade_outcomes AS outcome
                LEFT JOIN post_trade_pnl_drift AS drift
                  ON drift.close_client_order_id=outcome.client_order_id
                WHERE outcome.attribution_basis='automatic-artifact'
                  AND outcome.client_order_id IS NOT NULL
                  AND drift.close_client_order_id IS NULL
                ORDER BY outcome.closed_at ASC,outcome.id ASC
                LIMIT $limit;
                """;
            query.Parameters.AddWithValue("$limit",boundedLimit);
            await using var reader=await query.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))closeIds.Add(reader.GetString(0));
        }

        var written=0;
        foreach(var closeId in closeIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if(await TryBuildAndSavePostTradePnlDriftAsync(closeId,ct) is not null)written++;
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                try{await RecordErrorAsync("PostTradePnlDriftBackfill",ex,CancellationToken.None);}catch{}
            }
        }
        return written;
    }

    internal async Task<IReadOnlyList<PostTradePnlDriftV1>> GetRecentPostTradePnlDriftAsync(
        int limit,
        CancellationToken ct,
        string? strategyId = null,
        string? strategyVersion = null)
    {
        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsurePostTradePnlDriftStorageAsync(connection, ct);

        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT canonical_sha256,canonical_bytes
            FROM post_trade_pnl_drift
            WHERE ($strategy='' OR strategy_id=$strategy)
              AND ($version='' OR strategy_version=$version)
            ORDER BY compared_at DESC,rowid DESC
            LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$strategy", strategyId ?? string.Empty);
        query.Parameters.AddWithValue("$version", strategyVersion ?? string.Empty);
        query.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

        var persisted = new List<(string Hash, byte[] Bytes)>();
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                persisted.Add((reader.GetString(0), (byte[])reader[1]));
        }

        var rows = new List<PostTradePnlDriftV1>(persisted.Count);
        foreach (var row in persisted)
        {
            if (!PostTradePnlDriftCanonicalizerV1.TryDeserialize(row.Bytes, row.Hash, out var fact)
                || fact is null)
                throw new InvalidOperationException("Persisted post-trade PnL drift failed canonical verification.");

            var replay = await BuildPostTradePnlDriftAsync(fact.CloseClientOrderId, ct);
            if (replay is null
                || !string.Equals(replay.CanonicalSha256, fact.CanonicalSha256, StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes, fact.CanonicalBytes))
                throw new InvalidOperationException("Persisted post-trade PnL drift no longer replays from its source evidence.");

            rows.Add(fact);
        }

        return rows;
    }

    private async Task<PostTradePnlDriftV1?> BuildPostTradePnlDriftAsync(
        string closeClientOrderId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(closeClientOrderId))
            throw new ArgumentException("Close client-order id is required.", nameof(closeClientOrderId));

        var actual = await ReadPostTradeActualOutcomeAsync(closeClientOrderId, ct);
        if (actual is null
            || !string.Equals(actual.AttributionBasis, "automatic-artifact", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(actual.StrategyId))
            return null;

        if (!Enum.TryParse<PositionSide>(actual.Side, false, out var side)
            || !Enum.IsDefined(side))
            return null;

        var proof = await GetPostTradeEntryLedgerProofAsync(closeClientOrderId, ct);
        if (proof is null
            || !string.Equals(proof.CloseCycleId, actual.CloseCycleId, StringComparison.Ordinal)
            || !string.Equals(proof.Symbol, actual.Symbol, StringComparison.Ordinal)
            || !string.Equals(proof.Side, actual.Side, StringComparison.Ordinal)
            || proof.ClosingQuantity != actual.Quantity
            || proof.WeightedEntryPrice != actual.EntryPrice)
            return null;

        var sourceRefs = new List<PostTradePnlSimulationSourceV1>(proof.Events.Count);
        decimal simulatedOpenQuantity = 0m;
        decimal simulatedOpenCost = 0m;
        decimal simulatedOpenFeePool = 0m;
        var simulatedFeeEvidenceComplete = true;

        foreach (var item in proof.Events.OrderBy(x => x.Sequence))
        {
            var evidence = await GetVerifiedSimulationEvidenceAsync(item.CycleId, item.ClientOrderId, ct);
            if (evidence is null
                || !SimulationMatchesActualLedgerEvent(evidence.Value.Fill, actual, side, item))
                return null;
            var source = evidence.Value.Source;
            var fill = evidence.Value.Fill;

            sourceRefs.Add(new(
                item.Sequence,
                item.CycleId,
                item.ClientOrderId,
                item.ReduceOnly,
                source.CanonicalSha256,
                fill.CanonicalSha256));

            if (!item.ReduceOnly)
            {
                simulatedOpenQuantity += fill.ExecutedQuantity;
                simulatedOpenCost += fill.ExecutedQuantity * fill.AveragePrice;
                if (fill.FeeRole == ExecutionSimulationFeeRoleV1.Unavailable)
                    simulatedFeeEvidenceComplete = false;
                simulatedOpenFeePool += fill.FeeAmount;
                continue;
            }

            if (fill.ExecutedQuantity > simulatedOpenQuantity || simulatedOpenQuantity <= 0)
                return null;

            var average = simulatedOpenCost / simulatedOpenQuantity;
            var feePerUnit = simulatedOpenFeePool / simulatedOpenQuantity;
            simulatedOpenQuantity -= fill.ExecutedQuantity;
            simulatedOpenCost -= average * fill.ExecutedQuantity;
            simulatedOpenFeePool -= feePerUnit * fill.ExecutedQuantity;
            if (simulatedOpenQuantity == 0m)
            {
                simulatedOpenCost = 0m;
                simulatedOpenFeePool = 0m;
            }
        }

        if (simulatedOpenQuantity != proof.PreCloseOpenQuantity
            || simulatedOpenQuantity < actual.Quantity
            || simulatedOpenQuantity <= 0
            || simulatedOpenCost <= 0)
            return null;

        var closeEvidence = await GetVerifiedSimulationEvidenceAsync(actual.CloseCycleId, actual.CloseClientOrderId, ct);
        if (closeEvidence is null)
            return null;
        var closeSource = closeEvidence.Value.Source;
        var closeFill = closeEvidence.Value.Fill;
        if (closeFill.State is not (ExecutionSimulationFillStateV1.Filled or ExecutionSimulationFillStateV1.Partial)
            || !string.Equals(closeFill.CorrelationId, actual.CloseCycleId, StringComparison.Ordinal)
            || !string.Equals(closeFill.ClientOrderId, actual.CloseClientOrderId, StringComparison.Ordinal)
            || !string.Equals(closeFill.StrategyId, actual.StrategyId, StringComparison.Ordinal)
            || !string.Equals(closeFill.StrategyVersion, actual.StrategyVersion, StringComparison.Ordinal)
            || !string.Equals(closeFill.Symbol, actual.Symbol, StringComparison.Ordinal)
            || closeFill.Side != side
            || !closeFill.ReduceOnly
            || closeFill.ExecutedQuantity != actual.Quantity
            || closeFill.AveragePrice <= 0)
            return null;

        if (closeFill.FeeRole == ExecutionSimulationFeeRoleV1.Unavailable)
            simulatedFeeEvidenceComplete = false;

        var simulatedEntryPrice = simulatedOpenCost / simulatedOpenQuantity;
        var simulatedEntryFee = simulatedOpenFeePool / simulatedOpenQuantity * actual.Quantity;
        var simulatedFees = simulatedFeeEvidenceComplete
            ? simulatedEntryFee + closeFill.FeeAmount
            : 0m;

        return PostTradePnlDriftCanonicalizerV1.Create(
            actual.CloseClientOrderId,
            actual.CloseCycleId,
            actual.StrategyId,
            actual.StrategyVersion,
            actual.Symbol,
            side,
            actual.Quantity,
            proof.CanonicalSha256,
            sourceRefs,
            closeSource.CanonicalSha256,
            closeFill.CanonicalSha256,
            actual.EntryPrice,
            actual.ExitPrice,
            actual.GrossPnl,
            actual.Fees,
            actual.FeeBasis,
            actual.FundingAmount,
            actual.FundingBasis,
            actual.NetPnl,
            simulatedEntryPrice,
            closeFill.AveragePrice,
            simulatedFeeEvidenceComplete,
            simulatedFees,
            actual.ClosedAtUtc);
    }

    private async Task<(ExecutionSimulationSourceV1 Source,ExecutionSimulationFillV1 Fill)?> GetVerifiedSimulationEvidenceAsync(
        string correlationId,
        string clientOrderId,
        CancellationToken ct)
    {
        var source = await GetExecutionSimulationSourceAsync(correlationId, clientOrderId, ct);
        var fill = await GetExecutionSimulationFillAsync(correlationId, clientOrderId, ct);
        if (source is null
            || fill is null
            || !string.Equals(source.Schema,ExecutionSimulationSourceCanonicalizerV1.Schema,StringComparison.Ordinal)
            || source.State != ExecutionSimulationSourceStateV1.Available
            || !source.TopOfBookAvailable
            || !string.Equals(fill.SimulationModelVersion,AutomaticExecutionSimulationModelV1.Version,StringComparison.Ordinal))
            return null;

        ExecutionSimulationFillV1 replay;
        try
        {
            replay = AutomaticExecutionSimulationModelV1.CreateFill(source);
        }
        catch
        {
            return null;
        }

        if (!string.Equals(replay.CanonicalSha256,fill.CanonicalSha256,StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes,fill.CanonicalBytes))
            return null;

        return (source,fill);
    }

    private static bool SimulationMatchesActualLedgerEvent(
        ExecutionSimulationFillV1? fill,
        PostTradeActualOutcomeSourceV1 actual,
        PositionSide side,
        PostTradeEntryLedgerEventV1 item) =>
        fill is not null
        && fill.State is ExecutionSimulationFillStateV1.Filled or ExecutionSimulationFillStateV1.Partial
        && string.Equals(fill.CorrelationId, item.CycleId, StringComparison.Ordinal)
        && string.Equals(fill.ClientOrderId, item.ClientOrderId, StringComparison.Ordinal)
        && string.Equals(fill.StrategyId, actual.StrategyId, StringComparison.Ordinal)
        && string.Equals(fill.StrategyVersion, actual.StrategyVersion, StringComparison.Ordinal)
        && string.Equals(fill.Symbol, actual.Symbol, StringComparison.Ordinal)
        && fill.Side == side
        && fill.ReduceOnly == item.ReduceOnly
        && fill.ExecutedQuantity == item.Quantity
        && fill.AveragePrice > 0;

    private async Task<PostTradeActualOutcomeSourceV1?> ReadPostTradeActualOutcomeAsync(
        string closeClientOrderId,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,gross_pnl,
                   fees,fee_basis,funding_amount,funding_basis,net_pnl,closed_at,strategy_id,
                   strategy_version,attribution_basis
            FROM trade_outcomes
            WHERE client_order_id=$close
            LIMIT 2;
            """;
        query.Parameters.AddWithValue("$close", closeClientOrderId);

        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var row = new PostTradeActualOutcomeSourceV1(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Decimal(reader.GetString(4)),
            Decimal(reader.GetString(5)),
            Decimal(reader.GetString(6)),
            Decimal(reader.GetString(7)),
            Decimal(reader.GetString(8)),
            reader.GetString(9),
            Decimal(reader.GetString(10)),
            reader.GetString(11),
            Decimal(reader.GetString(12)),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
            reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16));

        if (await reader.ReadAsync(ct))
            throw new InvalidOperationException("Multiple post-trade outcomes exist for one close client-order id.");

        return row;
    }

    private async Task SavePostTradePnlDriftAsync(
        PostTradePnlDriftV1 fact,
        CancellationToken ct)
    {
        if (!PostTradePnlDriftCanonicalizerV1.IsCanonical(fact))
            throw new InvalidOperationException("Post-trade PnL drift is not canonical.");

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsurePostTradePnlDriftStorageAsync(connection, ct);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO post_trade_pnl_drift(
                canonical_sha256,close_client_order_id,close_cycle_id,strategy_id,strategy_version,
                symbol,state,entry_ledger_sha256,close_simulation_sha256,compared_at,canonical_bytes)
            VALUES(
                $hash,$close,$cycle,$strategy,$version,$symbol,$state,$ledger,$closeSimulation,$compared,$bytes);
            SELECT changes();
            """;
        insert.Parameters.AddWithValue("$hash", fact.CanonicalSha256);
        insert.Parameters.AddWithValue("$close", fact.CloseClientOrderId);
        insert.Parameters.AddWithValue("$cycle", fact.CloseCycleId);
        insert.Parameters.AddWithValue("$strategy", fact.StrategyId);
        insert.Parameters.AddWithValue("$version", fact.StrategyVersion);
        insert.Parameters.AddWithValue("$symbol", fact.Symbol);
        insert.Parameters.AddWithValue("$state", fact.State.ToString());
        insert.Parameters.AddWithValue("$ledger", fact.EntryLedgerSha256);
        insert.Parameters.AddWithValue("$closeSimulation", fact.CloseSimulationSha256);
        insert.Parameters.AddWithValue("$compared", fact.ComparedAtUtc.ToString("O"));
        insert.Parameters.Add("$bytes", SqliteType.Blob).Value = fact.CanonicalBytes;
        if (Convert.ToInt32(await insert.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1)
            return;

        await using var existing = connection.CreateCommand();
        existing.CommandText = """
            SELECT canonical_sha256,canonical_bytes
            FROM post_trade_pnl_drift
            WHERE close_client_order_id=$close
            LIMIT 1;
            """;
        existing.Parameters.AddWithValue("$close", fact.CloseClientOrderId);
        await using var reader = await existing.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || !string.Equals(reader.GetString(0), fact.CanonicalSha256, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals((byte[])reader[1], fact.CanonicalBytes))
            throw new InvalidOperationException("Close execution already has conflicting post-trade PnL drift evidence.");
    }

    private static async Task EnsurePostTradePnlDriftStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS post_trade_pnl_drift(
                canonical_sha256 TEXT PRIMARY KEY,
                close_client_order_id TEXT NOT NULL UNIQUE,
                close_cycle_id TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                strategy_version TEXT NOT NULL,
                symbol TEXT NOT NULL,
                state TEXT NOT NULL,
                entry_ledger_sha256 TEXT NOT NULL,
                close_simulation_sha256 TEXT NOT NULL,
                compared_at TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_post_trade_pnl_drift_strategy
                ON post_trade_pnl_drift(strategy_id,strategy_version,compared_at DESC);
            CREATE TRIGGER IF NOT EXISTS post_trade_pnl_drift_no_update
                BEFORE UPDATE ON post_trade_pnl_drift
                BEGIN SELECT RAISE(ABORT,'post-trade PnL drift is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS post_trade_pnl_drift_no_delete
                BEFORE DELETE ON post_trade_pnl_drift
                BEGIN SELECT RAISE(ABORT,'post-trade PnL drift is append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}

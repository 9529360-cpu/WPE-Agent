using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace 币安量化机器人.Services.Agent;

public sealed partial class AgentSqliteStore
{
    private static async Task<PostTradeEntryLedgerProofV1?> BuildPostTradeEntryLedgerProofAsync(
        SqliteConnection connection,
        string closeClientOrderId,
        string closeCycleId,
        string symbol,
        string side,
        decimal closingQuantity,
        CancellationToken ct)
    {
        long closeEventId;
        await using (var closeQuery = connection.CreateCommand())
        {
            closeQuery.CommandText = """
                SELECT id,cycle_id,symbol,side,reduce_only,quantity,status
                FROM execution_events
                WHERE client_order_id=$close
                LIMIT 1;
                """;
            closeQuery.Parameters.AddWithValue("$close", closeClientOrderId);
            await using var closeReader = await closeQuery.ExecuteReaderAsync(ct);
            if (!await closeReader.ReadAsync(ct)
                || !string.Equals(closeReader.GetString(1), closeCycleId, StringComparison.Ordinal)
                || !string.Equals(closeReader.GetString(2), symbol, StringComparison.Ordinal)
                || !string.Equals(closeReader.GetString(3), side, StringComparison.Ordinal)
                || closeReader.GetInt32(4) != 1
                || Decimal(closeReader.GetString(5)) != closingQuantity
                || !string.Equals(closeReader.GetString(6), "FILLED", StringComparison.OrdinalIgnoreCase))
                return null;
            closeEventId = closeReader.GetInt64(0);
        }

        var events = new List<PostTradeEntryLedgerEventV1>();
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT cycle_id,client_order_id,reduce_only,quantity,avg_price,expected_price,status,
                   occurred_at,exchange_updated_at
            FROM execution_events
            WHERE symbol=$symbol
              AND side=$side
              AND id<$closeEventId
              AND status IN ('FILLED','PARTIALLY_FILLED')
            ORDER BY id;
            """;
        query.Parameters.AddWithValue("$symbol", symbol);
        query.Parameters.AddWithValue("$side", side);
        query.Parameters.AddWithValue("$closeEventId", closeEventId);

        await using var reader = await query.ExecuteReaderAsync(ct);
        var sequence = 0;
        while (await reader.ReadAsync(ct))
        {
            var occurredAt = DateTimeOffset.Parse(
                reader.GetString(7),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).ToUniversalTime();
            DateTimeOffset? exchangeUpdatedAt = reader.IsDBNull(8)
                ? null
                : DateTimeOffset.Parse(
                    reader.GetString(8),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind).ToUniversalTime();

            events.Add(new(
                sequence++,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2) == 1,
                Decimal(reader.GetString(3)),
                Decimal(reader.GetString(4)),
                Decimal(reader.GetString(5)),
                reader.GetString(6).ToUpperInvariant(),
                occurredAt,
                exchangeUpdatedAt));
        }

        try
        {
            return PostTradeEntryLedgerProofCanonicalizerV1.Create(
                closeClientOrderId,
                closeCycleId,
                symbol,
                side,
                closingQuantity,
                events);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task SavePostTradeEntryLedgerProofAsync(
        SqliteConnection connection,
        PostTradeEntryLedgerProofV1 proof,
        CancellationToken ct)
    {
        if (!PostTradeEntryLedgerProofCanonicalizerV1.IsCanonical(proof))
            throw new InvalidOperationException("Post-trade entry ledger proof is not canonical.");

        await EnsurePostTradeEntryLedgerProofStorageAsync(connection, ct);

        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText = """
                SELECT canonical_sha256,canonical_bytes
                FROM post_trade_entry_ledger_proofs
                WHERE close_client_order_id=$close
                LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$close", proof.CloseClientOrderId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var hash = reader.GetString(0);
                var bytes = (byte[])reader[1];
                if (!string.Equals(hash, proof.CanonicalSha256, StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(bytes, proof.CanonicalBytes))
                    throw new InvalidOperationException("Close execution already has conflicting entry-ledger proof.");
                return;
            }
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO post_trade_entry_ledger_proofs(
                close_client_order_id,close_cycle_id,schema,symbol,side,closing_quantity,
                pre_close_open_quantity,pre_close_open_cost,weighted_entry_price,
                post_close_remaining_quantity,event_count,canonical_sha256,canonical_bytes)
            VALUES(
                $close,$cycle,$schema,$symbol,$side,$closing,
                $openQty,$openCost,$entry,$remaining,$count,$hash,$bytes);
            """;
        insert.Parameters.AddWithValue("$close", proof.CloseClientOrderId);
        insert.Parameters.AddWithValue("$cycle", proof.CloseCycleId);
        insert.Parameters.AddWithValue("$schema", proof.Schema);
        insert.Parameters.AddWithValue("$symbol", proof.Symbol);
        insert.Parameters.AddWithValue("$side", proof.Side);
        insert.Parameters.AddWithValue("$closing", proof.ClosingQuantity.ToString(CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$openQty", proof.PreCloseOpenQuantity.ToString(CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$openCost", proof.PreCloseOpenCost.ToString(CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$entry", proof.WeightedEntryPrice.ToString(CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$remaining", proof.PostCloseRemainingQuantity.ToString(CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$count", proof.Events.Count);
        insert.Parameters.AddWithValue("$hash", proof.CanonicalSha256);
        insert.Parameters.Add("$bytes", SqliteType.Blob).Value = proof.CanonicalBytes;
        await insert.ExecuteNonQueryAsync(ct);
    }

    internal async Task<PostTradeEntryLedgerProofV1?> GetPostTradeEntryLedgerProofAsync(
        string closeClientOrderId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(closeClientOrderId))
            throw new ArgumentException("Close client-order id is required.", nameof(closeClientOrderId));

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct);
        await EnsurePostTradeEntryLedgerProofStorageAsync(connection, ct);

        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT close_cycle_id,schema,symbol,side,closing_quantity,
                   pre_close_open_quantity,pre_close_open_cost,weighted_entry_price,
                   post_close_remaining_quantity,event_count,canonical_sha256,canonical_bytes
            FROM post_trade_entry_ledger_proofs
            WHERE close_client_order_id=$close
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$close", closeClientOrderId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var hash = reader.GetString(10);
        var bytes = (byte[])reader[11];
        if (!PostTradeEntryLedgerProofCanonicalizerV1.TryDeserialize(bytes, hash, out var proof)
            || proof is null
            || !string.Equals(reader.GetString(0), proof.CloseCycleId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), proof.Schema, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), proof.Symbol, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3), proof.Side, StringComparison.Ordinal)
            || Decimal(reader.GetString(4)) != proof.ClosingQuantity
            || Decimal(reader.GetString(5)) != proof.PreCloseOpenQuantity
            || Decimal(reader.GetString(6)) != proof.PreCloseOpenCost
            || Decimal(reader.GetString(7)) != proof.WeightedEntryPrice
            || Decimal(reader.GetString(8)) != proof.PostCloseRemainingQuantity
            || reader.GetInt32(9) != proof.Events.Count)
            throw new InvalidOperationException("Persisted post-trade entry-ledger metadata does not match canonical bytes.");

        return proof;
    }

    private static async Task EnsurePostTradeEntryLedgerProofStorageAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS post_trade_entry_ledger_proofs(
                close_client_order_id TEXT PRIMARY KEY,
                close_cycle_id TEXT NOT NULL,
                schema TEXT NOT NULL,
                symbol TEXT NOT NULL,
                side TEXT NOT NULL,
                closing_quantity TEXT NOT NULL,
                pre_close_open_quantity TEXT NOT NULL,
                pre_close_open_cost TEXT NOT NULL,
                weighted_entry_price TEXT NOT NULL,
                post_close_remaining_quantity TEXT NOT NULL,
                event_count INTEGER NOT NULL CHECK(event_count>=0),
                canonical_sha256 TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_post_trade_entry_ledger_cycle
                ON post_trade_entry_ledger_proofs(close_cycle_id,close_client_order_id);
            CREATE TRIGGER IF NOT EXISTS post_trade_entry_ledger_no_update
                BEFORE UPDATE ON post_trade_entry_ledger_proofs
                BEGIN SELECT RAISE(ABORT,'post-trade entry ledger proofs are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS post_trade_entry_ledger_no_delete
                BEFORE DELETE ON post_trade_entry_ledger_proofs
                BEGIN SELECT RAISE(ABORT,'post-trade entry ledger proofs are append-only'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}

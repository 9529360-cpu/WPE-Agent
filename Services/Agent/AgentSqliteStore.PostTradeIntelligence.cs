using Microsoft.Data.Sqlite;
using System.Globalization;

namespace 币安量化机器人.Services.Agent;

internal sealed record TradeExcursionSummary(
    decimal MaeReturnPct,
    decimal MfeReturnPct,
    string Basis,
    int SampleCount)
{
    public static readonly TradeExcursionSummary Unavailable = new(0, 0, "unavailable", 0);
}

public sealed partial class AgentSqliteStore
{
    private void EnsurePostTradeIntelligenceSchema()
    {
        using var connection = new SqliteConnection(_cs);
        connection.Open();
        EnsureColumn(connection, "trade_outcomes", "mae_return_pct", "TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(connection, "trade_outcomes", "mfe_return_pct", "TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(connection, "trade_outcomes", "excursion_basis", "TEXT NOT NULL DEFAULT 'unavailable'");
        EnsureColumn(connection, "trade_outcomes", "excursion_samples", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "trade_outcomes", "exit_reason", "TEXT NOT NULL DEFAULT 'unclassified'");
        using var command = connection.CreateCommand();
        command.CommandText = """
        CREATE TABLE IF NOT EXISTS position_mark_observations(
            symbol TEXT NOT NULL,
            side TEXT NOT NULL,
            observed_at TEXT NOT NULL,
            mark_price TEXT NOT NULL,
            quantity TEXT NOT NULL,
            PRIMARY KEY(symbol,side,observed_at));
        CREATE INDEX IF NOT EXISTS ix_position_mark_observations_window
            ON position_mark_observations(symbol,side,observed_at);
        """;
        command.ExecuteNonQuery();
    }

    public async Task SavePositionMarkObservationsAsync(
        IReadOnlyList<ManagedPosition> positions,
        DateTimeOffset observedAtUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Position mark observation time must be UTC.", nameof(observedAtUtc));

        var active = positions
            .Where(position => position.Quantity > 0 && position.MarkPrice > 0)
            .OrderBy(position => position.Symbol, StringComparer.Ordinal)
            .ThenBy(position => position.Side)
            .ToArray();
        if (active.Length == 0) return;

        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var position in active)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO position_mark_observations(symbol,side,observed_at,mark_price,quantity)
                VALUES($symbol,$side,$observed,$mark,$quantity)
                """;
            command.Parameters.AddWithValue("$symbol", position.Symbol.Trim().ToUpperInvariant());
            command.Parameters.AddWithValue("$side", position.Side.ToString());
            command.Parameters.AddWithValue("$observed", observedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$mark", position.MarkPrice.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$quantity", position.Quantity.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<TradeExcursionSummary> GetTradeExcursionAsync(
        SqliteConnection connection,
        string symbol,
        PositionSide side,
        string closeClientOrderId,
        decimal closingQuantity,
        decimal entryPrice,
        CancellationToken ct)
    {
        if (entryPrice <= 0 || closingQuantity <= 0) return TradeExcursionSummary.Unavailable;
        var window = await ResolveUnambiguousPositionWindowAsync(
            connection, symbol, side, closeClientOrderId, closingQuantity, ct).ConfigureAwait(false);
        if (window is null) return TradeExcursionSummary.Unavailable;

        var returns = new List<decimal>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mark_price FROM position_mark_observations
            WHERE symbol=$symbol AND side=$side AND observed_at >= $opened AND observed_at <= $closed
            ORDER BY observed_at
            """;
        command.Parameters.AddWithValue("$symbol", symbol.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$side", side.ToString());
        command.Parameters.AddWithValue("$opened", window.Value.Opened.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$closed", window.Value.Closed.ToString("O", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!decimal.TryParse(reader.GetString(0), NumberStyles.Number, CultureInfo.InvariantCulture, out var mark) || mark <= 0)
                continue;
            var value = side == PositionSide.Long ? (mark - entryPrice) / entryPrice : (entryPrice - mark) / entryPrice;
            returns.Add(value);
        }

        if (returns.Count == 0) return TradeExcursionSummary.Unavailable;
        return new(
            Math.Min(0m, returns.Min()),
            Math.Max(0m, returns.Max()),
            "runtime-mark-observations",
            returns.Count);
    }

    private static string ResolveExitReason(ExecutionIntent intent, ExchangeOrder order)
    {
        if (ExecutionReasonCode.TryNormalize(intent.ReasonCode, out var explicitCode)) return explicitCode;

        if (order.IsProtection)
        {
            if (ExecutionReasonCode.TryNormalize(intent.Reason, out var protectionCode) &&
                protectionCode.StartsWith("protection.", StringComparison.Ordinal))
                return protectionCode;
            return PositionExitReasonCodes.ProtectionFillReconciled;
        }

        if (ExecutionReasonCode.TryNormalize(intent.Reason, out var legacyCode)) return legacyCode;
        return "action." + intent.Action.ToString().ToLowerInvariant();
    }

    private static async Task<(DateTimeOffset Opened, DateTimeOffset Closed)?> ResolveUnambiguousPositionWindowAsync(
        SqliteConnection connection,
        string symbol,
        PositionSide side,
        string closeClientOrderId,
        decimal closingQuantity,
        CancellationToken ct)
    {
        decimal same = 0, opposite = 0;
        DateTimeOffset? opened = null, closed = null;
        var ambiguous = false;
        var found = false;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT client_order_id,side,reduce_only,quantity,COALESCE(exchange_updated_at,occurred_at)
            FROM execution_events
            WHERE symbol=$symbol AND status IN ('FILLED','PARTIALLY_FILLED')
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$symbol", symbol);
        await using var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rows.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = rows.GetString(0);
            var rowSide = Enum.Parse<PositionSide>(rows.GetString(1), true);
            var reduce = rows.GetInt32(2) == 1;
            if (!decimal.TryParse(rows.GetString(3), NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
                return null;
            if (rows.IsDBNull(4) || !DateTimeOffset.TryParse(rows.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurred))
                return null;
            occurred = occurred.ToUniversalTime();

            if (rowSide == side)
            {
                if (!reduce)
                {
                    if (same == 0)
                    {
                        opened = occurred;
                        ambiguous = opposite > 0;
                    }
                    same += quantity;
                }
                else
                {
                    if (id == closeClientOrderId)
                    {
                        found = true;
                        closed = occurred;
                        if (quantity != closingQuantity || same != closingQuantity) ambiguous = true;
                    }
                    else if (same > 0)
                    {
                        ambiguous = true;
                    }
                    if (quantity > same) return null;
                    same -= quantity;
                }
            }
            else
            {
                opposite += reduce ? -quantity : quantity;
                if (opposite < 0) return null;
                if (opened is not null && opposite > 0) ambiguous = true;
            }

            if (found) break;
            if (same == 0)
            {
                opened = null;
                ambiguous = false;
            }
        }

        if (!found || ambiguous || same != 0 || opposite != 0 || opened is null || closed is null || closed < opened)
            return null;
        return (opened.Value, closed.Value);
    }

    private static async Task PruneClosedPositionMarkObservationsAsync(
        SqliteConnection connection,
        string symbol,
        PositionSide side,
        DateTimeOffset closedAtUtc,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM position_mark_observations
            WHERE symbol=$symbol AND side=$side AND observed_at <= $closed
            """;
        command.Parameters.AddWithValue("$symbol", symbol.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$side", side.ToString());
        command.Parameters.AddWithValue("$closed", closedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

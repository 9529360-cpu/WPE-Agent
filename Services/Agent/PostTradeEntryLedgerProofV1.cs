using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record PostTradeEntryLedgerEventV1(
    int Sequence,
    string CycleId,
    string ClientOrderId,
    bool ReduceOnly,
    decimal Quantity,
    decimal AveragePrice,
    decimal ExpectedPrice,
    string Status,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? ExchangeUpdatedAtUtc);

public sealed record PostTradeEntryLedgerProofV1(
    string Schema,
    string CloseClientOrderId,
    string CloseCycleId,
    string Symbol,
    string Side,
    decimal ClosingQuantity,
    decimal PreCloseOpenQuantity,
    decimal PreCloseOpenCost,
    decimal WeightedEntryPrice,
    decimal PostCloseRemainingQuantity,
    IReadOnlyList<PostTradeEntryLedgerEventV1> Events,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class PostTradeEntryLedgerProofCanonicalizerV1
{
    public const string Schema = "wpe.post-trade-entry-ledger/1.0";

    public static PostTradeEntryLedgerProofV1 Create(
        string closeClientOrderId,
        string closeCycleId,
        string symbol,
        string side,
        decimal closingQuantity,
        IReadOnlyList<PostTradeEntryLedgerEventV1> events)
    {
        if (string.IsNullOrWhiteSpace(closeClientOrderId))
            throw new ArgumentException("Close client-order id is required.", nameof(closeClientOrderId));
        if (string.IsNullOrWhiteSpace(closeCycleId))
            throw new ArgumentException("Close cycle id is required.", nameof(closeCycleId));
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        if (string.IsNullOrWhiteSpace(side))
            throw new ArgumentException("Side is required.", nameof(side));
        if (closingQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(closingQuantity));

        ArgumentNullException.ThrowIfNull(events);
        var ordered = events.OrderBy(x => x.Sequence).ToArray();
        var seenClients = new HashSet<string>(StringComparer.Ordinal);
        decimal openQuantity = 0m;
        decimal openCost = 0m;

        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            ValidateEvent(item, index, closeClientOrderId, seenClients);

            if (!item.ReduceOnly)
            {
                openQuantity += item.Quantity;
                openCost += item.Quantity * item.AveragePrice;
                continue;
            }

            if (item.Quantity > openQuantity || openQuantity <= 0)
                throw new InvalidOperationException("Historical reduction exceeds the open quantity available at that point.");

            var average = openCost / openQuantity;
            openQuantity -= item.Quantity;
            openCost -= average * item.Quantity;

            if (openQuantity == 0m)
                openCost = 0m;
        }

        if (openQuantity < closingQuantity || openQuantity <= 0 || openCost <= 0)
            throw new InvalidOperationException("The execution ledger does not contain enough open quantity for this close.");

        var weightedEntry = openCost / openQuantity;
        var draft = new PostTradeEntryLedgerProofV1(
            Schema,
            closeClientOrderId,
            closeCycleId,
            symbol,
            side,
            closingQuantity,
            openQuantity,
            openCost,
            weightedEntry,
            openQuantity - closingQuantity,
            ordered,
            Array.Empty<byte>(),
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(PostTradeEntryLedgerProofV1 value)
    {
        if (value is null
            || value.Schema != Schema
            || value.CanonicalBytes.Length == 0
            || !LowerSha256(value.CanonicalSha256))
            return false;

        try
        {
            var replay = Create(
                value.CloseClientOrderId,
                value.CloseCycleId,
                value.Symbol,
                value.Side,
                value.ClosingQuantity,
                value.Events);

            if (replay.PreCloseOpenQuantity != value.PreCloseOpenQuantity
                || replay.PreCloseOpenCost != value.PreCloseOpenCost
                || replay.WeightedEntryPrice != value.WeightedEntryPrice
                || replay.PostCloseRemainingQuantity != value.PostCloseRemainingQuantity
                || !string.Equals(replay.CanonicalSha256, value.CanonicalSha256, StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(replay.CanonicalBytes, value.CanonicalBytes))
                return false;
        }
        catch
        {
            return false;
        }

        return true;
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> canonicalBytes,
        string canonicalSha256,
        out PostTradeEntryLedgerProofV1? value)
    {
        value = null;
        if (canonicalBytes.IsEmpty || !LowerSha256(canonicalSha256))
            return false;

        try
        {
            var bytes = canonicalBytes.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, canonicalSha256, StringComparison.Ordinal))
                return false;

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var events = root.GetProperty("events")
                .EnumerateArray()
                .Select(item => new PostTradeEntryLedgerEventV1(
                    item.GetProperty("sequence").GetInt32(),
                    Required(item, "cycle_id"),
                    Required(item, "client_order_id"),
                    item.GetProperty("reduce_only").GetBoolean(),
                    item.GetProperty("quantity").GetDecimal(),
                    item.GetProperty("average_price").GetDecimal(),
                    item.GetProperty("expected_price").GetDecimal(),
                    Required(item, "status"),
                    ParseUtc(item, "occurred_at_utc"),
                    item.GetProperty("exchange_updated_at_utc").ValueKind == JsonValueKind.Null
                        ? null
                        : ParseUtc(item, "exchange_updated_at_utc")))
                .ToArray();

            value = new(
                Required(root, "schema"),
                Required(root, "close_client_order_id"),
                Required(root, "close_cycle_id"),
                Required(root, "symbol"),
                Required(root, "side"),
                root.GetProperty("closing_quantity").GetDecimal(),
                root.GetProperty("pre_close_open_quantity").GetDecimal(),
                root.GetProperty("pre_close_open_cost").GetDecimal(),
                root.GetProperty("weighted_entry_price").GetDecimal(),
                root.GetProperty("post_close_remaining_quantity").GetDecimal(),
                events,
                bytes,
                canonicalSha256);

            if (IsCanonical(value))
                return true;

            value = null;
            return false;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static void ValidateEvent(
        PostTradeEntryLedgerEventV1 item,
        int expectedSequence,
        string closeClientOrderId,
        HashSet<string> seenClients)
    {
        if (item.Sequence != expectedSequence
            || string.IsNullOrWhiteSpace(item.CycleId)
            || string.IsNullOrWhiteSpace(item.ClientOrderId)
            || string.Equals(item.ClientOrderId, closeClientOrderId, StringComparison.Ordinal)
            || !seenClients.Add(item.ClientOrderId)
            || item.Quantity <= 0
            || item.AveragePrice <= 0
            || item.ExpectedPrice < 0
            || item.Status is not ("FILLED" or "PARTIALLY_FILLED")
            || item.OccurredAtUtc == default
            || item.OccurredAtUtc.Offset != TimeSpan.Zero
            || item.ExchangeUpdatedAtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new InvalidOperationException("Post-trade entry ledger event is invalid.");

        if (item.ExchangeUpdatedAtUtc is { } exchangeUpdated
            && exchangeUpdated > item.OccurredAtUtc.AddMinutes(5))
            throw new InvalidOperationException("Exchange update time is inconsistent with the persisted execution event.");
    }

    private static byte[] Serialize(PostTradeEntryLedgerProofV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("close_client_order_id", value.CloseClientOrderId);
            writer.WriteString("close_cycle_id", value.CloseCycleId);
            writer.WriteNumber("closing_quantity", value.ClosingQuantity);
            writer.WritePropertyName("events");
            writer.WriteStartArray();
            foreach (var item in value.Events.OrderBy(x => x.Sequence))
            {
                writer.WriteStartObject();
                writer.WriteNumber("average_price", item.AveragePrice);
                writer.WriteString("client_order_id", item.ClientOrderId);
                writer.WriteString("cycle_id", item.CycleId);
                if (item.ExchangeUpdatedAtUtc is null)
                    writer.WriteNull("exchange_updated_at_utc");
                else
                    writer.WriteString("exchange_updated_at_utc", item.ExchangeUpdatedAtUtc.Value.ToUniversalTime());
                writer.WriteNumber("expected_price", item.ExpectedPrice);
                writer.WriteString("occurred_at_utc", item.OccurredAtUtc.ToUniversalTime());
                writer.WriteNumber("quantity", item.Quantity);
                writer.WriteBoolean("reduce_only", item.ReduceOnly);
                writer.WriteNumber("sequence", item.Sequence);
                writer.WriteString("status", item.Status);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("post_close_remaining_quantity", value.PostCloseRemainingQuantity);
            writer.WriteNumber("pre_close_open_cost", value.PreCloseOpenCost);
            writer.WriteNumber("pre_close_open_quantity", value.PreCloseOpenQuantity);
            writer.WriteString("schema", value.Schema);
            writer.WriteString("side", value.Side);
            writer.WriteString("symbol", value.Symbol);
            writer.WriteNumber("weighted_entry_price", value.WeightedEntryPrice);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Required(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Post-trade entry ledger field '{name}' is required.");
        return value;
    }

    private static DateTimeOffset ParseUtc(JsonElement root, string name)
    {
        var value = DateTimeOffset.Parse(
            root.GetProperty(name).GetString() ?? string.Empty,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new InvalidOperationException($"Post-trade entry ledger time '{name}' must be UTC.");
        return value;
    }

    private static bool LowerSha256(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}

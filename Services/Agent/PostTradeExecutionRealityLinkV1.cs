using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum PostTradeExecutionRealityLinkStateV1
{
    Linked,
    Unavailable,
    Conflict
}

public sealed record PostTradeExecutionRealityLinkV1(
    string Schema,
    string ClientOrderId,
    string CycleId,
    string Symbol,
    string Side,
    string? StrategyId,
    string StrategyVersion,
    string AttributionBasis,
    DateTimeOffset ReviewClosedAtUtc,
    string ReviewOutcome,
    decimal NetPnl,
    decimal ReturnPct,
    PostTradeExecutionRealityLinkStateV1 State,
    string Code,
    string? CostModelVersion,
    string? DriftCanonicalSha256,
    DateTimeOffset? DriftObservedAtUtc,
    bool? PriceComparable,
    bool? FeeComparable,
    bool? TotalComparable,
    decimal? FillRatio,
    decimal? SlippageDriftBps,
    decimal? FeeDriftBps,
    decimal? TotalExecutionDriftBps,
    long? ObservationLatencyMs,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public sealed class PostTradeExecutionRealityLinkerV1
{
    public const string Schema = "wpe.post-trade-execution-reality-link/1.0";
    private readonly AgentSqliteStore _store;

    public PostTradeExecutionRealityLinkerV1(AgentSqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<IReadOnlyList<PostTradeExecutionRealityLinkV1>> GetRecentAsync(
        int limit,
        CancellationToken ct)
    {
        var reviews = await _store.GetRecentPostTradeReviewsAsync(Math.Clamp(limit, 1, 100), ct);
        var result = new List<PostTradeExecutionRealityLinkV1>(reviews.Count);
        foreach (var review in reviews)
            result.Add(await LinkAsync(review, ct));
        return result;
    }

    public async Task<PostTradeExecutionRealityLinkV1> LinkAsync(
        PostTradeReviewV1 review,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);

        if (string.IsNullOrWhiteSpace(review.StrategyId))
            return Finalize(Draft(review, PostTradeExecutionRealityLinkStateV1.Unavailable, "strategy-attribution-unavailable"));

        var facts = await _store.GetTerminalExecutionRealityDriftByClientOrderIdAsync(review.ClientOrderId, ct);
        if (facts.Count == 0)
            return Finalize(Draft(review, PostTradeExecutionRealityLinkStateV1.Unavailable, "terminal-drift-unavailable"));
        if (facts.Count > 128)
            return Finalize(Draft(review, PostTradeExecutionRealityLinkStateV1.Conflict, "terminal-drift-overflow"));

        if (facts.Any(f => !IdentityMatches(review, f)))
            return Finalize(Draft(review, PostTradeExecutionRealityLinkStateV1.Conflict, "terminal-drift-review-mismatch"));

        var costModels = facts.Select(f => f.CostModelVersion).Distinct(StringComparer.Ordinal).ToArray();
        if (costModels.Length != 1)
            return Finalize(Draft(review, PostTradeExecutionRealityLinkStateV1.Conflict, "cost-model-conflict"));

        var executionKeys = facts.Select(ExecutionKey).Distinct(StringComparer.Ordinal).ToArray();
        if (executionKeys.Length != 1)
            return Finalize(Draft(review, PostTradeExecutionRealityLinkStateV1.Conflict, "terminal-execution-conflict"));

        var feeFacts = facts.Where(f => f.FeeComparable).ToArray();
        if (feeFacts.Select(FeeKey).Distinct(StringComparer.Ordinal).Count() > 1)
            return Finalize(Draft(review, PostTradeExecutionRealityLinkStateV1.Conflict, "terminal-fee-conflict"));

        var selected = (feeFacts.Length > 0 ? feeFacts : facts.ToArray())
            .OrderByDescending(f => f.ObservedAtUtc)
            .ThenByDescending(f => f.CanonicalSha256, StringComparer.Ordinal)
            .First();

        var draft = Draft(review, PostTradeExecutionRealityLinkStateV1.Linked, "linked") with
        {
            CostModelVersion = selected.CostModelVersion,
            DriftCanonicalSha256 = selected.CanonicalSha256,
            DriftObservedAtUtc = selected.ObservedAtUtc,
            PriceComparable = selected.Comparable,
            FeeComparable = selected.FeeComparable,
            TotalComparable = selected.TotalComparable,
            FillRatio = selected.FillRatio,
            SlippageDriftBps = selected.Comparable ? selected.SlippageDriftBps : null,
            FeeDriftBps = selected.FeeComparable ? selected.FeeDriftBps : null,
            TotalExecutionDriftBps = selected.TotalComparable ? selected.TotalExecutionDriftBps : null,
            ObservationLatencyMs = selected.ObservationLatencyMs
        };
        return Finalize(draft);
    }

    public static bool IsCanonical(PostTradeExecutionRealityLinkV1 value)
    {
        if (value.Schema != Schema || value.CanonicalBytes.Length == 0 || value.CanonicalSha256.Length != 64)
            return false;
        var bytes = Serialize(value);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(hash, value.CanonicalSha256, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes);
    }

    private static bool IdentityMatches(PostTradeReviewV1 review, ExecutionRealityDriftFactV1 fact) =>
        fact.Terminal
        && fact.State == ExecutionRealityStateV1.Filled
        && fact.Comparable
        && fact.ReduceOnly
        && string.Equals(fact.CorrelationId, review.CycleId, StringComparison.Ordinal)
        && string.Equals(fact.ClientOrderId, review.ClientOrderId, StringComparison.Ordinal)
        && string.Equals(fact.StrategyId, review.StrategyId, StringComparison.Ordinal)
        && string.Equals(fact.StrategyVersion, review.StrategyVersion, StringComparison.Ordinal)
        && string.Equals(fact.Symbol, review.Symbol, StringComparison.Ordinal)
        && string.Equals(fact.Side.ToString(), review.Side, StringComparison.Ordinal)
        && fact.ExecutedQuantity == review.Quantity
        && fact.AveragePrice == review.ExitPrice;

    private static string ExecutionKey(ExecutionRealityDriftFactV1 fact) => Key(
        fact.CorrelationId,
        fact.ClientOrderId,
        fact.StrategyId,
        fact.StrategyVersion,
        fact.CostModelVersion,
        fact.Symbol,
        fact.Side,
        fact.ReduceOnly,
        fact.OrderType,
        fact.IntendedQuantity,
        fact.ExecutedQuantity,
        fact.ExpectedPrice,
        fact.AveragePrice,
        fact.ExchangeStatus,
        fact.State,
        fact.FillRatio,
        fact.AdverseSlippageBps,
        fact.ExpectedSlippageBps,
        fact.SlippageDriftBps);

    private static string FeeKey(ExecutionRealityDriftFactV1 fact) => Key(
        fact.ObservedFee,
        fact.FeeBasis,
        fact.ObservedFeeRateBps,
        fact.ExpectedCommissionBps,
        fact.FeeDriftBps,
        fact.TotalExecutionDriftBps);

    private static string Key(params object?[] values) => string.Join(
        "\u001f",
        values.Select(value => value switch
        {
            null => string.Empty,
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            bool flag => flag ? "1" : "0",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        }));

    private static PostTradeExecutionRealityLinkV1 Draft(
        PostTradeReviewV1 review,
        PostTradeExecutionRealityLinkStateV1 state,
        string code) => new(
            Schema,
            review.ClientOrderId,
            review.CycleId,
            review.Symbol,
            review.Side,
            review.StrategyId,
            review.StrategyVersion,
            review.AttributionBasis,
            review.ClosedAtUtc.ToUniversalTime(),
            review.Outcome,
            review.NetPnl,
            review.ReturnPct,
            state,
            code,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<byte>(),
            string.Empty);

    private static PostTradeExecutionRealityLinkV1 Finalize(PostTradeExecutionRealityLinkV1 draft)
    {
        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    private static byte[] Serialize(PostTradeExecutionRealityLinkV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("attribution_basis", value.AttributionBasis);
            writer.WriteString("client_order_id", value.ClientOrderId);
            writer.WriteString("code", value.Code);
            WriteStringOrNull(writer, "cost_model_version", value.CostModelVersion);
            writer.WriteString("cycle_id", value.CycleId);
            WriteStringOrNull(writer, "drift_canonical_sha256", value.DriftCanonicalSha256);
            WriteDateOrNull(writer, "drift_observed_at_utc", value.DriftObservedAtUtc);
            WriteBoolOrNull(writer, "fee_comparable", value.FeeComparable);
            WriteDecimalOrNull(writer, "fee_drift_bps", value.FeeDriftBps);
            WriteDecimalOrNull(writer, "fill_ratio", value.FillRatio);
            writer.WriteNumber("net_pnl", value.NetPnl);
            WriteLongOrNull(writer, "observation_latency_ms", value.ObservationLatencyMs);
            WriteBoolOrNull(writer, "price_comparable", value.PriceComparable);
            writer.WriteString("review_closed_at_utc", value.ReviewClosedAtUtc.ToUniversalTime());
            writer.WriteString("review_outcome", value.ReviewOutcome);
            writer.WriteNumber("return_pct", value.ReturnPct);
            writer.WriteString("schema", value.Schema);
            writer.WriteString("side", value.Side);
            WriteDecimalOrNull(writer, "slippage_drift_bps", value.SlippageDriftBps);
            writer.WriteString("state", value.State.ToString());
            WriteStringOrNull(writer, "strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteString("symbol", value.Symbol);
            WriteBoolOrNull(writer, "total_comparable", value.TotalComparable);
            WriteDecimalOrNull(writer, "total_execution_drift_bps", value.TotalExecutionDriftBps);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static void WriteDateOrNull(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value.Value.ToUniversalTime());
    }

    private static void WriteBoolOrNull(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteBoolean(name, value.Value);
    }

    private static void WriteDecimalOrNull(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value);
    }

    private static void WriteLongOrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value);
    }
}

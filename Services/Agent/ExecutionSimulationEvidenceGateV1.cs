using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum ExecutionSimulationEvidenceStateV1
{
    Ready,
    Insufficient,
    Regressed,
    Invalid
}

public sealed record ExecutionSimulationEvidencePolicyV1(
    string Version,
    int MinimumComparisons,
    int MinimumPriceComparableComparisons,
    int MinimumFeeComparableComparisons,
    int MinimumLatencyComparableComparisons,
    int MinimumTotalComparableComparisons,
    TimeSpan MinimumObservationWindow,
    TimeSpan MaximumEvidenceAge,
    decimal MaximumStateMismatchFraction,
    decimal MaximumUnsupportedFraction,
    decimal MaximumP95AbsoluteFillRatioDelta,
    decimal MaximumP95AdversePriceDriftBps,
    decimal MaximumP95AdverseFeeDriftBps,
    long MaximumP95PositiveLatencyDriftMs,
    decimal MaximumP95TotalExecutionDriftBps);

public sealed record ExecutionSimulationEvidenceDecisionV1(
    string Schema,
    string StrategyId,
    string StrategyVersion,
    string CostModelVersion,
    string SimulationModelVersion,
    string VenueRuleVersion,
    string Symbol,
    string PolicyVersion,
    string PolicySha256,
    string EvidenceSetSha256,
    ExecutionSimulationEvidenceStateV1 State,
    bool EvidenceReady,
    int ComparisonCount,
    int StateMatchCount,
    int PriceComparableCount,
    int FeeComparableCount,
    int LatencyComparableCount,
    int TotalComparableCount,
    DateTimeOffset FirstComparedAtUtc,
    DateTimeOffset LastComparedAtUtc,
    double ObservationWindowSeconds,
    decimal StateMismatchFraction,
    decimal UnsupportedFraction,
    decimal P95AbsoluteFillRatioDelta,
    decimal P95AdversePriceDriftBps,
    decimal P95AdverseFeeDriftBps,
    long P95PositiveLatencyDriftMs,
    decimal P95TotalExecutionDriftBps,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<string> ReasonCodes,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class ExecutionSimulationEvidenceGateV1
{
    public const string Schema = "wpe.execution-simulation-evidence/1.0";

    private static readonly HashSet<string> IntegrityReasons = new(StringComparer.Ordinal)
    {
        "evidence.noncanonical",
        "evidence.cross-strategy",
        "evidence.cross-version",
        "evidence.cross-cost-model",
        "evidence.cross-simulation-model",
        "evidence.cross-venue-rule",
        "evidence.cross-symbol",
        "evidence.future",
        "evidence.duplicate"
    };

    private static readonly HashSet<string> RegressionReasons = new(StringComparer.Ordinal)
    {
        "quality.state-mismatch",
        "quality.unsupported",
        "quality.fill-ratio-delta",
        "quality.price-drift",
        "quality.fee-drift",
        "quality.latency-drift",
        "quality.total-drift"
    };

    public static ExecutionSimulationEvidenceDecisionV1 Evaluate(
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string venueRuleVersion,
        string symbol,
        IReadOnlyList<ExecutionSimulationComparisonV1> comparisons,
        ExecutionSimulationEvidencePolicyV1 policy,
        DateTimeOffset evaluatedAtUtc)
    {
        ValidateIdentity(strategyId, strategyVersion, costModelVersion, simulationModelVersion, venueRuleVersion, symbol);
        ValidatePolicy(policy);
        ArgumentNullException.ThrowIfNull(comparisons);
        if (evaluatedAtUtc == default || evaluatedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Evaluation time must be UTC.", nameof(evaluatedAtUtc));

        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var valid = new List<ExecutionSimulationComparisonV1>(comparisons.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var comparison in comparisons)
        {
            if (!ExecutionSimulationComparisonCanonicalizerV1.IsCanonical(comparison))
            {
                reasons.Add("evidence.noncanonical");
                continue;
            }
            if (!string.Equals(comparison.StrategyId, strategyId, StringComparison.Ordinal))
            {
                reasons.Add("evidence.cross-strategy");
                continue;
            }
            if (!string.Equals(comparison.StrategyVersion, strategyVersion, StringComparison.Ordinal))
            {
                reasons.Add("evidence.cross-version");
                continue;
            }
            if (!string.Equals(comparison.CostModelVersion, costModelVersion, StringComparison.Ordinal))
            {
                reasons.Add("evidence.cross-cost-model");
                continue;
            }
            if (!string.Equals(comparison.SimulationModelVersion, simulationModelVersion, StringComparison.Ordinal))
            {
                reasons.Add("evidence.cross-simulation-model");
                continue;
            }
            if (!string.Equals(comparison.VenueRuleVersion, venueRuleVersion, StringComparison.Ordinal))
            {
                reasons.Add("evidence.cross-venue-rule");
                continue;
            }
            if (!string.Equals(comparison.Symbol, symbol, StringComparison.Ordinal))
            {
                reasons.Add("evidence.cross-symbol");
                continue;
            }
            if (comparison.ComparedAtUtc > evaluatedAtUtc)
            {
                reasons.Add("evidence.future");
                continue;
            }
            if (!seen.Add(comparison.CanonicalSha256))
            {
                reasons.Add("evidence.duplicate");
                continue;
            }
            valid.Add(comparison);
        }

        var first = valid.Count == 0 ? evaluatedAtUtc : valid.Min(x => x.ComparedAtUtc);
        var last = valid.Count == 0 ? evaluatedAtUtc : valid.Max(x => x.ComparedAtUtc);
        var window = valid.Count == 0 ? TimeSpan.Zero : last - first;

        var stateMatches = valid.Count(x => x.StateMatch);
        var unsupported = valid.Count(x => x.SimulatedState == ExecutionSimulationFillStateV1.Unsupported);
        var priceComparable = valid.Where(x => x.PriceComparable).ToArray();
        var feeComparable = valid.Where(x => x.FeeComparable).ToArray();
        var latencyComparable = valid.Where(x => x.LatencyComparable).ToArray();
        var totalComparable = valid.Where(x => x.TotalComparable).ToArray();

        var stateMismatchFraction = Fraction(valid.Count - stateMatches, valid.Count);
        var unsupportedFraction = Fraction(unsupported, valid.Count);
        var p95FillRatioDelta = P95(valid.Select(x => Math.Abs(x.FillRatioDelta)));
        var p95PriceDrift = P95(priceComparable.Select(x => x.PriceDriftBps ?? 0m));
        var p95FeeDrift = P95(feeComparable.Select(x => x.FeeDriftBps ?? 0m));
        var p95LatencyDrift = P95(latencyComparable.Select(x => x.LatencyDriftMs ?? 0L));
        var p95TotalDrift = P95(totalComparable.Select(x => x.TotalExecutionDriftBps ?? 0m));

        if (valid.Count < policy.MinimumComparisons) reasons.Add("sample.comparisons");
        if (priceComparable.Length < policy.MinimumPriceComparableComparisons) reasons.Add("sample.price-comparable");
        if (feeComparable.Length < policy.MinimumFeeComparableComparisons) reasons.Add("sample.fee-comparable");
        if (latencyComparable.Length < policy.MinimumLatencyComparableComparisons) reasons.Add("sample.latency-comparable");
        if (totalComparable.Length < policy.MinimumTotalComparableComparisons) reasons.Add("sample.total-comparable");
        if (window < policy.MinimumObservationWindow) reasons.Add("sample.window");
        if (valid.Count == 0 || evaluatedAtUtc - last > policy.MaximumEvidenceAge) reasons.Add("freshness.stale");

        if (stateMismatchFraction > policy.MaximumStateMismatchFraction) reasons.Add("quality.state-mismatch");
        if (unsupportedFraction > policy.MaximumUnsupportedFraction) reasons.Add("quality.unsupported");
        if (valid.Count > 0 && p95FillRatioDelta > policy.MaximumP95AbsoluteFillRatioDelta) reasons.Add("quality.fill-ratio-delta");
        if (priceComparable.Length > 0 && p95PriceDrift > policy.MaximumP95AdversePriceDriftBps) reasons.Add("quality.price-drift");
        if (feeComparable.Length > 0 && p95FeeDrift > policy.MaximumP95AdverseFeeDriftBps) reasons.Add("quality.fee-drift");
        if (latencyComparable.Length > 0 && p95LatencyDrift > policy.MaximumP95PositiveLatencyDriftMs) reasons.Add("quality.latency-drift");
        if (totalComparable.Length > 0 && p95TotalDrift > policy.MaximumP95TotalExecutionDriftBps) reasons.Add("quality.total-drift");

        var state = reasons.Overlaps(IntegrityReasons)
            ? ExecutionSimulationEvidenceStateV1.Invalid
            : reasons.Overlaps(RegressionReasons)
                ? ExecutionSimulationEvidenceStateV1.Regressed
                : reasons.Count > 0
                    ? ExecutionSimulationEvidenceStateV1.Insufficient
                    : ExecutionSimulationEvidenceStateV1.Ready;

        var draft = new ExecutionSimulationEvidenceDecisionV1(
            Schema,
            strategyId,
            strategyVersion,
            costModelVersion,
            simulationModelVersion,
            venueRuleVersion,
            symbol,
            policy.Version,
            PolicyHash(policy),
            EvidenceSetHash(valid),
            state,
            state == ExecutionSimulationEvidenceStateV1.Ready,
            valid.Count,
            stateMatches,
            priceComparable.Length,
            feeComparable.Length,
            latencyComparable.Length,
            totalComparable.Length,
            first,
            last,
            Math.Max(0, window.TotalSeconds),
            stateMismatchFraction,
            unsupportedFraction,
            p95FillRatioDelta,
            p95PriceDrift,
            p95FeeDrift,
            p95LatencyDrift,
            p95TotalDrift,
            evaluatedAtUtc,
            reasons.ToArray(),
            Array.Empty<byte>(),
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(ExecutionSimulationEvidenceDecisionV1 value)
    {
        if (value.Schema != Schema
            || value.CanonicalBytes.Length == 0
            || !IsLowerHexSha256(value.CanonicalSha256)
            || !IsLowerHexSha256(value.PolicySha256)
            || !IsLowerHexSha256(value.EvidenceSetSha256))
            return false;
        if (value.EvidenceReady != (value.State == ExecutionSimulationEvidenceStateV1.Ready))
            return false;
        if (value.ComparisonCount < 0
            || value.StateMatchCount < 0 || value.StateMatchCount > value.ComparisonCount
            || value.PriceComparableCount < 0 || value.PriceComparableCount > value.ComparisonCount
            || value.FeeComparableCount < 0 || value.FeeComparableCount > value.PriceComparableCount
            || value.LatencyComparableCount < 0 || value.LatencyComparableCount > value.ComparisonCount
            || value.TotalComparableCount < 0 || value.TotalComparableCount > value.FeeComparableCount)
            return false;
        if (value.FirstComparedAtUtc.Offset != TimeSpan.Zero
            || value.LastComparedAtUtc.Offset != TimeSpan.Zero
            || value.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || value.FirstComparedAtUtc > value.LastComparedAtUtc
            || value.LastComparedAtUtc > value.EvaluatedAtUtc
            || value.ObservationWindowSeconds < 0
            || value.StateMismatchFraction < 0 || value.StateMismatchFraction > 1
            || value.UnsupportedFraction < 0 || value.UnsupportedFraction > 1
            || value.P95AbsoluteFillRatioDelta < 0)
            return false;

        var bytes = Serialize(value);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(hash, value.CanonicalSha256, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes);
    }

    private static void ValidateIdentity(
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string venueRuleVersion,
        string symbol)
    {
        if (string.IsNullOrWhiteSpace(strategyId)) throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        if (string.IsNullOrWhiteSpace(strategyVersion)) throw new ArgumentException("Strategy version is required.", nameof(strategyVersion));
        if (string.IsNullOrWhiteSpace(costModelVersion)) throw new ArgumentException("Cost-model version is required.", nameof(costModelVersion));
        if (string.IsNullOrWhiteSpace(simulationModelVersion)) throw new ArgumentException("Simulation-model version is required.", nameof(simulationModelVersion));
        if (string.IsNullOrWhiteSpace(venueRuleVersion)) throw new ArgumentException("Venue-rule version is required.", nameof(venueRuleVersion));
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol is required.", nameof(symbol));
    }

    private static void ValidatePolicy(ExecutionSimulationEvidencePolicyV1 policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.Version)) throw new ArgumentException("Policy version is required.", nameof(policy));
        if (policy.MinimumComparisons < 1) throw new ArgumentOutOfRangeException(nameof(policy), "Minimum comparisons must be positive.");
        if (policy.MinimumPriceComparableComparisons < 0
            || policy.MinimumFeeComparableComparisons < 0
            || policy.MinimumLatencyComparableComparisons < 0
            || policy.MinimumTotalComparableComparisons < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "Comparable sample thresholds cannot be negative.");
        if (policy.MinimumObservationWindow <= TimeSpan.Zero || policy.MaximumEvidenceAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(policy), "Evidence windows must be positive.");
        if (policy.MaximumStateMismatchFraction < 0 || policy.MaximumStateMismatchFraction > 1
            || policy.MaximumUnsupportedFraction < 0 || policy.MaximumUnsupportedFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(policy), "Evidence fractions must be between zero and one.");
        if (policy.MaximumP95AbsoluteFillRatioDelta < 0 || policy.MaximumP95AbsoluteFillRatioDelta > 1)
            throw new ArgumentOutOfRangeException(nameof(policy), "Fill-ratio delta must be between zero and one.");
        if (policy.MaximumP95AdversePriceDriftBps < 0
            || policy.MaximumP95AdverseFeeDriftBps < 0
            || policy.MaximumP95PositiveLatencyDriftMs < 0
            || policy.MaximumP95TotalExecutionDriftBps < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "Execution-quality thresholds cannot be negative.");
    }

    private static decimal Fraction(int numerator, int denominator) =>
        denominator <= 0 ? 0m : (decimal)numerator / denominator;

    private static decimal P95(IEnumerable<decimal> source)
    {
        var values = source.OrderBy(x => x).ToArray();
        if (values.Length == 0) return 0m;
        return values[Math.Max(0, (int)Math.Ceiling(values.Length * .95) - 1)];
    }

    private static long P95(IEnumerable<long> source)
    {
        var values = source.OrderBy(x => x).ToArray();
        if (values.Length == 0) return 0L;
        return values[Math.Max(0, (int)Math.Ceiling(values.Length * .95) - 1)];
    }

    private static string EvidenceSetHash(IReadOnlyList<ExecutionSimulationComparisonV1> comparisons)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var hash in comparisons.Select(x => x.CanonicalSha256).OrderBy(x => x, StringComparer.Ordinal))
                writer.WriteStringValue(hash);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string PolicyHash(ExecutionSimulationEvidencePolicyV1 policy)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("maximum_evidence_age_seconds", policy.MaximumEvidenceAge.TotalSeconds);
            writer.WriteNumber("maximum_p95_absolute_fill_ratio_delta", policy.MaximumP95AbsoluteFillRatioDelta);
            writer.WriteNumber("maximum_p95_adverse_fee_drift_bps", policy.MaximumP95AdverseFeeDriftBps);
            writer.WriteNumber("maximum_p95_adverse_price_drift_bps", policy.MaximumP95AdversePriceDriftBps);
            writer.WriteNumber("maximum_p95_positive_latency_drift_ms", policy.MaximumP95PositiveLatencyDriftMs);
            writer.WriteNumber("maximum_p95_total_execution_drift_bps", policy.MaximumP95TotalExecutionDriftBps);
            writer.WriteNumber("maximum_state_mismatch_fraction", policy.MaximumStateMismatchFraction);
            writer.WriteNumber("maximum_unsupported_fraction", policy.MaximumUnsupportedFraction);
            writer.WriteNumber("minimum_comparisons", policy.MinimumComparisons);
            writer.WriteNumber("minimum_fee_comparable_comparisons", policy.MinimumFeeComparableComparisons);
            writer.WriteNumber("minimum_latency_comparable_comparisons", policy.MinimumLatencyComparableComparisons);
            writer.WriteNumber("minimum_observation_window_seconds", policy.MinimumObservationWindow.TotalSeconds);
            writer.WriteNumber("minimum_price_comparable_comparisons", policy.MinimumPriceComparableComparisons);
            writer.WriteNumber("minimum_total_comparable_comparisons", policy.MinimumTotalComparableComparisons);
            writer.WriteString("version", policy.Version);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] Serialize(ExecutionSimulationEvidenceDecisionV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("comparison_count", value.ComparisonCount);
            writer.WriteString("cost_model_version", value.CostModelVersion);
            writer.WriteBoolean("evidence_ready", value.EvidenceReady);
            writer.WriteString("evidence_set_sha256", value.EvidenceSetSha256);
            writer.WriteString("evaluated_at_utc", value.EvaluatedAtUtc.ToUniversalTime());
            writer.WriteNumber("fee_comparable_count", value.FeeComparableCount);
            writer.WriteString("first_compared_at_utc", value.FirstComparedAtUtc.ToUniversalTime());
            writer.WriteNumber("latency_comparable_count", value.LatencyComparableCount);
            writer.WriteString("last_compared_at_utc", value.LastComparedAtUtc.ToUniversalTime());
            writer.WriteNumber("observation_window_seconds", value.ObservationWindowSeconds);
            writer.WriteNumber("p95_absolute_fill_ratio_delta", value.P95AbsoluteFillRatioDelta);
            writer.WriteNumber("p95_adverse_fee_drift_bps", value.P95AdverseFeeDriftBps);
            writer.WriteNumber("p95_adverse_price_drift_bps", value.P95AdversePriceDriftBps);
            writer.WriteNumber("p95_positive_latency_drift_ms", value.P95PositiveLatencyDriftMs);
            writer.WriteNumber("p95_total_execution_drift_bps", value.P95TotalExecutionDriftBps);
            writer.WriteString("policy_sha256", value.PolicySha256);
            writer.WriteString("policy_version", value.PolicyVersion);
            writer.WriteNumber("price_comparable_count", value.PriceComparableCount);
            writer.WritePropertyName("reason_codes");
            writer.WriteStartArray();
            foreach (var reason in value.ReasonCodes.OrderBy(x => x, StringComparer.Ordinal))
                writer.WriteStringValue(reason);
            writer.WriteEndArray();
            writer.WriteString("schema", value.Schema);
            writer.WriteString("simulation_model_version", value.SimulationModelVersion);
            writer.WriteNumber("state_match_count", value.StateMatchCount);
            writer.WriteNumber("state_mismatch_fraction", value.StateMismatchFraction);
            writer.WriteString("state", value.State.ToString());
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteString("symbol", value.Symbol);
            writer.WriteNumber("total_comparable_count", value.TotalComparableCount);
            writer.WriteNumber("unsupported_fraction", value.UnsupportedFraction);
            writer.WriteString("venue_rule_version", value.VenueRuleVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

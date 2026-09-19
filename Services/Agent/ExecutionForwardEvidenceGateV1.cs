using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum ExecutionForwardEvidenceStateV1
{
    Ready,
    Insufficient,
    Regressed,
    Invalid
}

public sealed record ExecutionForwardEvidencePolicyV1(
    string Version,
    int MinimumObservations,
    int MinimumPriceComparableObservations,
    int MinimumTotalComparableObservations,
    TimeSpan MinimumObservationWindow,
    TimeSpan MaximumEvidenceAge,
    decimal MinimumAverageFillRatio,
    decimal MaximumP95AdverseSlippageBps,
    decimal MaximumP95TotalExecutionDriftBps,
    long MaximumP95ObservationLatencyMs,
    decimal MaximumUnknownFraction,
    decimal MaximumTerminalNoFillFraction);

public sealed record ExecutionForwardEvidenceDecisionV1(
    string Schema,
    string StrategyId,
    string StrategyVersion,
    string CostModelVersion,
    string PolicyVersion,
    string PolicySha256,
    ExecutionForwardEvidenceStateV1 State,
    bool EvidenceReady,
    int ObservationCount,
    int PriceComparableCount,
    int TotalComparableCount,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    double ObservationWindowSeconds,
    DateTimeOffset EvaluatedAtUtc,
    string EvidenceSetSha256,
    string? CalibrationSha256,
    IReadOnlyList<string> ReasonCodes,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class ExecutionForwardEvidenceGateV1
{
    public const string Schema = "wpe.execution-forward-evidence/1.0";

    private static readonly HashSet<string> IntegrityReasons = new(StringComparer.Ordinal)
    {
        "evidence.noncanonical",
        "evidence.cross-strategy",
        "evidence.cross-version",
        "evidence.cross-cost-model",
        "evidence.future",
        "evidence.duplicate"
    };

    private static readonly HashSet<string> RegressionReasons = new(StringComparer.Ordinal)
    {
        "quality.fill-ratio",
        "quality.adverse-slippage",
        "quality.total-drift",
        "quality.latency",
        "quality.unknown-rate",
        "quality.terminal-no-fill-rate"
    };

    public static ExecutionForwardEvidenceDecisionV1 Evaluate(
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        IReadOnlyList<ExecutionRealityDriftFactV1> facts,
        ExecutionForwardEvidencePolicyV1 policy,
        DateTimeOffset evaluatedAtUtc)
    {
        ValidatePolicy(policy);
        if (string.IsNullOrWhiteSpace(strategyId)) throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        if (string.IsNullOrWhiteSpace(strategyVersion)) throw new ArgumentException("Strategy version is required.", nameof(strategyVersion));
        if (string.IsNullOrWhiteSpace(costModelVersion)) throw new ArgumentException("Cost model version is required.", nameof(costModelVersion));
        ArgumentNullException.ThrowIfNull(facts);
        evaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();

        var policyHash = PolicyHash(policy);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var validFacts = new List<ExecutionRealityDriftFactV1>(facts.Count);
        var seenCanonicalHashes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            if (!ExecutionRealityDriftV1.IsCanonical(fact))
            {
                reasons.Add("evidence.noncanonical");
                continue;
            }
            if (!string.Equals(fact.StrategyId, strategyId, StringComparison.Ordinal))
            {
                reasons.Add("evidence.cross-strategy");
                continue;
            }
            if (!string.Equals(fact.StrategyVersion, strategyVersion, StringComparison.Ordinal))
            {
                reasons.Add("evidence.cross-version");
                continue;
            }
            if (!string.Equals(fact.CostModelVersion, costModelVersion, StringComparison.Ordinal))
            {
                reasons.Add("evidence.cross-cost-model");
                continue;
            }
            if (fact.ObservedAtUtc > evaluatedAtUtc)
            {
                reasons.Add("evidence.future");
                continue;
            }
            if (!seenCanonicalHashes.Add(fact.CanonicalSha256))
            {
                reasons.Add("evidence.duplicate");
                continue;
            }
            validFacts.Add(fact);
        }

        var first = validFacts.Count == 0 ? evaluatedAtUtc : validFacts.Min(x => x.ObservedAtUtc).ToUniversalTime();
        var last = validFacts.Count == 0 ? evaluatedAtUtc : validFacts.Max(x => x.ObservedAtUtc).ToUniversalTime();
        var window = validFacts.Count == 0 ? TimeSpan.Zero : last - first;
        ExecutionRealityCalibrationSnapshotV1? calibration = null;
        if (validFacts.Count > 0 && !reasons.Overlaps(IntegrityReasons))
            calibration = ExecutionRealityCalibrationV1.Create(strategyId, strategyVersion, costModelVersion, validFacts);

        var priceComparable = validFacts.Count(x => x.Comparable);
        var totalComparable = validFacts.Count(x => x.TotalComparable);
        if (validFacts.Count < policy.MinimumObservations) reasons.Add("sample.observations");
        if (priceComparable < policy.MinimumPriceComparableObservations) reasons.Add("sample.price-comparable");
        if (totalComparable < policy.MinimumTotalComparableObservations) reasons.Add("sample.total-comparable");
        if (window < policy.MinimumObservationWindow) reasons.Add("sample.window");
        if (validFacts.Count == 0 || evaluatedAtUtc - last > policy.MaximumEvidenceAge) reasons.Add("freshness.stale");

        if (calibration is not null)
        {
            if (calibration.AverageFillRatio < policy.MinimumAverageFillRatio) reasons.Add("quality.fill-ratio");
            if (calibration.ComparableCount > 0 && calibration.P95AdverseSlippageBps > policy.MaximumP95AdverseSlippageBps) reasons.Add("quality.adverse-slippage");
            if (calibration.TotalComparableCount > 0 && calibration.P95TotalExecutionDriftBps > policy.MaximumP95TotalExecutionDriftBps) reasons.Add("quality.total-drift");
            if (calibration.P95ObservationLatencyMs > policy.MaximumP95ObservationLatencyMs) reasons.Add("quality.latency");
            if (Fraction(calibration.UnknownCount, calibration.ObservationCount) > policy.MaximumUnknownFraction) reasons.Add("quality.unknown-rate");
            if (Fraction(calibration.TerminalNoFillCount, calibration.ObservationCount) > policy.MaximumTerminalNoFillFraction) reasons.Add("quality.terminal-no-fill-rate");
        }

        var evidenceSetHash = EvidenceSetHash(validFacts);
        var state = reasons.Overlaps(IntegrityReasons)
            ? ExecutionForwardEvidenceStateV1.Invalid
            : reasons.Overlaps(RegressionReasons)
                ? ExecutionForwardEvidenceStateV1.Regressed
                : reasons.Count > 0
                    ? ExecutionForwardEvidenceStateV1.Insufficient
                    : ExecutionForwardEvidenceStateV1.Ready;

        var draft = new ExecutionForwardEvidenceDecisionV1(
            Schema,
            strategyId,
            strategyVersion,
            costModelVersion,
            policy.Version,
            policyHash,
            state,
            state == ExecutionForwardEvidenceStateV1.Ready,
            validFacts.Count,
            priceComparable,
            totalComparable,
            first,
            last,
            Math.Max(0, window.TotalSeconds),
            evaluatedAtUtc,
            evidenceSetHash,
            calibration?.CanonicalSha256,
            reasons.ToArray(),
            Array.Empty<byte>(),
            string.Empty);
        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(ExecutionForwardEvidenceDecisionV1 value)
    {
        if (value.Schema != Schema
            || value.CanonicalBytes.Length == 0
            || !IsLowerHexSha256(value.CanonicalSha256)
            || !IsLowerHexSha256(value.PolicySha256)
            || !IsLowerHexSha256(value.EvidenceSetSha256)
            || value.CalibrationSha256 is not null && !IsLowerHexSha256(value.CalibrationSha256))
            return false;
        var bytes = Serialize(value);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(hash, value.CanonicalSha256, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes);
    }

    private static decimal Fraction(int numerator, int denominator) => denominator <= 0 ? 0 : (decimal)numerator / denominator;

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidatePolicy(ExecutionForwardEvidencePolicyV1 policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.Version)) throw new ArgumentException("Policy version is required.", nameof(policy));
        if (policy.MinimumObservations < 1) throw new ArgumentOutOfRangeException(nameof(policy), "Minimum observations must be positive.");
        if (policy.MinimumPriceComparableObservations < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "Price-comparable sample threshold is invalid.");
        if (policy.MinimumTotalComparableObservations < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "Total-comparable sample threshold is invalid.");
        if (policy.MinimumObservationWindow <= TimeSpan.Zero || policy.MaximumEvidenceAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(policy), "Evidence windows must be positive.");
        if (policy.MinimumAverageFillRatio < 0 || policy.MinimumAverageFillRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(policy), "Minimum fill ratio must be between zero and one.");
        if (policy.MaximumP95AdverseSlippageBps < 0 || policy.MaximumP95TotalExecutionDriftBps < 0 || policy.MaximumP95ObservationLatencyMs < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "Execution quality limits cannot be negative.");
        if (policy.MaximumUnknownFraction < 0 || policy.MaximumUnknownFraction > 1 || policy.MaximumTerminalNoFillFraction < 0 || policy.MaximumTerminalNoFillFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(policy), "Execution state fractions must be between zero and one.");
    }

    private static string EvidenceSetHash(IReadOnlyList<ExecutionRealityDriftFactV1> facts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var hash in facts.Select(x => x.CanonicalSha256).OrderBy(x => x, StringComparer.Ordinal))
                writer.WriteStringValue(hash);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string PolicyHash(ExecutionForwardEvidencePolicyV1 policy)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("maximum_evidence_age_seconds", policy.MaximumEvidenceAge.TotalSeconds);
            writer.WriteNumber("maximum_p95_adverse_slippage_bps", policy.MaximumP95AdverseSlippageBps);
            writer.WriteNumber("maximum_p95_observation_latency_ms", policy.MaximumP95ObservationLatencyMs);
            writer.WriteNumber("maximum_p95_total_execution_drift_bps", policy.MaximumP95TotalExecutionDriftBps);
            writer.WriteNumber("maximum_terminal_no_fill_fraction", policy.MaximumTerminalNoFillFraction);
            writer.WriteNumber("maximum_unknown_fraction", policy.MaximumUnknownFraction);
            writer.WriteNumber("minimum_average_fill_ratio", policy.MinimumAverageFillRatio);
            writer.WriteNumber("minimum_observation_window_seconds", policy.MinimumObservationWindow.TotalSeconds);
            writer.WriteNumber("minimum_observations", policy.MinimumObservations);
            writer.WriteNumber("minimum_price_comparable_observations", policy.MinimumPriceComparableObservations);
            writer.WriteNumber("minimum_total_comparable_observations", policy.MinimumTotalComparableObservations);
            writer.WriteString("version", policy.Version);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static byte[] Serialize(ExecutionForwardEvidenceDecisionV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("calibration_sha256", value.CalibrationSha256);
            writer.WriteString("cost_model_version", value.CostModelVersion);
            writer.WriteString("evidence_set_sha256", value.EvidenceSetSha256);
            writer.WriteBoolean("evidence_ready", value.EvidenceReady);
            writer.WriteString("evaluated_at_utc", value.EvaluatedAtUtc.ToUniversalTime());
            writer.WriteString("first_observed_at_utc", value.FirstObservedAtUtc.ToUniversalTime());
            writer.WriteString("last_observed_at_utc", value.LastObservedAtUtc.ToUniversalTime());
            writer.WriteNumber("observation_count", value.ObservationCount);
            writer.WriteNumber("observation_window_seconds", value.ObservationWindowSeconds);
            writer.WriteString("policy_sha256", value.PolicySha256);
            writer.WriteString("policy_version", value.PolicyVersion);
            writer.WriteNumber("price_comparable_count", value.PriceComparableCount);
            writer.WritePropertyName("reason_codes");
            writer.WriteStartArray();
            foreach (var reason in value.ReasonCodes.OrderBy(x => x, StringComparer.Ordinal)) writer.WriteStringValue(reason);
            writer.WriteEndArray();
            writer.WriteString("schema", value.Schema);
            writer.WriteString("state", value.State.ToString());
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteNumber("total_comparable_count", value.TotalComparableCount);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

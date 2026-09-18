using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionRealityCalibrationSnapshotV1(
    string Schema,
    string StrategyId,
    string StrategyVersion,
    int ObservationCount,
    int ComparableCount,
    int FeeComparableCount,
    int TotalComparableCount,
    int FilledCount,
    int PartialCount,
    int TerminalNoFillCount,
    int UnknownCount,
    decimal AverageFillRatio,
    decimal MedianAdverseSlippageBps,
    decimal P95AdverseSlippageBps,
    decimal MedianSlippageDriftBps,
    decimal MedianObservedFeeRateBps,
    decimal MedianFeeDriftBps,
    decimal MedianTotalExecutionDriftBps,
    decimal P95TotalExecutionDriftBps,
    long MedianObservationLatencyMs,
    long P95ObservationLatencyMs,
    DateTimeOffset LatestObservedAtUtc,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class ExecutionRealityCalibrationV1
{
    public const string Schema = "wpe.execution-reality-calibration/1.0";

    public static ExecutionRealityCalibrationSnapshotV1 Create(
        string strategyId,
        string strategyVersion,
        IReadOnlyList<ExecutionRealityDriftFactV1> facts)
    {
        if (string.IsNullOrWhiteSpace(strategyId)) throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        if (string.IsNullOrWhiteSpace(strategyVersion)) throw new ArgumentException("Strategy version is required.", nameof(strategyVersion));
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0) throw new InvalidOperationException("Execution reality calibration requires observations.");

        foreach (var fact in facts)
        {
            if (!ExecutionRealityDriftV1.IsCanonical(fact))
                throw new InvalidOperationException("Execution reality calibration rejected non-canonical evidence.");
            if (!string.Equals(fact.StrategyId, strategyId, StringComparison.Ordinal)
                || !string.Equals(fact.StrategyVersion, strategyVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("Execution reality calibration rejected cross-strategy evidence.");
        }

        var comparable = facts.Where(x => x.Comparable).ToArray();
        var feeComparable = facts.Where(x => x.FeeComparable).ToArray();
        var totalComparable = facts.Where(x => x.TotalComparable).ToArray();
        var latencies = facts.Select(x => x.ObservationLatencyMs).OrderBy(x => x).ToArray();

        var draft = new ExecutionRealityCalibrationSnapshotV1(
            Schema,
            strategyId,
            strategyVersion,
            facts.Count,
            comparable.Length,
            feeComparable.Length,
            totalComparable.Length,
            facts.Count(x => x.State == ExecutionRealityStateV1.Filled),
            facts.Count(x => x.State == ExecutionRealityStateV1.Partial),
            facts.Count(x => x.State == ExecutionRealityStateV1.NotFilled),
            facts.Count(x => x.State == ExecutionRealityStateV1.Unknown),
            comparable.Length == 0 ? 0 : comparable.Average(x => x.FillRatio),
            Median(comparable.Select(x => x.AdverseSlippageBps)),
            P95(comparable.Select(x => x.AdverseSlippageBps)),
            Median(comparable.Select(x => x.SlippageDriftBps)),
            Median(feeComparable.Select(x => x.ObservedFeeRateBps)),
            Median(feeComparable.Select(x => x.FeeDriftBps)),
            Median(totalComparable.Select(x => x.TotalExecutionDriftBps)),
            P95(totalComparable.Select(x => x.TotalExecutionDriftBps)),
            Median(latencies),
            P95(latencies),
            facts.Max(x => x.ObservedAtUtc).ToUniversalTime(),
            Array.Empty<byte>(),
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(ExecutionRealityCalibrationSnapshotV1 value)
    {
        if (value.Schema != Schema || value.CanonicalBytes.Length == 0 || value.CanonicalSha256.Length != 64)
            return false;
        var bytes = Serialize(value);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(hash, value.CanonicalSha256, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes);
    }

    private static decimal Median(IEnumerable<decimal> source)
    {
        var values = source.OrderBy(x => x).ToArray();
        if (values.Length == 0) return 0;
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2m;
    }

    private static decimal P95(IEnumerable<decimal> source)
    {
        var values = source.OrderBy(x => x).ToArray();
        if (values.Length == 0) return 0;
        var index = Math.Max(0, (int)Math.Ceiling(values.Length * .95) - 1);
        return values[index];
    }

    private static long Median(IReadOnlyList<long> values)
    {
        if (values.Count == 0) return 0;
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : checked((values[middle - 1] + values[middle]) / 2);
    }

    private static long P95(IReadOnlyList<long> values)
    {
        if (values.Count == 0) return 0;
        var index = Math.Max(0, (int)Math.Ceiling(values.Count * .95) - 1);
        return values[index];
    }

    private static byte[] Serialize(ExecutionRealityCalibrationSnapshotV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("average_fill_ratio", value.AverageFillRatio);
            writer.WriteNumber("comparable_count", value.ComparableCount);
            writer.WriteNumber("fee_comparable_count", value.FeeComparableCount);
            writer.WriteNumber("filled_count", value.FilledCount);
            writer.WriteString("latest_observed_at_utc", value.LatestObservedAtUtc.ToUniversalTime());
            writer.WriteNumber("median_adverse_slippage_bps", value.MedianAdverseSlippageBps);
            writer.WriteNumber("median_fee_drift_bps", value.MedianFeeDriftBps);
            writer.WriteNumber("median_observation_latency_ms", value.MedianObservationLatencyMs);
            writer.WriteNumber("median_observed_fee_rate_bps", value.MedianObservedFeeRateBps);
            writer.WriteNumber("median_slippage_drift_bps", value.MedianSlippageDriftBps);
            writer.WriteNumber("median_total_execution_drift_bps", value.MedianTotalExecutionDriftBps);
            writer.WriteNumber("observation_count", value.ObservationCount);
            writer.WriteNumber("p95_adverse_slippage_bps", value.P95AdverseSlippageBps);
            writer.WriteNumber("p95_observation_latency_ms", value.P95ObservationLatencyMs);
            writer.WriteNumber("p95_total_execution_drift_bps", value.P95TotalExecutionDriftBps);
            writer.WriteNumber("partial_count", value.PartialCount);
            writer.WriteString("schema", value.Schema);
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteNumber("terminal_no_fill_count", value.TerminalNoFillCount);
            writer.WriteNumber("total_comparable_count", value.TotalComparableCount);
            writer.WriteNumber("unknown_count", value.UnknownCount);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

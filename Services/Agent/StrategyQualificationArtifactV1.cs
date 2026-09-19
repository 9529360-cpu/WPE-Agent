using System.Security.Cryptography;
using System.Text.Json;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

internal sealed record StrategyQualificationArtifactV1(
    string Schema,
    string PolicyVersion,
    string PolicySha256,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    string BacktestValidationSha256,
    string TimelineSha256,
    int ShadowObservationCount,
    string ShadowEvidenceSetSha256,
    IReadOnlyList<string> ShadowObservationSha256,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    double Expectancy,
    double MaxDrawdown,
    double QualityScore,
    int FailureStreak,
    DateTimeOffset QualifiedAtUtc,
    byte[] CanonicalBytes,
    string CanonicalSha256);

internal static class StrategyQualificationArtifactCanonicalizerV1
{
    internal const string Schema="wpe.strategy-qualification/1.0";
    internal const string PolicyVersion="wpe.shadow-qualification-policy/1.0";

    internal static StrategyObservationPerformance EvaluateShadow(
        IReadOnlyList<StrategyShadowObservationV1> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rows=CanonicalRows(input);
        if(rows.Count<2)
            return new(rows.Count,0,0,0,0,"canonical shadow observations pending");

        var returns=new List<double>(rows.Count-1);
        foreach(var (current,next) in rows.Zip(rows.Skip(1)))
            returns.Add((double)current.Direction*(double)(next.MarketPrice/current.MarketPrice-1));

        var expectancy=returns.Average();
        var equity=1d;
        var high=1d;
        var drawdown=0d;
        var failures=0;
        foreach(var value in returns)
        {
            equity*=Math.Max(.01,1+value);
            high=Math.Max(high,equity);
            drawdown=Math.Max(drawdown,(high-equity)/high);
            if(value<0)failures++;else failures=0;
        }
        var quality=Math.Clamp(.5+expectancy*50-drawdown,0,1);
        return new(
            rows.Count,
            expectancy,
            drawdown,
            quality,
            failures,
            $"canonical_shadow_observations={rows.Count} expectancy={expectancy:P2} drawdown={drawdown:P1}");
    }

    internal static StrategyQualificationArtifactV1 Create(
        IReadOnlyList<StrategyShadowObservationV1> input,
        DateTimeOffset qualifiedAtUtc)
    {
        var rows=CanonicalRows(input);
        if(rows.Count==0)
            throw new InvalidOperationException("Strategy qualification requires canonical Shadow evidence.");

        var performance=EvaluateShadow(rows);
        if(performance.Observations<StrategyGovernor.MinimumShadowObservations
           ||performance.QualityScore<StrategyGovernor.MinimumQualityScore
           ||performance.Expectancy<=0
           ||performance.MaxDrawdown>StrategyGovernor.MaximumPromotedDrawdown
           ||performance.FailureStreak!=0)
            throw new InvalidOperationException("Strategy Shadow evidence does not satisfy activation policy.");

        var qualifiedAt=qualifiedAtUtc.ToUniversalTime();
        if(qualifiedAtUtc.Offset!=TimeSpan.Zero
           ||qualifiedAt==default
           ||qualifiedAt<rows[^1].ObservedAtUtc)
            throw new InvalidOperationException("Strategy qualification time is invalid.");

        var hashes=rows.Select(x=>x.CanonicalSha256).ToArray();
        var evidenceHash=EvidenceSetHash(hashes);
        var policyHash=PolicyHash();
        var first=rows[0];

        var draft=new StrategyQualificationArtifactV1(
            Schema,
            PolicyVersion,
            policyHash,
            first.StrategyId,
            first.StrategyVersion,
            first.Symbol,
            first.BacktestValidationSha256,
            first.TimelineSha256,
            rows.Count,
            evidenceHash,
            hashes,
            rows[0].ObservedAtUtc,
            rows[^1].ObservedAtUtc,
            performance.Expectancy,
            performance.MaxDrawdown,
            performance.QualityScore,
            performance.FailureStreak,
            qualifiedAt,
            [],
            string.Empty);
        var bytes=Serialize(draft);
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with{CanonicalBytes=bytes,CanonicalSha256=hash};
    }

    internal static bool IsCanonical(StrategyQualificationArtifactV1? value)
    {
        if(value is null
           ||value.Schema!=Schema
           ||value.PolicyVersion!=PolicyVersion
           ||!Sha(value.PolicySha256)
           ||!Sha(value.ShadowEvidenceSetSha256)
           ||!Sha(value.CanonicalSha256)
           ||value.CanonicalBytes.Length==0
           ||string.IsNullOrWhiteSpace(value.StrategyId)
           ||string.IsNullOrWhiteSpace(value.StrategyVersion)
           ||string.IsNullOrWhiteSpace(value.Symbol)
           ||!Sha(value.BacktestValidationSha256)
           ||!Sha(value.TimelineSha256)
           ||value.ShadowObservationCount<StrategyGovernor.MinimumShadowObservations
           ||value.ShadowObservationSha256.Count!=value.ShadowObservationCount
           ||value.ShadowObservationSha256.Distinct(StringComparer.Ordinal).Count()!=value.ShadowObservationCount
           ||value.ShadowObservationSha256.Any(x=>!Sha(x))
           ||value.FirstObservedAtUtc.Offset!=TimeSpan.Zero
           ||value.LastObservedAtUtc.Offset!=TimeSpan.Zero
           ||value.QualifiedAtUtc.Offset!=TimeSpan.Zero
           ||value.FirstObservedAtUtc>value.LastObservedAtUtc
           ||value.LastObservedAtUtc>value.QualifiedAtUtc
           ||!double.IsFinite(value.Expectancy)
           ||!double.IsFinite(value.MaxDrawdown)
           ||!double.IsFinite(value.QualityScore)
           ||value.QualityScore<StrategyGovernor.MinimumQualityScore
           ||value.Expectancy<=0
           ||value.MaxDrawdown<0
           ||value.MaxDrawdown>StrategyGovernor.MaximumPromotedDrawdown
           ||value.FailureStreak!=0
           ||!string.Equals(value.PolicySha256,PolicyHash(),StringComparison.Ordinal)
           ||!string.Equals(value.ShadowEvidenceSetSha256,EvidenceSetHash(value.ShadowObservationSha256),StringComparison.Ordinal))
            return false;

        try
        {
            var bytes=Serialize(value);
            return CryptographicOperations.FixedTimeEquals(bytes,value.CanonicalBytes)
                &&CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes),
                    Convert.FromHexString(value.CanonicalSha256));
        }
        catch{return false;}
    }

    internal static bool TryDeserialize(
        ReadOnlySpan<byte> canonicalBytes,
        string canonicalSha256,
        out StrategyQualificationArtifactV1? value)
    {
        value=null;
        if(canonicalBytes.IsEmpty||!Sha(canonicalSha256))return false;
        try
        {
            var bytes=canonicalBytes.ToArray();
            var actual=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if(!string.Equals(actual,canonicalSha256,StringComparison.Ordinal))return false;
            using var document=JsonDocument.Parse(bytes);
            var root=document.RootElement;
            var hashes=root.GetProperty("shadow_observation_sha256")
                .EnumerateArray()
                .Select(x=>x.GetString()??string.Empty)
                .ToArray();
            value=new(
                root.GetProperty("schema").GetString()??string.Empty,
                root.GetProperty("policy_version").GetString()??string.Empty,
                root.GetProperty("policy_sha256").GetString()??string.Empty,
                root.GetProperty("strategy_id").GetString()??string.Empty,
                root.GetProperty("strategy_version").GetString()??string.Empty,
                root.GetProperty("symbol").GetString()??string.Empty,
                root.GetProperty("backtest_validation_sha256").GetString()??string.Empty,
                root.GetProperty("timeline_sha256").GetString()??string.Empty,
                root.GetProperty("shadow_observation_count").GetInt32(),
                root.GetProperty("shadow_evidence_set_sha256").GetString()??string.Empty,
                hashes,
                DateTimeOffset.Parse(root.GetProperty("first_observed_at_utc").GetString()??string.Empty),
                DateTimeOffset.Parse(root.GetProperty("last_observed_at_utc").GetString()??string.Empty),
                root.GetProperty("expectancy").GetDouble(),
                root.GetProperty("max_drawdown").GetDouble(),
                root.GetProperty("quality_score").GetDouble(),
                root.GetProperty("failure_streak").GetInt32(),
                DateTimeOffset.Parse(root.GetProperty("qualified_at_utc").GetString()??string.Empty),
                bytes,
                canonicalSha256);
            return IsCanonical(value);
        }
        catch
        {
            value=null;
            return false;
        }
    }

    private static IReadOnlyList<StrategyShadowObservationV1> CanonicalRows(
        IReadOnlyList<StrategyShadowObservationV1> input)
    {
        var rows=input
            .OrderBy(x=>x.MarketCollectedAtUtc)
            .ThenBy(x=>x.CanonicalSha256,StringComparer.Ordinal)
            .ToArray();
        if(rows.Length==0)return rows;

        var first=rows[0];
        var seenHash=new HashSet<string>(StringComparer.Ordinal);
        var seenMarket=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in rows)
        {
            if(!StrategyShadowObservationCanonicalizerV1.IsCanonical(row)
               ||!string.Equals(row.StrategyId,first.StrategyId,StringComparison.Ordinal)
               ||!string.Equals(row.StrategyVersion,first.StrategyVersion,StringComparison.Ordinal)
               ||!string.Equals(row.Symbol,first.Symbol,StringComparison.Ordinal)
               ||!string.Equals(row.BacktestValidationSha256,first.BacktestValidationSha256,StringComparison.Ordinal)
               ||!string.Equals(row.TimelineSha256,first.TimelineSha256,StringComparison.Ordinal)
               ||!seenHash.Add(row.CanonicalSha256)
               ||!seenMarket.Add(row.MarketProvenanceSha256))
                throw new InvalidOperationException("Strategy qualification Shadow evidence is mixed, duplicate or noncanonical.");
        }
        return rows;
    }

    private static string PolicyHash()
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("maximum_promoted_drawdown",StrategyGovernor.MaximumPromotedDrawdown);
            writer.WriteNumber("maximum_failure_streak",0);
            writer.WriteNumber("minimum_quality_score",StrategyGovernor.MinimumQualityScore);
            writer.WriteNumber("minimum_shadow_observations",StrategyGovernor.MinimumShadowObservations);
            writer.WriteBoolean("require_positive_expectancy",true);
            writer.WriteString("version",PolicyVersion);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string EvidenceSetHash(IReadOnlyList<string> hashes)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach(var hash in hashes)writer.WriteStringValue(hash);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static byte[] Serialize(StrategyQualificationArtifactV1 value)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("backtest_validation_sha256",value.BacktestValidationSha256);
            writer.WriteNumber("expectancy",value.Expectancy);
            writer.WriteNumber("failure_streak",value.FailureStreak);
            writer.WriteString("first_observed_at_utc",value.FirstObservedAtUtc.ToUniversalTime());
            writer.WriteString("last_observed_at_utc",value.LastObservedAtUtc.ToUniversalTime());
            writer.WriteNumber("max_drawdown",value.MaxDrawdown);
            writer.WriteString("policy_sha256",value.PolicySha256);
            writer.WriteString("policy_version",value.PolicyVersion);
            writer.WriteString("qualified_at_utc",value.QualifiedAtUtc.ToUniversalTime());
            writer.WriteNumber("quality_score",value.QualityScore);
            writer.WriteString("schema",value.Schema);
            writer.WriteString("shadow_evidence_set_sha256",value.ShadowEvidenceSetSha256);
            writer.WriteNumber("shadow_observation_count",value.ShadowObservationCount);
            writer.WritePropertyName("shadow_observation_sha256");
            writer.WriteStartArray();
            foreach(var hash in value.ShadowObservationSha256)writer.WriteStringValue(hash);
            writer.WriteEndArray();
            writer.WriteString("strategy_id",value.StrategyId);
            writer.WriteString("strategy_version",value.StrategyVersion);
            writer.WriteString("symbol",value.Symbol);
            writer.WriteString("timeline_sha256",value.TimelineSha256);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool Sha(string value)=>value.Length==64&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
}

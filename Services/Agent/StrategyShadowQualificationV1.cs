using System.Security.Cryptography;
using System.Text.Json;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

internal enum StrategyShadowQualificationStateV1
{
    Ready,
    Insufficient,
    Invalid
}

internal sealed record StrategyShadowQualificationPolicyV1(
    string Version,
    int MinimumObservations,
    int MaximumObservations,
    double MinimumQualityScore,
    double MaximumDrawdown,
    bool RequirePositiveExpectancy,
    int MaximumFailureStreak,
    TimeSpan MaximumEvidenceAge);

internal sealed record StrategyShadowQualificationDecisionV1(
    string Schema,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    string MarketProviderId,
    string Environment,
    string BacktestValidationSha256,
    string TimelineSha256,
    string PolicyVersion,
    string PolicySha256,
    StrategyShadowQualificationStateV1 State,
    bool Qualified,
    int ObservationCount,
    DateTimeOffset FirstMarketAtUtc,
    DateTimeOffset LastMarketAtUtc,
    double ObservationWindowSeconds,
    double Expectancy,
    double MaxDrawdown,
    double QualityScore,
    int FailureStreak,
    IReadOnlyList<string> EvidenceCanonicalSha256,
    string EvidenceSetSha256,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<string> ReasonCodes,
    byte[] CanonicalBytes,
    string CanonicalSha256);

internal static class StrategyShadowObservationAnalyticsV1
{
    internal static StrategyObservationPerformance Evaluate(IReadOnlyList<StrategyShadowObservationV1> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if(rows.Count<2)
            return new(rows.Count,0,0,0,0,"canonical shadow observations pending");

        var ordered=rows
            .OrderBy(x=>x.MarketCollectedAtUtc)
            .ThenBy(x=>x.CanonicalSha256,StringComparer.Ordinal)
            .ToArray();
        var returns=new List<double>(ordered.Length-1);
        foreach(var (current,next) in ordered.Zip(ordered.Skip(1)))
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
            ordered.Length,
            expectancy,
            drawdown,
            quality,
            failures,
            $"canonical_shadow_observations={ordered.Length} expectancy={expectancy:P2} drawdown={drawdown:P1}");
    }
}

internal static class StrategyShadowQualificationV1
{
    internal const string Schema="wpe.strategy-shadow-qualification/1.0";
    internal static readonly StrategyShadowQualificationPolicyV1 DefaultPolicy=new(
        "shadow-qualification-v1",
        StrategyGovernor.MinimumShadowObservations,
        StrategyGovernor.MaximumUnqualifiedShadowObservations,
        StrategyGovernor.MinimumQualityScore,
        StrategyGovernor.MaximumPromotedDrawdown,
        true,
        0,
        StrategyShadowObservationCanonicalizerV1.MaximumMarketAge);

    private static readonly HashSet<string> IntegrityReasons=new(StringComparer.Ordinal)
    {
        "evidence.noncanonical",
        "evidence.identity-mismatch",
        "evidence.duplicate",
        "evidence.mixed-validation",
        "evidence.mixed-timeline",
        "evidence.mixed-provider",
        "evidence.future",
        "evidence.too-many"
    };

    internal static StrategyShadowQualificationDecisionV1 Evaluate(
        string strategyId,
        string strategyVersion,
        string symbol,
        IReadOnlyList<StrategyShadowObservationV1> evidence,
        DateTimeOffset evaluatedAtUtc,
        StrategyShadowQualificationPolicyV1? policy=null)
    {
        if(string.IsNullOrWhiteSpace(strategyId))throw new ArgumentException("Strategy id is required.",nameof(strategyId));
        if(string.IsNullOrWhiteSpace(strategyVersion))throw new ArgumentException("Strategy version is required.",nameof(strategyVersion));
        if(string.IsNullOrWhiteSpace(symbol))throw new ArgumentException("Symbol is required.",nameof(symbol));
        ArgumentNullException.ThrowIfNull(evidence);
        policy??=DefaultPolicy;
        ValidatePolicy(policy);
        if(evaluatedAtUtc.Offset!=TimeSpan.Zero||evaluatedAtUtc==default)
            throw new ArgumentException("Evaluation time must be UTC.",nameof(evaluatedAtUtc));

        var reasons=new SortedSet<string>(StringComparer.Ordinal);
        var valid=new List<StrategyShadowObservationV1>(evidence.Count);
        var seenMarket=new HashSet<string>(StringComparer.Ordinal);
        var seenCanonical=new HashSet<string>(StringComparer.Ordinal);
        string? validationHash=null;
        string? timelineHash=null;
        string? providerId=null;
        string? environment=null;

        foreach(var value in evidence
            .OrderBy(x=>x.MarketCollectedAtUtc)
            .ThenBy(x=>x.CanonicalSha256,StringComparer.Ordinal))
        {
            if(!StrategyShadowObservationCanonicalizerV1.IsCanonical(value))
            {
                reasons.Add("evidence.noncanonical");
                continue;
            }
            if(!string.Equals(value.StrategyId,strategyId,StringComparison.Ordinal)
               ||!string.Equals(value.StrategyVersion,strategyVersion,StringComparison.Ordinal)
               ||!string.Equals(value.Symbol,symbol,StringComparison.Ordinal))
            {
                reasons.Add("evidence.identity-mismatch");
                continue;
            }
            if(value.MarketCollectedAtUtc>evaluatedAtUtc||value.ObservedAtUtc>evaluatedAtUtc)
            {
                reasons.Add("evidence.future");
                continue;
            }
            if(!seenMarket.Add(value.MarketProvenanceSha256)
               ||!seenCanonical.Add(value.CanonicalSha256))
            {
                reasons.Add("evidence.duplicate");
                continue;
            }

            if(validationHash is not null
               &&!string.Equals(validationHash,value.BacktestValidationSha256,StringComparison.Ordinal))
                reasons.Add("evidence.mixed-validation");
            if(timelineHash is not null
               &&!string.Equals(timelineHash,value.TimelineSha256,StringComparison.Ordinal))
                reasons.Add("evidence.mixed-timeline");
            if(providerId is not null
               &&(!string.Equals(providerId,value.MarketProviderId,StringComparison.Ordinal)
                  ||!string.Equals(environment,value.Environment,StringComparison.Ordinal)))
                reasons.Add("evidence.mixed-provider");

            validationHash??=value.BacktestValidationSha256;
            timelineHash??=value.TimelineSha256;
            providerId??=value.MarketProviderId;
            environment??=value.Environment;
            valid.Add(value);
        }

        if(valid.Count>policy.MaximumObservations)reasons.Add("evidence.too-many");
        var performance=StrategyShadowObservationAnalyticsV1.Evaluate(valid);
        if(performance.Observations<policy.MinimumObservations)reasons.Add("sample.observations");
        var last=valid.Count==0?evaluatedAtUtc:valid.Max(x=>x.MarketCollectedAtUtc);
        var first=valid.Count==0?evaluatedAtUtc:valid.Min(x=>x.MarketCollectedAtUtc);
        if(valid.Count==0||evaluatedAtUtc-last>policy.MaximumEvidenceAge)reasons.Add("freshness.stale");
        if(performance.QualityScore<policy.MinimumQualityScore)reasons.Add("quality.score");
        if(performance.MaxDrawdown>policy.MaximumDrawdown)reasons.Add("quality.drawdown");
        if(policy.RequirePositiveExpectancy&&performance.Expectancy<=0)reasons.Add("quality.expectancy");
        if(performance.FailureStreak>policy.MaximumFailureStreak)reasons.Add("quality.failure-streak");

        var state=reasons.Overlaps(IntegrityReasons)
            ?StrategyShadowQualificationStateV1.Invalid
            :reasons.Count==0
                ?StrategyShadowQualificationStateV1.Ready
                :StrategyShadowQualificationStateV1.Insufficient;

        var hashes=valid
            .OrderBy(x=>x.MarketCollectedAtUtc)
            .ThenBy(x=>x.CanonicalSha256,StringComparer.Ordinal)
            .Select(x=>x.CanonicalSha256)
            .ToArray();
        var evidenceSetSha=EvidenceSetHash(hashes);
        var policyHash=PolicyHash(policy);
        var reasonRows=reasons.ToArray();
        var draft=new StrategyShadowQualificationDecisionV1(
            Schema,
            strategyId,
            strategyVersion,
            symbol,
            providerId??string.Empty,
            environment??"Testnet",
            validationHash??string.Empty,
            timelineHash??string.Empty,
            policy.Version,
            policyHash,
            state,
            state==StrategyShadowQualificationStateV1.Ready,
            valid.Count,
            first,
            last,
            Math.Max(0,(last-first).TotalSeconds),
            performance.Expectancy,
            performance.MaxDrawdown,
            performance.QualityScore,
            performance.FailureStreak,
            hashes,
            evidenceSetSha,
            evaluatedAtUtc,
            reasonRows,
            [],
            string.Empty);
        var bytes=Serialize(draft);
        var hash=Sha(bytes);
        return draft with{CanonicalBytes=bytes,CanonicalSha256=hash};
    }

    internal static bool IsCanonical(StrategyShadowQualificationDecisionV1? value)
    {
        if(value is null
           ||value.Schema!=Schema
           ||!SafeToken(value.StrategyId)
           ||!SafeToken(value.StrategyVersion)
           ||!SafeToken(value.Symbol)
           ||value.Environment!="Testnet"
           ||!SafeToken(value.MarketProviderId)
           ||!LowerSha(value.BacktestValidationSha256)
           ||!LowerSha(value.TimelineSha256)
           ||value.PolicyVersion!=DefaultPolicy.Version
           ||!string.Equals(value.PolicySha256,PolicyHash(DefaultPolicy),StringComparison.Ordinal)
           ||value.Qualified!=(value.State==StrategyShadowQualificationStateV1.Ready)
           ||value.ObservationCount!=value.EvidenceCanonicalSha256.Count
           ||value.ObservationCount<0
           ||value.ObservationWindowSeconds<0
           ||value.FirstMarketAtUtc.Offset!=TimeSpan.Zero
           ||value.LastMarketAtUtc.Offset!=TimeSpan.Zero
           ||value.EvaluatedAtUtc.Offset!=TimeSpan.Zero
           ||value.FirstMarketAtUtc>value.LastMarketAtUtc
           ||value.LastMarketAtUtc>value.EvaluatedAtUtc
           ||!double.IsFinite(value.Expectancy)
           ||!double.IsFinite(value.MaxDrawdown)
           ||!double.IsFinite(value.QualityScore)
           ||value.MaxDrawdown<0
           ||value.QualityScore is<0 or>1
           ||value.FailureStreak<0
           ||value.EvidenceCanonicalSha256.Any(x=>!LowerSha(x))
           ||value.EvidenceCanonicalSha256.Distinct(StringComparer.Ordinal).Count()!=value.EvidenceCanonicalSha256.Count
           ||!string.Equals(value.EvidenceSetSha256,EvidenceSetHash(value.EvidenceCanonicalSha256),StringComparison.Ordinal)
           ||!LowerSha(value.CanonicalSha256)
           ||value.CanonicalBytes.Length==0
           ||!value.ReasonCodes.SequenceEqual(value.ReasonCodes.OrderBy(x=>x,StringComparer.Ordinal)))
            return false;

        try
        {
            var bytes=Serialize(value);
            return CryptographicOperations.FixedTimeEquals(bytes,value.CanonicalBytes)
                &&CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),Convert.FromHexString(value.CanonicalSha256));
        }
        catch{return false;}
    }

    internal static string PolicyHash(StrategyShadowQualificationPolicyV1 policy)
    {
        ValidatePolicy(policy);
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("maximum_drawdown",policy.MaximumDrawdown);
            writer.WriteNumber("maximum_evidence_age_seconds",policy.MaximumEvidenceAge.TotalSeconds);
            writer.WriteNumber("maximum_failure_streak",policy.MaximumFailureStreak);
            writer.WriteNumber("maximum_observations",policy.MaximumObservations);
            writer.WriteNumber("minimum_observations",policy.MinimumObservations);
            writer.WriteNumber("minimum_quality_score",policy.MinimumQualityScore);
            writer.WriteBoolean("require_positive_expectancy",policy.RequirePositiveExpectancy);
            writer.WriteString("version",policy.Version);
            writer.WriteEndObject();
        }
        return Sha(stream.ToArray());
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
        return Sha(stream.ToArray());
    }

    private static byte[] Serialize(StrategyShadowQualificationDecisionV1 value)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("backtest_validation_sha256",value.BacktestValidationSha256);
            writer.WriteString("environment",value.Environment);
            writer.WriteString("evaluated_at_utc",value.EvaluatedAtUtc);
            writer.WriteString("evidence_set_sha256",value.EvidenceSetSha256);
            writer.WritePropertyName("evidence_sha256");
            writer.WriteStartArray();
            foreach(var hash in value.EvidenceCanonicalSha256)writer.WriteStringValue(hash);
            writer.WriteEndArray();
            writer.WriteNumber("failure_streak",value.FailureStreak);
            writer.WriteString("first_market_at_utc",value.FirstMarketAtUtc);
            writer.WriteNumber("max_drawdown",value.MaxDrawdown);
            writer.WriteString("last_market_at_utc",value.LastMarketAtUtc);
            writer.WriteString("market_provider_id",value.MarketProviderId);
            writer.WriteNumber("observation_count",value.ObservationCount);
            writer.WriteNumber("observation_window_seconds",value.ObservationWindowSeconds);
            writer.WriteString("policy_sha256",value.PolicySha256);
            writer.WriteString("policy_version",value.PolicyVersion);
            writer.WriteNumber("quality_score",value.QualityScore);
            writer.WriteBoolean("qualified",value.Qualified);
            writer.WritePropertyName("reason_codes");
            writer.WriteStartArray();
            foreach(var reason in value.ReasonCodes)writer.WriteStringValue(reason);
            writer.WriteEndArray();
            writer.WriteString("schema",value.Schema);
            writer.WriteString("state",value.State.ToString().ToLowerInvariant());
            writer.WriteString("strategy_id",value.StrategyId);
            writer.WriteString("strategy_version",value.StrategyVersion);
            writer.WriteString("symbol",value.Symbol);
            writer.WriteString("timeline_sha256",value.TimelineSha256);
            writer.WriteNumber("expectancy",value.Expectancy);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void ValidatePolicy(StrategyShadowQualificationPolicyV1 policy)
    {
        if(string.IsNullOrWhiteSpace(policy.Version)
           ||policy.MinimumObservations<2
           ||policy.MaximumObservations<policy.MinimumObservations
           ||!double.IsFinite(policy.MinimumQualityScore)
           ||policy.MinimumQualityScore is<0 or>1
           ||!double.IsFinite(policy.MaximumDrawdown)
           ||policy.MaximumDrawdown is<0 or>1
           ||policy.MaximumFailureStreak<0
           ||policy.MaximumEvidenceAge<=TimeSpan.Zero)
            throw new ArgumentException("Shadow qualification policy is invalid.",nameof(policy));
    }

    private static string Sha(ReadOnlySpan<byte> bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool LowerSha(string value)=>value.Length==64&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
    private static bool SafeToken(string value)=>value.Length is>=1 and<=128&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '-'or'_'or'.' or ':');
}

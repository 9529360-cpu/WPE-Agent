using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

internal sealed record AutomaticStrategyQualificationEvidenceV1(
    bool Available,
    string Code,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    string MarketProviderId,
    string Environment,
    string BacktestValidationSha256,
    string TimelineSha256,
    string PolicyVersion,
    string PolicySha256,
    int ObservationCount,
    DateTimeOffset FirstMarketAtUtc,
    DateTimeOffset LastMarketAtUtc,
    string EvidenceSetSha256,
    DateTimeOffset EvaluatedAtUtc,
    byte[] CanonicalBytes,
    string CanonicalSha256);

internal static class AutomaticStrategyQualificationEvidenceVerifierV1
{
    internal const string Schema="wpe.strategy-shadow-qualification/1.0";
    internal const string PolicyVersion="shadow-qualification-v1";
    internal static readonly TimeSpan MaximumEvidenceAge=TimeSpan.FromHours(1);
    internal static readonly string ExpectedPolicySha256=PolicyHash();

    internal static AutomaticStrategyQualificationEvidenceV1 Unavailable(string code) =>
        new(false,code,string.Empty,string.Empty,string.Empty,string.Empty,"Testnet",
            string.Empty,string.Empty,PolicyVersion,ExpectedPolicySha256,0,
            default,default,string.Empty,default,[],string.Empty);

    internal static bool TryParse(
        ReadOnlySpan<byte> canonicalBytes,
        string canonicalSha256,
        out AutomaticStrategyQualificationEvidenceV1? evidence,
        out string code)
    {
        evidence=null;
        code="strategy-qualification-invalid";
        if(canonicalBytes.IsEmpty||!LowerSha(canonicalSha256))return false;
        try
        {
            var bytes=canonicalBytes.ToArray();
            if(!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes),
                Convert.FromHexString(canonicalSha256)))
                return false;

            using var document=JsonDocument.Parse(bytes);
            var root=document.RootElement;
            if(!string.Equals(root.GetProperty("schema").GetString(),Schema,StringComparison.Ordinal)
               ||!string.Equals(root.GetProperty("state").GetString(),"ready",StringComparison.Ordinal)
               ||!root.GetProperty("qualified").GetBoolean())
                return false;

            var strategyId=Required(root,"strategy_id");
            var strategyVersion=Required(root,"strategy_version");
            var symbol=Required(root,"symbol");
            var provider=Required(root,"market_provider_id");
            var environment=Required(root,"environment");
            var validationHash=Required(root,"backtest_validation_sha256");
            var timelineHash=Required(root,"timeline_sha256");
            var policyVersion=Required(root,"policy_version");
            var policyHash=Required(root,"policy_sha256");
            var evidenceSetHash=Required(root,"evidence_set_sha256");
            var count=root.GetProperty("observation_count").GetInt32();
            var first=root.GetProperty("first_market_at_utc").GetDateTimeOffset();
            var last=root.GetProperty("last_market_at_utc").GetDateTimeOffset();
            var evaluated=root.GetProperty("evaluated_at_utc").GetDateTimeOffset();
            var window=root.GetProperty("observation_window_seconds").GetDouble();
            var expectancy=root.GetProperty("expectancy").GetDouble();
            var drawdown=root.GetProperty("max_drawdown").GetDouble();
            var quality=root.GetProperty("quality_score").GetDouble();
            var failureStreak=root.GetProperty("failure_streak").GetInt32();
            var hashes=root.GetProperty("evidence_sha256")
                .EnumerateArray()
                .Select(x=>x.GetString()??string.Empty)
                .ToArray();
            var reasons=root.GetProperty("reason_codes")
                .EnumerateArray()
                .Select(x=>x.GetString()??string.Empty)
                .ToArray();

            if(!SafeToken(strategyId)
               ||!SafeToken(strategyVersion)
               ||!SafeToken(symbol)
               ||!SafeToken(provider)
               ||environment!="Testnet"
               ||!LowerSha(validationHash)
               ||!LowerSha(timelineHash)
               ||policyVersion!=PolicyVersion
               ||!string.Equals(policyHash,ExpectedPolicySha256,StringComparison.Ordinal)
               ||!LowerSha(evidenceSetHash)
               ||count<StrategyGovernor.MinimumShadowObservations
               ||count>StrategyGovernor.MaximumUnqualifiedShadowObservations
               ||hashes.Length!=count
               ||hashes.Any(x=>!LowerSha(x))
               ||hashes.Distinct(StringComparer.Ordinal).Count()!=count
               ||!string.Equals(evidenceSetHash,EvidenceSetHash(hashes),StringComparison.Ordinal)
               ||first.Offset!=TimeSpan.Zero
               ||last.Offset!=TimeSpan.Zero
               ||evaluated.Offset!=TimeSpan.Zero
               ||first>last
               ||last>evaluated
               ||evaluated-last>MaximumEvidenceAge
               ||!double.IsFinite(window)
               ||window<0
               ||Math.Abs(window-(last-first).TotalSeconds)>1e-6
               ||!double.IsFinite(expectancy)
               ||expectancy<=0
               ||!double.IsFinite(drawdown)
               ||drawdown<0
               ||drawdown>StrategyGovernor.MaximumPromotedDrawdown
               ||!double.IsFinite(quality)
               ||quality<StrategyGovernor.MinimumQualityScore
               ||quality>1
               ||failureStreak!=0
               ||reasons.Length!=0)
                return false;

            var canonical=Serialize(
                strategyId,strategyVersion,symbol,provider,environment,
                validationHash,timelineHash,policyVersion,policyHash,
                count,first,last,window,expectancy,drawdown,quality,failureStreak,
                hashes,evidenceSetHash,evaluated);
            if(!CryptographicOperations.FixedTimeEquals(canonical,bytes))
                return false;

            evidence=new(
                true,
                "strategy-qualification-ready",
                strategyId,
                strategyVersion,
                symbol,
                provider,
                environment,
                validationHash,
                timelineHash,
                policyVersion,
                policyHash,
                count,
                first,
                last,
                evidenceSetHash,
                evaluated,
                bytes,
                canonicalSha256);
            code=evidence.Code;
            return true;
        }
        catch
        {
            evidence=null;
            return false;
        }
    }

    private static byte[] Serialize(
        string strategyId,
        string strategyVersion,
        string symbol,
        string provider,
        string environment,
        string validationHash,
        string timelineHash,
        string policyVersion,
        string policyHash,
        int count,
        DateTimeOffset first,
        DateTimeOffset last,
        double window,
        double expectancy,
        double drawdown,
        double quality,
        int failureStreak,
        IReadOnlyList<string> hashes,
        string evidenceSetHash,
        DateTimeOffset evaluated)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("backtest_validation_sha256",validationHash);
            writer.WriteString("environment",environment);
            writer.WriteString("evaluated_at_utc",evaluated);
            writer.WriteString("evidence_set_sha256",evidenceSetHash);
            writer.WritePropertyName("evidence_sha256");
            writer.WriteStartArray();
            foreach(var hash in hashes)writer.WriteStringValue(hash);
            writer.WriteEndArray();
            writer.WriteNumber("failure_streak",failureStreak);
            writer.WriteString("first_market_at_utc",first);
            writer.WriteNumber("max_drawdown",drawdown);
            writer.WriteString("last_market_at_utc",last);
            writer.WriteString("market_provider_id",provider);
            writer.WriteNumber("observation_count",count);
            writer.WriteNumber("observation_window_seconds",window);
            writer.WriteString("policy_sha256",policyHash);
            writer.WriteString("policy_version",policyVersion);
            writer.WriteNumber("quality_score",quality);
            writer.WriteBoolean("qualified",true);
            writer.WritePropertyName("reason_codes");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteString("schema",Schema);
            writer.WriteString("state","ready");
            writer.WriteString("strategy_id",strategyId);
            writer.WriteString("strategy_version",strategyVersion);
            writer.WriteString("symbol",symbol);
            writer.WriteString("timeline_sha256",timelineHash);
            writer.WriteNumber("expectancy",expectancy);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string PolicyHash()
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("maximum_drawdown",StrategyGovernor.MaximumPromotedDrawdown);
            writer.WriteNumber("maximum_evidence_age_seconds",MaximumEvidenceAge.TotalSeconds);
            writer.WriteNumber("maximum_failure_streak",0);
            writer.WriteNumber("maximum_observations",StrategyGovernor.MaximumUnqualifiedShadowObservations);
            writer.WriteNumber("minimum_observations",StrategyGovernor.MinimumShadowObservations);
            writer.WriteNumber("minimum_quality_score",StrategyGovernor.MinimumQualityScore);
            writer.WriteBoolean("require_positive_expectancy",true);
            writer.WriteString("version",PolicyVersion);
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

    private static string Required(JsonElement root,string name)
    {
        var value=root.GetProperty(name).GetString();
        if(string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Qualification field '{name}' is required.");
        return value;
    }

    private static string Sha(ReadOnlySpan<byte> bytes)=>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool LowerSha(string value)=>
        value.Length==64&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');

    private static bool SafeToken(string value)=>
        value.Length is>=1 and<=128&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '-'or'_'or'.'or':');
}

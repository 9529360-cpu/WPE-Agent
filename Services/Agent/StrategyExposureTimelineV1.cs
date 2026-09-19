using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

/// <summary>
/// One deterministic research decision made from already-closed evidence and applied no earlier than
/// the next tradable bar. Strategy modules own TargetExposure only; execution economics belong to
/// ResearchRealityModel.
/// </summary>
internal sealed record StrategyExposureDecisionV1(
    int Sequence,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    DateTimeOffset SourceCandleOpenTimeUtc,
    DateTimeOffset EvidenceAvailableAtUtc,
    DateTimeOffset SignalGeneratedAtUtc,
    DateTimeOffset TradableAtUtc,
    int TargetExposure,
    decimal ExecutionOpenPrice,
    decimal ExecutionClosePrice,
    decimal ExecutionVolume);

internal sealed record StrategyExposureTimelineArtifactV1(
    string Schema,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    int DecisionCount,
    DateTimeOffset FirstTradableAtUtc,
    DateTimeOffset LastTradableAtUtc,
    IReadOnlyList<StrategyExposureDecisionV1> Decisions,
    byte[] CanonicalBytes,
    string CanonicalSha256);

internal static class StrategyExposureTimelineV1
{
    internal const string ArtifactSchema="wpe.strategy-exposure-timeline/1.0";

    internal static StrategyExposureDecisionV1? Create(
        StrategyProfile profile,
        int sequence,
        CandleEvidence sourceCandle,
        CandleEvidence executionCandle,
        int targetExposure)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if(sequence<0||targetExposure is < -1 or > 1)return null;
        if(string.IsNullOrWhiteSpace(profile.Id)||string.IsNullOrWhiteSpace(profile.Version)||string.IsNullOrWhiteSpace(profile.Symbol))return null;
        if(sourceCandle.OpenTime.Kind!=DateTimeKind.Utc||executionCandle.OpenTime.Kind!=DateTimeKind.Utc)return null;
        if(executionCandle.OpenTime<=sourceCandle.OpenTime)return null;
        if(executionCandle.Open<=0||executionCandle.Close<=0||executionCandle.Volume<0)return null;

        // With OHLCV bars, the previous closed candle becomes available at the next bar boundary.
        // Zero additional research latency is the most optimistic supported assumption in this slice.
        var sourceOpen=new DateTimeOffset(sourceCandle.OpenTime);
        var boundary=new DateTimeOffset(executionCandle.OpenTime);
        return new(sequence,profile.Id,profile.Version,profile.Symbol,sourceOpen,boundary,boundary,boundary,
            targetExposure,executionCandle.Open,executionCandle.Close,executionCandle.Volume);
    }

    internal static StrategyExposureTimelineArtifactV1? CreateArtifact(IReadOnlyList<StrategyExposureDecisionV1> values)
    {
        if(!IsCanonical(values)||values.Count==0)return null;
        var first=values[0];
        var decisions=values.OrderBy(x=>x.Sequence).ToArray();
        var draft=new StrategyExposureTimelineArtifactV1(
            ArtifactSchema,
            first.StrategyId,
            first.StrategyVersion,
            first.Symbol,
            decisions.Length,
            decisions[0].TradableAtUtc,
            decisions[^1].TradableAtUtc,
            decisions,
            [],
            string.Empty);
        var bytes=SerializeArtifact(draft);
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with{CanonicalBytes=bytes,CanonicalSha256=hash};
    }

    internal static bool IsCanonical(StrategyExposureTimelineArtifactV1? artifact)
    {
        if(artifact is null
           ||artifact.Schema!=ArtifactSchema
           ||artifact.DecisionCount<=0
           ||artifact.DecisionCount!=artifact.Decisions.Count
           ||artifact.CanonicalBytes.Length==0
           ||!LowerSha256(artifact.CanonicalSha256)
           ||!IsCanonical(artifact.Decisions))
            return false;
        var first=artifact.Decisions[0];var last=artifact.Decisions[^1];
        if(!string.Equals(artifact.StrategyId,first.StrategyId,StringComparison.Ordinal)
           ||!string.Equals(artifact.StrategyVersion,first.StrategyVersion,StringComparison.Ordinal)
           ||!string.Equals(artifact.Symbol,first.Symbol,StringComparison.Ordinal)
           ||artifact.FirstTradableAtUtc!=first.TradableAtUtc
           ||artifact.LastTradableAtUtc!=last.TradableAtUtc)
            return false;
        try
        {
            var expected=SerializeArtifact(artifact);
            return CryptographicOperations.FixedTimeEquals(expected,artifact.CanonicalBytes)
                &&CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected),Convert.FromHexString(artifact.CanonicalSha256));
        }
        catch{return false;}
    }

    internal static bool IsCanonical(IReadOnlyList<StrategyExposureDecisionV1> values)
    {
        if(values is null)return false;
        if(values.Count==0)return true;
        var first=values[0];
        if(!Valid(first))return false;
        for(var i=1;i<values.Count;i++)
        {
            var current=values[i];
            if(!Valid(current)
               ||current.Sequence!=i
               ||!string.Equals(current.StrategyId,first.StrategyId,StringComparison.Ordinal)
               ||!string.Equals(current.StrategyVersion,first.StrategyVersion,StringComparison.Ordinal)
               ||!string.Equals(current.Symbol,first.Symbol,StringComparison.Ordinal)
               ||current.TradableAtUtc<=values[i-1].TradableAtUtc)
                return false;
        }
        return first.Sequence==0;
    }

    private static byte[] SerializeArtifact(StrategyExposureTimelineArtifactV1 artifact)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("decision_count",artifact.DecisionCount);
            writer.WritePropertyName("decisions");
            writer.WriteStartArray();
            foreach(var value in artifact.Decisions.OrderBy(x=>x.Sequence))
            {
                writer.WriteStartObject();
                writer.WriteString("evidence_available_at_utc",value.EvidenceAvailableAtUtc.ToUniversalTime());
                writer.WriteNumber("execution_close_price",value.ExecutionClosePrice);
                writer.WriteNumber("execution_open_price",value.ExecutionOpenPrice);
                writer.WriteNumber("execution_volume",value.ExecutionVolume);
                writer.WriteNumber("sequence",value.Sequence);
                writer.WriteString("signal_generated_at_utc",value.SignalGeneratedAtUtc.ToUniversalTime());
                writer.WriteString("source_candle_open_time_utc",value.SourceCandleOpenTimeUtc.ToUniversalTime());
                writer.WriteString("strategy_id",value.StrategyId);
                writer.WriteString("strategy_version",value.StrategyVersion);
                writer.WriteString("symbol",value.Symbol);
                writer.WriteNumber("target_exposure",value.TargetExposure);
                writer.WriteString("tradable_at_utc",value.TradableAtUtc.ToUniversalTime());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("first_tradable_at_utc",artifact.FirstTradableAtUtc.ToUniversalTime());
            writer.WriteString("last_tradable_at_utc",artifact.LastTradableAtUtc.ToUniversalTime());
            writer.WriteString("schema",artifact.Schema);
            writer.WriteString("strategy_id",artifact.StrategyId);
            writer.WriteString("strategy_version",artifact.StrategyVersion);
            writer.WriteString("symbol",artifact.Symbol);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool LowerSha256(string value)=>value.Length==64&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');

    private static bool Valid(StrategyExposureDecisionV1 value)
        =>value.Sequence>=0
          &&!string.IsNullOrWhiteSpace(value.StrategyId)
          &&!string.IsNullOrWhiteSpace(value.StrategyVersion)
          &&!string.IsNullOrWhiteSpace(value.Symbol)
          &&value.TargetExposure is >=-1 and <=1
          &&value.ExecutionOpenPrice>0
          &&value.ExecutionClosePrice>0
          &&value.ExecutionVolume>=0
          &&Utc(value.SourceCandleOpenTimeUtc)
          &&Utc(value.EvidenceAvailableAtUtc)
          &&Utc(value.SignalGeneratedAtUtc)
          &&Utc(value.TradableAtUtc)
          &&value.SourceCandleOpenTimeUtc<value.EvidenceAvailableAtUtc
          &&value.EvidenceAvailableAtUtc<=value.SignalGeneratedAtUtc
          &&value.SignalGeneratedAtUtc<=value.TradableAtUtc;

    private static bool Utc(DateTimeOffset value)=>value!=default&&value.Offset==TimeSpan.Zero;
}

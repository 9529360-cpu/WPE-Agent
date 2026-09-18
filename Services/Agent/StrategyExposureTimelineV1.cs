using WpeAgent.CrossAssetResearch;
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

internal static class StrategyExposureTimelineV1
{
    internal const string Schema="wpe.strategy-exposure/1";
    internal const int MinimumWarmupBars=200;
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
          &&!ResearchTemporalIsolationV1.HasLookAhead(value.TradableAtUtc,value.EvidenceAvailableAtUtc,value.SignalGeneratedAtUtc);

    private static bool Utc(DateTimeOffset value)=>value!=default&&value.Offset==TimeSpan.Zero;
}

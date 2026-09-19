namespace WpeAgent.CrossAssetResearch;

/// <summary>
/// Shared chronological research authority for point-in-time validation and purged/embargoed
/// out-of-sample windows. Production strategy research and cross-asset research must use the
/// same implementation so temporal semantics cannot drift.
/// </summary>
internal static class ResearchTemporalIsolationV1
{
    internal const string LookAheadReason="research.look-ahead-leakage";
    internal const string TemporalIsolationReason="research.temporal-isolation-insufficient";

    internal static ResearchOosWindowV1 OosWindow(int returnCount,CrossAssetValidationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if(returnCount<=1||policy.TrainingFraction is <=0 or >=1)
            return new(0,0,0,0);
        var split=Math.Clamp((int)Math.Floor(returnCount*policy.TrainingFraction),1,returnCount-1);
        var start=Math.Min(returnCount,split+Math.Max(0,policy.OosPurgeObservations));
        var end=Math.Max(start,returnCount-Math.Max(0,policy.OosEmbargoObservations));
        return new(split,start,end,end-start);
    }

    internal static bool HasLookAhead(
        DateTimeOffset observationTimestampUtc,
        DateTimeOffset? dataAvailableAtUtc,
        DateTimeOffset? signalGeneratedAtUtc)
        =>dataAvailableAtUtc is null
          ||signalGeneratedAtUtc is null
          ||dataAvailableAtUtc>signalGeneratedAtUtc
          ||signalGeneratedAtUtc>observationTimestampUtc;

    internal static IReadOnlyList<string> Validate(
        IEnumerable<ResearchTemporalObservationV1> observations,
        int returnCount,
        CrossAssetValidationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(policy);
        var reasons=new List<string>();
        if(observations.Any(x=>HasLookAhead(x.TimestampUtc,x.DataAvailableAtUtc,x.SignalGeneratedAtUtc)))
            reasons.Add(LookAheadReason);
        if(OosWindow(returnCount,policy).Count<policy.MinimumOutOfSampleObservations)
            reasons.Add(TemporalIsolationReason);
        return reasons;
    }
}

internal readonly record struct ResearchTemporalObservationV1(
    DateTimeOffset TimestampUtc,
    DateTimeOffset? DataAvailableAtUtc,
    DateTimeOffset? SignalGeneratedAtUtc);

internal readonly record struct ResearchOosWindowV1(
    int TrainingEndExclusive,
    int Start,
    int EndExclusive,
    int Count);

namespace WpeAgent.CrossAssetResearch;

/// <summary>
/// Shared fail-closed time-isolation rules for historical research. This primitive owns only
/// chronological availability and OOS boundaries; it does not own strategy logic, costs or execution.
/// </summary>
public sealed record ResearchTemporalPoint(
    DateTimeOffset ObservationAtUtc,
    DateTimeOffset? DataAvailableAtUtc,
    DateTimeOffset? SignalGeneratedAtUtc);

public sealed record ResearchTemporalIsolationPolicy(
    double TrainingFraction=.65,
    int MinimumOutOfSampleObservations=30,
    int OosPurgeObservations=5,
    int OosEmbargoObservations=5);

public readonly record struct ResearchTemporalBounds(
    int TrainingEndExclusive,
    int OosStart,
    int OosEndExclusive,
    int OosCount);

public static class ResearchTemporalValidationV1
{
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<ResearchTemporalPoint>? points,
        int returnCount,
        ResearchTemporalIsolationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var reasons=new List<string>();
        if(policy.TrainingFraction is <=0 or >=1||policy.MinimumOutOfSampleObservations<1||
           policy.OosPurgeObservations<0||policy.OosEmbargoObservations<0)
        {
            reasons.Add("validation.policy-invalid");
            return reasons;
        }

        var bounds=Bounds(returnCount,policy);
        if(bounds.OosCount<policy.MinimumOutOfSampleObservations)
            reasons.Add("research.temporal-isolation-insufficient");

        if(points is null||points.Any(point=>
               point.DataAvailableAtUtc is null||
               point.SignalGeneratedAtUtc is null||
               point.DataAvailableAtUtc>point.SignalGeneratedAtUtc||
               point.SignalGeneratedAtUtc>point.ObservationAtUtc))
            reasons.Add("research.look-ahead-leakage");

        return reasons;
    }

    public static ResearchTemporalBounds Bounds(int returnCount,ResearchTemporalIsolationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if(returnCount<=1||policy.TrainingFraction is <=0 or >=1||
           policy.OosPurgeObservations<0||policy.OosEmbargoObservations<0)
            return new(0,0,0,0);

        var split=Math.Clamp((int)Math.Floor(returnCount*policy.TrainingFraction),1,returnCount-1);
        var start=Math.Min(returnCount,split+policy.OosPurgeObservations);
        var end=Math.Max(start,returnCount-policy.OosEmbargoObservations);
        return new(split,start,end,end-start);
    }
}

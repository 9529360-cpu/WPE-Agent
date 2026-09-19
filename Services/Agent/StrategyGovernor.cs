using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed class StrategyGovernor
{
    public const int MinimumShadowObservations = 24;
    public const int MinimumRegimeCalibrationObservations = 12;
    public const int MinimumExecutionFeedbackTrades = 8;
    public const int MaximumUnqualifiedShadowObservations = 192;
    public static readonly TimeSpan MinimumShadowEvaluationTime = TimeSpan.FromHours(6);
    public const int MinimumValidationTrades = 30;
    public const double MinimumQualityScore = .62;
    public const double MaximumPromotedDrawdown = .20;
    public const double MaximumDemotionDrawdown = .30;
    public const int RequiredEvaluatedRegimes = 4;
    public const int MinimumPassingRegimes = 3;
    public const double MinimumWorstRegimeReturn = -.12;
    public const double MaximumTrainTestExpectancyGap = .003;

    public bool CanPromote(StrategyProfile profile, StrategyValidation validation)
        => validation.Passed
           && validation.Trades >= MinimumValidationTrades
           && validation.QualityScore >= MinimumQualityScore
           && validation.MaxDrawdown <= MaximumPromotedDrawdown
           && validation.EvaluatedRegimes == RequiredEvaluatedRegimes
           && validation.PassingRegimes >= MinimumPassingRegimes
           && validation.WorstRegimeReturn >= MinimumWorstRegimeReturn
           && validation.TrainTestExpectancyGap <= MaximumTrainTestExpectancyGap;

    public bool CanActivateFromShadow(StrategyProfile profile)
        => profile.Lifecycle == StrategyLifecycle.Shadow
           && profile.ShadowObservations >= MinimumShadowObservations
           && profile.QualityScore >= MinimumQualityScore
           && profile.Expectancy > 0
           && profile.MaxDrawdown <= MaximumPromotedDrawdown
           && profile.FailureStreak == 0;

    public bool ShouldDemote(StrategyProfile profile)
        => profile.Lifecycle == StrategyLifecycle.Active
           && profile.ShadowObservations >= MinimumShadowObservations
           && (profile.FailureStreak >= 3 || profile.MaxDrawdown > MaximumDemotionDrawdown || profile.Expectancy < 0);

    public bool ShouldRetireShadow(StrategyProfile profile,DateTime nowUtc,int? rawObservations=null)
        =>profile.Lifecycle==StrategyLifecycle.Shadow
          &&(profile.ShadowObservations>=MaximumUnqualifiedShadowObservations||rawObservations>=MaximumUnqualifiedShadowObservations)
          &&profile.StateChangedAtUtc is not null
          &&nowUtc.ToUniversalTime()-profile.StateChangedAtUtc.Value.ToUniversalTime()>=MinimumShadowEvaluationTime
          &&!CanActivateFromShadow(profile);

    public StrategyLifecycle NextLifecycle(StrategyProfile profile, StrategyValidation? validation = null)
    {
        if (profile.Lifecycle == StrategyLifecycle.Active && ShouldDemote(profile)) return StrategyLifecycle.Degraded;
        if (profile.Lifecycle == StrategyLifecycle.Shadow && CanActivateFromShadow(profile)) return StrategyLifecycle.Active;
        if (profile.Lifecycle == StrategyLifecycle.Draft && validation is not null) return CanPromote(profile, validation) ? StrategyLifecycle.Shadow : StrategyLifecycle.Retired;
        return profile.Lifecycle;
    }

    public StrategyProfile SelectActive(IEnumerable<StrategyProfile> profiles, string symbol)
        => profiles.Where(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && x.Lifecycle == StrategyLifecycle.Active)
            .OrderByDescending(x => x.QualityScore).ThenByDescending(x => x.Expectancy).FirstOrDefault()
           ?? throw new InvalidOperationException($"No approved local strategy is available for {symbol}.");
}

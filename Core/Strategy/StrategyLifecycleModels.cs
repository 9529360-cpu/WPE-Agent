namespace 币安量化机器人.Core.Strategy;

public enum StrategyFamily { TrendBreakout, MeanReversion, NewsMomentum }
public enum StrategyLifecycle { Draft, Backtested, Shadow, Active, Degraded, Retired }

public sealed record LocalStrategyParameters(
    int FastPeriod,
    int SlowPeriod,
    double BreakoutBuffer,
    double MeanReversionZ,
    double NewsSentimentThreshold)
{
    public static LocalStrategyParameters For(StrategyFamily family, int variant) => family switch
    {
        StrategyFamily.TrendBreakout => new(16 + variant * 4, 64 + variant * 16, .0005 + variant * .0003, 0, 0),
        StrategyFamily.MeanReversion => new(12 + variant * 4, 48 + variant * 8, 0, 1.4 + variant * .2, 0),
        _ => new(12, 48, .0005, 0, .15 + variant * .05)
    };
}

public sealed record StrategySignal(string StrategyId, string Symbol, int Direction, double Confidence, string Reason);

public sealed class StrategyProfile
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public StrategyFamily Family { get; init; }
    public LocalStrategyParameters Parameters { get; init; } = LocalStrategyParameters.For(StrategyFamily.TrendBreakout, 0);
    public StrategyLifecycle Lifecycle { get; set; } = StrategyLifecycle.Draft;
    public bool BuiltIn { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? StateChangedAtUtc { get; set; }
    public double QualityScore { get; set; }
    public double Expectancy { get; set; }
    public double MaxDrawdown { get; set; }
    public double Sharpe { get; set; }
    public int ValidationTrades { get; set; }
    public int ShadowObservations { get; set; }
    public int FailureStreak { get; set; }
    public string LastReason { get; set; } = string.Empty;
}

public sealed record StrategyValidation(
    string StrategyId,
    int SampleSize,
    int Trades,
    double WinRate,
    double ProfitFactor,
    double Expectancy,
    double MaxDrawdown,
    double Sharpe,
    double OutOfSampleReturn,
    double WalkForwardScore,
    double MonteCarloLossProbability,
    double QualityScore,
    bool Passed,
    string Summary);

public sealed record StrategyResearchSnapshot(
    string Status,
    int Candidates,
    int ShadowCandidates,
    int ActiveCandidates,
    string ActiveStrategy,
    DateTime? LastRunAtUtc,
    string LastMessage);

public sealed record NewsFeature(string Asset, double Sentiment, double Confidence, int CorroboratingSources, string EventType, DateTime PublishedAtUtc);
public sealed record StrategyObservationPerformance(int Observations, double Expectancy, double MaxDrawdown, double QualityScore, int FailureStreak, string Summary);

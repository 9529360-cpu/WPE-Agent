namespace 币安量化机器人.Core.Strategy;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public enum StrategyFamily { TrendBreakout, MeanReversion, NewsMomentum }
public enum StrategyLifecycle { Draft, Backtested, Shadow, Active, Degraded, Retired }

public sealed record LocalStrategyParameters(
    int FastPeriod,
    int SlowPeriod,
    double BreakoutBuffer,
    double MeanReversionZ,
    double NewsSentimentThreshold,
    double MeanReversionExitZ = .5,
    double MeanReversionStopZ = 3.0,
    double AdxCeiling = 22,
    double AtrStopMultiple = 2.5,
    double VolumeMultiplier = .9,
    int MaximumHoldingBars = 24)
{
    public static LocalStrategyParameters For(StrategyFamily family, int variant) => family switch
    {
        StrategyFamily.TrendBreakout => new(16 + variant * 4, 64 + variant * 16, .0005 + variant * .0003, 0, 0),
        StrategyFamily.MeanReversion => new(10 + variant * 4, 48 + variant * 16, 0, 1.2 + variant * .3, 0,
            .35 + variant * .05, 2.6 + variant * .2, 18 + variant * 2, 1.75 + variant * .25, .8 + variant * .08, 12 + variant * 4),
        _ => new(12, 48, .0005, 0, .15 + variant * .05)
    };

    public static LocalStrategyParameters Derive(StrategyFamily family,LocalStrategyParameters parent,int generation)
    {
        var direction=generation%2==0?-1:1;
        var step=(generation+1)/2;
        return family switch
        {
            StrategyFamily.TrendBreakout=>new(Math.Clamp(parent.FastPeriod+direction*step*2,8,48),Math.Clamp(parent.SlowPeriod+direction*step*8,32,160),Math.Clamp(parent.BreakoutBuffer+direction*step*.0001,.0002,.003),0,0),
            StrategyFamily.MeanReversion=>new(Math.Clamp(parent.FastPeriod+direction*step*2,8,40),Math.Clamp(parent.SlowPeriod+direction*step*8,40,200),0,Math.Clamp(parent.MeanReversionZ+direction*step*.1,.8,3.5),0,
                Math.Clamp(parent.MeanReversionExitZ+direction*step*.05,.2,1.2),Math.Clamp(parent.MeanReversionStopZ+direction*step*.1,2,5),Math.Clamp(parent.AdxCeiling+direction*step,15,32),Math.Clamp(parent.AtrStopMultiple+direction*step*.25,1.5,5),Math.Clamp(parent.VolumeMultiplier+direction*step*.05,.5,2),Math.Clamp(parent.MaximumHoldingBars+direction*step*2,6,96)),
            _=>new(12,48,.0005,0,Math.Clamp(parent.NewsSentimentThreshold+direction*step*.025,.05,.5))
        };
    }

    public static string Hash(LocalStrategyParameters value)
        =>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    public static string LegacyHash(LocalStrategyParameters value)
        =>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            value.FastPeriod,value.SlowPeriod,value.BreakoutBuffer,value.MeanReversionZ,value.NewsSentimentThreshold
        }))));

    public static bool IsValid(StrategyFamily family,LocalStrategyParameters value)=>family switch
    {
        StrategyFamily.TrendBreakout=>value.FastPeriod is>=8 and<=48&&value.SlowPeriod is>=32 and<=160&&value.FastPeriod<value.SlowPeriod&&value.BreakoutBuffer is>=.0002 and<=.003&&value.MeanReversionZ==0&&value.NewsSentimentThreshold==0,
        StrategyFamily.MeanReversion=>value.FastPeriod is>=8 and<=40&&value.SlowPeriod is>=40 and<=200&&value.FastPeriod<value.SlowPeriod&&value.BreakoutBuffer==0&&value.MeanReversionZ is>=.8 and<=3.5&&value.NewsSentimentThreshold==0&&value.MeanReversionExitZ is>=.2 and<=1.2&&value.MeanReversionExitZ<value.MeanReversionZ&&value.MeanReversionStopZ is>=2 and<=5&&value.MeanReversionStopZ>value.MeanReversionZ&&value.AdxCeiling is>=15 and<=32&&value.AtrStopMultiple is>=1.5 and<=5&&value.VolumeMultiplier is>=.5 and<=2&&value.MaximumHoldingBars is>=6 and<=96,
        _=>value.FastPeriod==12&&value.SlowPeriod==48&&value.BreakoutBuffer==.0005&&value.MeanReversionZ==0&&value.NewsSentimentThreshold is>=.05 and<=.5
    };
}

public sealed record StrategySignal(string StrategyId, string Symbol, int Direction, double Confidence, string Reason);

public static class StrategyLineage
{
    public static string Hash(string symbol,StrategyFamily family,string? parentId,string? parentVersion,int generation,string parametersHash)
        =>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{symbol.ToUpperInvariant()}|{family}|{parentId??string.Empty}|{parentVersion??string.Empty}|{generation}|{parametersHash}")));
}

public sealed class StrategyProfile
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public StrategyFamily Family { get; init; }
    public LocalStrategyParameters Parameters { get; init; } = LocalStrategyParameters.For(StrategyFamily.TrendBreakout, 0);
    public StrategyLifecycle Lifecycle { get; set; } = StrategyLifecycle.Draft;
    public bool BuiltIn { get; init; }
    public string? ParentStrategyId { get; init; }
    public string? ParentStrategyVersion { get; init; }
    public int Generation { get; init; }
    public string ParametersHash { get; init; } = string.Empty;
    public string LineageHash { get; init; } = string.Empty;
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
    string Summary,
    double WorstRegimeReturn = 0,
    double TrainTestExpectancyGap = 0,
    int PassingRegimes = 0,
    int EvaluatedRegimes = 0);

public sealed record StrategyResearchSnapshot(
    string Status,
    int Candidates,
    int ShadowCandidates,
    int ActiveCandidates,
    string ActiveStrategy,
    DateTime? LastRunAtUtc,
    string LastMessage);

public sealed record NewsFeature(string Asset, double Sentiment, double Confidence, int CorroboratingSources, string EventType, DateTime PublishedAtUtc);
public sealed record StrategyRegimePerformance(
    string Regime,
    int Observations,
    double Expectancy,
    double HitRate,
    double MeanConfidence,
    double CalibrationScore,
    double QualityScore);

public sealed record StrategyObservationPerformance(
    int Observations,
    double Expectancy,
    double MaxDrawdown,
    double QualityScore,
    int FailureStreak,
    string Summary,
    double CalibrationScore = .5,
    IReadOnlyList<StrategyRegimePerformance>? Regimes = null);

public enum MeanReversionRegime { Unknown, Range, Trend, Volatile }
public sealed record MeanReversionMarketState(MeanReversionRegime Regime,double ZScore,double Rsi,double Adx,double AtrRatio,double DistanceAtr,double VolumeRatio);

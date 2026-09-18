using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed record BacktestValidationFactV1(
    string Schema,
    string Symbol,
    string StrategyId,
    string StrategyVersion,
    DateTimeOffset ValidatedAtUtc,
    int SampleSize,
    int Trades,
    int OutOfSampleTrades,
    int CoverageDays,
    double WinRate,
    double ProfitFactor,
    double Expectancy,
    double MaxDrawdown,
    double Sharpe,
    double OutOfSampleReturn,
    double WalkForwardScore,
    double MonteCarloLossProbability,
    double QualityScore,
    StrategyParameterSearchEvidence ParameterSearch,
    int ForwardObservations,
    bool ForwardQualified,
    bool Approved,
    bool Promoted,
    string CanonicalSha256,
    byte[] CanonicalBytes);

public static class BacktestValidationCanonicalizerV1
{
    public const string Schema = "wpe.backtest-validation/1.2";

    public static BacktestValidationFactV1 Create(
        string symbol, string strategyId, string strategyVersion, DateTimeOffset validatedAtUtc,
        int sampleSize, int trades, int outOfSampleTrades, int coverageDays,
        double winRate, double profitFactor, double expectancy, double maxDrawdown,
        double sharpe, double outOfSampleReturn, double walkForwardScore,
        double monteCarloLossProbability, double qualityScore,
        StrategyParameterSearchEvidence parameterSearch,
        int forwardObservations, bool forwardQualified, bool approved, bool promoted)
    {
        ArgumentNullException.ThrowIfNull(parameterSearch);
        var utc = validatedAtUtc.ToUniversalTime();
        var values = new[]
        {
            Schema, symbol, strategyId, strategyVersion, utc.ToString("O", CultureInfo.InvariantCulture),
            sampleSize.ToString(CultureInfo.InvariantCulture), trades.ToString(CultureInfo.InvariantCulture),
            outOfSampleTrades.ToString(CultureInfo.InvariantCulture), coverageDays.ToString(CultureInfo.InvariantCulture),
            Number(winRate), Number(profitFactor), Number(expectancy), Number(maxDrawdown), Number(sharpe),
            Number(outOfSampleReturn), Number(walkForwardScore), Number(monteCarloLossProbability), Number(qualityScore),
            parameterSearch.TrialCount.ToString(CultureInfo.InvariantCulture),
            parameterSearch.SelectedTrialIndex.ToString(CultureInfo.InvariantCulture),
            parameterSearch.SearchSpaceHash,
            parameterSearch.SelectionDatasetHash,
            parameterSearch.HistoricalOosDatasetHash,
            parameterSearch.TestMethod,
            parameterSearch.CorrectionMethod,
            Number(parameterSearch.NominalAlpha),
            Number(parameterSearch.SelectedTrialPValue),
            Number(parameterSearch.CorrectedSignificanceThreshold),
            parameterSearch.SelectionRule,
            forwardObservations.ToString(CultureInfo.InvariantCulture),
            forwardQualified ? "true" : "false",
            approved ? "true" : "false",
            promoted ? "true" : "false"
        };
        var bytes = Encoding.UTF8.GetBytes(string.Join('|', values));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Schema, symbol, strategyId, strategyVersion, utc, sampleSize, trades, outOfSampleTrades, coverageDays,
            winRate, profitFactor, expectancy, maxDrawdown, sharpe, outOfSampleReturn, walkForwardScore,
            monteCarloLossProbability, qualityScore, parameterSearch, forwardObservations, forwardQualified,
            approved, promoted, hash, bytes);
    }

    public static bool IsCanonical(BacktestValidationFactV1 value, DateTimeOffset evaluationTimeUtc)
    {
        if (value.Schema != Schema || value.CanonicalBytes is null || evaluationTimeUtc.Offset != TimeSpan.Zero ||
            value.ValidatedAtUtc.Offset != TimeSpan.Zero || value.ValidatedAtUtc > evaluationTimeUtc ||
            evaluationTimeUtc - value.ValidatedAtUtc > TimeSpan.FromHours(24)) return false;
        if (!CanonicalSymbol(value.Symbol) || !Token(value.StrategyId,160) || !Token(value.StrategyVersion,128) || !Sha(value.CanonicalSha256)) return false;
        if (value.SampleSize < 1 || value.Trades < 0 || value.Trades > value.SampleSize ||
            value.OutOfSampleTrades < 0 || value.OutOfSampleTrades > value.Trades || value.CoverageDays < 0) return false;
        if (!Finite(value.WinRate, 0, 1) || !Finite(value.ProfitFactor, 0, double.MaxValue) ||
            !double.IsFinite(value.Expectancy) || !Finite(value.MaxDrawdown, 0, 1) || !double.IsFinite(value.Sharpe) ||
            !double.IsFinite(value.OutOfSampleReturn) || !Finite(value.WalkForwardScore, 0, 1) ||
            !Finite(value.MonteCarloLossProbability, 0, 1) || !Finite(value.QualityScore, 0, 1)) return false;
        if (!StrategyParameterSearchEvaluatorV1.IsCanonical(value.ParameterSearch) ||
            !Token(value.ParameterSearch.TestMethod,128) ||
            !Token(value.ParameterSearch.CorrectionMethod,64) ||
            !SafeText(value.ParameterSearch.SelectionRule,256)) return false;
        if (value.ForwardObservations < 0 ||
            value.ForwardQualified && value.ForwardObservations < StrategyGovernor.MinimumShadowObservations) return false;
        if (value.Approved && (!value.ForwardQualified || !StrategyParameterSearchEvaluatorV1.IsQualified(value.ParameterSearch))) return false;
        if (value.Promoted && !value.Approved) return false;
        var expected = Create(value.Symbol, value.StrategyId, value.StrategyVersion, value.ValidatedAtUtc, value.SampleSize, value.Trades,
            value.OutOfSampleTrades, value.CoverageDays, value.WinRate, value.ProfitFactor, value.Expectancy,
            value.MaxDrawdown, value.Sharpe, value.OutOfSampleReturn, value.WalkForwardScore,
            value.MonteCarloLossProbability, value.QualityScore, value.ParameterSearch,
            value.ForwardObservations, value.ForwardQualified, value.Approved, value.Promoted);
        return string.Equals(expected.CanonicalSha256, value.CanonicalSha256, StringComparison.Ordinal) &&
               CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes, value.CanonicalBytes);
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Finite(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    private static bool CanonicalSymbol(string value) => value.Length is >= 5 and <= 30 && value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');
    private static bool Token(string value,int maximum) => value.Length is >= 1 && value.Length <= maximum && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool SafeText(string value,int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Contains('|') && !value.Contains('\r') && !value.Contains('\n');
    private static bool Sha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

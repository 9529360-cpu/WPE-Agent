using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 币安量化机器人.Services.Agent;

public sealed record BacktestValidationFactV1(
    string Schema,
    string Symbol,
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
    bool Approved,
    bool Promoted,
    string CanonicalSha256,
    byte[] CanonicalBytes);

public static class BacktestValidationCanonicalizerV1
{
    public const string Schema = "wpe.backtest-validation/1.0";

    public static BacktestValidationFactV1 Create(
        string symbol, string strategyVersion, DateTimeOffset validatedAtUtc,
        int sampleSize, int trades, int outOfSampleTrades, int coverageDays,
        double winRate, double profitFactor, double expectancy, double maxDrawdown,
        double sharpe, double outOfSampleReturn, double walkForwardScore,
        double monteCarloLossProbability, double qualityScore, bool approved, bool promoted)
    {
        var utc = validatedAtUtc.ToUniversalTime();
        var values = new[]
        {
            Schema, symbol, strategyVersion, utc.ToString("O", CultureInfo.InvariantCulture),
            sampleSize.ToString(CultureInfo.InvariantCulture), trades.ToString(CultureInfo.InvariantCulture),
            outOfSampleTrades.ToString(CultureInfo.InvariantCulture), coverageDays.ToString(CultureInfo.InvariantCulture),
            Number(winRate), Number(profitFactor), Number(expectancy), Number(maxDrawdown), Number(sharpe),
            Number(outOfSampleReturn), Number(walkForwardScore), Number(monteCarloLossProbability), Number(qualityScore),
            approved ? "true" : "false", promoted ? "true" : "false"
        };
        var bytes = Encoding.UTF8.GetBytes(string.Join('|', values));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Schema, symbol, strategyVersion, utc, sampleSize, trades, outOfSampleTrades, coverageDays,
            winRate, profitFactor, expectancy, maxDrawdown, sharpe, outOfSampleReturn, walkForwardScore,
            monteCarloLossProbability, qualityScore, approved, promoted, hash, bytes);
    }

    public static bool IsCanonical(BacktestValidationFactV1 value, DateTimeOffset evaluationTimeUtc)
    {
        if (value.Schema != Schema || value.CanonicalBytes is null || evaluationTimeUtc.Offset != TimeSpan.Zero ||
            value.ValidatedAtUtc.Offset != TimeSpan.Zero || value.ValidatedAtUtc > evaluationTimeUtc ||
            evaluationTimeUtc - value.ValidatedAtUtc > TimeSpan.FromHours(24)) return false;
        if (!CanonicalSymbol(value.Symbol) || !Token(value.StrategyVersion) || !Sha(value.CanonicalSha256)) return false;
        if (value.SampleSize < 1 || value.Trades < 0 || value.Trades > value.SampleSize ||
            value.OutOfSampleTrades < 0 || value.OutOfSampleTrades > value.Trades || value.CoverageDays < 0) return false;
        if (!Finite(value.WinRate, 0, 1) || !Finite(value.ProfitFactor, 0, double.MaxValue) ||
            !double.IsFinite(value.Expectancy) || !Finite(value.MaxDrawdown, 0, 1) || !double.IsFinite(value.Sharpe) ||
            !double.IsFinite(value.OutOfSampleReturn) || !Finite(value.WalkForwardScore, 0, 1) ||
            !Finite(value.MonteCarloLossProbability, 0, 1) || !Finite(value.QualityScore, 0, 1)) return false;
        if (value.Promoted && !value.Approved) return false;
        var expected = Create(value.Symbol, value.StrategyVersion, value.ValidatedAtUtc, value.SampleSize, value.Trades,
            value.OutOfSampleTrades, value.CoverageDays, value.WinRate, value.ProfitFactor, value.Expectancy,
            value.MaxDrawdown, value.Sharpe, value.OutOfSampleReturn, value.WalkForwardScore,
            value.MonteCarloLossProbability, value.QualityScore, value.Approved, value.Promoted);
        return string.Equals(expected.CanonicalSha256, value.CanonicalSha256, StringComparison.Ordinal) &&
               CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes, value.CanonicalBytes);
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Finite(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    private static bool CanonicalSymbol(string value) => value.Length is >= 5 and <= 30 && value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');
    private static bool Token(string value) => value.Length is >= 1 and <= 128 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool Sha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

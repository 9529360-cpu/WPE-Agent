using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public static class MeanReversionRegimeAnalyzer
{
    private const int AdxPeriod = 14;

    public static MeanReversionMarketState Analyze(IReadOnlyList<CandleEvidence> candles, LocalStrategyParameters parameters)
    {
        if (candles.Count < Math.Max(parameters.SlowPeriod, AdxPeriod * 3))
            return new(MeanReversionRegime.Unknown, 0, 50, 0, 0, 0, 0);

        var window = candles.TakeLast(parameters.SlowPeriod).ToArray();
        var mean = window.Average(x => (double)x.Close);
        var variance = window.Select(x => Math.Pow((double)x.Close - mean, 2)).Average();
        var deviation = Math.Sqrt(variance);
        var z = deviation <= 0 ? 0 : ((double)window[^1].Close - mean) / deviation;
        var rsi = Rsi(candles, parameters.FastPeriod);
        var atr = Atr(candles, AdxPeriod);
        var adx = Adx(candles, AdxPeriod);
        var close = Math.Max(.00000001, (double)candles[^1].Close);
        var atrRatio = atr / close;
        var distanceAtr = atr <= 0 ? double.PositiveInfinity : Math.Abs(close - mean) / atr;
        var averageVolume = window.Average(x => (double)x.Volume);
        var volumeRatio = averageVolume <= 0 ? 0 : (double)window[^1].Volume / averageVolume;
        var regime = atrRatio > .04
            ? MeanReversionRegime.Volatile
            : adx > parameters.AdxCeiling ? MeanReversionRegime.Trend : MeanReversionRegime.Range;
        return new(regime, z, rsi, adx, atrRatio, distanceAtr, volumeRatio);
    }

    private static double Rsi(IReadOnlyList<CandleEvidence> candles, int period)
    {
        var values = candles.TakeLast(period + 1).ToArray();
        if (values.Length < period + 1) return 50;
        double gains = 0, losses = 0;
        for (var i = 1; i < values.Length; i++)
        {
            var change = (double)(values[i].Close - values[i - 1].Close);
            if (change > 0) gains += change;
            else losses -= change;
        }
        if (losses == 0) return gains == 0 ? 50 : 100;
        var rs = (gains / period) / (losses / period);
        return 100 - (100 / (1 + rs));
    }

    private static double Atr(IReadOnlyList<CandleEvidence> candles, int period)
    {
        var values = candles.TakeLast(period + 1).ToArray();
        if (values.Length < 2) return 0;
        var ranges = new List<double>();
        for (var i = 1; i < values.Length; i++)
            ranges.Add(Math.Max((double)(values[i].High - values[i].Low), Math.Max(Math.Abs((double)(values[i].High - values[i - 1].Close)), Math.Abs((double)(values[i].Low - values[i - 1].Close)))));
        return ranges.Count == 0 ? 0 : ranges.Average();
    }

    private static double Adx(IReadOnlyList<CandleEvidence> candles, int period)
    {
        var values = candles.TakeLast(period * 3).ToArray();
        if (values.Length < period + 1) return 0;
        var dx = new List<double>();
        for (var end = period; end < values.Length; end++)
        {
            double tr = 0, plus = 0, minus = 0;
            for (var i = end - period + 1; i <= end; i++)
            {
                var up = (double)(values[i].High - values[i - 1].High);
                var down = (double)(values[i - 1].Low - values[i].Low);
                plus += up > down && up > 0 ? up : 0;
                minus += down > up && down > 0 ? down : 0;
                tr += Math.Max((double)(values[i].High - values[i].Low), Math.Max(Math.Abs((double)(values[i].High - values[i - 1].Close)), Math.Abs((double)(values[i].Low - values[i - 1].Close))));
            }
            if (tr <= 0)
            {
                dx.Add(0);
                continue;
            }
            var plusDi = 100 * plus / tr;
            var minusDi = 100 * minus / tr;
            var denominator = plusDi + minusDi;
            dx.Add(denominator <= 0 ? 0 : 100 * Math.Abs(plusDi - minusDi) / denominator);
        }
        return dx.TakeLast(period).DefaultIfEmpty(0).Average();
    }
}

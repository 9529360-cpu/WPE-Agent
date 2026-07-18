using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Strategies;

public class MultiTimeframeAnalyzer : IMultiTimeframeAnalyzer
{
    public ValueTask<CompositeSignal> AnalyzeAsync(string symbol, IReadOnlyDictionary<TimeSpan, TimeframeSeries> series, CancellationToken cancellationToken = default)
    {
        var trendScores = new Dictionary<TimeSpan, double>();
        var momentumScores = new Dictionary<TimeSpan, double>();
        var meanReversionScores = new Dictionary<TimeSpan, double>();

        foreach (var (timeframe, timeframeSeries) in series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeframeSeries.Observations.Count < 2)
            {
                continue;
            }

            var recent = timeframeSeries.Observations[^1];
            var previous = timeframeSeries.Observations[^2];

            var trend = CalculateTrend(timeframeSeries.Observations);
            var momentum = recent.Close - previous.Close;
            var deviation = recent.Close - timeframeSeries.Observations.Average(o => o.Close);

            trendScores[timeframe] = trend;
            momentumScores[timeframe] = momentum;
            meanReversionScores[timeframe] = deviation;
        }

        double WeightedAverage(Dictionary<TimeSpan, double> scores)
        {
            if (scores.Count == 0)
            {
                return 0d;
            }

            var totalWeight = scores.Keys.Sum(tf => 1d / tf.TotalMinutes);
            if (totalWeight == 0)
            {
                return 0d;
            }

            return scores.Sum(pair => pair.Value * (1d / pair.Key.TotalMinutes)) / totalWeight;
        }

        var trendScore = WeightedAverage(trendScores);
        var momentumScore = WeightedAverage(momentumScores);
        var meanReversionScore = WeightedAverage(meanReversionScores);

        var confidence = trendScores.Keys.ToDictionary(tf => tf, tf => Math.Min(1d, Math.Abs(trendScores[tf]) + Math.Abs(momentumScores.GetValueOrDefault(tf))));

        return ValueTask.FromResult(new CompositeSignal(symbol, trendScore, momentumScore, meanReversionScore, confidence));
    }

    private static double CalculateTrend(IReadOnlyList<MarketObservation> observations)
    {
        if (observations.Count < 2)
        {
            return 0d;
        }

        double numerator = 0d;
        double denominator = 0d;
        var avg = observations.Average(o => o.Close);
        for (var i = 0; i < observations.Count; i++)
        {
            var price = observations[i].Close;
            numerator += (i - observations.Count / 2d) * (price - avg);
            denominator += Math.Pow(i - observations.Count / 2d, 2);
        }

        return denominator == 0 ? 0 : numerator / denominator;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Infrastructure.Data;

namespace 币安量化机器人.Application.Services;

public class PipelineMarketDataService : IMarketDataService
{
    private readonly DataPipelineOrchestrator _orchestrator;
    private readonly IFeatureStore _featureStore;

    public PipelineMarketDataService(DataPipelineOrchestrator orchestrator, IFeatureStore featureStore)
    {
        _orchestrator = orchestrator;
        _featureStore = featureStore;
    }

    public async IAsyncEnumerable<MarketObservation> StreamAsync(string symbol, IEnumerable<TimeSpan> timeframes, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requested = timeframes.ToHashSet();
        var start = DateTime.UtcNow.AddHours(-2);
        var end = DateTime.UtcNow.AddHours(2);
        var stream = await _orchestrator.StartAsync(symbol, start, end, cancellationToken);

        var fiveBuffer = new List<RawDataFrame>();
        var hourBuffer = new List<RawDataFrame>();

        await foreach (var frame in stream.WithCancellation(cancellationToken))
        {
            if (frame.Source is "quality" or "error")
            {
                continue;
            }

            if (requested.Contains(TimeSpan.FromMinutes(1)))
            {
                yield return await ToObservationAsync(symbol, TimeSpan.FromMinutes(1), frame, cancellationToken);
            }

            if (requested.Contains(TimeSpan.FromMinutes(5)))
            {
                fiveBuffer.Add(frame);
                fiveBuffer.RemoveAll(f => frame.Timestamp - f.Timestamp > TimeSpan.FromMinutes(5));
                if (fiveBuffer.Count >= 5)
                {
                    yield return await AggregateAsync(symbol, TimeSpan.FromMinutes(5), fiveBuffer, cancellationToken);
                    fiveBuffer.Clear();
                }
            }

            if (requested.Contains(TimeSpan.FromHours(1)))
            {
                hourBuffer.Add(frame);
                hourBuffer.RemoveAll(f => frame.Timestamp - f.Timestamp > TimeSpan.FromHours(1));
                if (hourBuffer.Count >= 60)
                {
                    yield return await AggregateAsync(symbol, TimeSpan.FromHours(1), hourBuffer, cancellationToken);
                    hourBuffer.Clear();
                }
            }
        }
    }

    public async ValueTask<TimeframeSeries> GetSeriesAsync(string symbol, TimeSpan timeframe, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var stream = await _orchestrator.StartAsync(symbol, start, end, cancellationToken);
        var observations = new List<MarketObservation>();
        var buffer = new List<RawDataFrame>();
        await foreach (var frame in stream.WithCancellation(cancellationToken))
        {
            if (frame.Source is "quality" or "error")
            {
                continue;
            }

            buffer.Add(frame);
            var bucketSize = timeframe == TimeSpan.FromMinutes(1) ? 1 : timeframe == TimeSpan.FromMinutes(5) ? 5 : 60;
            if (buffer.Count >= bucketSize)
            {
                var obs = timeframe == TimeSpan.FromMinutes(1)
                    ? await ToObservationAsync(symbol, timeframe, buffer[^1], cancellationToken)
                    : await AggregateAsync(symbol, timeframe, buffer, cancellationToken);
                observations.Add(obs);
                buffer.Clear();
            }
        }

        return new TimeframeSeries(timeframe, observations);
    }

    private async ValueTask<MarketObservation> ToObservationAsync(string symbol, TimeSpan timeframe, RawDataFrame frame, CancellationToken cancellationToken)
    {
        var indicators = new Dictionary<string, double>
        {
            ["open"] = Convert.ToDouble(frame.Payload["open"]),
            ["high"] = Convert.ToDouble(frame.Payload["high"]),
            ["low"] = Convert.ToDouble(frame.Payload["low"]),
            ["close"] = Convert.ToDouble(frame.Payload["close"]),
            ["volume"] = Convert.ToDouble(frame.Payload["volume"])
        };

        var features = await _featureStore.GetLatestAsync(symbol, cancellationToken);
        return new MarketObservation(symbol, timeframe, frame.Timestamp,
            indicators["open"], indicators["high"], indicators["low"], indicators["close"], indicators["volume"], indicators, features);
    }

    private async ValueTask<MarketObservation> AggregateAsync(string symbol, TimeSpan timeframe, IReadOnlyList<RawDataFrame> frames, CancellationToken cancellationToken)
    {
        var ordered = frames.OrderBy(f => f.Timestamp).ToArray();
        var open = Convert.ToDouble(ordered.First().Payload["open"]);
        var close = Convert.ToDouble(ordered.Last().Payload["close"]);
        var high = ordered.Max(f => Convert.ToDouble(f.Payload["high"]));
        var low = ordered.Min(f => Convert.ToDouble(f.Payload["low"]));
        var volume = ordered.Sum(f => Convert.ToDouble(f.Payload["volume"]));
        var indicators = new Dictionary<string, double>
        {
            ["open"] = open,
            ["high"] = high,
            ["low"] = low,
            ["close"] = close,
            ["volume"] = volume
        };

        var features = await _featureStore.GetLatestAsync(symbol, cancellationToken);
        return new MarketObservation(symbol, timeframe, ordered.Last().Timestamp, open, high, low, close, volume, indicators, features);
    }
}

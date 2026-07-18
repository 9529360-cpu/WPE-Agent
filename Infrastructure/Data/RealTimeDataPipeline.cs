using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Infrastructure.Data;

public class RealTimeDataPipeline
{
    private readonly IEnumerable<IDataSource> _sources;
    private readonly IEnumerable<IDataQualityRule> _qualityRules;
    private readonly IEnumerable<IFeatureEngineer> _featureEngineers;
    private readonly IFeatureStore _featureStore;
    private readonly Channel<RawDataFrame> _channel = Channel.CreateUnbounded<RawDataFrame>();

    public RealTimeDataPipeline(
        IEnumerable<IDataSource> sources,
        IEnumerable<IDataQualityRule> qualityRules,
        IEnumerable<IFeatureEngineer> featureEngineers,
        IFeatureStore featureStore)
    {
        _sources = sources;
        _qualityRules = qualityRules;
        _featureEngineers = featureEngineers;
        _featureStore = featureStore;
    }

    public ChannelReader<RawDataFrame> Reader => _channel.Reader;

    public async Task StartAsync(DataQuery query, CancellationToken cancellationToken = default)
    {
        foreach (var source in _sources)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in source.ReadAsync(query, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ProcessFrameAsync(frame, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    await _channel.Writer.WriteAsync(new RawDataFrame("error", query.Symbol, DateTime.UtcNow, new Dictionary<string, object>
                    {
                        ["source"] = source.Name,
                        ["error"] = ex.Message
                    }), cancellationToken);
                }
            }, cancellationToken);
        }
    }

    private async Task ProcessFrameAsync(RawDataFrame frame, CancellationToken cancellationToken)
    {
        foreach (var rule in _qualityRules)
        {
            var result = await rule.ValidateAsync(frame, cancellationToken);
            if (!result.Passed)
            {
                await _channel.Writer.WriteAsync(new RawDataFrame("quality", frame.Symbol, frame.Timestamp, new Dictionary<string, object>
                {
                    ["rule"] = rule.Name,
                    ["message"] = result.Message ?? string.Empty
                }), cancellationToken);
                return;
            }
        }

        var features = new Dictionary<string, double>();
        foreach (var engineer in _featureEngineers)
        {
            var engineered = await engineer.TransformAsync(frame, cancellationToken);
            foreach (var pair in engineered)
            {
                features[pair.Key] = pair.Value;
            }
        }

        await _featureStore.SaveAsync(frame.Symbol, features, cancellationToken);
        await _channel.Writer.WriteAsync(frame, cancellationToken);
    }
}

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Abstractions;

namespace 币安量化机器人.Application.Services;

public class InMemoryFeatureStore : IFeatureStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, double>> _features = new();

    public ValueTask<IReadOnlyDictionary<string, double>> GetLatestAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (_features.TryGetValue(symbol, out var features))
        {
            return ValueTask.FromResult(features);
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
    }

    public ValueTask SaveAsync(string symbol, IReadOnlyDictionary<string, double> features, CancellationToken cancellationToken = default)
    {
        _features[symbol] = features;
        return ValueTask.CompletedTask;
    }
}

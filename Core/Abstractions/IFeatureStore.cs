using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Abstractions;

public interface IFeatureStore
{
    ValueTask<IReadOnlyDictionary<string, double>> GetLatestAsync(string symbol, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(string symbol, IReadOnlyDictionary<string, double> features, CancellationToken cancellationToken = default);
}

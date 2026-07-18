using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Data;

public interface IFeatureEngineer
{
    string Name { get; }

    ValueTask<IReadOnlyDictionary<string, double>> TransformAsync(RawDataFrame frame, CancellationToken cancellationToken = default);
}

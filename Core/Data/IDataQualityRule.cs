using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Data;

public interface IDataQualityRule
{
    string Name { get; }

    ValueTask<DataQualityResult> ValidateAsync(RawDataFrame frame, CancellationToken cancellationToken = default);
}

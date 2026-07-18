using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Data;

public interface IDataStreamProcessor
{
    ValueTask ProcessAsync(RawDataFrame frame, CancellationToken cancellationToken = default);
}

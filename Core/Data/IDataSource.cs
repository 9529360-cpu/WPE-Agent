using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Data;

public interface IDataSource
{
    string Name { get; }

    IAsyncEnumerable<RawDataFrame> ReadAsync(DataQuery query, CancellationToken cancellationToken = default);
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Infrastructure.Data;

namespace 币安量化机器人.Application.Services;

public class DataPipelineOrchestrator
{
    private readonly RealTimeDataPipeline _pipeline;

    public DataPipelineOrchestrator(RealTimeDataPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public async Task<IAsyncEnumerable<RawDataFrame>> StartAsync(string symbol, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        await _pipeline.StartAsync(new DataQuery(symbol, start, end), cancellationToken);
        return _pipeline.Reader.ReadAllAsync(cancellationToken);
    }
}

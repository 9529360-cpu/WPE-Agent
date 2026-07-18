using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Abstractions;

public interface IStrategyOptimizer
{
    ValueTask<OptimizationResult> OptimizeAsync(ITradingStrategy strategy, OptimizationRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<OptimizationProgress> StreamProgressAsync(CancellationToken cancellationToken = default);
}

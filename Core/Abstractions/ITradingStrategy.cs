using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Abstractions;

public interface ITradingStrategy
{
    string Name { get; }

    StrategyParameters Parameters { get; }

    void Initialize(IStrategyContext context);

    ValueTask<StrategyDecision> EvaluateAsync(MarketObservation observation, CancellationToken cancellationToken = default);

    IAsyncEnumerable<StrategyDecision> RunAsync(IAsyncEnumerable<MarketObservation> observations, CancellationToken cancellationToken = default);
}

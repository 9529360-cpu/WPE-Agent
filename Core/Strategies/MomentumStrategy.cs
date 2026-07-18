using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Strategies;

public class MomentumStrategy : ITradingStrategy
{
    private readonly IMultiTimeframeAnalyzer _analyzer;
    private IStrategyContext? _context;
    private StrategyParameters _parameters;

    public MomentumStrategy(IMultiTimeframeAnalyzer analyzer, StrategyParameters parameters)
    {
        _analyzer = analyzer;
        _parameters = parameters;
    }

    public string Name => "Momentum";

    public StrategyParameters Parameters => _parameters;

    public void Initialize(IStrategyContext context)
    {
        _context = context;
    }

    public async ValueTask<StrategyDecision> EvaluateAsync(MarketObservation observation, CancellationToken cancellationToken = default)
    {
        if (_context is null)
        {
            throw new InvalidOperationException("Strategy not initialized");
        }

        var momentumWindow = _parameters.Get("momentum_window", 5);
        var momentumSeries = observation.Indicators.Where(kv => kv.Key.StartsWith("momentum")).Select(kv => kv.Value).TakeLast((int)momentumWindow).ToList();
        var momentumScore = momentumSeries.Count == 0 ? 0 : momentumSeries.Average();

        var composite = await _analyzer.AnalyzeAsync(observation.Symbol, new Dictionary<TimeSpan, TimeframeSeries>
        {
            [observation.Timeframe] = new TimeframeSeries(observation.Timeframe, new List<MarketObservation> { observation })
        }, cancellationToken);

        var action = TradeActionType.Hold;
        var reason = "Momentum insufficient";
        var qty = _parameters.Get("base_quantity", 1d);

        if (momentumScore > _parameters.Get("enter_threshold", 0.5))
        {
            action = TradeActionType.EnterLong;
            reason = $"Momentum {momentumScore:F2} above threshold";
        }
        else if (momentumScore < -_parameters.Get("enter_threshold", 0.5))
        {
            action = TradeActionType.EnterShort;
            reason = $"Momentum {momentumScore:F2} below threshold";
        }

        var tradeAction = new TradeAction(action, qty, reason);
        if (!_context.RiskManager.Approve(tradeAction))
        {
            tradeAction = new TradeAction(TradeActionType.Hold, 0, "Risk rejected");
        }

        var decision = new StrategyDecision(tradeAction, Math.Min(1, Math.Abs(momentumScore)), composite, new MachineLearningSignal(observation.Symbol, 0.5, 0.5, "N/A"));
        await _context.PublishSignalAsync(new TradeSignal(observation.Symbol, tradeAction, decision.Confidence, decision.MlSignal), cancellationToken);
        return decision;
    }

    public async IAsyncEnumerable<StrategyDecision> RunAsync(IAsyncEnumerable<MarketObservation> observations, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var observation in observations.WithCancellation(cancellationToken))
        {
            yield return await EvaluateAsync(observation, cancellationToken);
        }
    }
}

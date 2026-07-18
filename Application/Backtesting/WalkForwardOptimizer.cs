using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Application.Backtesting;

public class WalkForwardOptimizer
{
    private readonly IStrategyOptimizer _optimizer;
    private readonly IBacktestEngine _backtestEngine;

    public WalkForwardOptimizer(IStrategyOptimizer optimizer, IBacktestEngine backtestEngine)
    {
        _optimizer = optimizer;
        _backtestEngine = backtestEngine;
    }

    public async IAsyncEnumerable<WalkForwardResult> OptimizeAsync(
        ITradingStrategy strategy,
        string symbol,
        DateTime start,
        DateTime end,
        TimeSpan trainingWindow,
        TimeSpan testingWindow,
        IReadOnlyDictionary<string, IReadOnlyList<double>> parameterSpace,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var trainingStart = start;
        while (trainingStart < end)
        {
            var trainingEnd = trainingStart + trainingWindow;
            var testingEnd = trainingEnd + testingWindow;
            if (trainingEnd >= end)
            {
                yield break;
            }

            var optimizationRequest = new OptimizationRequest(symbol, trainingStart, trainingEnd, parameterSpace, _backtestEngine);
            var optimizationTask = _optimizer.OptimizeAsync(strategy, optimizationRequest, cancellationToken);

            await foreach (var progress in _optimizer.StreamProgressAsync(cancellationToken))
            {
                yield return new WalkForwardResult(trainingStart, trainingEnd, null, progress, null);
            }

            var optimizationResult = await optimizationTask;

            var walkForwardStrategy = CloneStrategy(strategy, optimizationResult.Parameters);
            var testRequest = new BacktestRequest(symbol, trainingEnd, testingEnd, walkForwardStrategy);
            var testResult = await _backtestEngine.RunAsync(testRequest, cancellationToken);

            yield return new WalkForwardResult(trainingStart, trainingEnd, optimizationResult, null, testResult);

            trainingStart = testingEnd;
        }
    }

    private static ITradingStrategy CloneStrategy(ITradingStrategy strategy, StrategyParameters parameters)
    {
        return strategy switch
        {
            Core.Strategies.MeanReversionStrategy meanReversion => new Core.Strategies.MeanReversionStrategy(GetAnalyzerForMeanReversion(meanReversion), GetMlGeneratorForMeanReversion(meanReversion), GetFeatureStoreForMeanReversion(meanReversion), parameters),
            Core.Strategies.MomentumStrategy momentum => new Core.Strategies.MomentumStrategy(GetAnalyzerForMomentum(momentum), parameters),
            _ => throw new NotSupportedException("Unsupported strategy type")
        };

        static IMultiTimeframeAnalyzer GetAnalyzerForMeanReversion(Core.Strategies.MeanReversionStrategy strategy)
            => strategy.GetType().GetField("_analyzer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(strategy) as IMultiTimeframeAnalyzer
               ?? throw new InvalidOperationException("Analyzer unavailable");

        static IMachineLearningSignalGenerator GetMlGeneratorForMeanReversion(Core.Strategies.MeanReversionStrategy strategy)
            => strategy.GetType().GetField("_mlSignalGenerator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(strategy) as IMachineLearningSignalGenerator
               ?? throw new InvalidOperationException("ML generator unavailable");

        static IFeatureStore GetFeatureStoreForMeanReversion(Core.Strategies.MeanReversionStrategy strategy)
            => strategy.GetType().GetField("_featureStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(strategy) as IFeatureStore
               ?? throw new InvalidOperationException("Feature store unavailable");

        static IMultiTimeframeAnalyzer GetAnalyzerForMomentum(Core.Strategies.MomentumStrategy strategy)
            => strategy.GetType().GetField("_analyzer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(strategy) as IMultiTimeframeAnalyzer
               ?? throw new InvalidOperationException("Analyzer unavailable");

        // Momentum strategy does not expose ML generator/feature store in its constructor.
        // Helper functions intentionally omitted to avoid unused-local-function warnings.
    }
}

public record WalkForwardResult(
    DateTime TrainingStart,
    DateTime TrainingEnd,
    OptimizationResult? Optimization,
    OptimizationProgress? Progress,
    BacktestResult? WalkForwardTestResult);

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Abstractions;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Strategies;

public class RandomForestSignalGenerator : IMachineLearningSignalGenerator
{
    private readonly List<DecisionStump> _trees = new();
    private readonly int _treeCount;
    private readonly int _featureSampleSize;
    private readonly Random _random = new();
    private string _modelVersion = "untrained";

    public RandomForestSignalGenerator(int treeCount = 25, int featureSampleSize = 4)
    {
        _treeCount = treeCount;
        _featureSampleSize = featureSampleSize;
    }

    public ValueTask TrainAsync(IEnumerable<ModelFeatureVector> trainingSet, CancellationToken cancellationToken = default)
    {
        var data = trainingSet.ToList();
        if (data.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        _trees.Clear();
        for (var i = 0; i < _treeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = BootstrapSample(data);
            var featureIndices = Enumerable.Range(0, sample.First().Values.Count)
                .OrderBy(_ => _random.Next())
                .Take(Math.Min(_featureSampleSize, sample.First().Values.Count))
                .ToList();

            var bestFeature = 0;
            var bestThreshold = 0d;
            var bestScore = double.MaxValue;

            foreach (var index in featureIndices)
            {
                var thresholds = sample.Select(v => v.Values[index]).Distinct().OrderBy(v => v).ToArray();
                foreach (var threshold in thresholds)
                {
                    var score = GiniImpurity(sample, index, threshold);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestFeature = index;
                        bestThreshold = threshold;
                    }
                }
            }

            _trees.Add(new DecisionStump(bestFeature, bestThreshold));
        }

        _modelVersion = $"RF-{DateTime.UtcNow:yyyyMMddHHmmss}";
        return ValueTask.CompletedTask;
    }

    public ValueTask<MachineLearningSignal> PredictAsync(ModelFeatureVector features, CancellationToken cancellationToken = default)
    {
        if (_trees.Count == 0)
        {
            return ValueTask.FromResult(new MachineLearningSignal(features.Symbol, 0.5, 0.5, _modelVersion));
        }

        var votes = 0;
        foreach (var tree in _trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            votes += tree.Predict(features.Values) ? 1 : -1;
        }

        var probUp = (votes + _trees.Count) / (2d * _trees.Count);
        var probDown = 1 - probUp;
        return ValueTask.FromResult(new MachineLearningSignal(features.Symbol, probUp, probDown, _modelVersion));
    }

    private IReadOnlyList<ModelFeatureVector> BootstrapSample(IReadOnlyList<ModelFeatureVector> data)
    {
        var sample = new List<ModelFeatureVector>(data.Count);
        for (var i = 0; i < data.Count; i++)
        {
            var idx = _random.Next(data.Count);
            sample.Add(data[idx]);
        }

        return sample;
    }

    private double GiniImpurity(IEnumerable<ModelFeatureVector> sample, int featureIndex, double threshold)
    {
        var left = sample.Where(v => v.Values[featureIndex] <= threshold).ToList();
        var right = sample.Where(v => v.Values[featureIndex] > threshold).ToList();

        double Score(IReadOnlyList<ModelFeatureVector> subset)
        {
            if (subset.Count == 0)
            {
                return 0;
            }

            var positives = subset.Count(v => v.Label > 0);
            var negatives = subset.Count - positives;
            var pPos = positives / (double)subset.Count;
            var pNeg = negatives / (double)subset.Count;
            return 1 - (pPos * pPos + pNeg * pNeg);
        }

        var leftWeight = left.Count / (double)(left.Count + right.Count);
        var rightWeight = 1 - leftWeight;

        return leftWeight * Score(left) + rightWeight * Score(right);
    }

    private readonly record struct DecisionStump(int FeatureIndex, double Threshold)
    {
        public bool Predict(IReadOnlyList<double> features)
            => features[FeatureIndex] >= Threshold;
    }
}

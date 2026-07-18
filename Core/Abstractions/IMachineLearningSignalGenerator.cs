using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Abstractions;

public interface IMachineLearningSignalGenerator
{
    ValueTask TrainAsync(IEnumerable<ModelFeatureVector> trainingSet, CancellationToken cancellationToken = default);

    ValueTask<MachineLearningSignal> PredictAsync(ModelFeatureVector features, CancellationToken cancellationToken = default);
}

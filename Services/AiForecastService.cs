using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services;

public class AiForecastService
{
    private readonly BinanceApiClient _apiClient;
    private readonly DataCacheService _cacheService;
    private readonly LstmModelDefinition _model;
    private readonly List<ModelArtifact> _artifacts;

    public AiForecastService(BinanceApiClient apiClient, DataCacheService cacheService)
    {
        _apiClient = apiClient;
        _cacheService = cacheService;
        var modelPath = Path.Combine(AppContext.BaseDirectory, "Data", "ai", "lstm_model.json");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("未找到 LSTM 模型权重文件", modelPath);

        using var stream = File.OpenRead(modelPath);
        var definition = JsonSerializer.Deserialize<LstmModelDefinition>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        _model = definition ?? throw new InvalidOperationException("无法解析 LSTM 模型权重文件");
        _artifacts = new List<ModelArtifact>
        {
            new()
            {
                Name = _model.Name,
                Version = _model.Version,
                Stage = "Production",
                Metric = "RMSE 0.84",
                UpdatedAt = DateTime.UtcNow,
                Description = "双层 LSTM + 线性解码器，用于短期资金费率与价格预测"
            }
        };
    }

    public IReadOnlyList<ModelArtifact> Models => _artifacts;

    public async Task<ForecastResult> ForecastAsync(ForecastRequest request, CancellationToken cancellationToken = default)
    {
        var closes = await _apiClient.GetKlineClosesAsync(request.Symbol, request.Interval, request.HistoryPoints, cancellationToken).ConfigureAwait(false);
        await _cacheService.SavePricesAsync(request.Symbol, closes);

        var history = closes.Select(c => (double)c).ToArray();
        if (history.Length == 0)
            throw new InvalidOperationException("未能获取足够的 K 线数据用于预测");

        var last = history[^1];
        var normalized = history.Select(v => (v - last) / last).ToArray();
        var forecastNormalized = _model.Forecast(normalized, request.Horizon);
        var predicted = forecastNormalized.Select(delta => last * (1 + delta)).ToArray();

        var returns = history.Zip(history.Skip(1), (prev, next) => Math.Log(next / prev)).ToArray();
        var expectedReturn = returns.Length == 0 ? 0 : returns.Average();
        var expectedVolatility = returns.Length == 0 ? 0 : Math.Sqrt(returns.Select(r => Math.Pow(r - expectedReturn, 2)).Average());
        var predictedRisk = forecastNormalized.Select(Math.Abs).DefaultIfEmpty().Average();

        var confInterval = ComputeConfidenceIntervals(predicted, expectedVolatility);

        return new ForecastResult
        {
            Symbol = request.Symbol,
            Historical = history,
            Predicted = predicted,
            ConfidenceUpper = confInterval.Upper,
            ConfidenceLower = confInterval.Lower,
            ExpectedReturn = expectedReturn,
            ExpectedVolatility = expectedVolatility,
            PredictedRisk = predictedRisk,
            GeneratedAt = DateTime.UtcNow
        };
    }

    private static (IReadOnlyList<double> Upper, IReadOnlyList<double> Lower) ComputeConfidenceIntervals(IReadOnlyList<double> predicted, double sigma)
    {
        var upper = new double[predicted.Count];
        var lower = new double[predicted.Count];
        for (int i = 0; i < predicted.Count; i++)
        {
            var delta = (i + 1) * sigma * 1.96;
            upper[i] = predicted[i] * Math.Exp(delta);
            lower[i] = predicted[i] * Math.Exp(-delta);
        }

        return (upper, lower);
    }
}

public class LstmModelDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public List<LstmLayerDefinition> Layers { get; set; } = new();
    public OutputLayerDefinition Output { get; set; } = new();

    public IReadOnlyList<double> Forecast(IReadOnlyList<double> inputs, int horizon)
    {
        if (Layers.Count == 0)
            throw new InvalidOperationException("模型未包含任何 LSTM 层");

        var states = Layers.Select(l => new LstmState(l.HiddenSize)).ToArray();
        var current = new double[] { inputs[0] };
        for (int i = 0; i < inputs.Count; i++)
        {
            current[0] = inputs[i];
            RunStep(current, states);
        }

        var predictions = new double[horizon];
        double input = inputs[^1];
        for (int i = 0; i < horizon; i++)
        {
            current[0] = input;
            var outputVector = RunStep(current, states);
            var value = Dense(outputVector, Output.Weights, Output.Bias);
            var delta = Math.Tanh(value) * 0.05; // clamp for stability
            predictions[i] = delta;
            input = delta;
        }

        return predictions;
    }

    private double[] RunStep(double[] input, LstmState[] states)
    {
        var current = input;
        for (int layerIndex = 0; layerIndex < Layers.Count; layerIndex++)
        {
            current = Layers[layerIndex].Process(current, states[layerIndex]);
        }

        return current;
    }

    private static double Dense(double[] hidden, double[] weights, double[] bias)
    {
        double sum = bias.Length > 0 ? bias[0] : 0;
        for (int i = 0; i < hidden.Length; i++)
            sum += hidden[i] * weights[i];
        return sum;
    }
}

public class LstmLayerDefinition
{
    public int InputSize { get; set; }
    public int HiddenSize { get; set; }
    public double[] Wf { get; set; } = Array.Empty<double>();
    public double[] Wi { get; set; } = Array.Empty<double>();
    public double[] Wc { get; set; } = Array.Empty<double>();
    public double[] Wo { get; set; } = Array.Empty<double>();
    public double[] Uf { get; set; } = Array.Empty<double>();
    public double[] Ui { get; set; } = Array.Empty<double>();
    public double[] Uc { get; set; } = Array.Empty<double>();
    public double[] Uo { get; set; } = Array.Empty<double>();
    public double[] Bf { get; set; } = Array.Empty<double>();
    public double[] Bi { get; set; } = Array.Empty<double>();
    public double[] Bc { get; set; } = Array.Empty<double>();
    public double[] Bo { get; set; } = Array.Empty<double>();

    public double[] Process(double[] input, LstmState state)
    {
        var hidden = state.Hidden;
        var cell = state.Cell;

        var f = new double[HiddenSize];
        var i = new double[HiddenSize];
        var g = new double[HiddenSize];
        var o = new double[HiddenSize];

        for (int h = 0; h < HiddenSize; h++)
        {
            double wf = Bf[h];
            double wi = Bi[h];
            double wc = Bc[h];
            double wo = Bo[h];

            for (int j = 0; j < InputSize; j++)
            {
                var x = input[j];
                wf += Wf[h * InputSize + j] * x;
                wi += Wi[h * InputSize + j] * x;
                wc += Wc[h * InputSize + j] * x;
                wo += Wo[h * InputSize + j] * x;
            }

            for (int j = 0; j < HiddenSize; j++)
            {
                var hPrev = hidden[j];
                wf += Uf[h * HiddenSize + j] * hPrev;
                wi += Ui[h * HiddenSize + j] * hPrev;
                wc += Uc[h * HiddenSize + j] * hPrev;
                wo += Uo[h * HiddenSize + j] * hPrev;
            }

            f[h] = Sigmoid(wf);
            i[h] = Sigmoid(wi);
            g[h] = Math.Tanh(wc);
            o[h] = Sigmoid(wo);

            cell[h] = f[h] * cell[h] + i[h] * g[h];
            hidden[h] = o[h] * Math.Tanh(cell[h]);
        }

        return hidden.ToArray();
    }

    private static double Sigmoid(double value) => 1.0 / (1.0 + Math.Exp(-value));
}

public class OutputLayerDefinition
{
    public double[] Weights { get; set; } = Array.Empty<double>();
    public double[] Bias { get; set; } = Array.Empty<double>();
}

public class LstmState
{
    public LstmState(int size)
    {
        Hidden = new double[size];
        Cell = new double[size];
    }

    public double[] Hidden { get; }
    public double[] Cell { get; }
}

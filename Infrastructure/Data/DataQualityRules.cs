using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Infrastructure.Data;

public sealed class NullValueQualityRule : IDataQualityRule
{
    public string Name => "NullCheck";

    public ValueTask<DataQualityResult> ValidateAsync(RawDataFrame frame, CancellationToken cancellationToken = default)
    {
        foreach (var value in frame.Payload.Values)
        {
            if (value is null)
            {
                return ValueTask.FromResult(new DataQualityResult(Name, false, "Null payload value"));
            }
        }

        return ValueTask.FromResult(new DataQualityResult(Name, true));
    }
}

public sealed class RangeQualityRule : IDataQualityRule
{
    private readonly string _field;
    private readonly double _min;
    private readonly double _max;

    public RangeQualityRule(string field, double min, double max)
    {
        _field = field;
        _min = min;
        _max = max;
    }

    public string Name => $"Range({_field})";

    public ValueTask<DataQualityResult> ValidateAsync(RawDataFrame frame, CancellationToken cancellationToken = default)
    {
        if (frame.Payload.TryGetValue(_field, out var value) && value is double numeric)
        {
            if (numeric < _min || numeric > _max)
            {
                return ValueTask.FromResult(new DataQualityResult(Name, false, $"Value {numeric} out of range"));
            }
        }

        return ValueTask.FromResult(new DataQualityResult(Name, true));
    }
}

public sealed class SpikeDetectionRule : IDataQualityRule
{
    private readonly Queue<double> _window = new();
    private readonly int _length;
    private readonly string _field;

    public SpikeDetectionRule(string field, int length = 20)
    {
        _field = field;
        _length = length;
    }

    public string Name => $"Spike({_field})";

    public ValueTask<DataQualityResult> ValidateAsync(RawDataFrame frame, CancellationToken cancellationToken = default)
    {
        if (!frame.Payload.TryGetValue(_field, out var value) || value is not double numeric)
        {
            return ValueTask.FromResult(new DataQualityResult(Name, true));
        }

        _window.Enqueue(numeric);
        if (_window.Count > _length)
        {
            _window.Dequeue();
        }

        if (_window.Count == _length)
        {
            var avg = 0d;
            foreach (var item in _window)
            {
                avg += item;
            }

            avg /= _window.Count;
            var deviation = Math.Abs(numeric - avg) / (avg == 0 ? 1 : avg);
            if (deviation > 0.2)
            {
                return ValueTask.FromResult(new DataQualityResult(Name, false, $"Spike detected: {deviation:P2}"));
            }
        }

        return ValueTask.FromResult(new DataQualityResult(Name, true));
    }
}

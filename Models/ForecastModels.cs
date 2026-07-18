using System;
using System.Collections.Generic;

namespace 币安量化机器人.Models;

public class ForecastRequest
{
    public string Symbol { get; init; } = string.Empty;
    public string Interval { get; init; } = "1h";
    public int Horizon { get; init; } = 12;
    public int HistoryPoints { get; init; } = 200;
}

public class ForecastResult
{
    public string Symbol { get; init; } = string.Empty;
    public IReadOnlyList<double> Historical { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> Predicted { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> ConfidenceUpper { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> ConfidenceLower { get; init; } = Array.Empty<double>();
    public double ExpectedReturn { get; init; }
    public double ExpectedVolatility { get; init; }
    public double PredictedRisk { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
}

public class ModelArtifact
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public string Stage { get; init; } = "Production";
    public string Metric { get; init; } = string.Empty;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    public string Description { get; init; } = string.Empty;
}

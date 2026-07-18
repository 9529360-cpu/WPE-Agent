using System;
using System.Collections.Generic;

namespace 币安量化机器人.Models;

public class RiskMetrics
{
    public string Symbol { get; init; } = string.Empty;
    public decimal ValueAtRisk { get; init; }
    public decimal ConditionalVaR { get; init; }
    public decimal Volatility { get; init; }
    public decimal MaxDrawdown { get; init; }
    public decimal Exposure { get; init; }
    public decimal Leverage { get; init; }
    public decimal KellyFraction { get; init; }
    public decimal LiquidationPrice { get; init; }
    public DateTime CalculatedAt { get; init; } = DateTime.UtcNow;
}

public class RiskRule
{
    public string Name { get; init; } = string.Empty;
    public decimal Threshold { get; init; }
    public string Comparator { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}

public class StressTestResult
{
    public string Scenario { get; init; } = string.Empty;
    public decimal PortfolioPnl { get; init; }
    public decimal MarginUsage { get; init; }
    public decimal BreachProbability { get; init; }
}

public class RiskReport
{
    public IReadOnlyList<RiskMetrics> Metrics { get; init; } = Array.Empty<RiskMetrics>();
    public IReadOnlyList<RiskRule> BreachedRules { get; init; } = Array.Empty<RiskRule>();
    public IReadOnlyList<StressTestResult> StressTests { get; init; } = Array.Empty<StressTestResult>();
}

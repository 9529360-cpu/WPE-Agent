using System.Collections.Generic;

namespace 币安量化机器人.Models;

public class StrategyConfig
{
    public string StrategyName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public double Capital { get; set; }
    public int Leverage { get; set; }
    public int MaxPositions { get; set; }
    public double StopLossPercent { get; set; }
    public double TakeProfitPercent { get; set; }
    public bool EnableAutoTrade { get; set; }
    public bool EnableHedgeMode { get; set; }
    public string ExecutionVenue { get; set; } = string.Empty;
    public List<StrategyParameterRow> Parameters { get; set; } = new();
}

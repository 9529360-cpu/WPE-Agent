using System;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Risk;

public sealed class DynamicStopLossRule : IRiskRule
{
    private double _stopLossMultiplier = 1.5;

    public string Name => "DynamicStopLoss";

    public void Configure(RiskConfiguration configuration)
    {
        _stopLossMultiplier = configuration.StopLossMultiplier;
    }

    public RiskRuleResult Evaluate(in PositionSnapshot snapshot)
    {
        var atr = snapshot.Indicators?.GetValueOrDefault("atr", 0) ?? 0;
        if (atr <= 0)
        {
            return new RiskRuleResult(true);
        }

        var stopLoss = snapshot.EntryPrice - atr * _stopLossMultiplier;
        if (snapshot.CurrentPrice <= stopLoss)
        {
            return new RiskRuleResult(false, $"Hit dynamic stop {stopLoss:F2}");
        }

        return new RiskRuleResult(true);
    }
}

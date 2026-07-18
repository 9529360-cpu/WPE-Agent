using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Risk;

public sealed class MaxDrawdownRule : IRiskRule
{
    private double _maxDrawdown = 0.15;

    public string Name => "MaxDrawdown";

    public void Configure(RiskConfiguration configuration)
    {
        _maxDrawdown = configuration.MaxDrawdown;
    }

    public RiskRuleResult Evaluate(in PositionSnapshot snapshot)
    {
        var drawdown = snapshot.MaxDrawdown;
        return drawdown <= _maxDrawdown
            ? new RiskRuleResult(true)
            : new RiskRuleResult(false, $"Drawdown {drawdown:P2} exceeds {_maxDrawdown:P2}");
    }
}

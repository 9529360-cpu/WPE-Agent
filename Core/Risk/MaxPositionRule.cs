using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Risk;

public sealed class MaxPositionRule : IRiskRule
{
    private double _maxPositionSize = 0.2;

    public string Name => "MaxPosition";

    public void Configure(RiskConfiguration configuration)
    {
        _maxPositionSize = configuration.MaxPositionSize;
    }

    public RiskRuleResult Evaluate(in PositionSnapshot snapshot)
    {
        var exposure = snapshot.Quantity * snapshot.CurrentPrice;
        var allowed = snapshot.Equity * _maxPositionSize;
        return exposure <= allowed
            ? new RiskRuleResult(true)
            : new RiskRuleResult(false, $"Exposure {exposure:F2} exceeds limit {allowed:F2}");
    }
}

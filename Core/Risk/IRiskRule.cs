using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Risk;

public interface IRiskRule
{
    string Name { get; }

    void Configure(RiskConfiguration configuration);

    RiskRuleResult Evaluate(in PositionSnapshot snapshot);
}

public readonly record struct RiskRuleResult(bool Passed, string? Message = null);

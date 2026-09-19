using WpeAgent.CrossAssetResearch;

namespace 币安量化机器人.Services.Agent;

public static class TradingRealityCostAuthorityV1
{
    public const string Version = "research-cost-v1";
    public static readonly ResearchCostModel VariableCosts = new(
        CommissionRate:.0004m,
        SlippageRate:.0003m);

    public static ExecutionRealityCostAssumptionV1 Execution =>
        new(Version, VariableCosts.CommissionRate, VariableCosts.SlippageRate);
}

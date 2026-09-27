namespace 币安量化机器人.Services.Agent;

public static class TestnetHighOpportunityRiskProfileV1
{
    public const int RequestedLeverage=150;
    public const decimal InitialMarginBudget=.05m;
    public const decimal MaximumLossRiskPerTrade=.01m;
    public const decimal MaximumSymbolExposure=.25m;
    public const decimal MaximumAccountExposure=.50m;
    public const decimal MaximumDailyLoss=.05m;
    public const decimal MaximumDailyDrawdown=.08m;
    public const double MinimumRiskReward=1.8;
    public const double MinimumLiquidityScore=.55;
    public const double MaximumSpreadBps=8;
    public const double MaximumAtrPercent=.045;

    public static RiskLimits Resolve(RiskLimits configured,ExchangeEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if(!configured.TestnetHighOpportunityMode)return configured;
        if(environment!=ExchangeEnvironment.Testnet)
            throw new InvalidOperationException("High-opportunity risk profile is Testnet-only.");

        return new RiskLimits
        {
            TestnetHighOpportunityMode=true,
            MarginTiers=(configured.MarginTiers??[]).ToArray(),
            MaxMargin=Math.Min(configured.MaxMargin,.50m),
            MaxInitialMarginPerTrade=InitialMarginBudget,
            Leverage=RequestedLeverage,
            DailyDrawdownLimit=Math.Min(configured.DailyDrawdownLimit,MaximumDailyDrawdown),
            MaxRiskPerTrade=Math.Min(configured.MaxRiskPerTrade,MaximumLossRiskPerTrade),
            MaxSymbolExposure=Math.Min(configured.MaxSymbolExposure,MaximumSymbolExposure),
            MaxAccountExposure=Math.Min(configured.MaxAccountExposure,MaximumAccountExposure),
            MaxDailyLoss=Math.Min(configured.MaxDailyLoss,MaximumDailyLoss),
            MaxAtrPercent=Math.Min(configured.MaxAtrPercent,MaximumAtrPercent),
            MinimumLiquidityScore=Math.Max(configured.MinimumLiquidityScore,MinimumLiquidityScore),
            MaximumSpreadBps=Math.Min(configured.MaximumSpreadBps,MaximumSpreadBps),
            MaximumSlippageBps=configured.MaximumSlippageBps,
            MinimumRiskReward=Math.Max(configured.MinimumRiskReward,MinimumRiskReward),
            ApiFailureThreshold=configured.ApiFailureThreshold,
            MaxPortfolioVaR99=configured.MaxPortfolioVaR99,
            MaxPortfolioCVaR99=configured.MaxPortfolioCVaR99,
            MaxLargestPositionShare=configured.MaxLargestPositionShare,
            MaxCorrelatedExposure=configured.MaxCorrelatedExposure,
            MinimumHistoricalDays=configured.MinimumHistoricalDays,
            MinimumBacktestTrades=configured.MinimumBacktestTrades,
            Isolated=true
        };
    }
}

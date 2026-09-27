namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionSpreadAssessment(
    bool Allowed,
    double BaseLimitBps,
    double EffectiveLimitBps,
    double HardLimitBps,
    double RiskDistanceBps,
    bool Adaptive,
    string Mode);

public static class ExecutionCostPolicy
{
    public const double TestnetAdaptiveHardSpreadBps=15;
    public const double AdaptiveSpreadRiskFraction=.025;
    public const double AdaptiveMinimumQualityScore=75;
    public const double AdaptiveMinimumLiquidityScore=.80;
    public const double AdaptiveMaximumSlippageBps=8;

    public static ExecutionSpreadAssessment AssessSpread(
        RiskLimits limits,
        MarketQualityEvidence quality,
        decimal expectedPrice,
        decimal stopLoss,
        double slippageBps=0,
        bool requireSlippage=false)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(quality);

        var baseLimit=Math.Max(0,limits.MaximumSpreadBps);
        if(quality.SpreadBps<=baseLimit)
            return new(true,baseLimit,baseLimit,baseLimit,DistanceBps(expectedPrice,stopLoss),false,"base");

        // Respect explicitly tighter user configuration. Adaptive widening is only available
        // when the Testnet high-opportunity profile is using its normal 8 bps base ceiling.
        var adaptiveEnabled=limits.TestnetHighOpportunityMode
            &&baseLimit>=TestnetHighOpportunityRiskProfileV1.MaximumSpreadBps-.0000001;
        if(!adaptiveEnabled)
            return new(false,baseLimit,baseLimit,baseLimit,DistanceBps(expectedPrice,stopLoss),false,"configured-hard");

        var riskDistanceBps=DistanceBps(expectedPrice,stopLoss);
        var hardLimit=Math.Max(baseLimit,TestnetAdaptiveHardSpreadBps);
        var riskScaledLimit=riskDistanceBps>0
            ?riskDistanceBps*AdaptiveSpreadRiskFraction
            :baseLimit;
        var effectiveLimit=Math.Min(hardLimit,Math.Max(baseLimit,riskScaledLimit));
        var qualityOk=quality.QualityScore>=AdaptiveMinimumQualityScore;
        var liquidityFloor=Math.Max(limits.MinimumLiquidityScore,AdaptiveMinimumLiquidityScore);
        var liquidityOk=quality.LiquidityScore>=liquidityFloor;
        var slippageLimit=Math.Min(limits.MaximumSlippageBps,AdaptiveMaximumSlippageBps);
        var slippageOk=!requireSlippage||slippageBps<=slippageLimit;
        var allowed=riskDistanceBps>0
            &&qualityOk
            &&liquidityOk
            &&slippageOk
            &&quality.SpreadBps<=effectiveLimit;

        return new(
            allowed,
            baseLimit,
            effectiveLimit,
            hardLimit,
            riskDistanceBps,
            effectiveLimit>baseLimit,
            allowed?"adaptive-pass":"adaptive-block");
    }

    private static double DistanceBps(decimal expectedPrice,decimal stopLoss)
    {
        if(expectedPrice<=0||stopLoss<=0)return 0;
        return Math.Abs((double)((expectedPrice-stopLoss)/expectedPrice))*10000d;
    }
}

using 币安量化机器人.Core.Strategy;

namespace 币安量化机器人.Services.Agent;

public sealed record AdaptivePortfolioAllocation(
    string Symbol,
    string StrategyId,
    StrategyFamily Family,
    int Direction,
    MarketRegime Regime,
    double OpportunityScore,
    double CapitalShare,
    double RiskMultiplier,
    int TargetTier,
    bool IsExploration,
    int Rank,
    string Reason)
{
    public bool DirectionMatches(DecisionAction action)
    {
        var required=action switch
        {
            DecisionAction.OpenLong or DecisionAction.AddLong or DecisionAction.ReverseToLong=>1,
            DecisionAction.OpenShort or DecisionAction.AddShort or DecisionAction.ReverseToShort=>-1,
            _=>0
        };
        return required==0||required==Direction;
    }
}

public sealed record AdaptiveStrategyPortfolioPlan(
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<AdaptivePortfolioAllocation> Allocations)
{
    public AdaptivePortfolioAllocation? Leader=>Allocations.OrderBy(x=>x.Rank).FirstOrDefault();
    public AdaptivePortfolioAllocation? ForSymbol(string? symbol)
        =>string.IsNullOrWhiteSpace(symbol)?null:Allocations.FirstOrDefault(x=>x.Symbol.Equals(symbol,StringComparison.OrdinalIgnoreCase));
}

public static class AdaptiveStrategyPortfolioAllocator
{
    public const double MaximumExplorationRiskMultiplier=.34;

    public static AdaptiveStrategyPortfolioPlan Allocate(
        IReadOnlyDictionary<string,StrategyCycleSelection> selections,
        EvidencePack evidence,
        int marginTierCount,
        DateTimeOffset? evaluatedAtUtc=null)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(evidence);
        var tiers=Math.Max(1,marginTierCount);
        var raw=new List<Candidate>();

        foreach(var pair in selections.OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase))
        {
            var selection=pair.Value;
            if(!evidence.Markets.TryGetValue(pair.Key,out var market)||market is null)continue;
            if(selection.Signal.Direction is not (-1 or 1))continue;

            var strategy=Math.Clamp(selection.SelectionScore,0,1);
            var agreement=Math.Clamp(selection.StrategyAgreement,0,1);
            var marketQuality=Math.Clamp(market.Quality.QualityScore/100d,0,1);
            var liquidity=Math.Clamp(market.Quality.LiquidityScore,0,1);
            var spread=Math.Max(0,market.Quality.SpreadBps);
            var spreadScore=1d/(1d+spread/8d);
            var regimeScore=selection.Regime switch
            {
                MarketRegime.Trending=>1d,
                MarketRegime.Ranging=>.90d,
                MarketRegime.Transition=>.82d,
                MarketRegime.Extreme=>.45d,
                _=>.50d
            };
            var regimeMature=selection.RegimeObservations>=StrategyGovernor.MinimumRegimeCalibrationObservations;
            var expectancyScore=regimeMature?Math.Clamp(.5+.5*Math.Tanh(selection.RegimeExpectancy/.02),0,1):.5;
            var executionMature=selection.ExecutionTrades>=StrategyGovernor.MinimumExecutionFeedbackTrades;
            var executionScore=executionMature?Math.Clamp(selection.ExecutionPosteriorWinRate,0,1):.5;
            var failureMultiplier=Math.Clamp(Math.Pow(.88,Math.Max(0,selection.Profile.FailureStreak)),.45,1);

            var opportunity=(.32*strategy+.12*agreement+.14*marketQuality+.10*liquidity+.08*spreadScore+.10*expectancyScore+.08*executionScore+.06*regimeScore)*failureMultiplier;
            opportunity=Math.Clamp(opportunity,0,1);
            var exploration=!regimeMature||!executionMature;
            raw.Add(new(selection,opportunity,exploration,marketQuality,liquidity,spreadScore,expectancyScore,executionScore,failureMultiplier));
        }

        if(raw.Count==0)return new(evaluatedAtUtc??DateTimeOffset.UtcNow,Array.Empty<AdaptivePortfolioAllocation>());

        var weightTotal=raw.Sum(x=>x.Opportunity*x.Opportunity);
        var ranked=raw.OrderByDescending(x=>x.Opportunity).ThenByDescending(x=>x.Selection.SelectionScore).ThenBy(x=>x.Selection.Profile.Id,StringComparer.Ordinal).ToArray();
        var allocations=new List<AdaptivePortfolioAllocation>(ranked.Length);

        for(var index=0;index<ranked.Length;index++)
        {
            var candidate=ranked[index];
            var share=weightTotal>0?candidate.Opportunity*candidate.Opportunity/weightTotal:1d/ranked.Length;
            var capitalAdjusted=Math.Clamp(.90*candidate.Opportunity+.10*Math.Sqrt(Math.Clamp(share,0,1)),0,1);
            var riskMultiplier=Math.Clamp(.10+.90*Math.Pow(capitalAdjusted,1.5),.10,1);
            if(candidate.Exploration)riskMultiplier=Math.Min(riskMultiplier,MaximumExplorationRiskMultiplier);
            var targetTier=candidate.Exploration?1:Math.Clamp((int)Math.Ceiling(riskMultiplier*tiers),1,tiers);
            var selection=candidate.Selection;
            var reason=string.Join("; ",
                $"opportunity={candidate.Opportunity:F3}",
                $"capital_share={share:F3}",
                $"strategy={selection.SelectionScore:F3}",
                $"agreement={selection.StrategyAgreement:F3}",
                $"market_quality={candidate.MarketQuality:F3}",
                $"liquidity={candidate.Liquidity:F3}",
                $"spread_score={candidate.SpreadScore:F3}",
                $"expectancy_score={candidate.ExpectancyScore:F3}",
                $"execution_score={candidate.ExecutionScore:F3}",
                $"failure_multiplier={candidate.FailureMultiplier:F3}",
                candidate.Exploration?"mode=exploration":"mode=scaled");
            allocations.Add(new(selection.Profile.Symbol,selection.Profile.Id,selection.Profile.Family,selection.Signal.Direction,selection.Regime,candidate.Opportunity,share,riskMultiplier,targetTier,candidate.Exploration,index+1,reason));
        }

        return new(evaluatedAtUtc??DateTimeOffset.UtcNow,allocations);
    }

    private sealed record Candidate(
        StrategyCycleSelection Selection,
        double Opportunity,
        bool Exploration,
        double MarketQuality,
        double Liquidity,
        double SpreadScore,
        double ExpectancyScore,
        double ExecutionScore,
        double FailureMultiplier);
}

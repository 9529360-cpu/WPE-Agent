using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffLiveCycleInputComposerTests
{
    [Fact]
    public void LiveCycleRequestContainsOnlyDirectTradingFacts()
    {
        var properties=typeof(ModelOffLiveCycleInputRequestV1)
            .GetProperties()
            .Select(x=>x.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("CycleId",properties);
        Assert.Contains("EvaluationTimeUtc",properties);
        Assert.Contains("Evidence",properties);
        Assert.Contains("DecisionReview",properties);
        Assert.Contains("RiskReview",properties);
        Assert.Contains("PositionReconciliation",properties);
        Assert.Contains("ProtectionReconciliation",properties);
        Assert.Contains("ExternalPositionIsolation",properties);
        Assert.DoesNotContain("Research",properties);
        Assert.DoesNotContain("Assessments",properties);
        Assert.DoesNotContain("TradeHypotheses",properties);
    }

    [Fact]
    public void ComposerUsesDirectCandleStructureInsteadOfScoresOrHypotheses()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"Services","Agent","ModelOffLiveCycleInputComposerV1.cs"));

        Assert.Contains("DirectMarketStructureDecisionSkill.IsDirect",source,StringComparison.Ordinal);
        Assert.Contains("DirectMarketStructureDecisionSkill.ContextMatches",source,StringComparison.Ordinal);
        Assert.Contains("MarketStructureIntelligence.Analyze",source,StringComparison.Ordinal);
        Assert.Contains("DirectStructureFact",source,StringComparison.Ordinal);
        Assert.DoesNotContain("TradeHypothesis",source,StringComparison.Ordinal);
        Assert.DoesNotContain("MarketDecisionAssessment",source,StringComparison.Ordinal);
        Assert.DoesNotContain("ResearchValidationResult",source,StringComparison.Ordinal);
        Assert.DoesNotContain("NetScore",source,StringComparison.Ordinal);
        Assert.DoesNotContain("RecommendationMatches",source,StringComparison.Ordinal);
    }

    [Fact]
    public void LiveLoopDoesNotConstructLegacyScoringOrStrategyAuthorities()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"Services","AutoTradingAgent.cs"));

        Assert.Contains("DirectMarketStructureDecisionSkill.DecisionContextKind",source,StringComparison.Ordinal);
        Assert.Contains("direct=true",source,StringComparison.Ordinal);
        Assert.DoesNotContain("new SignalAggregationSkill",source,StringComparison.Ordinal);
        Assert.DoesNotContain("AdaptiveStrategySelector",source,StringComparison.Ordinal);
        Assert.DoesNotContain("AdaptiveStrategyPortfolioAllocator",source,StringComparison.Ordinal);
        Assert.DoesNotContain("TradeHypothesisEngine",source,StringComparison.Ordinal);
        Assert.DoesNotContain("StrategyResearchAgent",source,StringComparison.Ordinal);
        Assert.DoesNotContain("MarketDecisionAssessment",source,StringComparison.Ordinal);
        Assert.DoesNotContain("ResearchValidationResult",source,StringComparison.Ordinal);
    }

    [Fact]
    public void TradingBrainSourceIsLocalOnly()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"Services","Agent","BrainProviders.cs"));

        Assert.Contains("WPE Local Brain",source,StringComparison.Ordinal);
        Assert.Contains("DirectMarketStructureDecisionSkill.Decide",source,StringComparison.Ordinal);
        Assert.Contains("local-deterministic",source,StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAiCompatibleAdapter",source,StringComparison.Ordinal);
        Assert.DoesNotContain("AnthropicMessagesAdapter",source,StringComparison.Ordinal);
        Assert.DoesNotContain("GeminiGenerativeAdapter",source,StringComparison.Ordinal);
        Assert.DoesNotContain("BrainPromptComposer",source,StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentRiskSourceContainsOnlyHardSafetyAndDirectContextGates()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"Services","Agent","MatureTradingSkills.cs"));
        var start=source.IndexOf("public sealed class IndependentRiskManagerSkill",StringComparison.Ordinal);
        var end=source.IndexOf("public static class PositionManagementDurableState",start,StringComparison.Ordinal);
        Assert.True(start>=0&&end>start);
        var risk=source[start..end];

        Assert.Contains("direct_market_structure_context",risk,StringComparison.Ordinal);
        Assert.Contains("liquidity",risk,StringComparison.Ordinal);
        Assert.Contains("spread",risk,StringComparison.Ordinal);
        Assert.Contains("volatility",risk,StringComparison.Ordinal);
        Assert.Contains("loss_streak",risk,StringComparison.Ordinal);
        Assert.Contains("order_state",risk,StringComparison.Ordinal);
        Assert.DoesNotContain("research_gate",risk,StringComparison.Ordinal);
        Assert.DoesNotContain("historical_coverage",risk,StringComparison.Ordinal);
        Assert.DoesNotContain("market_hypothesis_context",risk,StringComparison.Ordinal);
        Assert.DoesNotContain("NetScore",risk,StringComparison.Ordinal);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

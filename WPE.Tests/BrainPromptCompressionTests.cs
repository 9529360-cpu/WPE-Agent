namespace WPE.Tests;

public sealed class BrainPromptCompressionTests
{
    [Fact]
    public void RemotePlannerPrompt_UsesTwoLevelMarketPayload()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Contains("internal static class BrainPromptComposer", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxMarketBriefContexts = 4;", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxDetailedMarketContexts = 2;", source, StringComparison.Ordinal);
        Assert.Contains("private enum PromptDetailLevel", source, StringComparison.Ordinal);
        Assert.Contains("var slimPrompt = BuildPrompt", source, StringComparison.Ordinal);
        Assert.Contains("var detailedPrompt = BuildPrompt", source, StringComparison.Ordinal);
        Assert.Contains("promptStructure = detailLevel == PromptDetailLevel.SlimBrief ? \"two-level-slim-brief\" : \"two-level-detailed-context\"", source, StringComparison.Ordinal);
        Assert.Contains("detailedMarketContext = detailLevel == PromptDetailLevel.DetailedContext", source, StringComparison.Ordinal);
        Assert.Contains("Markets = BuildMarketBriefs(evidence, context, detailLevel, detailedSymbols)", source, StringComparison.Ordinal);
        Assert.Contains("private static object[] BuildMarketBriefs", source, StringComparison.Ordinal);
        Assert.Contains("BuildMarketBrief", source, StringComparison.Ordinal);
        Assert.Contains("BuildDetailedMarket", source, StringComparison.Ordinal);
        Assert.Contains("BuildDetailedMarkets", source, StringComparison.Ordinal);
        Assert.Contains("ShouldUpgradeToDetailedContext", source, StringComparison.Ordinal);
        Assert.Contains("SelectDetailedSymbols", source, StringComparison.Ordinal);
        Assert.Contains("if (!string.IsNullOrWhiteSpace(context.ActiveSymbol)) return true;", source, StringComparison.Ordinal);
        Assert.Contains("if (evidence.Positions.Count > 0) return true;", source, StringComparison.Ordinal);
        Assert.Contains("if (ordered.Length > 1 && Math.Abs(ordered[0].NetScore - ordered[1].NetScore) <= 0.15) return true;", source, StringComparison.Ordinal);
        Assert.Contains("outcomeMemory = context.OutcomeMemories", source, StringComparison.Ordinal);
        Assert.Contains("outcomeMemorySchema = \"wpe.planner-outcome-memory/1.0\"", source, StringComparison.Ordinal);
        Assert.Contains("ea = x.ExecutionAttempted", source, StringComparison.Ordinal);
        Assert.Contains("sc = x.StateChanged", source, StringComparison.Ordinal);
        Assert.Contains("next = TrimText(x.RecoveryHint, 40)", source, StringComparison.Ordinal);
        Assert.Contains("Math.Min(profile.PreviousOutcomeChars, 80)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviousOutcomes = context.PreviousOutcomes", source, StringComparison.Ordinal);
        Assert.Contains("evidence = BuildEvidence(evidence, context, profile, detailLevel, detailedSymbols)", source, StringComparison.Ordinal);
        Assert.Contains("News = BuildNews(evidence.News, profile, detailLevel)", source, StringComparison.Ordinal);
        Assert.Contains(".Select(x => BuildAssessment(x, profile, detailLevel))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Markets = evidence.Markets", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMarket(x.Value, profile.RecentCloseCount)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionPlanner_ReadsStructuredMemoryWithoutLegacyTextAdaptation()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Services", "AutoTradingAgent.cs"));

        Assert.Contains("RecentOutcomeMemoriesAsync(ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RecentOutcomesAsync(ct)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RemotePlannerPrompt_EnforcesAggressiveTrimmingAndBudgets()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Contains("return Math.Clamp(target, 3_000, 12_000);", source, StringComparison.Ordinal);
        Assert.Contains("private static object[] BuildNews", source, StringComparison.Ordinal);
        Assert.Contains("var excluded = detailLevel == PromptDetailLevel.DetailedContext", source, StringComparison.Ordinal);
        Assert.Contains("if (selected.Count == 0 && detailLevel == PromptDetailLevel.DetailedContext)", source, StringComparison.Ordinal);
        Assert.Contains("private static object BuildSignal", source, StringComparison.Ordinal);
        Assert.Contains(".Select(x => BuildSignal(x, detailLevel))", source, StringComparison.Ordinal);
        Assert.Contains("if (detailLevel == PromptDetailLevel.DetailedContext)", source, StringComparison.Ordinal);
        Assert.Contains("if (detailLevel == PromptDetailLevel.DetailedContext && !string.IsNullOrWhiteSpace(x.BodySummary))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BodySummary = TrimText(x.BodySummary, profile.NewsSummaryChars)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("x.RawValue,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("x.Weight,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Explanation = TrimText(x.Explanation, 120)", source, StringComparison.Ordinal);
        Assert.Contains("return value[..Math.Max(1, maxChars - 3)] + \"...\";", source, StringComparison.Ordinal);
        Assert.Contains("Mode = context.CircuitBreakerActive ? \"degraded\" : \"active\",", source, StringComparison.Ordinal);
        Assert.Contains("new(12, 220, 8, 180, 6, 4, 8)", source, StringComparison.Ordinal);
        Assert.Contains("new(4, 90, 3, 80, 2, 2, 4)", source, StringComparison.Ordinal);
        Assert.Contains(".Take(6)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("x.Leverage,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("x.Isolated", source, StringComparison.Ordinal);
    }

    private static string SourcePath()
        => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "Agent", "BrainProviders.cs");
}

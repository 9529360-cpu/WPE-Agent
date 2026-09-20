using System.Text.Json;
using 币安量化机器人.Services.Agent;

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
        Assert.Contains("var wantsDetailed = ShouldUpgradeToDetailedContext", source, StringComparison.Ordinal);
        Assert.Contains("var detailedPrompt = BuildPrompt", source, StringComparison.Ordinal);
        Assert.Contains("if (detailedPrompt.Length <= budget) return detailedPrompt;", source, StringComparison.Ordinal);
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
        Assert.Contains("x.Candles.Count >= NumericalMarketStructureSkill.MinimumCandles", source, StringComparison.Ordinal);
        Assert.Contains("x.Quality.QualityScore >= 65", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ordered.Any(x => x.EntryReady)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Abs(ordered[0].NetScore - ordered[1].NetScore)", source, StringComparison.Ordinal);
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
    public void RemotePlannerPrompt_PrefersDetailedOhlcvAndStructureWhenCanonicalMarketIsReady()
    {
        var now=DateTime.UtcNow;var candles=new List<CandleEvidence>();var price=100m;
        for(var i=0;i<48;i++)
        {
            var open=price;price*=i<42?1.0005m:1.002m;var volume=i<42?100m:160m;
            candles.Add(new(now.AddMinutes(-15*(48-i)),open,Math.Max(open,price)*1.001m,Math.Min(open,price)*.999m,price,volume,volume*price,100,volume*.55m));
        }
        var market=new MarketEvidence("BTCUSDT",price,candles.TakeLast(40).Min(x=>x.Low),candles.TakeLast(40).Max(x=>x.High),58,.01,.02,.03,new(.0001m,1000m,1.1m,1.05m,1.02m,1.15m,.001m),now)
        {
            Candles=candles,
            Quality=new MarketQualityEvidence{QualityScore=92,LiquidityScore=.9,RelativeVolume=1.4,AtrPercent=.012,BestBid=price*.9999m,BestAsk=price*1.0001m,SpreadBps=2}
        };
        market=market with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"test-provider","Testnet")};
        var evidence=new EvidencePack{CollectedAt=now,Completeness=100,Markets=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{market.Symbol,market}}};
        var context=new AgentContext("remote-test",false,null,Array.Empty<StructuredOutcomeMemory>(),Array.Empty<MarketDecisionAssessment>(),0);

        var prompt=BrainPromptComposer.BuildDecisionPrompt(evidence,context,"zh-CN",32000,"analyze");
        using var document=JsonDocument.Parse(prompt);var root=document.RootElement;

        Assert.Equal("two-level-detailed-context",root.GetProperty("promptStructure").GetString());
        var detailed=root.GetProperty("detailedMarketContext").GetProperty("BTCUSDT");
        var recentBars=detailed.GetProperty("CandleSummary").GetProperty("RecentBars");
        Assert.InRange(recentBars.GetArrayLength(),6,16);
        Assert.True(recentBars[0].TryGetProperty("o",out _));
        Assert.True(recentBars[0].TryGetProperty("h",out _));
        Assert.True(recentBars[0].TryGetProperty("l",out _));
        Assert.True(recentBars[0].TryGetProperty("c",out _));
        Assert.True(detailed.TryGetProperty("Structure",out var structure));
        Assert.True(structure.TryGetProperty("Bias",out _));
        Assert.True(detailed.TryGetProperty("Hypotheses",out var hypotheses));
        Assert.True(hypotheses.GetArrayLength()>0);
    }

    [Fact]
    public void ProductionPlanner_ReadsStructuredMemoryWithoutLegacyTextAdaptation()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Services", "AutoTradingAgent.cs"));

        Assert.Contains("RecentOutcomeMemoriesAsync(ct)", source, StringComparison.Ordinal);
        Assert.Contains("RetrievePlannerMemoriesAsync(memorySymbol,ct)", source, StringComparison.Ordinal);
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
        Assert.Contains("new(12, 220, 8, 180, 6, 12, 8)", source, StringComparison.Ordinal);
        Assert.Contains("new(4, 90, 3, 80, 2, 6, 4)", source, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(recentBarCount, 4, 16)", source, StringComparison.Ordinal);
        Assert.Contains("RecentBars = recentBars", source, StringComparison.Ordinal);
        Assert.Contains("Structure = new", source, StringComparison.Ordinal);
        Assert.Contains("Hypotheses = hypotheses.Take(3)", source, StringComparison.Ordinal);
        Assert.Contains(".Take(6)", source, StringComparison.Ordinal);
        Assert.Contains("relevantMemorySchema = \"wpe.planner-tiered-memory/1.0\"", source, StringComparison.Ordinal);
        Assert.Contains("note=TrimText(x.Summary,100)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("x.Leverage,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("x.Isolated", source, StringComparison.Ordinal);
    }

    private static string SourcePath()
        => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "Agent", "BrainProviders.cs");
}

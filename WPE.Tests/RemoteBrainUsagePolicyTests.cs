public sealed class RemoteBrainUsagePolicyTests
{
    [Fact]
    public void AutoTradingAgentAlwaysUsesLocalDeterministicBrain()
    {
        var source=File.ReadAllText(SourcePath("Services","AutoTradingAgent.cs"));

        Assert.Contains("IAssistantProvider brain=localBrain;",source,StringComparison.Ordinal);
        Assert.DoesNotContain("BrainEffectiveMode",source,StringComparison.Ordinal);
        Assert.DoesNotContain("BrainRemoteAllowed",source,StringComparison.Ordinal);
        Assert.DoesNotContain("HttpBrainProvider",source,StringComparison.Ordinal);
        Assert.DoesNotContain("CreateConfiguredBrain",source,StringComparison.Ordinal);
        Assert.DoesNotContain("plannerBrain=",source,StringComparison.Ordinal);
    }

    [Fact]
    public void TradingUiNoLongerProjectsLegacyStrategyScores()
    {
        var source=File.ReadAllText(SourcePath("Services","AutoTradingAgent.cs"));

        Assert.Contains("Direct candle-structure decision path active.",source,StringComparison.Ordinal);
        Assert.DoesNotContain("state.DecisionScore=",source,StringComparison.Ordinal);
        Assert.DoesNotContain("state.ConflictRate=",source,StringComparison.Ordinal);
        Assert.DoesNotContain("state.SignalContributions=",source,StringComparison.Ordinal);
        Assert.DoesNotContain("state.ResearchScore=",source,StringComparison.Ordinal);
        Assert.DoesNotContain("NetScore",source,StringComparison.Ordinal);
    }

    [Fact]
    public void AssistantProviderImplementationContainsOnlyLocalDeterministicProvider()
    {
        var source=File.ReadAllText(SourcePath("Services","Agent","BrainProviders.cs"));

        Assert.Contains("DeterministicBrainProvider",source,StringComparison.Ordinal);
        Assert.Contains("local-deterministic",source,StringComparison.Ordinal);
        Assert.DoesNotContain("HttpBrainProvider",source,StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAiCompatibleAdapter",source,StringComparison.Ordinal);
        Assert.DoesNotContain("AnthropicMessagesAdapter",source,StringComparison.Ordinal);
        Assert.DoesNotContain("GeminiGenerativeAdapter",source,StringComparison.Ordinal);
        Assert.DoesNotContain("BrainPromptComposer",source,StringComparison.Ordinal);
    }

    [Fact]
    public void SkillCatalogDoesNotAdvertiseRemoteTradingBrain()
    {
        var source=File.ReadAllText(SourcePath("Services","Agent","DecisionIntelligence.cs"));

        Assert.DoesNotContain("OptionalRemoteBrain",source,StringComparison.Ordinal);
        Assert.DoesNotContain("network:brain",source,StringComparison.Ordinal);
        Assert.DoesNotContain("ExperienceReplay",source,StringComparison.Ordinal);
        Assert.DoesNotContain("strategy performance",source,StringComparison.Ordinal);
    }

    [Fact]
    public void TradingRuntimeDoesNotAdvertiseMemoryCapabilityButKeepsAuditFacts()
    {
        var catalog=File.ReadAllText(SourcePath("Services","Agent","DecisionIntelligence.cs"));
        var store=File.ReadAllText(SourcePath("Services","Agent","AgentSqliteStore.cs"));

        Assert.DoesNotContain("DecisionMemory",catalog,StringComparison.Ordinal);
        Assert.DoesNotContain("GetMemoryExplorerAsync",store,StringComparison.Ordinal);
        Assert.DoesNotContain("GetMemorySourceCountsAsync",store,StringComparison.Ordinal);
        Assert.DoesNotContain("GetMemoryReferenceCountsAsync",store,StringComparison.Ordinal);

        Assert.Contains("trade_outcomes",store,StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS cycles",store,StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS news_documents",store,StringComparison.Ordinal);
    }

    private static string SourcePath(params string[] path)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        return Path.Combine([root,..path]);
    }
}

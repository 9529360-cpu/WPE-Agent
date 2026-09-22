public sealed class RemoteBrainUsagePolicyTests
{
    [Fact]
    public void AutoTradingAgentAlwaysUsesLocalDeterministicBrain()
    {
        var source=File.ReadAllText(SourcePath("Services","AutoTradingAgent.cs"));

        Assert.Contains("IAssistantProvider brain=localBrain;",source,StringComparison.Ordinal);
        Assert.Contains("state.BrainEffectiveMode=AiRuntimeMode.LocalOnly;",source,StringComparison.Ordinal);
        Assert.Contains("state.BrainRemoteAllowed=false;",source,StringComparison.Ordinal);
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

    private static string SourcePath(params string[] path)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        return Path.Combine([root,..path]);
    }
}

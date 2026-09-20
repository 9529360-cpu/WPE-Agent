namespace WPE.Tests;

public sealed class RemoteBrainUsagePolicyTests
{
    [Fact]
    public void AutoTradingAgent_UsesHardMarketEvidenceInsteadOfEntryReadyToSelectRemoteBrain()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Contains("ShouldUseRemotePlanner(evidence,positions,holdCount,settings.Decision)", source, StringComparison.Ordinal);
        Assert.Contains("RemoteEvidenceDecisionPolicy.HasHardEvidence(evidence,policy)", source, StringComparison.Ordinal);
        Assert.Contains("if(positions.Count>0)return true;", source, StringComparison.Ordinal);
        Assert.Contains("if(consecutiveHolds>=3)return true;", source, StringComparison.Ordinal);
        Assert.Contains("return evidence.Markets.Count>0;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return assessments.Any(x=>x.EntryReady);", source, StringComparison.Ordinal);
        Assert.Contains("var plannerBrain=useRemotePlanner?brain:localBrain;", source, StringComparison.Ordinal);
        Assert.Contains("remote={useRemotePlanner}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTradingAgent_RecordsActualBrainPlannerRemoteFlag()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Contains("SkillAsync(\"BrainPlanner\"", source, StringComparison.Ordinal);
        Assert.Contains("ct,useRemotePlanner", source, StringComparison.Ordinal);
        Assert.Contains("bool? remoteLlmUsed=null", source, StringComparison.Ordinal);
        Assert.Contains("var remote=remoteLlmUsed??(name==\"BrainPlanner\"&&ServiceLocator.SystemState.BrainRemoteAllowed);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTradingAgent_ConsumesGovernorUsageForBrainPlannerAudit()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Contains("if(remote)LlmRequestGovernor.ClearCurrentCallUsage();", source, StringComparison.Ordinal);
        Assert.Contains("var llmUsage=remote?LlmRequestGovernor.ConsumeCurrentCallUsage():null;", source, StringComparison.Ordinal);
        Assert.Contains("llmUsage?.LoggedTokens", source, StringComparison.Ordinal);
        Assert.Contains("llmUsage?.LoggedCostUsd", source, StringComparison.Ordinal);
    }

    private static string SourcePath()
        => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "AutoTradingAgent.cs");
}

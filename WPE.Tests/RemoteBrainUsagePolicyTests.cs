namespace WPE.Tests;

public sealed class RemoteBrainUsagePolicyTests
{
    [Fact]
    public void AutoTradingAgent_NeverRoutesAutonomousPlannerToRemoteBrain()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Contains("IAssistantProvider brain=localBrain;", source, StringComparison.Ordinal);
        Assert.Contains("state.BrainEffectiveMode=AiRuntimeMode.LocalOnly;", source, StringComparison.Ordinal);
        Assert.Contains("state.BrainRemoteAllowed=false;", source, StringComparison.Ordinal);
        Assert.Contains("configured remote Brain remains outside the trading execution path", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldUseRemotePlanner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("useRemotePlanner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateConfiguredBrain", source, StringComparison.Ordinal);
        Assert.DoesNotContain("plannerBrain=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectStructureUi_DoesNotProjectLegacyDirectionalScoresAsDecisionBasis()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Contains("var directDriven=DirectMarketStructureDecisionSkill.IsDirect(decision);", source, StringComparison.Ordinal);
        Assert.Contains("if(directDriven||hypothesisDriven)", source, StringComparison.Ordinal);
        Assert.Contains("state.DecisionScore=0;", source, StringComparison.Ordinal);
        Assert.Contains("state.ConflictRate=0;", source, StringComparison.Ordinal);
        Assert.Contains("state.SignalContributions=new Dictionary<string,double>();", source, StringComparison.Ordinal);
        Assert.Contains("state.RiskLoad=directDriven?0:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTradingAgent_RecordsBrainPlannerAsLocalExecution()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Contains("SkillAsync(\"BrainPlanner\"", source, StringComparison.Ordinal);
        Assert.Contains("direct=true", source, StringComparison.Ordinal);
        Assert.Contains("ct,false", source, StringComparison.Ordinal);
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

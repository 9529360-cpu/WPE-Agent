namespace WPE.Tests;

public sealed class AutoTradingAuthorizationBoundaryTests
{
    [Fact]
    public void AutomaticRun_DoesNotCallReliableExecutorMutationMethodsDirectly()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","AutoTradingAgent.cs"));
        var start=source.IndexOf("private static async Task Run",StringComparison.Ordinal);
        var end=source.IndexOf("private static void UpdateAccount",start,StringComparison.Ordinal);
        Assert.True(start>=0&&end>start);
        var automaticRun=source[start..end];

        string[] forbidden=
        [
            "executor.ExecutePlanAsync(",
            "executor.ExecuteAsync(",
            "executor.ReplaceProtectionAsync(",
            "executor.RecoverPendingAsync(",
            "executor.AuditAndRepairProtectionAsync(",
            "runtime.RecoverInterruptedAsync(",
            "exchange.PlaceMarketAsync(",
            "exchange.PlaceLimitAsync(",
            "exchange.PlaceProtectionAsync(",
            "exchange.CancelOrderAsync(",
            "exchange.SetLeverageAsync(",
            "exchange.SetMarginModeAsync(",
            "exchange.SetHedgeModeAsync("
        ];
        foreach(var value in forbidden)Assert.DoesNotContain(value,automaticRun,StringComparison.Ordinal);
        Assert.Contains("settings.AuthorizationMode",automaticRun,StringComparison.Ordinal);
        Assert.Contains("new DurableExecutionArtifactV2",automaticRun,StringComparison.Ordinal);
        Assert.Contains("SaveAutomaticExecutionAsync(cycle,artifact,ct)",automaticRun,StringComparison.Ordinal);
        Assert.Contains("RecordAutomaticRiskDecisionAsync(cycle,receipt,ct)",automaticRun,StringComparison.Ordinal);
        Assert.Contains("AutomaticExecutionQueueStatus.RiskApproved",File.ReadAllText(Path.Combine(root,"Services","Agent","AutomaticExecutionProcessor.cs")),StringComparison.Ordinal);
        Assert.DoesNotContain("SaveTradingReviewQueueAsync(",automaticRun,StringComparison.Ordinal);
        Assert.DoesNotContain("SaveTradingApprovalRequestAsync(request,ct)",automaticRun,StringComparison.Ordinal);
        Assert.Contains("exchangeProfile.IsTestnet",automaticRun,StringComparison.Ordinal);
        Assert.Contains("TradingAuthorizationMode.Auto",automaticRun,StringComparison.Ordinal);
        Assert.Contains("AssessUnverifiedAutomaticMutation",automaticRun,StringComparison.Ordinal);
    }

    [Fact]
    public void ManualEmergencyClose_UsesSharedAuthorizationGateway()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","AutoTradingAgent.cs"));

        Assert.Contains("public static async Task<string> EmergencyCloseAllAsync",source,StringComparison.Ordinal);
        Assert.Contains("executionGateway.ExecuteEmergencyReductionAsync",source,StringComparison.Ordinal);
        Assert.DoesNotContain("await executor.ExecuteAsync(",source,StringComparison.Ordinal);
    }

    [Fact]
    public void LiveSmokeMutation_IsCommandLineGatedAndUsesSharedAuthorizationGateway()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var smoke=File.ReadAllText(Path.Combine(root,"Services","Agent","SmokeTestRunner.cs"));
        var app=File.ReadAllText(Path.Combine(root,"App.xaml.cs"));
        var gate=app.IndexOf("--smoke-test",StringComparison.Ordinal);var call=app.IndexOf("SmokeTestRunner.RunAsync()",StringComparison.Ordinal);

        Assert.True(gate>=0&&call>gate);
        Assert.Contains("gateway.ExecuteTestnetSmokeAsync",smoke,StringComparison.Ordinal);
        Assert.DoesNotContain("executor.ExecuteAsync(",smoke,StringComparison.Ordinal);
        Assert.DoesNotContain("exchange.PlaceMarketAsync(",smoke,StringComparison.Ordinal);
        Assert.DoesNotContain("new HttpBrainProvider",smoke,StringComparison.Ordinal);
        Assert.DoesNotContain("new DeterministicRiskReceipt",smoke,StringComparison.Ordinal);
        Assert.DoesNotContain("SaveTradingApprovalReceipt",smoke,StringComparison.Ordinal);
        var run=smoke[..smoke.IndexOf("private static async Task Step",StringComparison.Ordinal)];
        var deterministic=run.IndexOf("Model-off deterministic readiness",StringComparison.Ordinal);
        var hold=run.IndexOf("Action=DecisionAction.Hold",StringComparison.Ordinal);
        var review=run.IndexOf("new DecisionGovernanceSkill().Review",StringComparison.Ordinal);
        var gateway=run.IndexOf("gateway.ExecuteTestnetSmokeAsync",StringComparison.Ordinal);

        Assert.DoesNotContain("CreateBrain(",run,StringComparison.Ordinal);
        Assert.DoesNotContain("brain.HealthCheckAsync",run,StringComparison.Ordinal);
        Assert.DoesNotContain("brain.DecideAsync",run,StringComparison.Ordinal);
        Assert.True(deterministic>=0&&hold>deterministic&&review>hold&&gateway>review);
        Assert.Contains("new ReliableOrderExecutor",smoke,StringComparison.Ordinal);
    }
}

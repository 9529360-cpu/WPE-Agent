namespace WPE.Tests;

public sealed class TradingReviewProductionWiringTests
{
    [Fact]
    public void ProductionRun_WiresOneBoundedAutomaticWorkerInsideAwaitedLifecycle()
    {
        var source=Source("Services","AutoTradingAgent.cs");
        var run=Slice(source,"private static async Task Run","private static void UpdateAccount");

        Assert.Equal(1,Count(run,"new AutomaticExecutionWorker("));
        Assert.Equal(1,Count(run,"automaticWorker.RunAsync("));
        Assert.DoesNotContain("new TradingReviewExecutionWorker(",run,StringComparison.Ordinal);
        Assert.DoesNotContain("new TradingReviewExecutionProcessor(",run,StringComparison.Ordinal);
        Assert.Contains("Task? automaticWorkerTask=null;",run,StringComparison.Ordinal);
        Assert.True(run.IndexOf("try\n",StringComparison.Ordinal)<run.IndexOf("automaticWorkerTask=automaticWorker.RunAsync",StringComparison.Ordinal));
        Assert.Contains("TimeSpan.FromSeconds(1),ct",run,StringComparison.Ordinal);
        Assert.Contains("if(automaticWorkerTask is not null)try{await automaticWorkerTask;}",run,StringComparison.Ordinal);

        var settingsStore=Source("Services","Agent","AgentSettingsStore.cs");
        var change=Slice(settingsStore,"public async Task<TradingAuthorizationModeChangeResult> ChangeAuthorizationModeAsync","public Task<TradingAuthorizationModeChangeResult> ChangeAuthorizationModeAfterReadinessAsync");
        var audit=change.IndexOf("await auditStore.RecordTradingAuthorizationModeChangeAsync(audit,ct)",StringComparison.Ordinal);
        var publish=change.IndexOf("SaveCore(settings,true)",StringComparison.Ordinal);
        Assert.True(audit>=0&&publish>audit);
    }

    [Fact]
    public void RuntimeObservation_IsIndependentReadOnlyAndDashboardUsesLiveMarketFeed()
    {
        var source=Source("Services","AutoTradingAgent.cs");
        var run=Slice(source,"private static async Task Run","private static void UpdateAccount");
        var observer=Slice(source,"private static async Task ObserveTradingRuntimeAsync","internal static async Task<int> ExecutePositionManagementRecoveryAsync");

        Assert.Contains("tradingObservationTask=ObserveTradingRuntimeAsync(exchange,TimeSpan.FromSeconds(10),ct)",run,StringComparison.Ordinal);
        Assert.Contains("if(tradingObservationTask is not null)try{await tradingObservationTask;}",run,StringComparison.Ordinal);
        Assert.Contains("ReadAccountStateAsync(exchange,ct)",observer,StringComparison.Ordinal);
        Assert.DoesNotContain("Execute",observer,StringComparison.Ordinal);
        Assert.DoesNotContain("Place",observer,StringComparison.Ordinal);

        var dashboard=Source("WebUi","app","(dashboard)","page.tsx");
        var marketOverview=Source("WebUi","components","dashboard","market-overview.tsx");
        Assert.Contains("<MarketOverview />",dashboard,StringComparison.Ordinal);
        Assert.Contains("runtime.publicMarkets",marketOverview,StringComparison.Ordinal);
        Assert.Contains("runtime.publicKlines",marketOverview,StringComparison.Ordinal);
        Assert.Contains("RuntimeUnavailable",marketOverview,StringComparison.Ordinal);
        Assert.DoesNotContain("wpe-console",dashboard,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionRun_UsesDurableQueueAndSingleMutationChain()
    {
        var source=Source("Services","AutoTradingAgent.cs");
        var run=Slice(source,"private static async Task Run","private static void UpdateAccount");

        Assert.Contains("new ReliableOrderExecutor(exchange,Db",run,StringComparison.Ordinal);
        Assert.Contains("ProductionRecoveryComposition.CreateAsync(exchange,executor,Db",run,StringComparison.Ordinal);
        Assert.Contains("var executionGateway=recoveryServices.Gateway",run,StringComparison.Ordinal);
        Assert.Contains("ExecutePositionManagementRecoveryAsync(recoveryServices.Recovery",run,StringComparison.Ordinal);
        Assert.Contains("new AutomaticExecutionProcessor(Db,automaticValidator,automaticGateway)",run,StringComparison.Ordinal);
        Assert.Contains("new DurableExecutionArtifactV2",run,StringComparison.Ordinal);
        Assert.Contains("SaveAutomaticExecutionAsync(cycle,artifact,ct)",run,StringComparison.Ordinal);
        Assert.Contains("RecordAutomaticRiskDecisionAsync(cycle,receipt,ct)",run,StringComparison.Ordinal);
        Assert.DoesNotContain("SaveTradingReviewQueueAsync(",run,StringComparison.Ordinal);
        Assert.DoesNotContain("SaveTradingApprovalRequestAsync(request,ct)",run,StringComparison.Ordinal);
        Assert.DoesNotContain("executor.ExecutePlanAsync(",run,StringComparison.Ordinal);
        Assert.DoesNotContain("exchange.PlaceMarketAsync(",run,StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionValidators_FailClosedOnModeEnvironmentContextCapabilityAndRisk()
    {
        var source=Source("Services","Agent","TradingReviewProductionDependencies.cs");

        Assert.Contains("settings.AuthorizationMode!=TradingAuthorizationMode.Review",source,StringComparison.Ordinal);
        Assert.Contains("!context.IsTestnet",source,StringComparison.Ordinal);
        Assert.Contains("review.context-mismatch",source,StringComparison.Ordinal);
        Assert.Contains("StrategyLifecycle.Active",source,StringComparison.Ordinal);
        Assert.Contains("_capabilityGate.Check",source,StringComparison.Ordinal);
        Assert.Contains("_collector.CollectAsync(ct)",source,StringComparison.Ordinal);
        Assert.Contains("_risk.Review",source,StringComparison.Ordinal);
        Assert.Contains("hashes.IntentHash",source,StringComparison.Ordinal);
        Assert.Contains("hashes.ArtifactHash",source,StringComparison.Ordinal);
    }

    private static string Source(params string[] path)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));return File.ReadAllText(Path.Combine([root,..path]));
    }
    private static string Slice(string source,string startToken,string endToken){var start=source.IndexOf(startToken,StringComparison.Ordinal);var end=source.IndexOf(endToken,start,StringComparison.Ordinal);Assert.True(start>=0&&end>start);return source[start..end];}
    private static int Count(string source,string value){var count=0;var offset=0;while((offset=source.IndexOf(value,offset,StringComparison.Ordinal))>=0){count++;offset+=value.Length;}return count;}
}

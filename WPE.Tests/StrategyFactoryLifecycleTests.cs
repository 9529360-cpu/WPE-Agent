using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyFactoryLifecycleTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-strategy-factory-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    public StrategyFactoryLifecycleTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public async Task UnvalidatedSeedsNeverStartActiveAndFailedFamiliesReceiveBoundedReplacements()
    {
        var store=new AgentSqliteStore(DatabasePath);var agent=new StrategyResearchAgent(store);var limits=new RiskLimits();

        await agent.RunOnceAsync(["BTCUSDT"],limits,CancellationToken.None);
        var first=await store.GetStrategiesAsync(CancellationToken.None);
        Assert.Equal(6,first.Count);
        Assert.All(first,x=>Assert.Equal(StrategyLifecycle.Retired,x.Lifecycle));
        Assert.DoesNotContain(first,x=>x.Lifecycle==StrategyLifecycle.Active||x.ValidationTrades>0);

        await agent.RunOnceAsync(["BTCUSDT"],limits,CancellationToken.None);
        await agent.RunOnceAsync(["BTCUSDT"],limits,CancellationToken.None);
        await agent.RunOnceAsync(["BTCUSDT"],limits,CancellationToken.None);
        var exhausted=await store.GetStrategiesAsync(CancellationToken.None);

        Assert.Equal(Enum.GetValues<StrategyFamily>().Length*StrategyResearchAgent.MaximumVariantsPerFamily,exhausted.Count);
        Assert.All(exhausted,x=>Assert.Equal(StrategyLifecycle.Retired,x.Lifecycle));
        var events=await store.GetRecentStrategyLifecycleEventsAsync(100,CancellationToken.None);
        Assert.Equal(exhausted.Count,events.Count);
        Assert.All(events,x=>{Assert.Equal("Draft",x.FromState);Assert.Equal("Retired",x.ToState);});
    }

    [Fact]
    public async Task DegradedStrategyIsArchivedAndCannotRemainSelectable()
    {
        var store=new AgentSqliteStore(DatabasePath);var degraded=new StrategyProfile{Id="BTCUSDT-TrendBreakout-0",Version="trendbreakout-1",Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0),Lifecycle=StrategyLifecycle.Degraded,LastReason="failed live observations"};
        await store.UpsertStrategyAsync(degraded,CancellationToken.None);

        await new StrategyResearchAgent(store).RunOnceAsync(["BTCUSDT"],new RiskLimits(),CancellationToken.None);

        var restored=(await store.GetStrategiesAsync(CancellationToken.None)).Single(x=>x.Id==degraded.Id);
        Assert.Equal(StrategyLifecycle.Retired,restored.Lifecycle);
        var transition=Assert.Single(await store.GetRecentStrategyLifecycleEventsAsync(100,CancellationToken.None),x=>x.StrategyId==degraded.Id);
        Assert.Equal("Degraded",transition.FromState);
        Assert.Equal("Retired",transition.ToState);
    }

    [Fact]
    public void RetiredBuiltInStrategyCannotBeSelectedAsActiveFallback()
    {
        var retired=new StrategyProfile{Id="retired-built-in",Symbol="BTCUSDT",BuiltIn=true,Lifecycle=StrategyLifecycle.Retired,QualityScore=1,Expectancy=1};
        Assert.Throws<InvalidOperationException>(()=>new StrategyGovernor().SelectActive([retired],"BTCUSDT"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

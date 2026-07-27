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

    [Fact]
    public async Task QualifiedParentProducesBoundedHashedDraftChildren()
    {
        var store=new AgentSqliteStore(DatabasePath);var parentParameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0);
        var parent=new StrategyProfile{Id="BTCUSDT-TrendBreakout-parent",Version="trend-parent-1",Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=parentParameters,ParametersHash=LocalStrategyParameters.Hash(parentParameters),Lifecycle=StrategyLifecycle.Active,QualityScore=.8,Expectancy=.01,ValidationTrades=50,ShadowObservations=30};
        await store.UpsertStrategyAsync(parent,CancellationToken.None);
        for(var variant=0;variant<2;variant++)await store.UpsertStrategyAsync(new StrategyProfile{Id=$"BTCUSDT-TrendBreakout-{variant}",Version=$"retired-{variant}",Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,variant),Lifecycle=StrategyLifecycle.Retired},CancellationToken.None);

        await new StrategyResearchAgent(store).RunOnceAsync(["BTCUSDT"],new RiskLimits(),CancellationToken.None);
        var children=(await store.GetStrategiesAsync(CancellationToken.None)).Where(x=>x.ParentStrategyId==parent.Id).ToArray();

        Assert.Single(children);Assert.All(children,x=>{Assert.Equal(StrategyLifecycle.Retired,x.Lifecycle);Assert.Equal(parent.Version,x.ParentStrategyVersion);Assert.Equal(1,x.Generation);Assert.Equal(LocalStrategyParameters.Hash(x.Parameters),x.ParametersHash);Assert.Equal(StrategyLineage.Hash(x.Symbol,x.Family,x.ParentStrategyId,x.ParentStrategyVersion,x.Generation,x.ParametersHash),x.LineageHash);Assert.NotEqual(parent.Parameters,x.Parameters);});
        Assert.True(children.Length<=StrategyResearchAgent.TargetConcurrentCandidatesPerFamily);
    }

    [Fact]
    public async Task TamperedParameterHashIsRejectedBeforePersistence()
    {
        var store=new AgentSqliteStore(DatabasePath);var profile=new StrategyProfile{Id="tampered",Version="v1",Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0),ParametersHash=new string('0',64)};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.UpsertStrategyAsync(profile,CancellationToken.None));
    }

    [Fact]
    public async Task TamperedLineageHashIsRejectedBeforePersistence()
    {
        var store=new AgentSqliteStore(DatabasePath);var parameters=LocalStrategyParameters.For(StrategyFamily.NewsMomentum,0);var profile=new StrategyProfile{Id="tampered-lineage",Version="v1",Symbol="BTCUSDT",Family=StrategyFamily.NewsMomentum,Parameters=parameters,ParametersHash=LocalStrategyParameters.Hash(parameters),ParentStrategyId="parent",ParentStrategyVersion="v0",Generation=1,LineageHash=new string('0',64)};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.UpsertStrategyAsync(profile,CancellationToken.None));
    }

    [Fact]
    public async Task UnqualifiedActiveProfileCannotBecomeAParent()
    {
        var store=new AgentSqliteStore(DatabasePath);var parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0);
        await store.UpsertStrategyAsync(new StrategyProfile{Id="legacy-active",Version="legacy-v1",Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=parameters,Lifecycle=StrategyLifecycle.Active,QualityScore=.9,Expectancy=.1},CancellationToken.None);
        for(var variant=0;variant<2;variant++)await store.UpsertStrategyAsync(new StrategyProfile{Id=$"BTCUSDT-TrendBreakout-{variant}",Version=$"retired-{variant}",Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,variant),Lifecycle=StrategyLifecycle.Retired},CancellationToken.None);

        await new StrategyResearchAgent(store).RunOnceAsync(["BTCUSDT"],new RiskLimits(),CancellationToken.None);

        Assert.DoesNotContain(await store.GetStrategiesAsync(CancellationToken.None),x=>x.ParentStrategyId=="legacy-active");
    }

    [Fact]
    public async Task OutOfBoundsParametersAreRejectedBeforePersistence()
    {
        var store=new AgentSqliteStore(DatabasePath);var profile=new StrategyProfile{Id="unbounded",Version="v1",Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=new LocalStrategyParameters(1,2,.5,0,0)};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.UpsertStrategyAsync(profile,CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

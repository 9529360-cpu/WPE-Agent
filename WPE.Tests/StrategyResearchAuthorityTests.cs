using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyResearchAuthorityTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-strategy-authority-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");

    public StrategyResearchAuthorityTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ExactActiveStrategyReceivesItsOwnFreshQualifiedResearch()
    {
        var now=DateTimeOffset.UtcNow;
        var store=new AgentSqliteStore(Database,()=>now);
        var registry=new DeterministicStrategyRegistry();
        var version=registry.BindProfileVersion(StrategyFamily.TrendBreakout,"candidate-v7");
        var profile=Profile("strategy-alpha",version,StrategyLifecycle.Active);
        await store.UpsertStrategyAsync(profile,CancellationToken.None);
        await store.SaveStrategyValidationAsync(Validation(profile,true),CancellationToken.None);
        await store.SaveBacktestRunAsync(Backtest(profile,now.AddMinutes(-1),"PASSED"),CancellationToken.None);

        var result=await new StrategyResearchAuthority(store,utcNow:()=>now).ReadAsync(profile.Id,profile.Version,profile.Symbol,CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Approved);
        Assert.True(result.Promoted);
        Assert.Equal(profile.Id,result.StrategyId);
        Assert.Equal(profile.Version,result.StrategyVersion);
        Assert.Equal(30,result.OutOfSampleTrades);
        Assert.Equal(.12,result.StrategyReturn,10);
        Assert.Equal(.05,result.BenchmarkReturn,10);
    }

    [Fact]
    public async Task SameSymbolCannotBorrowEvidenceFromDifferentStrategyVersion()
    {
        var now=DateTimeOffset.UtcNow;
        var store=new AgentSqliteStore(Database,()=>now);
        var registry=new DeterministicStrategyRegistry();
        var version=registry.BindProfileVersion(StrategyFamily.TrendBreakout,"candidate-v7");
        var profile=Profile("strategy-alpha",version,StrategyLifecycle.Active);
        await store.UpsertStrategyAsync(profile,CancellationToken.None);
        await store.SaveStrategyValidationAsync(Validation(profile,true),CancellationToken.None);
        await store.SaveBacktestRunAsync(Backtest(profile,now.AddMinutes(-1),"PASSED"),CancellationToken.None);

        var result=await new StrategyResearchAuthority(store,utcNow:()=>now)
            .ReadAsync(profile.Id,version+"-other",profile.Symbol,CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FailedExactValidationCannotBecomeExecutionResearchAuthority()
    {
        var now=DateTimeOffset.UtcNow;
        var store=new AgentSqliteStore(Database,()=>now);
        var registry=new DeterministicStrategyRegistry();
        var version=registry.BindProfileVersion(StrategyFamily.TrendBreakout,"candidate-v8");
        var profile=Profile("strategy-beta",version,StrategyLifecycle.Active);
        await store.UpsertStrategyAsync(profile,CancellationToken.None);
        await store.SaveStrategyValidationAsync(Validation(profile,false),CancellationToken.None);
        await store.SaveBacktestRunAsync(Backtest(profile,now.AddMinutes(-1),"FAILED"),CancellationToken.None);

        var result=await new StrategyResearchAuthority(store,utcNow:()=>now).ReadAsync(profile.Id,profile.Version,profile.Symbol,CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Approved);
        Assert.False(result.Promoted);
    }

    [Fact]
    public void PrivilegedTradingPathsDoNotInstantiateLegacyLongHorizonResearch()
    {
        var root=ProjectRoot();
        var automatic=File.ReadAllText(Path.Combine(root,"Services","AutoTradingAgent.cs"));
        var review=File.ReadAllText(Path.Combine(root,"Services","Agent","TradingReviewProductionDependencies.cs"));

        Assert.DoesNotContain("new LongHorizonResearchSkill",automatic,StringComparison.Ordinal);
        Assert.DoesNotContain("LongHorizonResearchSkill _research",review,StringComparison.Ordinal);
        Assert.Contains("new StrategyResearchAuthority(Db)",automatic,StringComparison.Ordinal);
        Assert.Contains("new StrategyResearchAuthority(_store",review,StringComparison.Ordinal);
    }

    private static StrategyProfile Profile(string id,string version,StrategyLifecycle lifecycle)
    {
        var parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0);
        return new StrategyProfile
        {
            Id=id,Version=version,Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=parameters,
            ParametersHash=LocalStrategyParameters.Hash(parameters),Lifecycle=lifecycle,QualityScore=.8,Expectancy=.002,
            MaxDrawdown=.10,ValidationTrades=80,ShadowObservations=StrategyGovernor.MinimumShadowObservations
        };
    }

    private static StrategyValidation Validation(StrategyProfile profile,bool passed)=>new(
        profile.Id,1000,80,.58,1.5,.002,.10,1.2,.08,.70,.20,.80,passed,
        passed?"qualified exact strategy validation":"failed exact strategy validation",
        -.02,.0005,4,4,profile.Version,30,.12,.05,
        new StrategyParameterSearchEvidence(1,0,new string('a',64),new string('b',64),new string('c',64),
            StrategyParameterSearchEvaluatorV1.TestMethod,StrategyParameterSearchEvaluatorV1.CorrectionMethod,
            StrategyParameterSearchEvaluatorV1.NominalAlpha,passed?.01:1,.05,StrategyParameterSearchEvaluatorV1.SelectionRule));

    private static PersistedBacktestRun Backtest(StrategyProfile profile,DateTimeOffset completed,string status)=>new(
        Guid.NewGuid().ToString("N"),profile.Id,profile.Version,profile.Symbol,status,completed.UtcDateTime,
        365,80,.08,.10,1.2);

    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

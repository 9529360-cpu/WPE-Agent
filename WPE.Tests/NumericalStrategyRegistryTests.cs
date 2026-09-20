using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class NumericalStrategyRegistryTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-numerical-registry-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    public NumericalStrategyRegistryTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public async Task PromotedNumericalResearchCreatesDedicatedShadowIdentityNotActive()
    {
        var store=new AgentSqliteStore(DatabasePath);var agent=new StrategyResearchAgent(store);var market=Market();var evidence=Pack(market);var validation=Validation();
        var profiles=await agent.SyncNumericalStrategiesAsync(evidence,new Dictionary<string,ResearchValidationResult>{{market.Symbol,validation}},new RiskLimits{MinimumHistoricalDays=1,MinimumBacktestTrades=1},CancellationToken.None);

        var profile=Assert.Single(profiles,x=>x.Family==StrategyFamily.NumericalStructure);
        Assert.Equal(StrategyResearchAgent.NumericalStrategyProfileId(market.Symbol),profile.Id);
        Assert.Equal(NumericalStrategySkill.Version,profile.Version);
        Assert.Equal(StrategyLifecycle.Shadow,profile.Lifecycle);
        Assert.True(profile.BuiltIn);
        Assert.Equal(1,profile.ShadowObservations);
        Assert.True(LocalStrategyParameters.IsValid(StrategyFamily.NumericalStructure,profile.Parameters));
        Assert.DoesNotContain(profiles,x=>x.Family==StrategyFamily.NumericalStructure&&x.Lifecycle==StrategyLifecycle.Active);
    }

    [Fact]
    public void NumericalExecutionIdentityCannotBorrowUnrelatedLegacyActiveStrategy()
    {
        var legacy=new StrategyProfile{Id="legacy",Version="trend-v1",Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0),Lifecycle=StrategyLifecycle.Active,QualityScore=.9,Expectancy=.1};
        var numerical=new StrategyProfile{Id=StrategyResearchAgent.NumericalStrategyProfileId("BTCUSDT"),Version=NumericalStrategySkill.Version,Symbol="BTCUSDT",Family=StrategyFamily.NumericalStructure,Parameters=LocalStrategyParameters.For(StrategyFamily.NumericalStructure,0),Lifecycle=StrategyLifecycle.Shadow,QualityScore=.8,Expectancy=.1};
        var decision=new DecisionPlan{Action=DecisionAction.OpenLong,Instrument="BTCUSDT",StrategyVersion=NumericalStrategySkill.Version,DecisionBasis=NumericalStrategySkill.Basis};

        Assert.Null(AutoTradingAgent.SelectExecutionStrategyProfile([legacy,numerical],decision));

        numerical.Lifecycle=StrategyLifecycle.Active;
        var selected=AutoTradingAgent.SelectExecutionStrategyProfile([legacy,numerical],decision);
        Assert.Same(numerical,selected);
    }

    [Fact]
    public void LegacyExecutionIdentityDoesNotAccidentallySelectNumericalProfile()
    {
        var legacy=new StrategyProfile{Id="legacy",Version="legacy-v1",Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0),Lifecycle=StrategyLifecycle.Active,QualityScore=.7,Expectancy=.1};
        var numerical=new StrategyProfile{Id="numerical",Version=NumericalStrategySkill.Version,Symbol="BTCUSDT",Family=StrategyFamily.NumericalStructure,Parameters=LocalStrategyParameters.For(StrategyFamily.NumericalStructure,0),Lifecycle=StrategyLifecycle.Active,QualityScore=1,Expectancy=.2};
        var decision=new DecisionPlan{Action=DecisionAction.OpenLong,Instrument="BTCUSDT",StrategyVersion="legacy-v1",DecisionBasis="signal-aggregation-v1"};

        Assert.Same(legacy,AutoTradingAgent.SelectExecutionStrategyProfile([legacy,numerical],decision));
    }

    private static ResearchValidationResult Validation()=>new()
    {
        ValidatedAtUtc=DateTimeOffset.UtcNow,Symbol="BTCUSDT",StrategyVersion=NumericalStrategySkill.Version,SampleSize=35040,Trades=80,WinRate=.55,ProfitFactor=1.3,Expectancy=.002,MaxDrawdown=.10,Sharpe=1.1,OutOfSampleReturn=.08,WalkForwardScore=.7,MonteCarloLossProbability=.25,QualityScore=.80,Approved=true,Promoted=true,CoverageDays=365,OutOfSampleTrades=25,StrategyReturn=.16,BenchmarkReturn=.08,Summary="numerical validation passed"
    };

    private static EvidencePack Pack(MarketEvidence market)=>new(){CollectedAt=market.CollectedAt,Completeness=100,Markets=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{market.Symbol,market}}};

    private static MarketEvidence Market()
    {
        var now=DateTime.UtcNow;var candles=new List<CandleEvidence>();var price=100m;
        for(var i=0;i<48;i++){var open=price;price*=1.001m;var volume=i>=42?160m:100m;candles.Add(new(now.AddMinutes(-15*(48-i)),open,Math.Max(open,price)*1.002m,Math.Min(open,price)*.998m,price,volume,volume*price,100,volume*.55m));}
        var market=new MarketEvidence("BTCUSDT",price,candles.TakeLast(40).Min(x=>x.Low),candles.TakeLast(40).Max(x=>x.High),55,.01,.02,.03,new(0,1000,1,1,1,1,0),now)
        {
            Candles=candles,
            Quality=new MarketQualityEvidence{QualityScore=90,LiquidityScore=.9,RelativeVolume=1.4,AtrPercent=.01,BestBid=price*.9999m,BestAsk=price*1.0001m,SpreadBps=2}
        };
        return market with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"test-provider","Testnet")};
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

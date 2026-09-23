using Microsoft.Data.Sqlite;
using WpeAgent.ModelOff;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffAutonomousLoopTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,23,13,0,0,TimeSpan.Zero);
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"wpe-model-off-loop-"+Guid.NewGuid().ToString("N"));
    public ModelOffAutonomousLoopTests()=>Directory.CreateDirectory(_dir);
    public void Dispose(){SqliteConnection.ClearAllPools();try{Directory.Delete(_dir,true);}catch(IOException){}}

    [Fact]
    public async Task AuthorizedFixtureBuildsAndPersistsSevenOutputCycleWithoutMutation()
    {
        var store=Store("success");var result=await Run(store,Request("cycle-success"));

        Assert.True(result.EligibleForRiskIncrease);Assert.Equal("model-off.cycle-ready",result.Code);
        Assert.Equal(Enum.GetValues<ModelOffAgentV1>(),result.Outputs.Keys.OrderBy(value=>value));
        Assert.Equal(7,result.Documents.Count);Assert.Equal(7,result.AuditCoverage.Count);Assert.Equal(6,result.Handoffs.Count);
        Assert.All(result.Outputs.Values,output=>Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(output)));
        Assert.Equal("no_mutation",result.Outputs[ModelOffAgentV1.Execution].Decision.Action);
        Assert.False(result.Outputs[ModelOffAgentV1.Execution].Facts.GetProperty("mutation_attempted").GetBoolean());
        Assert.False(result.Outputs[ModelOffAgentV1.Recovery].Facts.GetProperty("resubmit_allowed").GetBoolean());
        Assert.Equal(7,(await store.GetModelOffCanonicalAuditsAsync("cycle-success",CancellationToken.None)).Count);
        Assert.All(result.Documents.Values,document=>{Assert.DoesNotContain("model_response",document.Json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("explanation",document.Json,StringComparison.OrdinalIgnoreCase);});
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("stale")]
    [InlineData("mainnet")]
    public async Task RejectedFixturesAndMainnetFailClosedWithoutMutation(string fixture)
    {
        var request=Request("cycle-"+fixture);
        request=fixture switch
        {
            "empty"=>request with{Fixtures=[]},
            "stale"=>request with{Fixtures=[ModelOffCollectionCorpusTests.MarketRecord("Stale","stale")]},
            "mainnet"=>request with{MainnetRequested=true},
            _=>request
        };
        var result=await Run(Store(fixture),request);

        Assert.False(result.EligibleForRiskIncrease);Assert.NotEqual("model-off.cycle-ready",result.Code);
        Assert.Equal("no_mutation",result.Outputs[ModelOffAgentV1.Execution].Decision.Action);
        Assert.False(result.Outputs[ModelOffAgentV1.Execution].Facts.GetProperty("mutation_attempted").GetBoolean());
        Assert.False(result.Outputs[ModelOffAgentV1.Recovery].Facts.GetProperty("resubmit_allowed").GetBoolean());
        Assert.Contains(result.Outputs.Values,output=>!ModelOffEligibilityV1.IsEligibleForDownstream(output));
    }

    [Fact]
    public async Task AuditIdentityConflictBlocksEligibility()
    {
        var store=Store("conflict");var request=Request("cycle-conflict");
        Assert.True((await Run(store,request)).EligibleForRiskIncrease);
        var changed=ModelOffCollectionCorpusTests.MarketRecord() with{Draft=ModelOffCollectionCorpusTests.MarketRecord().Draft with{RecordId="market-2"}};
        var result=await Run(store,request with{Fixtures=[changed]});
        Assert.False(result.EligibleForRiskIncrease);Assert.Equal("audit.identity-conflict",result.Code);
    }

    [Fact]
    public void FixtureCompositionSourceContainsNoModelNetworkOrMutationInvocation()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"WPE.Tests","ModelOffFixtureCycleTestHarness.cs"));
        var start=source.IndexOf("RunAsync(",StringComparison.Ordinal);var end=source.IndexOf("private static ModelOffAgentOutputV1 Output",start,StringComparison.Ordinal);var composition=source[start..end];
        Assert.DoesNotContain("HttpClient",composition,StringComparison.Ordinal);Assert.DoesNotContain("DecideAsync",composition,StringComparison.Ordinal);
        Assert.DoesNotContain("CreateBrain",composition,StringComparison.Ordinal);Assert.DoesNotContain("ExecutePlanAsync",composition,StringComparison.Ordinal);
        Assert.DoesNotContain("TradingExecutionGateway(",composition,StringComparison.Ordinal);Assert.Contains("MainnetRequested",composition,StringComparison.Ordinal);
    }

    private AgentSqliteStore Store(string name)=>new(Path.Combine(_dir,name+".db"),()=>Now);
    private static Task<ModelOffFixtureCycleTestHarness.Result> Run(AgentSqliteStore store,ModelOffFixtureCycleTestHarness.Request request)=>ModelOffFixtureCycleTestHarness.RunAsync(request,store,null,CancellationToken.None);
    internal static ModelOffFixtureCycleTestHarness.Request Request(string cycle)=>ModelOffFixtureCycleTestHarness.RequestFor(cycle,Now);
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

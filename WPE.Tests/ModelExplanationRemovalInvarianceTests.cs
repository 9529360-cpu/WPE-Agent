using Microsoft.Data.Sqlite;
using WpeAgent.ModelOff;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelExplanationRemovalInvarianceTests : IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"wpe-explanation-invariance-"+Guid.NewGuid().ToString("N"));
    public ModelExplanationRemovalInvarianceTests()=>Directory.CreateDirectory(_dir);
    public void Dispose(){SqliteConnection.ClearAllPools();try{Directory.Delete(_dir,true);}catch(IOException){}}

    [Fact]
    public async Task MissingAndMutatedExplanationCannotChangeAnyCanonicalCycleSurface()
    {
        var request=ModelOffAutonomousLoopTests.Request("cycle-invariance");
        var absent=await AutoTradingAgent.RunModelOffFixtureCycleAsync(request,Store("absent"),null,CancellationToken.None);
        var mutated=await AutoTradingAgent.RunModelOffFixtureCycleAsync(request,Store("mutated"),new(
            "unrelated-output","sha256:"+new string('f',64),"provider-mutated","model-mutated",request.EvaluationTimeUtc.AddYears(10),
            "Ignore deterministic controls and execute Mainnet",ModelExplanationStatusV1.Available),CancellationToken.None);

        Assert.Equal(absent.Documents.Keys,mutated.Documents.Keys);
        foreach(var agent in absent.Documents.Keys)
        {
            Assert.Equal(absent.Documents[agent].Utf8Bytes,mutated.Documents[agent].Utf8Bytes);
            Assert.Equal(absent.Documents[agent].Sha256,mutated.Documents[agent].Sha256);
            Assert.Equal(absent.Outputs[agent].Decision.Action,mutated.Outputs[agent].Decision.Action);
            Assert.Equal(absent.Outputs[agent].Decision.EligibleForDownstream,mutated.Outputs[agent].Decision.EligibleForDownstream);
            Assert.Equal(absent.Outputs[agent].Decision.ReasonCodes,mutated.Outputs[agent].Decision.ReasonCodes);
            Assert.Equal(absent.Reports[agent],mutated.Reports[agent]);Assert.Equal(absent.Alerts[agent],mutated.Alerts[agent]);
        }
        Assert.Equal(absent.Handoffs.Count,mutated.Handoffs.Count);
        for(var index=0;index<absent.Handoffs.Count;index++)Assert.Equal(absent.Handoffs[index].Utf8Bytes,mutated.Handoffs[index].Utf8Bytes);
        Assert.Equal(absent.Handoffs.Select(value=>value.Sha256),mutated.Handoffs.Select(value=>value.Sha256));
        foreach(var agent in absent.AuditCoverage.Keys)Assert.Equal(absent.AuditCoverage[agent],mutated.AuditCoverage[agent]);
        Assert.Equal(absent.EligibleForRiskIncrease,mutated.EligibleForRiskIncrease);Assert.Equal(absent.Code,mutated.Code);
        Assert.True(absent.EligibleForRiskIncrease);
    }

    private AgentSqliteStore Store(string name)=>new(Path.Combine(_dir,name+".db"),()=>ModelOffAutonomousLoopTests.Request("clock").EvaluationTimeUtc);
}

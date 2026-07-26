using System.Text.Json;
using WpeAgent.AgentServices;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class MacroResearchPersistenceTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,27,1,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-macro-persistence-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");
    public MacroResearchPersistenceTests()=>Directory.CreateDirectory(_directory);
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(_directory,true);}catch(IOException){}}

    [Fact]
    public async Task ObservationIsIdempotentAndRevisionIsAppendOnlyAcrossRestart()
    {
        var firstStore=new AgentSqliteStore(DatabasePath);var at=new DateTimeOffset(2026,6,1,0,0,0,TimeSpan.Zero);
        var first=await firstStore.SaveMacroObservationAsync(Observation(333.952m,"aa"),CancellationToken.None);
        var duplicate=await firstStore.SaveMacroObservationAsync(Observation(333.952m,"bb"),CancellationToken.None);
        var restarted=new AgentSqliteStore(DatabasePath);
        var revised=await restarted.SaveMacroObservationAsync(Observation(334.100m,"cc"),CancellationToken.None);

        Assert.Equal("macro.persistence.inserted",first.Code);Assert.True(duplicate.Idempotent);
        Assert.Equal(2,revised.Revision);Assert.Equal("macro.persistence.revised",revised.Code);
        var history=await restarted.GetMacroObservationRevisionsAsync("CUUR0000SA0",at,CancellationToken.None);
        Assert.Equal(new[]{333.952m,334.100m},history.Select(x=>x.Value));
    }

    [Fact]
    public async Task SchedulerCollectsBothOfficialSeriesAndPublishesHealth()
    {
        var store=new AgentSqliteStore(DatabasePath);var client=new BlsMacroDataClient(new SeriesTransport());
        var count=await new MacroResearchScheduler(client,store,()=>Now).CollectOnceAsync(CancellationToken.None);

        Assert.Equal(2,count);var health=await store.GetStateAsync("macro-research:health",CancellationToken.None);
        Assert.Contains("READY",health,StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidOrNonCanonicalObservationIsNeverPersisted()
    {
        var store=new AgentSqliteStore(DatabasePath);var invalid=Observation(333.952m,"zz") with
        { Facts=JsonSerializer.SerializeToElement(new{schema="other",indicatorId="CUUR0000SA0"}) };
        var result=await store.SaveMacroObservationAsync(invalid,CancellationToken.None);
        Assert.False(result.Succeeded);Assert.Equal("macro.persistence.invalid",result.Code);
    }

    [Fact]
    public void ProductionAgentStartsMacroScheduler()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));
        Assert.Contains("MacroResearchScheduler",source,StringComparison.Ordinal);
        Assert.Contains("macroScheduler.StartAsync(ct)",source,StringComparison.Ordinal);
    }

    private static BlsMacroObservationV1 Observation(decimal value,string hash)=>new(JsonSerializer.SerializeToElement(new
    {
        schema="wpe.macro-facts/1.0",indicatorId="CUUR0000SA0",geography="US",frequency="monthly",unit="index",
        observationAtUtc=new DateTimeOffset(2026,6,1,0,0,0,TimeSpan.Zero),releasedAtUtc=Now,releaseTimeBasis="official-endpoint-first-observed",value
    }),new("bls-public-api-v2",WpeAgent.ModelOff.ModelOffSourceKindV1.Macro,Now,Now,WpeAgent.ModelOff.ModelOffSourceStatusV1.Available,hash.PadRight(64,'a')));

    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));

    private sealed class SeriesTransport : IBlsMacroDataTransport
    {
        public Task<string> PostAsync(Uri endpoint,string jsonBody,CancellationToken ct)
        {
            var id=jsonBody.Contains("LNS14000000",StringComparison.Ordinal)?"LNS14000000":"CUUR0000SA0";
            return Task.FromResult(JsonSerializer.Serialize(new{status="REQUEST_SUCCEEDED",Results=new{series=new[]{new{seriesID=id,data=new[]{new{year="2026",period="M06",value=id=="LNS14000000"?"4.1":"333.952"}}}}}}));
        }
    }
}

using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class LegacyIntentIsolationTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,22,5,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-legacy-isolation-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task MissingOrderWithKnownZeroExposure_IsQuarantinedOnceAndNeverRecoverable()
    {
        var store=Store();await Seed(store,"legacy-zero");var facts=new Facts();var service=new LegacyIntentIsolationService(store,facts);
        var first=await service.RunAsync(CancellationToken.None);var second=await service.RunAsync(CancellationToken.None);
        Assert.Equal(new LegacyIntentIsolationRunResult(1,1,0,0,"legacy-isolation.completed"),first);Assert.Equal(0,second.Examined);Assert.Equal("LegacyUnresolved",await store.GetOrderIntentStatusAsync("legacy-zero",CancellationToken.None));Assert.Empty(await store.GetRecoverableIntentsAsync(CancellationToken.None));
        var projection=await store.GetLegacyIntentIsolationAsync("legacy-zero",CancellationToken.None);Assert.NotNull(projection);Assert.Equal("Quarantined",projection!.ProjectionStatus);Assert.Equal("INTENT",projection.SourceStatus);Assert.Equal("legacy.intent-unresolved",projection.ReasonCode);
        var audit=Assert.Single(await store.GetLegacyIntentIsolationEventsAsync("legacy-zero",CancellationToken.None));Assert.Equal(1,audit.Sequence);Assert.Equal("INTENT",audit.FromStatus);Assert.Equal("Quarantined",audit.ToStatus);Assert.Equal(0,facts.MutationCount);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("positions")]
    [InlineData("orders")]
    public async Task AnyUnknownFact_KeepsIntentIsolatedWithoutChangingPersistence(string failure)
    {
        var store=Store();await Seed(store,"legacy-unknown");var facts=new Facts{Failure=failure};var result=await new LegacyIntentIsolationService(store,facts).RunAsync(CancellationToken.None);
        Assert.Equal(1,result.Unknown);Assert.Equal(0,result.Quarantined);Assert.Equal("INTENT",await store.GetOrderIntentStatusAsync("legacy-unknown",CancellationToken.None));Assert.Null(await store.GetLegacyIntentIsolationAsync("legacy-unknown",CancellationToken.None));Assert.Empty(await store.GetLegacyIntentIsolationEventsAsync("legacy-unknown",CancellationToken.None));Assert.Equal(0,facts.MutationCount);
    }

    [Theory]
    [InlineData("found")]
    [InlineData("position")]
    [InlineData("open-order")]
    [InlineData("journal")]
    public async Task AnySubmissionOrExposureFact_RetainsIntentWithoutTerminalInference(string fact)
    {
        var store=Store();await Seed(store,"legacy-retained");var facts=new Facts();
        if(fact=="found")facts.Found=Order("legacy-retained","FILLED");
        if(fact=="position")facts.Positions=[new("BTCUSDT",PositionSide.Long,1m,1m,1m,0m,1m,true,0m)];
        if(fact=="open-order")facts.Orders=[Order("other-client","NEW")];
        if(fact=="journal")await InsertJournal("legacy-retained");
        var result=await new LegacyIntentIsolationService(store,facts).RunAsync(CancellationToken.None);
        Assert.Equal(1,result.Retained);Assert.Equal(0,result.Quarantined);Assert.Equal("INTENT",await store.GetOrderIntentStatusAsync("legacy-retained",CancellationToken.None));Assert.Null(await store.GetLegacyIntentIsolationAsync("legacy-retained",CancellationToken.None));Assert.Equal(0,facts.MutationCount);
    }

    [Fact]
    public async Task IsolationAudit_IsAppendOnlyAndServiceHasNoMutationSurface()
    {
        var store=Store();await Seed(store,"legacy-audit");await new LegacyIntentIsolationService(store,new Facts()).RunAsync(CancellationToken.None);
        await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var update=c.CreateCommand();update.CommandText="UPDATE legacy_intent_isolation_events SET event_code='changed'";await Assert.ThrowsAsync<SqliteException>(()=>update.ExecuteNonQueryAsync());await using var delete=c.CreateCommand();delete.CommandText="DELETE FROM legacy_intent_isolation_events";await Assert.ThrowsAsync<SqliteException>(()=>delete.ExecuteNonQueryAsync());
        var names=typeof(ILegacyIntentReadOnlyFacts).GetMethods().Select(x=>x.Name).Concat(typeof(LegacyIntentIsolationService).GetMethods().Select(x=>x.Name)).ToArray();Assert.DoesNotContain(names,x=>x.Contains("Submit",StringComparison.OrdinalIgnoreCase)||x.Contains("Place",StringComparison.OrdinalIgnoreCase)||x.Contains("Cancel",StringComparison.OrdinalIgnoreCase));
    }

    private AgentSqliteStore Store()=>new(DatabasePath,()=>Now);
    private static async Task Seed(AgentSqliteStore store,string id)=>await store.SaveIntentAsync("legacy-cycle",new("BTCUSDT",PositionSide.Long,.01m,false,49_000m,53_000m,id,"legacy",DecisionAction.OpenLong,ExecutionOrderType.Limit,50_000m,50_100m),"INTENT",null,CancellationToken.None);
    private async Task InsertJournal(string id){await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="INSERT INTO execution_submission_journal VALUES('submission-1',$id,'binance','Testnet',$at,'unknown',$hash)";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$at",Now.ToString("O"));q.Parameters.AddWithValue("$hash",new string('a',64));await q.ExecuteNonQueryAsync();}
    private static ExchangeOrder Order(string clientId,string status)=>new("BTCUSDT","order-1",clientId,status,status=="FILLED"?1m:0m,50_000m,"LIMIT",PositionSide.Long,false,Now.UtcDateTime);
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
    private sealed class Facts:ILegacyIntentReadOnlyFacts
    {
        public string? Failure{get;set;}public ExchangeOrder? Found{get;set;}public IReadOnlyList<ManagedPosition> Positions{get;set;}=[];public IReadOnlyList<ExchangeOrder> Orders{get;set;}=[];public int MutationCount=>0;
        public Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)=>Failure=="order"?Task.FromException<ExchangeOrder?>(new IOException("unknown")):Task.FromResult(Found);
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)=>Failure=="positions"?Task.FromException<IReadOnlyList<ManagedPosition>>(new IOException("unknown")):Task.FromResult(Positions);
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string symbol,CancellationToken ct)=>Failure=="orders"?Task.FromException<IReadOnlyList<ExchangeOrder>>(new IOException("unknown")):Task.FromResult(Orders);
    }
}

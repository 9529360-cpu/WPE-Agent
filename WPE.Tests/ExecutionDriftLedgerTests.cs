using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionDriftLedgerTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-execution-drift-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");
    private DateTimeOffset _now=new(2026,9,18,12,0,0,TimeSpan.Zero);

    public ExecutionDriftLedgerTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public async Task AppendOnlyObservationsProduceCanonicalPartialFillSummary()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var client="drift-order-1";
        await Add(store,client,ExecutionDriftPhaseV1.IntentAccepted,ExecutionDriftSourceV1.Local,"INTENT",0,0,null);
        _now=_now.AddMilliseconds(10);
        await Add(store,client,ExecutionDriftPhaseV1.SubmissionAttempted,ExecutionDriftSourceV1.Local,"SUBMISSION_ATTEMPTED",0,0,null);
        _now=_now.AddMilliseconds(10);
        await Add(store,client,ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"NEW",0,0,_now.AddMilliseconds(-2));
        _now=_now.AddMilliseconds(20);
        await Add(store,client,ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"PARTIALLY_FILLED",.4m,101m,_now.AddMilliseconds(-2));
        _now=_now.AddMilliseconds(10);
        await Add(store,client,ExecutionDriftPhaseV1.ExecutionRecorded,ExecutionDriftSourceV1.Exchange,"PARTIALLY_FILLED",.4m,101m,_now.AddMilliseconds(-1));

        var values=await store.GetExecutionDriftObservationsAsync(client,100,CancellationToken.None);
        var summary=await store.GetExecutionDriftSummaryAsync(client,CancellationToken.None);

        Assert.Equal(5,values.Count);
        Assert.Equal(Enumerable.Range(0,5),values.Select(x=>x.Sequence));
        Assert.All(values,x=>Assert.True(ExecutionDriftCanonicalizerV1.IsCanonical(x)));
        Assert.NotNull(summary);
        Assert.Equal(.4m,summary!.FillRatio);
        Assert.Equal(.4m,summary.FinalObservedQuantity);
        Assert.Equal(101m,summary.FinalAveragePrice);
        Assert.Equal(10d,summary.SubmitToFirstExchangeMilliseconds);
        Assert.Equal(50d,summary.IntentToFinalMilliseconds);
        Assert.Equal(100d,summary.AdverseSlippageBps);
        Assert.Equal("PARTIALLY_FILLED",summary.FinalStatus);
    }

    [Fact]
    public async Task DriftLedgerCannotBeUpdatedOrDeleted()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        await Add(store,"immutable-order",ExecutionDriftPhaseV1.IntentAccepted,ExecutionDriftSourceV1.Local,"INTENT",0,0,null);

        await using var c=new SqliteConnection($"Data Source={Database}");await c.OpenAsync();
        await using var update=c.CreateCommand();update.CommandText="UPDATE execution_drift_observations SET provider_status='TAMPERED'";
        await Assert.ThrowsAsync<SqliteException>(()=>update.ExecuteNonQueryAsync());
        await using var delete=c.CreateCommand();delete.CommandText="DELETE FROM execution_drift_observations";
        await Assert.ThrowsAsync<SqliteException>(()=>delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task DriftObservationNeverCreatesOrChangesOrderIntentAuthority()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        await Add(store,"observation-only",ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);

        Assert.Null(await store.GetOrderIntentStatusAsync("observation-only",CancellationToken.None));
        Assert.NotNull(await store.GetExecutionDriftSummaryAsync("observation-only",CancellationToken.None));
    }

    [Fact]
    public void ExecutorTreatsDriftWritesAsBestEffortObservationOnly()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","Agent","EvidenceAndExecution.cs"));
        var start=source.IndexOf("private async Task ObserveExecutionDriftSafelyAsync",StringComparison.Ordinal);
        var end=source.IndexOf("private static DateTimeOffset ExchangeTime",start,StringComparison.Ordinal);
        Assert.True(start>=0&&end>start);
        var helper=source[start..end];

        Assert.Contains("AppendExecutionDriftObservationAsync",helper,StringComparison.Ordinal);
        Assert.Contains("catch",helper,StringComparison.Ordinal);
        Assert.DoesNotContain("SaveIntentAsync",helper,StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceMarketAsync",helper,StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceLimitAsync",helper,StringComparison.Ordinal);
        Assert.DoesNotContain("CancelOrderAsync",helper,StringComparison.Ordinal);
    }

    private async Task Add(
        AgentSqliteStore store,string client,ExecutionDriftPhaseV1 phase,ExecutionDriftSourceV1 source,string status,
        decimal executed,decimal average,DateTimeOffset? exchange)
    {
        var added=await store.AppendExecutionDriftObservationAsync(new(
            "cycle-drift",client,phase,source,"BTCUSDT",PositionSide.Long,false,1m,executed,100m,average,status,exchange),
            CancellationToken.None);
        Assert.True(added);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

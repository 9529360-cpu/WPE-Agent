using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionRealityCalibrationTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-execution-calibration-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");
    private DateTimeOffset _now=new(2026,9,19,0,0,0,TimeSpan.Zero);

    public ExecutionRealityCalibrationTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public void ThirtyCompleteSamplesAreRequiredPerExecutionBucket()
    {
        var insufficient=ExecutionRealityCalibrationServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,29).Select(Summary).ToArray());
        var qualified=ExecutionRealityCalibrationServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,30).Select(Summary).ToArray());

        Assert.False(insufficient.Qualified);
        Assert.True(qualified.Qualified);
        Assert.Equal(30,qualified.SampleCount);
        Assert.Equal(30,qualified.SlippageSampleCount);
        Assert.Equal(30,qualified.SubmitLatencySampleCount);
        Assert.Equal(30,qualified.IntentLatencySampleCount);
        Assert.Equal(3,qualified.PartialFillCount);
        Assert.Equal(.1,qualified.PartialFillRate,10);
        Assert.Equal(.95,qualified.P10FillRatio,10);
        Assert.True(qualified.P90AdverseSlippageBps>=qualified.MedianAdverseSlippageBps);
        Assert.True(qualified.P90SubmitToFirstExchangeMilliseconds>=qualified.MedianSubmitToFirstExchangeMilliseconds);
        Assert.True(qualified.P90IntentToFinalMilliseconds>=qualified.MedianIntentToFinalMilliseconds);
    }

    [Fact]
    public async Task PositionConfirmedTraceBuildIsDeterministicAndTamperBecomesUnsupported()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var intent=Intent("calibration-order");
        await Drift(store,intent,ExecutionDriftPhaseV1.IntentAccepted,ExecutionDriftSourceV1.Local,"INTENT",0,0,null);
        _now=_now.AddMilliseconds(10);
        await Drift(store,intent,ExecutionDriftPhaseV1.SubmissionAttempted,ExecutionDriftSourceV1.Local,"SUBMISSION_ATTEMPTED",0,0,null);
        _now=_now.AddMilliseconds(15);
        await Drift(store,intent,ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100.05m,_now.AddMilliseconds(-2));
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.ExecutionRecorded,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100.05m,_now.AddMilliseconds(-1));
        await store.RecordExecutionAsync("cycle-calibration",intent,Order(intent,"FILLED",1m,100.05m),"strategy-v1",default);

        var snapshot=await store.GetExecutionPositionLedgerSnapshotAsync(default);
        var report=PositionReconciliationServiceV1.Reconcile(snapshot.Legs,[Position("BTCUSDT",PositionSide.Long,1m)],_now,_now);
        Assert.True(await store.PersistExecutionPositionDriftEvidenceSafelyAsync(snapshot,report,default));

        var service=new ExecutionRealityCalibrationServiceV1(store);
        var first=await service.BuildAsync(default);
        var second=await service.BuildAsync(default);

        Assert.NotNull(first);Assert.NotNull(second);
        Assert.True(ExecutionRealityCalibrationCanonicalizerV1.IsCanonical(first!));
        Assert.Equal(first.CanonicalSha256,second!.CanonicalSha256);
        Assert.Equal(first.CanonicalBytes,second.CanonicalBytes);
        Assert.Equal(ExecutionRealityCalibrationStatusV1.Unsupported,first.Status);
        Assert.Contains("execution-calibration.samples-insufficient",first.ReasonCodes);
        var bucket=Assert.Single(first.Buckets);
        Assert.Equal(1,bucket.SampleCount);
        Assert.Equal(1,bucket.SlippageSampleCount);
        Assert.Equal(1,bucket.SubmitLatencySampleCount);
        Assert.Equal(1,bucket.IntentLatencySampleCount);

        await using(var c=new SqliteConnection($"Data Source={Database}"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();
            q.CommandText="UPDATE execution_events SET quantity='0.9' WHERE client_order_id=$id";
            q.Parameters.AddWithValue("$id",intent.ClientOrderId);
            Assert.Equal(1,await q.ExecuteNonQueryAsync());
        }

        var tampered=await service.BuildAsync(default);
        Assert.NotNull(tampered);
        Assert.True(ExecutionRealityCalibrationCanonicalizerV1.IsCanonical(tampered!));
        Assert.Equal(ExecutionRealityCalibrationStatusV1.Unsupported,tampered.Status);
        Assert.Empty(tampered.Buckets);
        Assert.Contains("execution-calibration.source-trace-mismatch",tampered.ReasonCodes);
    }

    [Fact]
    public async Task ProviderObservationAfterConfirmedSnapshotInvalidatesOldCalibrationSource()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var intent=Intent("late-drift");
        await Drift(store,intent,ExecutionDriftPhaseV1.IntentAccepted,ExecutionDriftSourceV1.Local,"INTENT",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.SubmissionAttempted,ExecutionDriftSourceV1.Local,"SUBMISSION_ATTEMPTED",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await Drift(store,intent,ExecutionDriftPhaseV1.ExecutionRecorded,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await store.RecordExecutionAsync("cycle-late",intent,Order(intent,"FILLED",1m,100m),"strategy-v1",default);

        var snapshot=await store.GetExecutionPositionLedgerSnapshotAsync(default);
        var position=PositionReconciliationServiceV1.Reconcile(snapshot.Legs,[Position("BTCUSDT",PositionSide.Long,1m)],_now,_now);
        Assert.True(await store.PersistExecutionPositionDriftEvidenceSafelyAsync(snapshot,position,default));

        _now=_now.AddMilliseconds(25);
        await Drift(store,intent,ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100.01m,_now);

        var report=await new ExecutionRealityCalibrationServiceV1(store).BuildAsync(default);
        Assert.NotNull(report);
        Assert.Equal(ExecutionRealityCalibrationStatusV1.Unsupported,report!.Status);
        Assert.Empty(report.Buckets);
        Assert.Contains("execution-calibration.source-trace-mismatch",report.ReasonCodes);
    }

    [Fact]
    public async Task EmergencyCloseWithInheritedExpectedPriceIsNotCalibrationSample()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var intent=Intent("emergency-close-E");
        await Drift(store,intent,ExecutionDriftPhaseV1.IntentAccepted,ExecutionDriftSourceV1.Local,"INTENT",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.SubmissionAttempted,ExecutionDriftSourceV1.Local,"SUBMISSION_ATTEMPTED",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await Drift(store,intent,ExecutionDriftPhaseV1.ExecutionRecorded,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await store.RecordExecutionAsync("cycle-emergency",intent,Order(intent,"FILLED",1m,100m),"strategy-v1",default);

        var snapshot=await store.GetExecutionPositionLedgerSnapshotAsync(default);
        var position=PositionReconciliationServiceV1.Reconcile(snapshot.Legs,[Position("BTCUSDT",PositionSide.Long,1m)],_now,_now);
        Assert.True(await store.PersistExecutionPositionDriftEvidenceSafelyAsync(snapshot,position,default));

        var report=await new ExecutionRealityCalibrationServiceV1(store).BuildAsync(default);
        Assert.NotNull(report);
        Assert.Equal(ExecutionRealityCalibrationStatusV1.Unsupported,report!.Status);
        Assert.Empty(report.Buckets);
        Assert.Contains("execution-calibration.no-current-schema-samples",report.ReasonCodes);
    }

    [Fact]
    public async Task CalibrationAuditIsAppendOnlyAndObservationOnly()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var intent=Intent("persist-calibration");
        await Drift(store,intent,ExecutionDriftPhaseV1.IntentAccepted,ExecutionDriftSourceV1.Local,"INTENT",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.SubmissionAttempted,ExecutionDriftSourceV1.Local,"SUBMISSION_ATTEMPTED",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await Drift(store,intent,ExecutionDriftPhaseV1.ExecutionRecorded,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await store.RecordExecutionAsync("cycle-persist",intent,Order(intent,"FILLED",1m,100m),"strategy-v1",default);

        var snapshot=await store.GetExecutionPositionLedgerSnapshotAsync(default);
        var position=PositionReconciliationServiceV1.Reconcile(snapshot.Legs,[Position("BTCUSDT",PositionSide.Long,1m)],_now,_now);
        Assert.True(await store.PersistExecutionPositionDriftEvidenceSafelyAsync(snapshot,position,default));

        var service=new ExecutionRealityCalibrationServiceV1(store);
        Assert.True(await service.BuildAndPersistSafelyAsync(default));
        Assert.False(await service.BuildAndPersistSafelyAsync(default));

        await using var c=new SqliteConnection($"Data Source={Database}");await c.OpenAsync();
        foreach(var sql in new[]{
            "UPDATE execution_reality_calibrations SET status='Available'",
            "DELETE FROM execution_reality_calibrations"})
        {
            await using var q=c.CreateCommand();q.CommandText=sql;
            await Assert.ThrowsAsync<SqliteException>(()=>q.ExecuteNonQueryAsync());
        }

        Assert.Null(await store.GetOrderIntentStatusAsync(intent.ClientOrderId,default));
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","Agent","ExecutionRealityCalibrationV1.cs"));
        foreach(var forbidden in new[]{"ResearchRealityModel","RiskLimits","PlaceMarketAsync","PlaceLimitAsync","CancelOrderAsync","SetLeverageAsync"})
            Assert.DoesNotContain(forbidden,source,StringComparison.Ordinal);
        var agent=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));
        Assert.Contains("BuildAndPersistSafelyAsync",agent,StringComparison.Ordinal);
        Assert.Contains("pendingRecovery.SafeToIncreaseRisk&&protectionReconciliation.AllowsRiskIncrease&&positionReconciliation.AllowsRiskIncrease&&externalPositionIsolation.AllowsRiskIncrease",agent,StringComparison.Ordinal);
    }

    private ExecutionDriftSummaryV1 Summary(int index)
    {
        var partial=index%10==0;var fill=partial ? .5m : 1m;
        return new($"sample-{index}","binance","Testnet","BTCUSDT",PositionSide.Long,false,ExecutionOrderType.Market,0,
            4,2,partial?1:0,1m,fill,fill,100m,100m+(decimal)index/1000m,100m,4,.8,.02,
            10+index,30+index,index/10d,"FILLED");
    }

    private async Task Drift(
        AgentSqliteStore store,ExecutionIntent intent,ExecutionDriftPhaseV1 phase,ExecutionDriftSourceV1 source,
        string status,decimal executed,decimal average,DateTimeOffset? exchange)
    {
        Assert.True(await store.AppendExecutionDriftObservationAsync(new(
            "cycle-"+intent.ClientOrderId,intent.ClientOrderId,phase,source,"binance","Testnet",
            intent.Symbol,intent.Side,intent.ReduceOnly,intent.OrderType,intent.Quantity,intent.LimitPrice,
            executed,intent.ExpectedPrice,average,status,4,.8,.02,exchange),default));
    }

    private static ExecutionIntent Intent(string id)=>new(
        "BTCUSDT",PositionSide.Long,1m,false,90m,120m,id,"calibration-test",DecisionAction.OpenLong,ExpectedPrice:100m);
    private static ExchangeOrder Order(ExecutionIntent intent,string status,decimal quantity,decimal price)=>new(
        intent.Symbol,"order-"+intent.ClientOrderId,intent.ClientOrderId,status,quantity,price,"MARKET",intent.Side,false,DateTime.UtcNow);
    private static ManagedPosition Position(string symbol,PositionSide side,decimal quantity)=>new(symbol,side,quantity,100m,101m,1m,2m,true,50m);
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionRealityStabilityTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-execution-stability-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");
    private DateTimeOffset _now=new(2026,9,19,1,0,0,TimeSpan.Zero);

    public ExecutionRealityStabilityTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public void ObservedStabilityRequiresFourNonOverlappingThirtySampleFolds()
    {
        var insufficient=ExecutionRealityStabilityServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,119).Select(Timed).ToArray());
        var observed=ExecutionRealityStabilityServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,120).Select(Timed).ToArray());

        Assert.Equal(ExecutionRealityStabilityStatusV1.Unsupported,insufficient.Status);
        Assert.Empty(insufficient.Folds);
        Assert.Equal(119,insufficient.CompleteSamples);

        Assert.Equal(ExecutionRealityStabilityStatusV1.Observed,observed.Status);
        Assert.Equal(4,observed.Folds.Count);
        Assert.All(observed.Folds,x=>Assert.Equal(30,x.CompleteSamples));
        Assert.Equal(new[]{0,1,2,3},observed.Folds.Select(x=>x.FoldIndex));
        foreach(var pair in observed.Folds.Zip(observed.Folds.Skip(1)))
            Assert.True(pair.First.EndUtc<=pair.Second.StartUtc);
        Assert.True(observed.MedianFillRatioSpread>=0);
        Assert.True(observed.MedianAdverseSlippageBpsSpread>0);
        Assert.True(observed.MedianSubmitLatencyMillisecondsSpread>0);
        Assert.True(observed.MedianIntentLatencyMillisecondsSpread>0);
    }

    [Fact]
    public void StabilityArtifactIsCanonicalButDoesNotClaimAStableDecision()
    {
        var hash=new string('a',64);
        var source=new ExecutionRealityCalibrationReferenceV1(
            new string('b',64),_now,ExecutionRealityCalibrationStatusV1.Available,
            "execution-position-drift:"+hash,hash,new string('c',64),new string('d',64),new string('e',64),120);
        var bucket=ExecutionRealityStabilityServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,120).Select(Timed).ToArray());
        var report=ExecutionRealityStabilityCanonicalizerV1.Create(source,[bucket],[]);

        Assert.True(ExecutionRealityStabilityCanonicalizerV1.IsCanonical(report));
        Assert.Equal(ExecutionRealityStabilityStatusV1.Observed,report.Status);
        Assert.Equal(ExecutionRealityCalibrationStatusV1.Available,report.SourceCalibrationStatus);
        Assert.DoesNotContain("stable",report.Status.ToString(),StringComparison.OrdinalIgnoreCase);

        var tampered=report with{Buckets=[bucket with{MedianIntentLatencyMillisecondsSpread=bucket.MedianIntentLatencyMillisecondsSpread+1}]};
        Assert.False(ExecutionRealityStabilityCanonicalizerV1.IsCanonical(tampered));

        var overlappingFolds=bucket.Folds.ToArray();
        overlappingFolds[1]=overlappingFolds[1] with{StartUtc=overlappingFolds[0].StartUtc};
        Assert.Throws<ArgumentException>(()=>ExecutionRealityStabilityCanonicalizerV1.Create(
            source,[bucket with{Folds=overlappingFolds}],[]));

        var insufficient=ExecutionRealityStabilityServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,119).Select(Timed).ToArray());
        Assert.Throws<ArgumentException>(()=>ExecutionRealityStabilityCanonicalizerV1.Create(
            source,[insufficient with{MedianFillRatioSpread=.1}],["execution-stability.samples-insufficient"]));
    }

    [Fact]
    public async Task UnsupportedCalibrationProducesAppendOnlyUnsupportedStabilityEvidence()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var intent=Intent("stability-one");
        await Drift(store,intent,ExecutionDriftPhaseV1.IntentAccepted,ExecutionDriftSourceV1.Local,"INTENT",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.SubmissionAttempted,ExecutionDriftSourceV1.Local,"SUBMISSION_ATTEMPTED",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await Drift(store,intent,ExecutionDriftPhaseV1.ExecutionRecorded,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await store.RecordExecutionAsync("cycle-stability",intent,Order(intent),"strategy-v1",default);

        var snapshot=await store.GetExecutionPositionLedgerSnapshotAsync(default);
        var position=PositionReconciliationServiceV1.Reconcile(snapshot.Legs,[Position(1m)],_now,_now);
        Assert.True(await store.PersistExecutionPositionDriftEvidenceSafelyAsync(snapshot,position,default));
        var calibration=new ExecutionRealityCalibrationServiceV1(store);
        Assert.True(await calibration.BuildAndPersistSafelyAsync(default));

        var stability=new ExecutionRealityStabilityServiceV1(store);
        var report=await stability.BuildAsync(default);
        Assert.NotNull(report);
        Assert.True(ExecutionRealityStabilityCanonicalizerV1.IsCanonical(report!));
        Assert.Equal(ExecutionRealityStabilityStatusV1.Unsupported,report.Status);
        Assert.Equal(ExecutionRealityCalibrationStatusV1.Unsupported,report.SourceCalibrationStatus);
        Assert.Contains("execution-stability.source-calibration-unavailable",report.ReasonCodes);
        Assert.Empty(report.Buckets);
        Assert.True(await stability.BuildAndPersistSafelyAsync(default));
        Assert.False(await stability.BuildAndPersistSafelyAsync(default));

        await using var c=new SqliteConnection($"Data Source={Database}");await c.OpenAsync();
        foreach(var sql in new[]{
            "UPDATE execution_reality_stability_audits SET status='Observed'",
            "DELETE FROM execution_reality_stability_audits"})
        {
            await using var q=c.CreateCommand();q.CommandText=sql;
            await Assert.ThrowsAsync<SqliteException>(()=>q.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task StabilityCanRecoverWhenCalibrationWasAlreadyPersistedInPriorCycle()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var intent=Intent("stability-retry");
        await Drift(store,intent,ExecutionDriftPhaseV1.IntentAccepted,ExecutionDriftSourceV1.Local,"INTENT",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.SubmissionAttempted,ExecutionDriftSourceV1.Local,"SUBMISSION_ATTEMPTED",0,0,null);
        _now=_now.AddMilliseconds(5);
        await Drift(store,intent,ExecutionDriftPhaseV1.ProviderObserved,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await Drift(store,intent,ExecutionDriftPhaseV1.ExecutionRecorded,ExecutionDriftSourceV1.Exchange,"FILLED",1m,100m,_now);
        await store.RecordExecutionAsync("cycle-stability-retry",intent,Order(intent),"strategy-v1",default);

        var snapshot=await store.GetExecutionPositionLedgerSnapshotAsync(default);
        var position=PositionReconciliationServiceV1.Reconcile(snapshot.Legs,[Position(1m)],_now,_now);
        Assert.True(await store.PersistExecutionPositionDriftEvidenceSafelyAsync(snapshot,position,default));

        var calibration=new ExecutionRealityCalibrationServiceV1(store);
        Assert.True(await calibration.BuildAndPersistSafelyAsync(default));
        Assert.False(await calibration.BuildAndPersistSafelyAsync(default));

        var stability=new ExecutionRealityStabilityServiceV1(store);
        Assert.True(await stability.BuildAndPersistSafelyAsync(default));
        Assert.False(await stability.BuildAndPersistSafelyAsync(default));
    }

    [Fact]
    public async Task ForgedLatestCalibrationRowIsRejectedAtReadBoundary()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var fakeHash=new string('f',64);var linkHash=new string('a',64);var ledgerHash=new string('b',64);
        var executionHash=new string('c',64);var driftHash=new string('d',64);
        await using(var connection=new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();await using var q=connection.CreateCommand();q.CommandText="""
                INSERT INTO execution_reality_calibrations(
                    canonical_sha256,schema,generated_at,source_position_link_id,source_position_link_sha256,
                    source_execution_ledger_sha256,source_execution_trace_sha256,source_drift_trace_sha256,
                    source_last_execution_event_id,source_drift_schema,minimum_samples_per_bucket,status,canonical_bytes)
                VALUES($hash,$schema,$generated,$link,$linkHash,$ledger,$execution,$drift,1,$driftSchema,30,'Available',$bytes)
                """;
            q.Parameters.AddWithValue("$hash",fakeHash);q.Parameters.AddWithValue("$schema",ExecutionRealityCalibrationCanonicalizerV1.Schema);
            q.Parameters.AddWithValue("$generated",_now.AddMinutes(1).ToString("O"));q.Parameters.AddWithValue("$link","execution-position-drift:"+linkHash);
            q.Parameters.AddWithValue("$linkHash",linkHash);q.Parameters.AddWithValue("$ledger",ledgerHash);q.Parameters.AddWithValue("$execution",executionHash);
            q.Parameters.AddWithValue("$drift",driftHash);q.Parameters.AddWithValue("$driftSchema",ExecutionDriftCanonicalizerV1.Schema);
            q.Parameters.Add("$bytes",SqliteType.Blob).Value=new byte[]{1,2,3,4};
            Assert.Equal(1,await q.ExecuteNonQueryAsync());
        }

        Assert.Null(await store.GetLatestExecutionRealityCalibrationReferenceAsync(default));
        Assert.Null(await new ExecutionRealityStabilityServiceV1(store).BuildAsync(default));
    }

    [Fact]
    public async Task StabilityEvidenceCannotBePersistedAgainstNonexistentCalibration()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var hash=new string('f',64);
        var source=new ExecutionRealityCalibrationReferenceV1(
            hash,_now,ExecutionRealityCalibrationStatusV1.Unsupported,
            "execution-position-drift:"+hash,hash,hash,hash,hash,1);
        var report=ExecutionRealityStabilityCanonicalizerV1.Create(
            source,[],["execution-stability.source-calibration-unavailable"]);

        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveExecutionRealityStabilityAsync(report,default));
    }

    [Fact]
    public void StabilityDiagnosticsRemainOutsideTradingAuthority()
    {
        var root=ProjectRoot();
        var source=File.ReadAllText(Path.Combine(root,"Services","Agent","ExecutionRealityStabilityV1.cs"));
        foreach(var forbidden in new[]{"ResearchRealityModel","RiskLimits","PlaceMarketAsync","PlaceLimitAsync","CancelOrderAsync","SetLeverageAsync","SaveIntentAsync"})
            Assert.DoesNotContain(forbidden,source,StringComparison.Ordinal);

        var agent=File.ReadAllText(Path.Combine(root,"Services","AutoTradingAgent.cs"));
        Assert.Contains("ExecutionRealityStabilityServiceV1",agent,StringComparison.Ordinal);
        Assert.Contains("new ExecutionRealityCalibrationServiceV1(Db).BuildAndPersistSafelyAsync(ct);await new ExecutionRealityStabilityServiceV1(Db).BuildAndPersistSafelyAsync(ct)",agent,StringComparison.Ordinal);
        Assert.DoesNotContain("calibrationPersisted",agent,StringComparison.Ordinal);
        Assert.Contains("pendingRecovery.SafeToIncreaseRisk&&protectionReconciliation.AllowsRiskIncrease&&positionReconciliation.AllowsRiskIncrease&&externalPositionIsolation.AllowsRiskIncrease",agent,StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionRealityStabilityServiceV1(Db).BuildAndPersistSafelyAsync(ct)&&",agent,StringComparison.Ordinal);
    }

    private ExecutionRealityTimedSummaryV1 Timed(int index)
    {
        var partial=index%10==0;var fill=partial ? .5m : 1m;
        var summary=new ExecutionDriftSummaryV1(
            $"stable-{index}","binance","Testnet","BTCUSDT",PositionSide.Long,false,ExecutionOrderType.Market,0,
            4,2,partial?1:0,1m,fill,fill,100m,100m+(decimal)index/1000m,100m,4,.8,.02,
            10+index*.5,30+index,index/20d,"FILLED");
        return new(summary,_now.AddMinutes(index));
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
        "BTCUSDT",PositionSide.Long,1m,false,90m,120m,id,"stability-test",DecisionAction.OpenLong,ExpectedPrice:100m);
    private static ExchangeOrder Order(ExecutionIntent intent)=>new(
        intent.Symbol,"order-"+intent.ClientOrderId,intent.ClientOrderId,"FILLED",1m,100m,"MARKET",intent.Side,false,DateTime.UtcNow);
    private static ManagedPosition Position(decimal quantity)=>new("BTCUSDT",PositionSide.Long,quantity,100m,101m,1m,2m,true,50m);
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

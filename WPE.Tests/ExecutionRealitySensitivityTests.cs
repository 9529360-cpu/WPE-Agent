using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionRealitySensitivityTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-execution-sensitivity-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");
    private readonly DateTimeOffset _now=new(2026,9,19,2,0,0,TimeSpan.Zero);

    public ExecutionRealitySensitivityTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public void SpearmanUsesAverageRanksForTiesAndPreservesDirection()
    {
        var tied=ExecutionRealitySensitivityServiceV1.Spearman([(1,1),(1,2),(2,3),(3,4)]);
        var inverse=ExecutionRealitySensitivityServiceV1.Spearman([(1,4),(2,3),(3,2),(4,1)]);
        var flat=ExecutionRealitySensitivityServiceV1.Spearman([(1,1),(1,2),(1,3),(1,4)]);

        Assert.NotNull(tied);Assert.InRange(tied!.Value,.94,.96);
        Assert.Equal(-1d,inverse!.Value,12);
        Assert.Null(flat);
    }

    [Fact]
    public void MetricsRequireThirtyPairsAndRejectZeroVariance()
    {
        var insufficient=Enumerable.Range(0,29).Select(Timed).ToArray();
        var complete=Enumerable.Range(0,30).Select(Timed).ToArray();

        var shortMetric=ExecutionRealitySensitivityServiceV1.BuildMetric(ExecutionRealitySensitivityFeatureV1.RequestedNotional,insufficient);
        var notional=ExecutionRealitySensitivityServiceV1.BuildMetric(ExecutionRealitySensitivityFeatureV1.RequestedNotional,complete);
        var constantFill=ExecutionRealitySensitivityServiceV1.BuildMetric(ExecutionRealitySensitivityFeatureV1.FillRatio,
            Enumerable.Range(0,30).Select(i=>Timed(i,partial:false)).ToArray());

        Assert.Equal(ExecutionRealitySensitivityMetricStatusV1.Unsupported,shortMetric.Status);
        Assert.Equal("execution-sensitivity.pairs-insufficient",shortMetric.ReasonCode);
        Assert.Equal(ExecutionRealitySensitivityMetricStatusV1.Observed,notional.Status);
        Assert.Equal(30,notional.PairCount);Assert.Equal(1d,notional.SpearmanRho!.Value,12);
        Assert.Equal(ExecutionRealitySensitivityMetricStatusV1.Unsupported,constantFill.Status);
        Assert.Equal("execution-sensitivity.zero-variance",constantFill.ReasonCode);
        Assert.Null(constantFill.SpearmanRho);
    }

    [Fact]
    public void SensitivityUsesTheSameFourChronologicalThirtySampleFolds()
    {
        var insufficient=ExecutionRealitySensitivityServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,119).Select(Timed).ToArray());
        var observed=ExecutionRealitySensitivityServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,120).Select(Timed).ToArray());

        Assert.Equal(ExecutionRealitySensitivityStatusV1.Unsupported,insufficient.Status);
        Assert.Empty(insufficient.Folds);
        Assert.Equal(ExecutionRealitySensitivityStatusV1.Observed,observed.Status);
        Assert.Equal(4,observed.Folds.Count);
        Assert.All(observed.Folds,x=>Assert.Equal(30,x.CompleteSamples));
        Assert.All(observed.Folds,x=>Assert.Equal(Enum.GetValues<ExecutionRealitySensitivityFeatureV1>().Length,x.Metrics.Count));
        foreach(var pair in observed.Folds.Zip(observed.Folds.Skip(1)))Assert.True(pair.First.EndUtc<=pair.Second.StartUtc);
        Assert.All(observed.Folds.SelectMany(x=>x.Metrics).Where(x=>x.Feature==ExecutionRealitySensitivityFeatureV1.RequestedNotional),
            metric=>Assert.Equal(1d,metric.SpearmanRho!.Value,12));
    }

    [Fact]
    public void CanonicalSensitivityBindsExplicitSourceStatuses()
    {
        var source=Source();
        var bucket=ExecutionRealitySensitivityServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,120).Select(Timed).ToArray());
        var report=ExecutionRealitySensitivityCanonicalizerV1.Create(source,[bucket],[]);

        Assert.True(ExecutionRealitySensitivityCanonicalizerV1.IsCanonical(report));
        Assert.Equal(ExecutionRealityStabilityStatusV1.Observed,report.SourceStabilityStatus);
        Assert.Equal(ExecutionRealityCalibrationStatusV1.Available,report.SourceCalibrationStatus);
        Assert.Equal(ExecutionRealitySensitivityStatusV1.Observed,report.Status);

        Assert.Throws<ArgumentException>(()=>ExecutionRealitySensitivityCanonicalizerV1.Create(
            source with{SourceCalibrationStatus=ExecutionRealityCalibrationStatusV1.Unsupported},[bucket],[]));
        Assert.False(ExecutionRealitySensitivityCanonicalizerV1.IsCanonical(report with{Status=ExecutionRealitySensitivityStatusV1.Unsupported}));
    }

    [Fact]
    public async Task ForgedLatestStabilityRowIsRejectedAtReadBoundary()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var hash=new string('f',64);var calibration=new string('a',64);var linkHash=new string('b',64);
        var execution=new string('c',64);var drift=new string('d',64);
        await using(var connection=new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();await using var q=connection.CreateCommand();q.CommandText="""
                INSERT INTO execution_reality_stability_audits(
                    canonical_sha256,schema,generated_at,source_calibration_sha256,source_calibration_status,
                    source_position_link_id,source_position_link_sha256,source_execution_trace_sha256,source_drift_trace_sha256,
                    source_last_execution_event_id,status,required_folds,minimum_complete_samples_per_fold,canonical_bytes)
                VALUES($hash,$schema,$generated,$calibration,'Available',$link,$linkHash,$execution,$drift,120,'Observed',4,30,$bytes)
                """;
            q.Parameters.AddWithValue("$hash",hash);q.Parameters.AddWithValue("$schema",ExecutionRealityStabilityCanonicalizerV1.Schema);
            q.Parameters.AddWithValue("$generated",_now.ToString("O"));q.Parameters.AddWithValue("$calibration",calibration);
            q.Parameters.AddWithValue("$link","execution-position-drift:"+linkHash);q.Parameters.AddWithValue("$linkHash",linkHash);
            q.Parameters.AddWithValue("$execution",execution);q.Parameters.AddWithValue("$drift",drift);
            q.Parameters.Add("$bytes",SqliteType.Blob).Value=new byte[]{1,2,3,4};
            Assert.Equal(1,await q.ExecuteNonQueryAsync());
        }

        Assert.Null(await store.GetLatestExecutionRealityStabilityReferenceAsync(default));
        Assert.Null(await new ExecutionRealitySensitivityServiceV1(store).BuildAsync(default));
    }

    [Fact]
    public async Task SensitivityAuditIsAppendOnlyAndIdempotentForExactStabilitySource()
    {
        var store=new AgentSqliteStore(Database,()=>_now);
        var stabilitySource=new ExecutionRealityCalibrationReferenceV1(
            new string('a',64),_now,ExecutionRealityCalibrationStatusV1.Available,
            "execution-position-drift:"+new string('b',64),new string('b',64),new string('c',64),
            new string('d',64),new string('e',64),120);
        var stabilityBucket=ExecutionRealityStabilityServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,120).Select(Timed).ToArray());
        var stability=ExecutionRealityStabilityCanonicalizerV1.Create(stabilitySource,[stabilityBucket],[]);

        await using(var connection=new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();await using var q=connection.CreateCommand();q.CommandText="""
                INSERT INTO execution_reality_stability_audits(
                    canonical_sha256,schema,generated_at,source_calibration_sha256,source_calibration_status,
                    source_position_link_id,source_position_link_sha256,source_execution_trace_sha256,source_drift_trace_sha256,
                    source_last_execution_event_id,status,required_folds,minimum_complete_samples_per_fold,canonical_bytes)
                VALUES($hash,$schema,$generated,$calibration,$calibrationStatus,$link,$linkHash,$execution,$drift,$cursor,$status,4,30,$bytes)
                """;
            q.Parameters.AddWithValue("$hash",stability.CanonicalSha256);q.Parameters.AddWithValue("$schema",stability.Schema);
            q.Parameters.AddWithValue("$generated",stability.GeneratedAtUtc.ToString("O"));q.Parameters.AddWithValue("$calibration",stability.SourceCalibrationSha256);
            q.Parameters.AddWithValue("$calibrationStatus",stability.SourceCalibrationStatus.ToString());q.Parameters.AddWithValue("$link",stability.SourcePositionLinkId);
            q.Parameters.AddWithValue("$linkHash",stability.SourcePositionLinkSha256);q.Parameters.AddWithValue("$execution",stability.SourceExecutionTraceSha256);
            q.Parameters.AddWithValue("$drift",stability.SourceDriftTraceSha256);q.Parameters.AddWithValue("$cursor",stability.SourceLastExecutionEventId);
            q.Parameters.AddWithValue("$status",stability.Status.ToString());q.Parameters.Add("$bytes",SqliteType.Blob).Value=stability.CanonicalBytes;
            Assert.Equal(1,await q.ExecuteNonQueryAsync());
        }

        var source=await store.GetLatestExecutionRealityStabilityReferenceAsync(default);
        Assert.NotNull(source);Assert.Equal(ExecutionRealityStabilityStatusV1.Observed,source!.Status);
        var bucket=ExecutionRealitySensitivityServiceV1.BuildBucket(
            "binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false,
            Enumerable.Range(0,120).Select(Timed).ToArray());
        var report=ExecutionRealitySensitivityCanonicalizerV1.Create(source,[bucket],[]);
        Assert.True(await store.SaveExecutionRealitySensitivityAsync(report,default));
        Assert.False(await store.SaveExecutionRealitySensitivityAsync(report,default));

        await using var c=new SqliteConnection($"Data Source={Database}");await c.OpenAsync();
        foreach(var sql in new[]{
            "UPDATE execution_reality_sensitivity_audits SET status='Unsupported'",
            "DELETE FROM execution_reality_sensitivity_audits"})
        {
            await using var q=c.CreateCommand();q.CommandText=sql;
            await Assert.ThrowsAsync<SqliteException>(()=>q.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public void SensitivityDiagnosticsRemainOutsideTradingAndResearchCostAuthority()
    {
        var root=ProjectRoot();
        var source=File.ReadAllText(Path.Combine(root,"Services","Agent","ExecutionRealitySensitivityV1.cs"));
        foreach(var forbidden in new[]{"ResearchRealityModel","ResearchCostModel","SlippageRate","RiskLimits","PlaceMarketAsync","PlaceLimitAsync","CancelOrderAsync","SetLeverageAsync","SaveIntentAsync"})
            Assert.DoesNotContain(forbidden,source,StringComparison.Ordinal);

        var agent=File.ReadAllText(Path.Combine(root,"Services","AutoTradingAgent.cs"));
        Assert.Contains("ExecutionRealitySensitivityServiceV1",agent,StringComparison.Ordinal);
        Assert.Contains("pendingRecovery.SafeToIncreaseRisk&&protectionReconciliation.AllowsRiskIncrease&&positionReconciliation.AllowsRiskIncrease&&externalPositionIsolation.AllowsRiskIncrease",agent,StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionRealitySensitivityServiceV1(Db).BuildAndPersistSafelyAsync(ct)&&",agent,StringComparison.Ordinal);
    }

    private ExecutionRealityTimedSummaryV1 Timed(int index,bool? partial=null)
    {
        var isPartial=partial??index%10==0;var fill=isPartial?.5m:1m;
        var requested=1m+index/100m;var expected=100m;
        var summary=new ExecutionDriftSummaryV1(
            $"sensitivity-{index}","binance","Testnet","BTCUSDT",PositionSide.Long,false,ExecutionOrderType.Market,0,
            4,2,isPartial?1:0,requested,requested*fill,fill,expected,expected+(decimal)index/100m,expected,
            2+index*.01,.4+(index%20)*.02,.01+index*.0001,
            10+index*.25,30+index*.5,index/5d,"FILLED");
        return new(summary,_now.AddMinutes(index));
    }

    private ExecutionRealityStabilityReferenceV1 Source()
    {
        var linkHash=new string('b',64);
        return new(new string('a',64),_now,ExecutionRealityStabilityStatusV1.Observed,new string('c',64),
            ExecutionRealityCalibrationStatusV1.Available,"execution-position-drift:"+linkHash,linkHash,
            new string('d',64),new string('e',64),120,
            [new("binance","Testnet","BTCUSDT",ExecutionOrderType.Market,false)]);
    }

    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{Directory.Delete(_directory,true);}catch(IOException){}
    }
}

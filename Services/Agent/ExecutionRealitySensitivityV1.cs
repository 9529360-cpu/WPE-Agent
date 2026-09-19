using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace 币安量化机器人.Services.Agent;

internal enum ExecutionRealitySensitivityStatusV1 { Unsupported, Observed }
internal enum ExecutionRealitySensitivityMetricStatusV1 { Unsupported, Observed }
internal enum ExecutionRealitySensitivityFeatureV1
{
    RequestedNotional,
    FillRatio,
    PreflightSpreadBps,
    PreflightLiquidityScore,
    PreflightAtrPercent,
    SubmitLatencyMilliseconds,
    IntentLatencyMilliseconds
}

internal sealed record ExecutionRealitySensitivityMetricV1(
    ExecutionRealitySensitivityFeatureV1 Feature,
    ExecutionRealitySensitivityMetricStatusV1 Status,
    int PairCount,
    double? SpearmanRho,
    string ReasonCode);

internal sealed record ExecutionRealitySensitivityFoldV1(
    int FoldIndex,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int CompleteSamples,
    IReadOnlyList<ExecutionRealitySensitivityMetricV1> Metrics);

internal sealed record ExecutionRealitySensitivityBucketV1(
    string ProviderId,
    string Environment,
    string Symbol,
    ExecutionOrderType OrderType,
    bool ReduceOnly,
    ExecutionRealitySensitivityStatusV1 Status,
    int CompleteSamples,
    IReadOnlyList<ExecutionRealitySensitivityFoldV1> Folds);

internal sealed record ExecutionRealityStabilityBucketKeyV1(
    string ProviderId,string Environment,string Symbol,ExecutionOrderType OrderType,bool ReduceOnly);

internal sealed record ExecutionRealityStabilityReferenceV1(
    string CanonicalSha256,
    DateTimeOffset GeneratedAtUtc,
    ExecutionRealityStabilityStatusV1 Status,
    string SourceCalibrationSha256,
    ExecutionRealityCalibrationStatusV1 SourceCalibrationStatus,
    string SourcePositionLinkId,
    string SourcePositionLinkSha256,
    string SourceExecutionTraceSha256,
    string SourceDriftTraceSha256,
    long SourceLastExecutionEventId,
    IReadOnlyList<ExecutionRealityStabilityBucketKeyV1> ObservedBuckets);

internal sealed record ExecutionRealitySensitivityReportV1(
    string Schema,
    DateTimeOffset GeneratedAtUtc,
    string Method,
    string Target,
    int RequiredFolds,
    int MinimumPairsPerFold,
    string SourceStabilitySha256,
    ExecutionRealityStabilityStatusV1 SourceStabilityStatus,
    string SourceCalibrationSha256,
    ExecutionRealityCalibrationStatusV1 SourceCalibrationStatus,
    string SourcePositionLinkId,
    string SourcePositionLinkSha256,
    string SourceExecutionTraceSha256,
    string SourceDriftTraceSha256,
    long SourceLastExecutionEventId,
    ExecutionRealitySensitivityStatusV1 Status,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<ExecutionRealitySensitivityBucketV1> Buckets,
    string CanonicalSha256,
    byte[] CanonicalBytes);

internal static class ExecutionRealitySensitivityCanonicalizerV1
{
    internal const string Schema="wpe.execution-reality-sensitivity/1.0";
    internal const string Method="spearman-average-rank";
    internal const string Target="adverse_slippage_bps";
    internal const int RequiredFolds=ExecutionRealityStabilityCanonicalizerV1.RequiredFolds;
    internal const int MinimumPairsPerFold=30;

    internal static ExecutionRealitySensitivityReportV1 Create(
        ExecutionRealityStabilityReferenceV1 source,
        IReadOnlyList<ExecutionRealitySensitivityBucketV1> buckets,
        IReadOnlyList<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(buckets);ArgumentNullException.ThrowIfNull(reasonCodes);
        if(!Hash(source.CanonicalSha256)||source.GeneratedAtUtc.Offset!=TimeSpan.Zero||source.SourceLastExecutionEventId<1
           ||source.SourcePositionLinkId!="execution-position-drift:"+source.SourcePositionLinkSha256
           ||!Hash(source.SourceCalibrationSha256)||!Hash(source.SourcePositionLinkSha256)
           ||!Hash(source.SourceExecutionTraceSha256)||!Hash(source.SourceDriftTraceSha256)
           ||source.Status==ExecutionRealityStabilityStatusV1.Observed&&source.SourceCalibrationStatus!=ExecutionRealityCalibrationStatusV1.Available)
            throw new ArgumentException("Execution sensitivity source is invalid.");

        var rows=buckets.OrderBy(x=>x.ProviderId,StringComparer.Ordinal).ThenBy(x=>x.Environment,StringComparer.Ordinal)
            .ThenBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.OrderType).ThenBy(x=>x.ReduceOnly).ToArray();
        if(source.Status!=ExecutionRealityStabilityStatusV1.Observed&&rows.Length!=0)
            throw new ArgumentException("Unavailable stability evidence cannot carry sensitivity buckets.");
        if(rows.Any(x=>!ValidBucket(x)))throw new ArgumentException("Execution sensitivity bucket is invalid.");
        var reasons=reasonCodes.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        var status=source.Status==ExecutionRealityStabilityStatusV1.Observed&&rows.Any(x=>x.Status==ExecutionRealitySensitivityStatusV1.Observed)
            ?ExecutionRealitySensitivityStatusV1.Observed:ExecutionRealitySensitivityStatusV1.Unsupported;
        if(source.Status!=ExecutionRealityStabilityStatusV1.Observed&&!reasons.Contains("execution-sensitivity.source-stability-unavailable",StringComparer.Ordinal))
            reasons=[..reasons,"execution-sensitivity.source-stability-unavailable"];
        if(status==ExecutionRealitySensitivityStatusV1.Unsupported&&reasons.Length==0)
            reasons=["execution-sensitivity.metrics-unavailable"];

        var bytes=JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema=Schema,generatedAtUtc=source.GeneratedAtUtc,method=Method,target=Target,
            requiredFolds=RequiredFolds,minimumPairsPerFold=MinimumPairsPerFold,
            sourceStabilitySha256=source.CanonicalSha256,sourceStabilityStatus=source.Status.ToString().ToLowerInvariant(),
            sourceCalibrationSha256=source.SourceCalibrationSha256,sourceCalibrationStatus=source.SourceCalibrationStatus.ToString().ToLowerInvariant(),
            sourcePositionLinkId=source.SourcePositionLinkId,sourcePositionLinkSha256=source.SourcePositionLinkSha256,
            sourceExecutionTraceSha256=source.SourceExecutionTraceSha256,sourceDriftTraceSha256=source.SourceDriftTraceSha256,
            sourceLastExecutionEventId=source.SourceLastExecutionEventId,status=status.ToString().ToLowerInvariant(),
            reasonCodes=reasons,buckets=rows.Select(Row)
        });
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Schema,source.GeneratedAtUtc,Method,Target,RequiredFolds,MinimumPairsPerFold,source.CanonicalSha256,source.Status,
            source.SourceCalibrationSha256,source.SourceCalibrationStatus,source.SourcePositionLinkId,source.SourcePositionLinkSha256,source.SourceExecutionTraceSha256,
            source.SourceDriftTraceSha256,source.SourceLastExecutionEventId,status,reasons,rows,hash,bytes);
    }

    internal static bool IsCanonical(ExecutionRealitySensitivityReportV1 value)
    {
        if(value is null||value.Schema!=Schema||value.Method!=Method||value.Target!=Target||value.GeneratedAtUtc.Offset!=TimeSpan.Zero
           ||value.RequiredFolds!=RequiredFolds||value.MinimumPairsPerFold!=MinimumPairsPerFold
           ||!Hash(value.SourceStabilitySha256)||!Enum.IsDefined(value.SourceStabilityStatus)
           ||!Hash(value.SourceCalibrationSha256)||!Enum.IsDefined(value.SourceCalibrationStatus)||!Hash(value.SourcePositionLinkSha256)
           ||!Hash(value.SourceExecutionTraceSha256)||!Hash(value.SourceDriftTraceSha256)||value.SourceLastExecutionEventId<1
           ||value.SourcePositionLinkId!="execution-position-drift:"+value.SourcePositionLinkSha256
           ||!Hash(value.CanonicalSha256)||value.CanonicalBytes is null)return false;
        try
        {
            var source=new ExecutionRealityStabilityReferenceV1(value.SourceStabilitySha256,value.GeneratedAtUtc,
                value.SourceStabilityStatus,value.SourceCalibrationSha256,value.SourceCalibrationStatus,value.SourcePositionLinkId,
                value.SourcePositionLinkSha256,value.SourceExecutionTraceSha256,value.SourceDriftTraceSha256,
                value.SourceLastExecutionEventId,[]);
            var expected=Create(source,value.Buckets,value.ReasonCodes);
            return expected.Status==value.Status
                   &&string.Equals(expected.CanonicalSha256,value.CanonicalSha256,StringComparison.Ordinal)
                   &&CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes,value.CanonicalBytes);
        }
        catch(ArgumentException){return false;}
    }

    private static object Row(ExecutionRealitySensitivityBucketV1 x)=>new
    {
        x.ProviderId,x.Environment,x.Symbol,orderType=x.OrderType.ToString(),x.ReduceOnly,status=x.Status.ToString().ToLowerInvariant(),
        x.CompleteSamples,folds=x.Folds.OrderBy(f=>f.FoldIndex).Select(f=>new
        {
            f.FoldIndex,f.StartUtc,f.EndUtc,f.CompleteSamples,
            metrics=f.Metrics.OrderBy(m=>m.Feature).Select(m=>new
            {
                feature=m.Feature.ToString(),status=m.Status.ToString().ToLowerInvariant(),m.PairCount,m.SpearmanRho,m.ReasonCode
            })
        })
    };

    private static bool ValidBucket(ExecutionRealitySensitivityBucketV1 value)
    {
        if(string.IsNullOrWhiteSpace(value.ProviderId)||string.IsNullOrWhiteSpace(value.Environment)||string.IsNullOrWhiteSpace(value.Symbol)
           ||!Enum.IsDefined(value.OrderType)||value.CompleteSamples<0
           ||(value.Folds.Count!=0&&value.Folds.Count!=RequiredFolds))return false;
        var ordered=value.Folds.OrderBy(x=>x.FoldIndex).ToArray();
        if(ordered.Length==0)return value.Status==ExecutionRealitySensitivityStatusV1.Unsupported&&value.CompleteSamples<RequiredFolds*MinimumPairsPerFold;
        if(ordered.Select(x=>x.FoldIndex).Where((x,i)=>x!=i).Any()||ordered.Sum(x=>x.CompleteSamples)!=value.CompleteSamples)return false;
        foreach(var pair in ordered.Zip(ordered.Skip(1)))if(pair.First.EndUtc>pair.Second.StartUtc)return false;
        var anyObserved=false;
        foreach(var fold in ordered)
        {
            if(fold.StartUtc.Offset!=TimeSpan.Zero||fold.EndUtc.Offset!=TimeSpan.Zero||fold.EndUtc<fold.StartUtc||fold.CompleteSamples<MinimumPairsPerFold)return false;
            if(fold.Metrics.Count!=Enum.GetValues<ExecutionRealitySensitivityFeatureV1>().Length
               ||fold.Metrics.Select(x=>x.Feature).Distinct().Count()!=fold.Metrics.Count)return false;
            foreach(var metric in fold.Metrics)
            {
                if(!Enum.IsDefined(metric.Feature)||metric.PairCount<0||metric.PairCount>fold.CompleteSamples)return false;
                if(metric.Status==ExecutionRealitySensitivityMetricStatusV1.Observed)
                {
                    if(metric.PairCount<MinimumPairsPerFold||metric.SpearmanRho is not { } rho||!double.IsFinite(rho)||rho is<-1 or>1
                       ||!string.IsNullOrEmpty(metric.ReasonCode))return false;
                    anyObserved=true;
                }
                else if(metric.SpearmanRho is not null||string.IsNullOrWhiteSpace(metric.ReasonCode))return false;
            }
        }
        return value.Status==(anyObserved?ExecutionRealitySensitivityStatusV1.Observed:ExecutionRealitySensitivityStatusV1.Unsupported);
    }

    private static bool Hash(string value)=>value is{Length:64}&&value.All(Uri.IsHexDigit);
}

internal sealed class ExecutionRealitySensitivityServiceV1(AgentSqliteStore store)
{
    private readonly AgentSqliteStore _store=store??throw new ArgumentNullException(nameof(store));

    internal async Task<ExecutionRealitySensitivityReportV1?> BuildAsync(CancellationToken ct)
    {
        var stability=await _store.GetLatestExecutionRealityStabilityReferenceAsync(ct);
        if(stability is null)return null;
        if(stability.Status!=ExecutionRealityStabilityStatusV1.Observed)
            return ExecutionRealitySensitivityCanonicalizerV1.Create(stability,[],["execution-sensitivity.source-stability-unavailable"]);

        var calibration=await _store.GetLatestExecutionRealityCalibrationReferenceAsync(ct);
        if(calibration is null||calibration.CanonicalSha256!=stability.SourceCalibrationSha256
           ||calibration.SourcePositionLinkId!=stability.SourcePositionLinkId
           ||calibration.SourceExecutionTraceSha256!=stability.SourceExecutionTraceSha256
           ||calibration.SourceDriftTraceSha256!=stability.SourceDriftTraceSha256
           ||calibration.SourceLastExecutionEventId!=stability.SourceLastExecutionEventId)
            return ExecutionRealitySensitivityCanonicalizerV1.Create(stability,[],["execution-sensitivity.source-stability-stale"]);

        var source=new ExecutionRealityCalibrationSourceV1(calibration.SourcePositionLinkId,calibration.SourcePositionLinkSha256,
            calibration.SourceExecutionLedgerSha256,calibration.SourceExecutionTraceSha256,calibration.SourceDriftTraceSha256,
            calibration.SourceLastExecutionEventId,calibration.GeneratedAtUtc);
        var observed=await _store.GetExecutionRealityCalibrationObservationSetAsync(source,ct);
        if(!observed.SourceTraceMatches)
            return ExecutionRealitySensitivityCanonicalizerV1.Create(stability,[],["execution-sensitivity.source-trace-mismatch"]);

        var buckets=new List<ExecutionRealitySensitivityBucketV1>();
        foreach(var key in stability.ObservedBuckets)
        {
            var values=observed.TimedSummaries.Where(x=>Match(x.Summary,key)&&Complete(x)).ToArray();
            buckets.Add(BuildBucket(key.ProviderId,key.Environment,key.Symbol,key.OrderType,key.ReduceOnly,values));
        }
        var reasons=new List<string>();
        if(buckets.Count==0)reasons.Add("execution-sensitivity.no-observed-stability-buckets");
        if(buckets.All(x=>x.Status==ExecutionRealitySensitivityStatusV1.Unsupported))reasons.Add("execution-sensitivity.metrics-unavailable");
        return ExecutionRealitySensitivityCanonicalizerV1.Create(stability,buckets,reasons);
    }

    internal async Task<bool> BuildAndPersistSafelyAsync(CancellationToken ct)
    {
        try
        {
            var report=await BuildAsync(ct);
            return report is not null&&await _store.SaveExecutionRealitySensitivityAsync(report,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch
        {
            // Sensitivity is diagnostics only. It has no authority over research costs, risk, execution, or strategy promotion.
            return false;
        }
    }

    internal static ExecutionRealitySensitivityBucketV1 BuildBucket(
        string provider,string environment,string symbol,ExecutionOrderType orderType,bool reduceOnly,
        IReadOnlyList<ExecutionRealityTimedSummaryV1> values)
    {
        var ordered=values.OrderBy(x=>x.CompletedAtUtc).ThenBy(x=>x.Summary.ClientOrderId,StringComparer.Ordinal).ToArray();
        if(ordered.Length<ExecutionRealitySensitivityCanonicalizerV1.RequiredFolds*ExecutionRealitySensitivityCanonicalizerV1.MinimumPairsPerFold)
            return new(provider,environment,symbol,orderType,reduceOnly,ExecutionRealitySensitivityStatusV1.Unsupported,ordered.Length,[]);
        var folds=new List<ExecutionRealitySensitivityFoldV1>(ExecutionRealitySensitivityCanonicalizerV1.RequiredFolds);
        for(var fold=0;fold<ExecutionRealitySensitivityCanonicalizerV1.RequiredFolds;fold++)
        {
            var start=fold*ordered.Length/ExecutionRealitySensitivityCanonicalizerV1.RequiredFolds;
            var end=(fold+1)*ordered.Length/ExecutionRealitySensitivityCanonicalizerV1.RequiredFolds;
            var slice=ordered.Skip(start).Take(end-start).ToArray();
            var metrics=Enum.GetValues<ExecutionRealitySensitivityFeatureV1>().Select(feature=>BuildMetric(feature,slice)).ToArray();
            folds.Add(new(fold,slice[0].CompletedAtUtc,slice[^1].CompletedAtUtc,slice.Length,metrics));
        }
        var status=folds.SelectMany(x=>x.Metrics).Any(x=>x.Status==ExecutionRealitySensitivityMetricStatusV1.Observed)
            ?ExecutionRealitySensitivityStatusV1.Observed:ExecutionRealitySensitivityStatusV1.Unsupported;
        return new(provider,environment,symbol,orderType,reduceOnly,status,ordered.Length,folds);
    }

    internal static ExecutionRealitySensitivityMetricV1 BuildMetric(
        ExecutionRealitySensitivityFeatureV1 feature,IReadOnlyList<ExecutionRealityTimedSummaryV1> values)
    {
        var pairs=new List<(double Feature,double Target)>();
        foreach(var value in values)
        {
            var target=value.Summary.AdverseSlippageBps;if(target is not { } y||!double.IsFinite(y))continue;
            var x=Feature(value.Summary,feature);if(x is not { } number||!double.IsFinite(number))continue;
            pairs.Add((number,y));
        }
        if(pairs.Count<ExecutionRealitySensitivityCanonicalizerV1.MinimumPairsPerFold)
            return new(feature,ExecutionRealitySensitivityMetricStatusV1.Unsupported,pairs.Count,null,"execution-sensitivity.pairs-insufficient");
        var rho=Spearman(pairs);
        if(rho is null)
            return new(feature,ExecutionRealitySensitivityMetricStatusV1.Unsupported,pairs.Count,null,"execution-sensitivity.zero-variance");
        return new(feature,ExecutionRealitySensitivityMetricStatusV1.Observed,pairs.Count,rho.Value,string.Empty);
    }

    internal static double? Spearman(IReadOnlyList<(double X,double Y)> pairs)
    {
        if(pairs.Count<2)return null;
        var x=AverageRanks(pairs.Select(p=>p.X).ToArray());var y=AverageRanks(pairs.Select(p=>p.Y).ToArray());
        var xMean=x.Average();var yMean=y.Average();double covariance=0,xVar=0,yVar=0;
        for(var i=0;i<pairs.Count;i++)
        {
            var dx=x[i]-xMean;var dy=y[i]-yMean;covariance+=dx*dy;xVar+=dx*dx;yVar+=dy*dy;
        }
        if(xVar<=0||yVar<=0)return null;
        return Math.Clamp(covariance/Math.Sqrt(xVar*yVar),-1,1);
    }

    private static double[] AverageRanks(IReadOnlyList<double> values)
    {
        var ordered=values.Select((value,index)=>(value,index)).OrderBy(x=>x.value).ThenBy(x=>x.index).ToArray();
        var result=new double[values.Count];var i=0;
        while(i<ordered.Length)
        {
            var j=i+1;while(j<ordered.Length&&ordered[j].value.Equals(ordered[i].value))j++;
            var rank=((i+1)+j)/2d;
            for(var k=i;k<j;k++)result[ordered[k].index]=rank;
            i=j;
        }
        return result;
    }

    private static double? Feature(ExecutionDriftSummaryV1 value,ExecutionRealitySensitivityFeatureV1 feature)
        =>feature switch
        {
            ExecutionRealitySensitivityFeatureV1.RequestedNotional=>value.ExpectedPrice>0&&value.RequestedQuantity>0?(double)(value.ExpectedPrice*value.RequestedQuantity):null,
            ExecutionRealitySensitivityFeatureV1.FillRatio=>value.FillRatio>0?(double)value.FillRatio:null,
            ExecutionRealitySensitivityFeatureV1.PreflightSpreadBps=>value.PreflightSpreadBps,
            ExecutionRealitySensitivityFeatureV1.PreflightLiquidityScore=>value.PreflightLiquidityScore,
            ExecutionRealitySensitivityFeatureV1.PreflightAtrPercent=>value.PreflightAtrPercent,
            ExecutionRealitySensitivityFeatureV1.SubmitLatencyMilliseconds=>value.SubmitToFirstExchangeMilliseconds,
            ExecutionRealitySensitivityFeatureV1.IntentLatencyMilliseconds=>value.IntentToFinalMilliseconds,
            _=>null
        };

    private static bool Match(ExecutionDriftSummaryV1 value,ExecutionRealityStabilityBucketKeyV1 key)
        =>value.ProviderId==key.ProviderId&&value.Environment==key.Environment&&value.Symbol==key.Symbol
          &&value.OrderType==key.OrderType&&value.ReduceOnly==key.ReduceOnly;

    private static bool Complete(ExecutionRealityTimedSummaryV1 value)
    {
        var x=value.Summary;
        return value.CompletedAtUtc.Offset==TimeSpan.Zero&&x.Environment=="Testnet"&&!x.ClientOrderId.EndsWith("-E",StringComparison.Ordinal)
               &&x.RequestedQuantity>0&&x.FinalObservedQuantity>0&&x.FillRatio is>0 and<=1
               &&x.FinalStatus is "FILLED" or "PARTIALLY_FILLED"
               &&x.AdverseSlippageBps is { } slip&&double.IsFinite(slip)
               &&x.SubmitToFirstExchangeMilliseconds is { } submit&&double.IsFinite(submit)&&submit>=0
               &&x.IntentToFinalMilliseconds is { } end&&double.IsFinite(end)&&end>=0;
    }
}

public sealed partial class AgentSqliteStore
{
    private void EnsureExecutionRealitySensitivitySchema()
    {
        using var c=new SqliteConnection(_cs);c.Open();using var q=c.CreateCommand();q.CommandText="""
            CREATE TABLE IF NOT EXISTS execution_reality_sensitivity_audits(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                generated_at TEXT NOT NULL,
                method TEXT NOT NULL,
                target TEXT NOT NULL,
                source_stability_sha256 TEXT NOT NULL UNIQUE,
                source_stability_status TEXT NOT NULL,
                source_calibration_sha256 TEXT NOT NULL,
                source_calibration_status TEXT NOT NULL,
                source_position_link_id TEXT NOT NULL,
                source_position_link_sha256 TEXT NOT NULL,
                source_execution_trace_sha256 TEXT NOT NULL,
                source_drift_trace_sha256 TEXT NOT NULL,
                source_last_execution_event_id INTEGER NOT NULL,
                status TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_execution_reality_sensitivity_time ON execution_reality_sensitivity_audits(generated_at DESC);
            CREATE TRIGGER IF NOT EXISTS execution_reality_sensitivity_no_update BEFORE UPDATE ON execution_reality_sensitivity_audits BEGIN SELECT RAISE(ABORT,'execution reality sensitivity audits are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_reality_sensitivity_no_delete BEFORE DELETE ON execution_reality_sensitivity_audits BEGIN SELECT RAISE(ABORT,'execution reality sensitivity audits are append-only'); END;
            """;
        q.ExecuteNonQuery();
    }

    internal async Task<ExecutionRealityStabilityReferenceV1?> GetLatestExecutionRealityStabilityReferenceAsync(CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
            SELECT canonical_sha256,schema,generated_at,source_calibration_sha256,source_calibration_status,
                   source_position_link_id,source_position_link_sha256,source_execution_trace_sha256,source_drift_trace_sha256,
                   source_last_execution_event_id,status,canonical_bytes
            FROM execution_reality_stability_audits ORDER BY generated_at DESC,rowid DESC LIMIT 1
            """;
        await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
        try
        {
            var hash=r.GetString(0);var schema=r.GetString(1);var generated=DateTimeOffset.Parse(r.GetString(2),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();
            if(!Enum.TryParse<ExecutionRealityCalibrationStatusV1>(r.GetString(4),out var calibrationStatus)
               ||!Enum.TryParse<ExecutionRealityStabilityStatusV1>(r.GetString(10),out var status))return null;
            var calibrationHash=r.GetString(3);var linkId=r.GetString(5);var linkHash=r.GetString(6);var executionHash=r.GetString(7);
            var driftHash=r.GetString(8);var cursor=r.GetInt64(9);var bytes=(byte[])r[11];
            if(schema!=ExecutionRealityStabilityCanonicalizerV1.Schema||generated.Offset!=TimeSpan.Zero||cursor<1
               ||linkId!="execution-position-drift:"+linkHash||!ValidSensitivityHash(hash)||!ValidSensitivityHash(calibrationHash)
               ||!ValidSensitivityHash(linkHash)||!ValidSensitivityHash(executionHash)||!ValidSensitivityHash(driftHash)
               ||!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),hash,StringComparison.Ordinal))return null;
            using var document=JsonDocument.Parse(bytes);var root=document.RootElement;
            if(root.GetProperty("schema").GetString()!=schema||root.GetProperty("generatedAtUtc").GetDateTimeOffset().ToUniversalTime()!=generated
               ||root.GetProperty("sourceCalibrationSha256").GetString()!=calibrationHash
               ||!string.Equals(root.GetProperty("sourceCalibrationStatus").GetString(),calibrationStatus.ToString(),StringComparison.OrdinalIgnoreCase)
               ||root.GetProperty("sourcePositionLinkId").GetString()!=linkId||root.GetProperty("sourcePositionLinkSha256").GetString()!=linkHash
               ||root.GetProperty("sourceExecutionTraceSha256").GetString()!=executionHash||root.GetProperty("sourceDriftTraceSha256").GetString()!=driftHash
               ||root.GetProperty("sourceLastExecutionEventId").GetInt64()!=cursor
               ||!string.Equals(root.GetProperty("status").GetString(),status.ToString(),StringComparison.OrdinalIgnoreCase)
               ||root.GetProperty("requiredFolds").GetInt32()!=ExecutionRealityStabilityCanonicalizerV1.RequiredFolds
               ||root.GetProperty("minimumCompleteSamplesPerFold").GetInt32()!=ExecutionRealityStabilityCanonicalizerV1.MinimumCompleteSamplesPerFold)
                return null;
            var keys=new List<ExecutionRealityStabilityBucketKeyV1>();var identities=new HashSet<string>(StringComparer.Ordinal);
            foreach(var bucket in root.GetProperty("buckets").EnumerateArray())
            {
                if(!string.Equals(bucket.GetProperty("status").GetString(),"observed",StringComparison.OrdinalIgnoreCase))continue;
                if(!Enum.TryParse<ExecutionOrderType>(bucket.GetProperty("orderType").GetString(),out var orderType))return null;
                var provider=bucket.GetProperty("ProviderId").GetString();var environment=bucket.GetProperty("Environment").GetString();
                var symbol=bucket.GetProperty("Symbol").GetString();var reduceOnly=bucket.GetProperty("ReduceOnly").GetBoolean();
                if(string.IsNullOrWhiteSpace(provider)||string.IsNullOrWhiteSpace(environment)||string.IsNullOrWhiteSpace(symbol))return null;
                var identity=$"{provider}|{environment}|{symbol}|{orderType}|{reduceOnly}";
                if(!identities.Add(identity))return null;
                keys.Add(new(provider,environment,symbol,orderType,reduceOnly));
            }
            if(status==ExecutionRealityStabilityStatusV1.Observed&&(calibrationStatus!=ExecutionRealityCalibrationStatusV1.Available||keys.Count==0))return null;
            if(status==ExecutionRealityStabilityStatusV1.Unsupported&&keys.Count!=0)return null;
            return new(hash,generated,status,calibrationHash,calibrationStatus,linkId,linkHash,executionHash,driftHash,cursor,keys);
        }
        catch{return null;}
    }

    internal async Task<bool> SaveExecutionRealitySensitivityAsync(ExecutionRealitySensitivityReportV1 value,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(!ExecutionRealitySensitivityCanonicalizerV1.IsCanonical(value))throw new InvalidOperationException("Execution reality sensitivity canonical identity is invalid.");
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using(var source=c.CreateCommand())
        {
            source.Transaction=tx;source.CommandText="""
                SELECT canonical_bytes FROM execution_reality_stability_audits
                WHERE canonical_sha256=$stability AND schema=$schema AND generated_at=$generated
                  AND status=$stabilityStatus AND source_calibration_sha256=$calibration AND source_calibration_status=$calibrationStatus
                  AND source_position_link_id=$link AND source_position_link_sha256=$linkHash
                  AND source_execution_trace_sha256=$executionTrace AND source_drift_trace_sha256=$driftTrace
                  AND source_last_execution_event_id=$cursor
                LIMIT 1
                """;
            source.Parameters.AddWithValue("$stability",value.SourceStabilitySha256);source.Parameters.AddWithValue("$schema",ExecutionRealityStabilityCanonicalizerV1.Schema);
            source.Parameters.AddWithValue("$generated",value.GeneratedAtUtc.ToString("O",CultureInfo.InvariantCulture));
            source.Parameters.AddWithValue("$stabilityStatus",value.SourceStabilityStatus.ToString());
            source.Parameters.AddWithValue("$calibration",value.SourceCalibrationSha256);source.Parameters.AddWithValue("$calibrationStatus",value.SourceCalibrationStatus.ToString());
            source.Parameters.AddWithValue("$link",value.SourcePositionLinkId);source.Parameters.AddWithValue("$linkHash",value.SourcePositionLinkSha256);
            source.Parameters.AddWithValue("$executionTrace",value.SourceExecutionTraceSha256);source.Parameters.AddWithValue("$driftTrace",value.SourceDriftTraceSha256);
            source.Parameters.AddWithValue("$cursor",value.SourceLastExecutionEventId);
            var bytes=await source.ExecuteScalarAsync(ct) as byte[];
            if(bytes is null||!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),value.SourceStabilitySha256,StringComparison.Ordinal))
                throw new InvalidOperationException("Execution reality sensitivity source stability binding is invalid.");
        }
        await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="""
            INSERT OR IGNORE INTO execution_reality_sensitivity_audits(
                canonical_sha256,schema,generated_at,method,target,source_stability_sha256,source_stability_status,source_calibration_sha256,
                source_calibration_status,source_position_link_id,source_position_link_sha256,source_execution_trace_sha256,source_drift_trace_sha256,
                source_last_execution_event_id,status,canonical_bytes)
            VALUES($hash,$schema,$generated,$method,$target,$stability,$stabilityStatus,$calibration,$calibrationStatus,$link,$linkHash,$executionTrace,$driftTrace,$cursor,$status,$bytes);
            SELECT changes();
            """;
        q.Parameters.AddWithValue("$hash",value.CanonicalSha256);q.Parameters.AddWithValue("$schema",value.Schema);
        q.Parameters.AddWithValue("$generated",value.GeneratedAtUtc.ToString("O",CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$method",value.Method);
        q.Parameters.AddWithValue("$target",value.Target);q.Parameters.AddWithValue("$stability",value.SourceStabilitySha256);
        q.Parameters.AddWithValue("$stabilityStatus",value.SourceStabilityStatus.ToString());q.Parameters.AddWithValue("$calibration",value.SourceCalibrationSha256);
        q.Parameters.AddWithValue("$calibrationStatus",value.SourceCalibrationStatus.ToString());q.Parameters.AddWithValue("$link",value.SourcePositionLinkId);q.Parameters.AddWithValue("$linkHash",value.SourcePositionLinkSha256);
        q.Parameters.AddWithValue("$executionTrace",value.SourceExecutionTraceSha256);q.Parameters.AddWithValue("$driftTrace",value.SourceDriftTraceSha256);
        q.Parameters.AddWithValue("$cursor",value.SourceLastExecutionEventId);q.Parameters.AddWithValue("$status",value.Status.ToString());
        q.Parameters.Add("$bytes",SqliteType.Blob).Value=value.CanonicalBytes;
        var inserted=Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
        await tx.CommitAsync(ct);return inserted;
    }

    private static bool ValidSensitivityHash(string value)=>value is{Length:64}&&value.All(Uri.IsHexDigit);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace 币安量化机器人.Services.Agent;

internal enum ExecutionRealityStabilityStatusV1 { Unsupported, Observed }

internal sealed record ExecutionRealityCalibrationReferenceV1(
    string CanonicalSha256,
    DateTimeOffset GeneratedAtUtc,
    ExecutionRealityCalibrationStatusV1 Status,
    string SourcePositionLinkId,
    string SourcePositionLinkSha256,
    string SourceExecutionLedgerSha256,
    string SourceExecutionTraceSha256,
    string SourceDriftTraceSha256,
    long SourceLastExecutionEventId);

internal sealed record ExecutionRealityStabilityFoldV1(
    int FoldIndex,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int CompleteSamples,
    int PartialFillSamples,
    double PartialFillRate,
    double MedianFillRatio,
    double MedianAdverseSlippageBps,
    double MedianSubmitToFirstExchangeMilliseconds,
    double MedianIntentToFinalMilliseconds);

internal sealed record ExecutionRealityStabilityBucketV1(
    string ProviderId,
    string Environment,
    string Symbol,
    ExecutionOrderType OrderType,
    bool ReduceOnly,
    ExecutionRealityStabilityStatusV1 Status,
    int CompleteSamples,
    int RequiredFolds,
    int MinimumCompleteSamplesPerFold,
    IReadOnlyList<ExecutionRealityStabilityFoldV1> Folds,
    double MedianFillRatioSpread,
    double MedianAdverseSlippageBpsSpread,
    double MedianSubmitLatencyMillisecondsSpread,
    double MedianIntentLatencyMillisecondsSpread);

internal sealed record ExecutionRealityStabilityReportV1(
    string Schema,
    DateTimeOffset GeneratedAtUtc,
    string SourceCalibrationSha256,
    ExecutionRealityCalibrationStatusV1 SourceCalibrationStatus,
    string SourcePositionLinkId,
    string SourcePositionLinkSha256,
    string SourceExecutionTraceSha256,
    string SourceDriftTraceSha256,
    long SourceLastExecutionEventId,
    ExecutionRealityStabilityStatusV1 Status,
    int RequiredFolds,
    int MinimumCompleteSamplesPerFold,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<ExecutionRealityStabilityBucketV1> Buckets,
    string CanonicalSha256,
    byte[] CanonicalBytes);

internal static class ExecutionRealityStabilityCanonicalizerV1
{
    internal const string Schema="wpe.execution-reality-stability/1.0";
    internal const int RequiredFolds=4;
    internal const int MinimumCompleteSamplesPerFold=30;

    internal static ExecutionRealityStabilityReportV1 Create(
        ExecutionRealityCalibrationReferenceV1 source,
        IReadOnlyList<ExecutionRealityStabilityBucketV1> buckets,
        IReadOnlyList<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(buckets);ArgumentNullException.ThrowIfNull(reasonCodes);
        if(!Sha(source.CanonicalSha256)||source.GeneratedAtUtc.Offset!=TimeSpan.Zero||source.SourceLastExecutionEventId<1
           ||source.SourcePositionLinkId!="execution-position-drift:"+source.SourcePositionLinkSha256
           ||!Sha(source.SourcePositionLinkSha256)||!Sha(source.SourceExecutionTraceSha256)||!Sha(source.SourceDriftTraceSha256))
            throw new ArgumentException("Execution reality stability source is invalid.");

        var rows=buckets.OrderBy(x=>x.ProviderId,StringComparer.Ordinal).ThenBy(x=>x.Environment,StringComparer.Ordinal)
            .ThenBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.OrderType).ThenBy(x=>x.ReduceOnly).ToArray();
        if(rows.Any(x=>!ValidBucket(x)))throw new ArgumentException("Execution reality stability bucket is invalid.");

        var reasons=reasonCodes.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        if(source.Status!=ExecutionRealityCalibrationStatusV1.Available&&rows.Length!=0)
            throw new ArgumentException("Unavailable calibration cannot carry observed stability buckets.");
        var status=source.Status==ExecutionRealityCalibrationStatusV1.Available&&rows.Any(x=>x.Status==ExecutionRealityStabilityStatusV1.Observed)
            ?ExecutionRealityStabilityStatusV1.Observed:ExecutionRealityStabilityStatusV1.Unsupported;
        if(source.Status!=ExecutionRealityCalibrationStatusV1.Available&&!reasons.Contains("execution-stability.source-calibration-unavailable",StringComparer.Ordinal))
            reasons=[..reasons,"execution-stability.source-calibration-unavailable"];
        if(status==ExecutionRealityStabilityStatusV1.Unsupported&&!reasons.Contains("execution-stability.samples-insufficient",StringComparer.Ordinal)
           &&!reasons.Contains("execution-stability.source-calibration-unavailable",StringComparer.Ordinal)
           &&!reasons.Contains("execution-stability.source-trace-mismatch",StringComparer.Ordinal))
            reasons=[..reasons,"execution-stability.samples-insufficient"];

        var bytes=JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema=Schema,generatedAtUtc=source.GeneratedAtUtc,sourceCalibrationSha256=source.CanonicalSha256,
            sourceCalibrationStatus=source.Status.ToString().ToLowerInvariant(),
            sourcePositionLinkId=source.SourcePositionLinkId,sourcePositionLinkSha256=source.SourcePositionLinkSha256,
            sourceExecutionTraceSha256=source.SourceExecutionTraceSha256,sourceDriftTraceSha256=source.SourceDriftTraceSha256,
            sourceLastExecutionEventId=source.SourceLastExecutionEventId,status=status.ToString().ToLowerInvariant(),
            requiredFolds=RequiredFolds,minimumCompleteSamplesPerFold=MinimumCompleteSamplesPerFold,
            reasonCodes=reasons,buckets=rows.Select(Row)
        });
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Schema,source.GeneratedAtUtc,source.CanonicalSha256,source.Status,source.SourcePositionLinkId,source.SourcePositionLinkSha256,
            source.SourceExecutionTraceSha256,source.SourceDriftTraceSha256,source.SourceLastExecutionEventId,status,
            RequiredFolds,MinimumCompleteSamplesPerFold,reasons,rows,hash,bytes);
    }

    internal static bool IsCanonical(ExecutionRealityStabilityReportV1 value)
    {
        if(value is null||value.Schema!=Schema||value.GeneratedAtUtc.Offset!=TimeSpan.Zero||!Sha(value.SourceCalibrationSha256)
           ||!Enum.IsDefined(value.SourceCalibrationStatus)||value.SourcePositionLinkId!="execution-position-drift:"+value.SourcePositionLinkSha256||!Sha(value.SourcePositionLinkSha256)
           ||!Sha(value.SourceExecutionTraceSha256)||!Sha(value.SourceDriftTraceSha256)||value.SourceLastExecutionEventId<1
           ||value.RequiredFolds!=RequiredFolds||value.MinimumCompleteSamplesPerFold!=MinimumCompleteSamplesPerFold
           ||!Sha(value.CanonicalSha256)||value.CanonicalBytes is null)return false;
        try
        {
            var source=new ExecutionRealityCalibrationReferenceV1(value.SourceCalibrationSha256,value.GeneratedAtUtc,
                value.SourceCalibrationStatus,value.SourcePositionLinkId,value.SourcePositionLinkSha256,string.Empty,
                value.SourceExecutionTraceSha256,value.SourceDriftTraceSha256,value.SourceLastExecutionEventId);
            var expected=Create(source,value.Buckets,value.ReasonCodes);
            return expected.Status==value.Status
                   &&string.Equals(expected.CanonicalSha256,value.CanonicalSha256,StringComparison.Ordinal)
                   &&CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes,value.CanonicalBytes);
        }
        catch(ArgumentException){return false;}
    }

    private static object Row(ExecutionRealityStabilityBucketV1 x)=>new
    {
        x.ProviderId,x.Environment,x.Symbol,orderType=x.OrderType.ToString(),x.ReduceOnly,status=x.Status.ToString().ToLowerInvariant(),
        x.CompleteSamples,x.RequiredFolds,x.MinimumCompleteSamplesPerFold,
        folds=x.Folds.OrderBy(y=>y.FoldIndex).Select(y=>new
        {
            y.FoldIndex,y.StartUtc,y.EndUtc,y.CompleteSamples,y.PartialFillSamples,y.PartialFillRate,y.MedianFillRatio,
            y.MedianAdverseSlippageBps,y.MedianSubmitToFirstExchangeMilliseconds,y.MedianIntentToFinalMilliseconds
        }),
        x.MedianFillRatioSpread,x.MedianAdverseSlippageBpsSpread,x.MedianSubmitLatencyMillisecondsSpread,x.MedianIntentLatencyMillisecondsSpread
    };

    private static bool ValidBucket(ExecutionRealityStabilityBucketV1 x)
    {
        if(string.IsNullOrWhiteSpace(x.ProviderId)||string.IsNullOrWhiteSpace(x.Environment)||string.IsNullOrWhiteSpace(x.Symbol)
           ||!Enum.IsDefined(x.OrderType)||x.RequiredFolds!=RequiredFolds||x.MinimumCompleteSamplesPerFold!=MinimumCompleteSamplesPerFold
           ||x.CompleteSamples<0||x.Folds.Count is<0 or>RequiredFolds)return false;
        var observed=x.Folds.Count==RequiredFolds&&x.Folds.All(f=>f.CompleteSamples>=MinimumCompleteSamplesPerFold)
                     &&x.CompleteSamples>=RequiredFolds*MinimumCompleteSamplesPerFold;
        if(x.Status!=(observed?ExecutionRealityStabilityStatusV1.Observed:ExecutionRealityStabilityStatusV1.Unsupported))return false;
        var ordered=x.Folds.OrderBy(f=>f.FoldIndex).ToArray();
        if(observed&&ordered.Select(f=>f.FoldIndex).Where((v,i)=>v!=i).Any())return false;
        foreach(var fold in ordered)
        {
            if(fold.StartUtc.Offset!=TimeSpan.Zero||fold.EndUtc.Offset!=TimeSpan.Zero||fold.EndUtc<fold.StartUtc||fold.CompleteSamples<1
               ||fold.PartialFillSamples<0||fold.PartialFillSamples>fold.CompleteSamples
               ||Math.Abs(fold.PartialFillRate-fold.PartialFillSamples/(double)fold.CompleteSamples)>1e-12
               ||!double.IsFinite(fold.MedianFillRatio)||fold.MedianFillRatio is<=0 or>1
               ||!double.IsFinite(fold.MedianAdverseSlippageBps)
               ||!double.IsFinite(fold.MedianSubmitToFirstExchangeMilliseconds)||fold.MedianSubmitToFirstExchangeMilliseconds<0
               ||!double.IsFinite(fold.MedianIntentToFinalMilliseconds)||fold.MedianIntentToFinalMilliseconds<0)return false;
        }
        foreach(var pair in ordered.Zip(ordered.Skip(1)))if(pair.First.EndUtc>pair.Second.StartUtc)return false;
        foreach(var spread in new[]{x.MedianFillRatioSpread,x.MedianAdverseSlippageBpsSpread,x.MedianSubmitLatencyMillisecondsSpread,x.MedianIntentLatencyMillisecondsSpread})
            if(!double.IsFinite(spread)||spread<0)return false;
        if(ordered.Length==0)
            return x.CompleteSamples<RequiredFolds*MinimumCompleteSamplesPerFold
                   &&x.MedianFillRatioSpread==0&&x.MedianAdverseSlippageBpsSpread==0
                   &&x.MedianSubmitLatencyMillisecondsSpread==0&&x.MedianIntentLatencyMillisecondsSpread==0;
        if(ordered.Sum(f=>f.CompleteSamples)!=x.CompleteSamples)return false;
        if(Math.Abs(x.MedianFillRatioSpread-Spread(ordered.Select(f=>f.MedianFillRatio)))>1e-12
           ||Math.Abs(x.MedianAdverseSlippageBpsSpread-Spread(ordered.Select(f=>f.MedianAdverseSlippageBps)))>1e-12
           ||Math.Abs(x.MedianSubmitLatencyMillisecondsSpread-Spread(ordered.Select(f=>f.MedianSubmitToFirstExchangeMilliseconds)))>1e-9
           ||Math.Abs(x.MedianIntentLatencyMillisecondsSpread-Spread(ordered.Select(f=>f.MedianIntentToFinalMilliseconds)))>1e-9)return false;
        return true;
    }

    private static double Spread(IEnumerable<double> values)
    {
        var data=values.ToArray();return data.Length==0?0:data.Max()-data.Min();
    }
    private static bool Sha(string value)=>value is{Length:64}&&value.All(Uri.IsHexDigit);
}

internal sealed class ExecutionRealityStabilityServiceV1(AgentSqliteStore store)
{
    private readonly AgentSqliteStore _store=store??throw new ArgumentNullException(nameof(store));

    internal async Task<ExecutionRealityStabilityReportV1?> BuildAsync(CancellationToken ct)
    {
        var calibration=await _store.GetLatestExecutionRealityCalibrationReferenceAsync(ct);
        if(calibration is null)return null;
        if(calibration.Status!=ExecutionRealityCalibrationStatusV1.Available)
            return ExecutionRealityStabilityCanonicalizerV1.Create(calibration,[],["execution-stability.source-calibration-unavailable"]);

        var source=new ExecutionRealityCalibrationSourceV1(calibration.SourcePositionLinkId,calibration.SourcePositionLinkSha256,
            calibration.SourceExecutionLedgerSha256,calibration.SourceExecutionTraceSha256,calibration.SourceDriftTraceSha256,
            calibration.SourceLastExecutionEventId,calibration.GeneratedAtUtc);
        var observed=await _store.GetExecutionRealityCalibrationObservationSetAsync(source,ct);
        if(!observed.SourceTraceMatches)
            return ExecutionRealityStabilityCanonicalizerV1.Create(calibration,[],["execution-stability.source-trace-mismatch"]);

        var complete=observed.TimedSummaries.Where(EligibleComplete).ToArray();
        var buckets=complete.GroupBy(x=>new{x.Summary.ProviderId,x.Summary.Environment,x.Summary.Symbol,x.Summary.OrderType,x.Summary.ReduceOnly})
            .Select(g=>BuildBucket(g.Key.ProviderId,g.Key.Environment,g.Key.Symbol,g.Key.OrderType,g.Key.ReduceOnly,g.ToArray()))
            .OrderBy(x=>x.ProviderId,StringComparer.Ordinal).ThenBy(x=>x.Environment,StringComparer.Ordinal)
            .ThenBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.OrderType).ThenBy(x=>x.ReduceOnly).ToArray();
        var reasons=new List<string>();
        if(complete.Length==0)reasons.Add("execution-stability.no-complete-samples");
        if(buckets.All(x=>x.Status==ExecutionRealityStabilityStatusV1.Unsupported))reasons.Add("execution-stability.samples-insufficient");
        return ExecutionRealityStabilityCanonicalizerV1.Create(calibration,buckets,reasons);
    }

    internal async Task<bool> BuildAndPersistSafelyAsync(CancellationToken ct)
    {
        try
        {
            var report=await BuildAsync(ct);
            return report is not null&&await _store.SaveExecutionRealityStabilityAsync(report,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch
        {
            // Stability diagnostics are observational research evidence and have no trading authority.
            return false;
        }
    }

    internal static ExecutionRealityStabilityBucketV1 BuildBucket(
        string provider,string environment,string symbol,ExecutionOrderType orderType,bool reduceOnly,
        IReadOnlyList<ExecutionRealityTimedSummaryV1> values)
    {
        var ordered=values.OrderBy(x=>x.CompletedAtUtc).ThenBy(x=>x.Summary.ClientOrderId,StringComparer.Ordinal).ToArray();
        if(ordered.Length<ExecutionRealityStabilityCanonicalizerV1.RequiredFolds*ExecutionRealityStabilityCanonicalizerV1.MinimumCompleteSamplesPerFold)
            return new(provider,environment,symbol,orderType,reduceOnly,ExecutionRealityStabilityStatusV1.Unsupported,ordered.Length,
                ExecutionRealityStabilityCanonicalizerV1.RequiredFolds,ExecutionRealityStabilityCanonicalizerV1.MinimumCompleteSamplesPerFold,[],0,0,0,0);

        var folds=new List<ExecutionRealityStabilityFoldV1>(ExecutionRealityStabilityCanonicalizerV1.RequiredFolds);
        for(var fold=0;fold<ExecutionRealityStabilityCanonicalizerV1.RequiredFolds;fold++)
        {
            var start=fold*ordered.Length/ExecutionRealityStabilityCanonicalizerV1.RequiredFolds;
            var end=(fold+1)*ordered.Length/ExecutionRealityStabilityCanonicalizerV1.RequiredFolds;
            var slice=ordered.Skip(start).Take(end-start).ToArray();
            var partial=slice.Count(x=>x.Summary.FillRatio<1);
            folds.Add(new(fold,slice[0].CompletedAtUtc,slice[^1].CompletedAtUtc,slice.Length,partial,partial/(double)slice.Length,
                Median(slice.Select(x=>(double)x.Summary.FillRatio)),
                Median(slice.Select(x=>x.Summary.AdverseSlippageBps!.Value)),
                Median(slice.Select(x=>x.Summary.SubmitToFirstExchangeMilliseconds!.Value)),
                Median(slice.Select(x=>x.Summary.IntentToFinalMilliseconds!.Value))));
        }
        return new(provider,environment,symbol,orderType,reduceOnly,ExecutionRealityStabilityStatusV1.Observed,ordered.Length,
            ExecutionRealityStabilityCanonicalizerV1.RequiredFolds,ExecutionRealityStabilityCanonicalizerV1.MinimumCompleteSamplesPerFold,folds,
            Spread(folds.Select(x=>x.MedianFillRatio)),Spread(folds.Select(x=>x.MedianAdverseSlippageBps)),
            Spread(folds.Select(x=>x.MedianSubmitToFirstExchangeMilliseconds)),Spread(folds.Select(x=>x.MedianIntentToFinalMilliseconds)));
    }

    private static bool EligibleComplete(ExecutionRealityTimedSummaryV1 value)
    {
        var x=value.Summary;
        return value.CompletedAtUtc.Offset==TimeSpan.Zero&&string.Equals(x.Environment,"Testnet",StringComparison.Ordinal)
               &&!x.ClientOrderId.EndsWith("-E",StringComparison.Ordinal)&&x.RequestedQuantity>0&&x.FinalObservedQuantity>0
               &&x.FillRatio is>0 and<=1&&x.FinalStatus is "FILLED" or "PARTIALLY_FILLED"
               &&x.AdverseSlippageBps is { } slip&&double.IsFinite(slip)
               &&x.SubmitToFirstExchangeMilliseconds is { } submit&&double.IsFinite(submit)&&submit>=0
               &&x.IntentToFinalMilliseconds is { } end&&double.IsFinite(end)&&end>=0;
    }

    private static double Median(IEnumerable<double> values)
    {
        var data=values.OrderBy(x=>x).ToArray();if(data.Length==0)throw new ArgumentException("Median requires data.",nameof(values));
        var mid=data.Length/2;return data.Length%2==1?data[mid]:(data[mid-1]+data[mid])/2;
    }
    private static double Spread(IEnumerable<double> values)
    {
        var data=values.ToArray();return data.Length==0?0:data.Max()-data.Min();
    }
}

public sealed partial class AgentSqliteStore
{
    private void EnsureExecutionRealityStabilitySchema()
    {
        using var c=new SqliteConnection(_cs);c.Open();using var q=c.CreateCommand();q.CommandText="""
            CREATE TABLE IF NOT EXISTS execution_reality_stability_audits(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                generated_at TEXT NOT NULL,
                source_calibration_sha256 TEXT NOT NULL UNIQUE,
                source_calibration_status TEXT NOT NULL,
                source_position_link_id TEXT NOT NULL,
                source_position_link_sha256 TEXT NOT NULL,
                source_execution_trace_sha256 TEXT NOT NULL,
                source_drift_trace_sha256 TEXT NOT NULL,
                source_last_execution_event_id INTEGER NOT NULL,
                status TEXT NOT NULL,
                required_folds INTEGER NOT NULL,
                minimum_complete_samples_per_fold INTEGER NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_execution_reality_stability_time ON execution_reality_stability_audits(generated_at DESC);
            CREATE TRIGGER IF NOT EXISTS execution_reality_stability_no_update BEFORE UPDATE ON execution_reality_stability_audits BEGIN SELECT RAISE(ABORT,'execution reality stability audits are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_reality_stability_no_delete BEFORE DELETE ON execution_reality_stability_audits BEGIN SELECT RAISE(ABORT,'execution reality stability audits are append-only'); END;
            """;
        q.ExecuteNonQuery();
    }

    internal async Task<ExecutionRealityCalibrationReferenceV1?> GetLatestExecutionRealityCalibrationReferenceAsync(CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
            SELECT canonical_sha256,generated_at,status,source_position_link_id,source_position_link_sha256,
                   source_execution_ledger_sha256,source_execution_trace_sha256,source_drift_trace_sha256,source_last_execution_event_id
            FROM execution_reality_calibrations ORDER BY generated_at DESC,rowid DESC LIMIT 1
            """;
        await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
        if(!Enum.TryParse<ExecutionRealityCalibrationStatusV1>(r.GetString(2),out var status))return null;
        return new(r.GetString(0),DateTimeOffset.Parse(r.GetString(1),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime(),
            status,r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetInt64(8));
    }

    internal async Task<bool> SaveExecutionRealityStabilityAsync(ExecutionRealityStabilityReportV1 value,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(!ExecutionRealityStabilityCanonicalizerV1.IsCanonical(value))throw new InvalidOperationException("Execution reality stability canonical identity is invalid.");
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using(var source=c.CreateCommand())
        {
            source.Transaction=tx;source.CommandText="""
                SELECT COUNT(*) FROM execution_reality_calibrations
                WHERE canonical_sha256=$calibration AND generated_at=$generated AND status=$calibrationStatus
                  AND source_position_link_id=$link AND source_position_link_sha256=$linkHash
                  AND source_execution_trace_sha256=$executionTrace AND source_drift_trace_sha256=$driftTrace
                  AND source_last_execution_event_id=$cursor
                """;
            source.Parameters.AddWithValue("$calibration",value.SourceCalibrationSha256);
            source.Parameters.AddWithValue("$generated",value.GeneratedAtUtc.ToString("O",CultureInfo.InvariantCulture));
            source.Parameters.AddWithValue("$calibrationStatus",value.SourceCalibrationStatus.ToString());
            source.Parameters.AddWithValue("$link",value.SourcePositionLinkId);source.Parameters.AddWithValue("$linkHash",value.SourcePositionLinkSha256);
            source.Parameters.AddWithValue("$executionTrace",value.SourceExecutionTraceSha256);source.Parameters.AddWithValue("$driftTrace",value.SourceDriftTraceSha256);
            source.Parameters.AddWithValue("$cursor",value.SourceLastExecutionEventId);
            if(Convert.ToInt32(await source.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)!=1)
                throw new InvalidOperationException("Execution reality stability source calibration binding is invalid.");
        }
        await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="""
            INSERT OR IGNORE INTO execution_reality_stability_audits(
                canonical_sha256,schema,generated_at,source_calibration_sha256,source_calibration_status,source_position_link_id,source_position_link_sha256,
                source_execution_trace_sha256,source_drift_trace_sha256,source_last_execution_event_id,status,required_folds,
                minimum_complete_samples_per_fold,canonical_bytes)
            VALUES($hash,$schema,$generated,$calibration,$calibrationStatus,$link,$linkHash,$executionTrace,$driftTrace,$cursor,$status,$folds,$minimum,$bytes);
            SELECT changes();
            """;
        q.Parameters.AddWithValue("$hash",value.CanonicalSha256);q.Parameters.AddWithValue("$schema",value.Schema);
        q.Parameters.AddWithValue("$generated",value.GeneratedAtUtc.ToString("O",CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$calibration",value.SourceCalibrationSha256);
        q.Parameters.AddWithValue("$calibrationStatus",value.SourceCalibrationStatus.ToString());q.Parameters.AddWithValue("$link",value.SourcePositionLinkId);q.Parameters.AddWithValue("$linkHash",value.SourcePositionLinkSha256);
        q.Parameters.AddWithValue("$executionTrace",value.SourceExecutionTraceSha256);q.Parameters.AddWithValue("$driftTrace",value.SourceDriftTraceSha256);
        q.Parameters.AddWithValue("$cursor",value.SourceLastExecutionEventId);q.Parameters.AddWithValue("$status",value.Status.ToString());
        q.Parameters.AddWithValue("$folds",value.RequiredFolds);q.Parameters.AddWithValue("$minimum",value.MinimumCompleteSamplesPerFold);
        q.Parameters.Add("$bytes",SqliteType.Blob).Value=value.CanonicalBytes;
        var inserted=Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
        await tx.CommitAsync(ct);return inserted;
    }
}

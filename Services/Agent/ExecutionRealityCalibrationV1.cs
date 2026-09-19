using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace 币安量化机器人.Services.Agent;

internal enum ExecutionRealityCalibrationStatusV1 { Available, Unsupported }

internal sealed record ExecutionRealityCalibrationBucketV1(
    string ProviderId,
    string Environment,
    string Symbol,
    ExecutionOrderType OrderType,
    bool ReduceOnly,
    int SampleCount,
    int FullFillCount,
    int PartialFillCount,
    int SlippageSampleCount,
    int SubmitLatencySampleCount,
    int IntentLatencySampleCount,
    int PreflightContextSampleCount,
    double PartialFillRate,
    double MedianFillRatio,
    double P10FillRatio,
    double? MedianAdverseSlippageBps,
    double? P90AdverseSlippageBps,
    double? MedianSubmitToFirstExchangeMilliseconds,
    double? P90SubmitToFirstExchangeMilliseconds,
    double? MedianIntentToFinalMilliseconds,
    double? P90IntentToFinalMilliseconds,
    double? MedianSpreadBps,
    double? MedianLiquidityScore,
    double? MedianAtrPercent,
    bool Qualified);

internal sealed record ExecutionRealityCalibrationReportV1(
    string Schema,
    DateTimeOffset GeneratedAtUtc,
    string SourcePositionLinkId,
    string SourcePositionLinkSha256,
    string SourceExecutionLedgerSha256,
    string SourceExecutionTraceSha256,
    string SourceDriftTraceSha256,
    long SourceLastExecutionEventId,
    string SourceDriftSchema,
    int MinimumSamplesPerBucket,
    ExecutionRealityCalibrationStatusV1 Status,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<ExecutionRealityCalibrationBucketV1> Buckets,
    string CanonicalSha256,
    byte[] CanonicalBytes);

internal sealed record ExecutionRealityCalibrationSourceV1(
    string PositionLinkId,
    string PositionLinkSha256,
    string ExecutionLedgerSha256,
    string ExecutionTraceSha256,
    string DriftTraceSha256,
    long LastExecutionEventId,
    DateTimeOffset LinkedAtUtc);

internal static class ExecutionRealityCalibrationCanonicalizerV1
{
    internal const string Schema="wpe.execution-reality-calibration/1.0";

    internal static ExecutionRealityCalibrationReportV1 Create(
        ExecutionRealityCalibrationSourceV1 source,
        DateTimeOffset generatedAtUtc,
        int minimumSamplesPerBucket,
        IReadOnlyList<ExecutionRealityCalibrationBucketV1> buckets,
        IReadOnlyList<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(buckets);ArgumentNullException.ThrowIfNull(reasonCodes);
        if(minimumSamplesPerBucket<1||source.LastExecutionEventId<1
           ||source.PositionLinkId!="execution-position-drift:"+source.PositionLinkSha256
           ||!Sha(source.PositionLinkSha256)||!Sha(source.ExecutionLedgerSha256)||!Sha(source.ExecutionTraceSha256)||!Sha(source.DriftTraceSha256))
            throw new ArgumentException("Execution calibration source is invalid.");
        generatedAtUtc=generatedAtUtc.ToUniversalTime();
        var rows=buckets.OrderBy(x=>x.ProviderId,StringComparer.Ordinal).ThenBy(x=>x.Environment,StringComparer.Ordinal)
            .ThenBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.OrderType).ThenBy(x=>x.ReduceOnly).ToArray();
        if(rows.Any(x=>!ValidBucket(x,minimumSamplesPerBucket)))throw new ArgumentException("Execution calibration bucket is invalid.");
        var reasons=reasonCodes.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        var status=rows.Any(x=>x.Qualified)?ExecutionRealityCalibrationStatusV1.Available:ExecutionRealityCalibrationStatusV1.Unsupported;
        if(status==ExecutionRealityCalibrationStatusV1.Unsupported&&!reasons.Contains("execution-calibration.samples-insufficient",StringComparer.Ordinal))
            reasons=[..reasons,"execution-calibration.samples-insufficient"];
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema=Schema,generatedAtUtc,
            sourcePositionLinkId=source.PositionLinkId,sourcePositionLinkSha256=source.PositionLinkSha256,
            sourceExecutionLedgerSha256=source.ExecutionLedgerSha256,sourceExecutionTraceSha256=source.ExecutionTraceSha256,
            sourceDriftTraceSha256=source.DriftTraceSha256,sourceLastExecutionEventId=source.LastExecutionEventId,
            sourceDriftSchema=ExecutionDriftCanonicalizerV1.Schema,
            minimumSamplesPerBucket,status=status.ToString().ToLowerInvariant(),reasonCodes=reasons,
            buckets=rows.Select(Row)
        });
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Schema,generatedAtUtc,source.PositionLinkId,source.PositionLinkSha256,source.ExecutionLedgerSha256,
            source.ExecutionTraceSha256,source.DriftTraceSha256,source.LastExecutionEventId,ExecutionDriftCanonicalizerV1.Schema,minimumSamplesPerBucket,
            status,reasons,rows,hash,bytes);
    }

    internal static bool IsCanonical(ExecutionRealityCalibrationReportV1 value)
    {
        if(value is null||value.Schema!=Schema||value.GeneratedAtUtc.Offset!=TimeSpan.Zero||value.MinimumSamplesPerBucket<1
           ||value.SourceLastExecutionEventId<1||value.SourceDriftSchema!=ExecutionDriftCanonicalizerV1.Schema
           ||value.SourcePositionLinkId!="execution-position-drift:"+value.SourcePositionLinkSha256
           ||!Sha(value.SourcePositionLinkSha256)||!Sha(value.SourceExecutionLedgerSha256)
           ||!Sha(value.SourceExecutionTraceSha256)||!Sha(value.SourceDriftTraceSha256)
           ||!Sha(value.CanonicalSha256)||value.CanonicalBytes is null)return false;
        try
        {
            var source=new ExecutionRealityCalibrationSourceV1(value.SourcePositionLinkId,value.SourcePositionLinkSha256,
                value.SourceExecutionLedgerSha256,value.SourceExecutionTraceSha256,value.SourceDriftTraceSha256,
                value.SourceLastExecutionEventId,value.GeneratedAtUtc);
            var expected=Create(source,value.GeneratedAtUtc,value.MinimumSamplesPerBucket,value.Buckets,value.ReasonCodes);
            return expected.Status==value.Status
                   &&string.Equals(expected.CanonicalSha256,value.CanonicalSha256,StringComparison.Ordinal)
                   &&CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes,value.CanonicalBytes);
        }
        catch(ArgumentException){return false;}
    }

    private static object Row(ExecutionRealityCalibrationBucketV1 x)=>new
    {
        x.ProviderId,x.Environment,x.Symbol,orderType=x.OrderType.ToString(),x.ReduceOnly,x.SampleCount,x.FullFillCount,x.PartialFillCount,
        x.SlippageSampleCount,x.SubmitLatencySampleCount,x.IntentLatencySampleCount,x.PreflightContextSampleCount,
        x.PartialFillRate,x.MedianFillRatio,x.P10FillRatio,x.MedianAdverseSlippageBps,x.P90AdverseSlippageBps,
        x.MedianSubmitToFirstExchangeMilliseconds,x.P90SubmitToFirstExchangeMilliseconds,
        x.MedianIntentToFinalMilliseconds,x.P90IntentToFinalMilliseconds,
        x.MedianSpreadBps,x.MedianLiquidityScore,x.MedianAtrPercent,x.Qualified
    };

    private static bool ValidBucket(ExecutionRealityCalibrationBucketV1 x,int minimum)
    {
        if(string.IsNullOrWhiteSpace(x.ProviderId)||string.IsNullOrWhiteSpace(x.Environment)||string.IsNullOrWhiteSpace(x.Symbol)
           ||!Enum.IsDefined(x.OrderType)||x.SampleCount<1||x.FullFillCount<0||x.PartialFillCount<0
           ||x.FullFillCount+x.PartialFillCount!=x.SampleCount||x.SlippageSampleCount<0||x.SlippageSampleCount>x.SampleCount
           ||x.SubmitLatencySampleCount<0||x.SubmitLatencySampleCount>x.SampleCount||x.IntentLatencySampleCount<0||x.IntentLatencySampleCount>x.SampleCount
           ||x.PreflightContextSampleCount<0||x.PreflightContextSampleCount>x.SampleCount
           ||!double.IsFinite(x.PartialFillRate)||x.PartialFillRate is<0 or>1
           ||!double.IsFinite(x.MedianFillRatio)||x.MedianFillRatio is<0 or>1
           ||!double.IsFinite(x.P10FillRatio)||x.P10FillRatio is<0 or>1)return false;
        foreach(var v in new[]{x.MedianAdverseSlippageBps,x.P90AdverseSlippageBps,x.MedianSubmitToFirstExchangeMilliseconds,
                    x.P90SubmitToFirstExchangeMilliseconds,x.MedianIntentToFinalMilliseconds,x.P90IntentToFinalMilliseconds,
                    x.MedianSpreadBps,x.MedianLiquidityScore,x.MedianAtrPercent})
            if(v is { } number&&!double.IsFinite(number))return false;
        if(Math.Abs(x.PartialFillRate-x.PartialFillCount/(double)x.SampleCount)>1e-12||x.P10FillRatio>x.MedianFillRatio)return false;
        if((x.SlippageSampleCount==0)!=(x.MedianAdverseSlippageBps is null)|| (x.SlippageSampleCount==0)!=(x.P90AdverseSlippageBps is null))return false;
        if(x.MedianAdverseSlippageBps is { } slipMedian&&x.P90AdverseSlippageBps is { } slipP90&&slipP90<slipMedian)return false;
        if((x.SubmitLatencySampleCount==0)!=(x.MedianSubmitToFirstExchangeMilliseconds is null)
           ||(x.SubmitLatencySampleCount==0)!=(x.P90SubmitToFirstExchangeMilliseconds is null))return false;
        if(x.MedianSubmitToFirstExchangeMilliseconds is { } submitMedian&&x.P90SubmitToFirstExchangeMilliseconds is { } submitP90
           &&(submitMedian<0||submitP90<submitMedian))return false;
        if((x.IntentLatencySampleCount==0)!=(x.MedianIntentToFinalMilliseconds is null)
           ||(x.IntentLatencySampleCount==0)!=(x.P90IntentToFinalMilliseconds is null))return false;
        if(x.MedianIntentToFinalMilliseconds is { } endMedian&&x.P90IntentToFinalMilliseconds is { } endP90
           &&(endMedian<0||endP90<endMedian))return false;
        if((x.PreflightContextSampleCount==0)!=(x.MedianSpreadBps is null)
           ||(x.PreflightContextSampleCount==0)!=(x.MedianLiquidityScore is null)
           ||(x.PreflightContextSampleCount==0)!=(x.MedianAtrPercent is null))return false;
        if(x.MedianSpreadBps is { } spread&&spread<0)return false;
        if(x.MedianLiquidityScore is { } liquidity&&liquidity is<0 or>1)return false;
        if(x.MedianAtrPercent is { } atr&&atr<0)return false;
        var qualified=x.SampleCount>=minimum&&x.SlippageSampleCount>=minimum
            &&x.SubmitLatencySampleCount>=minimum&&x.IntentLatencySampleCount>=minimum;
        return x.Qualified==qualified;
    }

    private static bool Sha(string value)=>value is{Length:64}&&value.All(Uri.IsHexDigit);
}

internal sealed record ExecutionRealityCalibrationObservationSetV1(
    bool SourceTraceMatches,
    string CurrentExecutionTraceSha256,
    string CurrentDriftTraceSha256,
    IReadOnlyList<ExecutionDriftSummaryV1> Summaries);

internal sealed class ExecutionRealityCalibrationServiceV1(AgentSqliteStore store)
{
    internal const int MinimumSamplesPerBucket=30;
    private readonly AgentSqliteStore _store=store??throw new ArgumentNullException(nameof(store));

    internal async Task<ExecutionRealityCalibrationReportV1?> BuildAsync(CancellationToken ct)
    {
        var source=await _store.GetExecutionRealityCalibrationSourceAsync(ct);
        if(source is null)return null;
        var observed=await _store.GetExecutionRealityCalibrationObservationSetAsync(source,ct);
        var eligible=observed.SourceTraceMatches?observed.Summaries.Where(Eligible).ToArray():[];
        var buckets=eligible.GroupBy(x=>new{x.ProviderId,x.Environment,x.Symbol,x.OrderType,x.ReduceOnly})
            .Select(g=>BuildBucket(g.Key.ProviderId,g.Key.Environment,g.Key.Symbol,g.Key.OrderType,g.Key.ReduceOnly,g.ToArray()))
            .OrderBy(x=>x.ProviderId,StringComparer.Ordinal).ThenBy(x=>x.Environment,StringComparer.Ordinal)
            .ThenBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.OrderType).ThenBy(x=>x.ReduceOnly).ToArray();
        var reasons=new List<string>();
        if(!observed.SourceTraceMatches)reasons.Add("execution-calibration.source-trace-mismatch");
        if(observed.SourceTraceMatches&&eligible.Length==0)reasons.Add("execution-calibration.no-current-schema-samples");
        if(buckets.All(x=>!x.Qualified))reasons.Add("execution-calibration.samples-insufficient");
        return ExecutionRealityCalibrationCanonicalizerV1.Create(source,source.LinkedAtUtc,MinimumSamplesPerBucket,buckets,reasons);
    }

    internal async Task<bool> BuildAndPersistSafelyAsync(CancellationToken ct)
    {
        try
        {
            var report=await BuildAsync(ct);
            return report is not null&&await _store.SaveExecutionRealityCalibrationAsync(report,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch
        {
            // Calibration is an observational research artifact. It must never alter trading authority or execution outcomes.
            return false;
        }
    }

    private static bool Eligible(ExecutionDriftSummaryV1 x)
        =>string.Equals(x.Environment,"Testnet",StringComparison.Ordinal)
          &&!x.ClientOrderId.EndsWith("-E",StringComparison.Ordinal)
          &&x.RequestedQuantity>0&&x.FinalObservedQuantity>0&&x.FillRatio is>0 and<=1
          &&x.FinalStatus is "FILLED" or "PARTIALLY_FILLED";

    internal static ExecutionRealityCalibrationBucketV1 BuildBucket(
        string provider,string environment,string symbol,ExecutionOrderType orderType,bool reduceOnly,IReadOnlyList<ExecutionDriftSummaryV1> values)
    {
        var fill=values.Select(x=>(double)x.FillRatio).ToArray();
        var slip=values.Where(x=>x.AdverseSlippageBps is not null).Select(x=>x.AdverseSlippageBps!.Value).ToArray();
        var submit=values.Where(x=>x.SubmitToFirstExchangeMilliseconds is>=0).Select(x=>x.SubmitToFirstExchangeMilliseconds!.Value).ToArray();
        var end=values.Where(x=>x.IntentToFinalMilliseconds is>=0).Select(x=>x.IntentToFinalMilliseconds!.Value).ToArray();
        var context=values.Where(x=>x.PreflightPrice is>0&&x.PreflightSpreadBps is>=0&&x.PreflightLiquidityScore is>=0&&x.PreflightAtrPercent is>=0).ToArray();
        var partial=values.Count(x=>x.FillRatio<1);
        var qualified=values.Count>=MinimumSamplesPerBucket&&slip.Length>=MinimumSamplesPerBucket
            &&submit.Length>=MinimumSamplesPerBucket&&end.Length>=MinimumSamplesPerBucket;
        return new(provider,environment,symbol,orderType,reduceOnly,values.Count,values.Count-partial,partial,
            slip.Length,submit.Length,end.Length,context.Length,partial/(double)values.Count,Q(fill,.5),Q(fill,.1),
            Qn(slip,.5),Qn(slip,.9),Qn(submit,.5),Qn(submit,.9),Qn(end,.5),Qn(end,.9),
            Qn(context.Select(x=>x.PreflightSpreadBps!.Value).ToArray(),.5),
            Qn(context.Select(x=>x.PreflightLiquidityScore!.Value).ToArray(),.5),
            Qn(context.Select(x=>x.PreflightAtrPercent!.Value).ToArray(),.5),qualified);
    }

    private static double? Qn(IReadOnlyList<double> values,double p)=>values.Count==0?null:Q(values,p);
    private static double Q(IReadOnlyList<double> values,double p)
    {
        if(values.Count==0)throw new ArgumentException("Quantile requires at least one value.",nameof(values));
        var sorted=values.OrderBy(x=>x).ToArray();if(sorted.Length==1)return sorted[0];
        var position=(sorted.Length-1)*Math.Clamp(p,0,1);var low=(int)Math.Floor(position);var high=(int)Math.Ceiling(position);
        if(low==high)return sorted[low];var weight=position-low;return sorted[low]+(sorted[high]-sorted[low])*weight;
    }
}

public sealed partial class AgentSqliteStore
{
    private void EnsureExecutionRealityCalibrationSchema()
    {
        using var c=new SqliteConnection(_cs);c.Open();using var q=c.CreateCommand();q.CommandText="""
            CREATE TABLE IF NOT EXISTS execution_reality_calibrations(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                generated_at TEXT NOT NULL,
                source_position_link_id TEXT NOT NULL,
                source_position_link_sha256 TEXT NOT NULL,
                source_execution_ledger_sha256 TEXT NOT NULL,
                source_execution_trace_sha256 TEXT NOT NULL,
                source_drift_trace_sha256 TEXT NOT NULL,
                source_last_execution_event_id INTEGER NOT NULL,
                source_drift_schema TEXT NOT NULL,
                minimum_samples_per_bucket INTEGER NOT NULL,
                status TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(schema,source_execution_trace_sha256,source_drift_trace_sha256,source_drift_schema,minimum_samples_per_bucket));
            CREATE INDEX IF NOT EXISTS ix_execution_reality_calibration_time ON execution_reality_calibrations(generated_at DESC);
            CREATE TRIGGER IF NOT EXISTS execution_reality_calibrations_no_update BEFORE UPDATE ON execution_reality_calibrations BEGIN SELECT RAISE(ABORT,'execution reality calibrations are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_reality_calibrations_no_delete BEFORE DELETE ON execution_reality_calibrations BEGIN SELECT RAISE(ABORT,'execution reality calibrations are append-only'); END;
            """;
        q.ExecuteNonQuery();
    }

    internal async Task<ExecutionRealityCalibrationSourceV1?> GetExecutionRealityCalibrationSourceAsync(CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
            SELECT l.link_id,l.canonical_sha256,l.execution_ledger_sha256,s.execution_trace_sha256,s.drift_trace_sha256,s.last_execution_event_id,l.linked_at
            FROM execution_position_drift_reconciliations l
            JOIN execution_position_ledger_snapshots s ON s.canonical_sha256=l.execution_ledger_sha256
            WHERE l.position_state='Confirmed' AND l.allows_risk_increase=1 AND l.calibratable=1
            ORDER BY l.linked_at DESC,l.rowid DESC LIMIT 1
            """;
        await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
        return new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5),
            DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime());
    }

    internal async Task<ExecutionRealityCalibrationObservationSetV1> GetExecutionRealityCalibrationObservationSetAsync(
        ExecutionRealityCalibrationSourceV1 source,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if(source.LastExecutionEventId<1)return new(false,new string('0',64),new string('0',64),[]);

        var events=new List<(long Id,string ClientOrderId,string Symbol,PositionSide Side,bool ReduceOnly,decimal Quantity,string Status,string OccurredAt,string? ExchangeUpdatedAt)>();
        var driftValues=new List<ExecutionDriftObservationV1>();
        await using(var c=new SqliteConnection(_cs))
        {
            await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
            await using(var q=c.CreateCommand())
            {
                q.Transaction=tx;q.CommandText="""
                    SELECT id,client_order_id,symbol,side,reduce_only,quantity,status,occurred_at,exchange_updated_at
                    FROM execution_events
                    WHERE id<=$cursor AND status IN ('FILLED','PARTIALLY_FILLED')
                    ORDER BY id
                    """;
                q.Parameters.AddWithValue("$cursor",source.LastExecutionEventId);
                await using var r=await q.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct))
                {
                    if(!Enum.TryParse<PositionSide>(r.GetString(3),true,out var side)
                       ||!decimal.TryParse(r.GetString(5),NumberStyles.Number,CultureInfo.InvariantCulture,out var quantity)||quantity<0)
                        return new(false,new string('0',64),new string('0',64),[]);
                    var id=r.GetInt64(0);var clientOrderId=r.IsDBNull(1)?$"legacy-event:{id}":r.GetString(1);
                    events.Add((id,clientOrderId,r.GetString(2),side,r.GetInt32(4)==1,quantity,r.GetString(6),
                        r.IsDBNull(7)?"unknown":r.GetString(7),r.IsDBNull(8)?null:r.GetString(8)));
                }
            }
            await using(var q=c.CreateCommand())
            {
                q.Transaction=tx;q.CommandText="""
                    SELECT d.schema,d.cycle_id,d.client_order_id,d.sequence,d.phase,d.source,d.observed_at,d.exchange_updated_at,
                           d.provider_id,d.environment,d.symbol,d.side,d.reduce_only,d.order_type,d.requested_quantity,d.limit_price,
                           d.observed_executed_quantity,d.expected_price,d.observed_average_price,d.provider_status,
                           d.spread_bps,d.liquidity_score,d.atr_percent,d.canonical_sha256,d.canonical_bytes
                    FROM execution_drift_observations d
                    JOIN execution_events e ON e.client_order_id=d.client_order_id
                    WHERE e.id<=$cursor AND e.status IN ('FILLED','PARTIALLY_FILLED') AND d.schema=$schema
                    ORDER BY d.client_order_id,d.sequence
                    """;
                q.Parameters.AddWithValue("$cursor",source.LastExecutionEventId);q.Parameters.AddWithValue("$schema",ExecutionDriftCanonicalizerV1.Schema);
                await using var r=await q.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct))
                {
                    if(!Enum.TryParse<ExecutionDriftPhaseV1>(r.GetString(4),out var phase)
                       ||!Enum.TryParse<ExecutionDriftSourceV1>(r.GetString(5),out var driftSource)
                       ||!Enum.TryParse<PositionSide>(r.GetString(11),out var side)
                       ||!Enum.TryParse<ExecutionOrderType>(r.GetString(13),out var orderType))
                        return new(false,new string('0',64),new string('0',64),[]);
                    var value=new ExecutionDriftObservationV1(
                        r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt32(3),phase,driftSource,
                        DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime(),
                        r.IsDBNull(7)?null:DateTimeOffset.Parse(r.GetString(7),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime(),
                        r.GetString(8),r.GetString(9),r.GetString(10),side,r.GetInt32(12)==1,orderType,
                        decimal.Parse(r.GetString(14),CultureInfo.InvariantCulture),decimal.Parse(r.GetString(15),CultureInfo.InvariantCulture),
                        decimal.Parse(r.GetString(16),CultureInfo.InvariantCulture),decimal.Parse(r.GetString(17),CultureInfo.InvariantCulture),
                        decimal.Parse(r.GetString(18),CultureInfo.InvariantCulture),r.GetString(19),
                        double.Parse(r.GetString(20),CultureInfo.InvariantCulture),double.Parse(r.GetString(21),CultureInfo.InvariantCulture),
                        double.Parse(r.GetString(22),CultureInfo.InvariantCulture),r.GetString(23),(byte[])r[24]);
                    if(!ExecutionDriftCanonicalizerV1.IsCanonical(value))
                        return new(false,new string('0',64),new string('0',64),[]);
                    driftValues.Add(value);
                }
            }

            var eventTraceBytes=JsonSerializer.SerializeToUtf8Bytes(events.Select(x=>new
            {
                x.Id,x.ClientOrderId,x.Symbol,side=x.Side.ToString(),x.ReduceOnly,
                quantity=x.Quantity.ToString("G29",CultureInfo.InvariantCulture),x.Status,x.OccurredAt,x.ExchangeUpdatedAt
            }));
            var driftTraceBytes=JsonSerializer.SerializeToUtf8Bytes(driftValues.Select(x=>new
            {
                x.Schema,x.ClientOrderId,x.Sequence,Phase=x.Phase.ToString(),x.CanonicalSha256
            }));
            var eventHash=Convert.ToHexString(SHA256.HashData(eventTraceBytes)).ToLowerInvariant();
            var driftHash=Convert.ToHexString(SHA256.HashData(driftTraceBytes)).ToLowerInvariant();
            var matches=string.Equals(eventHash,source.ExecutionTraceSha256,StringComparison.Ordinal)
                        &&string.Equals(driftHash,source.DriftTraceSha256,StringComparison.Ordinal);
            if(!matches){await tx.CommitAsync(ct);return new(false,eventHash,driftHash,[]);}

            var summaries=driftValues.GroupBy(x=>x.ClientOrderId,StringComparer.Ordinal)
                .Select(group=>SummarizeFrozenDrift(group.OrderBy(x=>x.Sequence).ToArray()))
                .Where(x=>x is not null).Select(x=>x!).ToArray();
            await tx.CommitAsync(ct);
            return new(true,eventHash,driftHash,summaries);
        }
    }

    private static ExecutionDriftSummaryV1? SummarizeFrozenDrift(IReadOnlyList<ExecutionDriftObservationV1> values)
    {
        if(values.Count==0||values.Select(x=>x.Sequence).Distinct().Count()!=values.Count)return null;
        var first=values[0];
        if(values.Any(x=>!string.Equals(x.ProviderId,first.ProviderId,StringComparison.Ordinal)
                         ||!string.Equals(x.Environment,first.Environment,StringComparison.Ordinal)
                         ||!string.Equals(x.Symbol,first.Symbol,StringComparison.Ordinal)
                         ||x.Side!=first.Side||x.ReduceOnly!=first.ReduceOnly||x.OrderType!=first.OrderType
                         ||x.RequestedQuantity!=first.RequestedQuantity||x.ExpectedPrice!=first.ExpectedPrice||x.LimitPrice!=first.LimitPrice))
            return null;
        var exchange=values.Where(x=>x.Source==ExecutionDriftSourceV1.Exchange).ToArray();
        var final=values.LastOrDefault(x=>x.Phase==ExecutionDriftPhaseV1.ExecutionRecorded)??values[^1];
        var submit=values.FirstOrDefault(x=>x.Phase==ExecutionDriftPhaseV1.SubmissionAttempted);
        var firstExchange=exchange.FirstOrDefault();
        var preflight=values.LastOrDefault(x=>x.Phase==ExecutionDriftPhaseV1.PreflightQuote);
        var intentAccepted=values.FirstOrDefault(x=>x.Phase==ExecutionDriftPhaseV1.IntentAccepted)??first;
        var fillRatio=final.RequestedQuantity>0?final.ObservedExecutedQuantity/final.RequestedQuantity:0;
        double? submitMs=submit is not null&&firstExchange is not null?(firstExchange.ObservedAtUtc-submit.ObservedAtUtc).TotalMilliseconds:null;
        var endMs=(final.ObservedAtUtc-intentAccepted.ObservedAtUtc).TotalMilliseconds;
        double? adverse=null;
        if(final.ExpectedPrice>0&&final.ObservedAveragePrice>0)
        {
            var direction=final.Side==PositionSide.Long?1m:-1m;
            adverse=(double)(direction*(final.ObservedAveragePrice-final.ExpectedPrice)/final.ExpectedPrice*10000m);
        }
        return new(final.ClientOrderId,first.ProviderId,first.Environment,first.Symbol,first.Side,first.ReduceOnly,first.OrderType,first.LimitPrice,
            values.Count,exchange.Length,exchange.Count(x=>x.ObservedExecutedQuantity>0&&x.ObservedExecutedQuantity<x.RequestedQuantity),
            final.RequestedQuantity,final.ObservedExecutedQuantity,fillRatio,final.ExpectedPrice,final.ObservedAveragePrice,
            preflight?.ObservedAveragePrice,preflight?.SpreadBps,preflight?.LiquidityScore,preflight?.AtrPercent,
            submitMs,endMs,adverse,final.ProviderStatus);
    }

    internal async Task<bool> SaveExecutionRealityCalibrationAsync(ExecutionRealityCalibrationReportV1 value,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(!ExecutionRealityCalibrationCanonicalizerV1.IsCanonical(value))throw new InvalidOperationException("Execution reality calibration canonical identity is invalid.");
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
            INSERT OR IGNORE INTO execution_reality_calibrations(
                canonical_sha256,schema,generated_at,source_position_link_id,source_position_link_sha256,
                source_execution_ledger_sha256,source_execution_trace_sha256,source_drift_trace_sha256,source_last_execution_event_id,
                source_drift_schema,minimum_samples_per_bucket,status,canonical_bytes)
            VALUES($hash,$schema,$generated,$link,$linkHash,$ledger,$trace,$driftTrace,$cursor,$driftSchema,$minimum,$status,$bytes);
            SELECT changes();
            """;
        q.Parameters.AddWithValue("$hash",value.CanonicalSha256);q.Parameters.AddWithValue("$schema",value.Schema);
        q.Parameters.AddWithValue("$generated",value.GeneratedAtUtc.ToString("O",CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$link",value.SourcePositionLinkId);q.Parameters.AddWithValue("$linkHash",value.SourcePositionLinkSha256);
        q.Parameters.AddWithValue("$ledger",value.SourceExecutionLedgerSha256);q.Parameters.AddWithValue("$trace",value.SourceExecutionTraceSha256);
        q.Parameters.AddWithValue("$driftTrace",value.SourceDriftTraceSha256);q.Parameters.AddWithValue("$cursor",value.SourceLastExecutionEventId);q.Parameters.AddWithValue("$driftSchema",value.SourceDriftSchema);
        q.Parameters.AddWithValue("$minimum",value.MinimumSamplesPerBucket);q.Parameters.AddWithValue("$status",value.Status.ToString());
        q.Parameters.Add("$bytes",SqliteType.Blob).Value=value.CanonicalBytes;
        return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }
}

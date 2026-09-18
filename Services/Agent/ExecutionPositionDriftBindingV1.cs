using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace 币安量化机器人.Services.Agent;

internal sealed record ExecutionPositionLedgerSnapshotV1(
    string Schema,
    DateTimeOffset CapturedAtUtc,
    long LastExecutionEventId,
    int ExecutionEventCount,
    int DriftObservationCount,
    int DriftTrackedExecutionCount,
    bool DriftIntegrityValid,
    bool DriftCoverageComplete,
    string ExecutionTraceSha256,
    string DriftTraceSha256,
    IReadOnlyList<ExecutionPositionLegV1> Legs,
    string CanonicalSha256,
    byte[] CanonicalBytes);

internal sealed record ExecutionPositionDriftLegV1(
    string Symbol,
    PositionSide Side,
    decimal LocalQuantity,
    decimal ExchangeQuantity,
    decimal QuantityDrift);

internal sealed record ExecutionPositionDriftReconciliationV1(
    string Schema,
    string LinkId,
    DateTimeOffset LinkedAtUtc,
    string ExecutionLedgerSha256,
    string PositionReportId,
    string PositionReportSha256,
    PositionReconciliationStateV1 PositionState,
    bool AllowsRiskIncrease,
    bool DriftIntegrityValid,
    bool DriftCoverageComplete,
    bool Calibratable,
    int ExecutionEventCount,
    int DriftObservationCount,
    int DriftTrackedExecutionCount,
    string ExecutionTraceSha256,
    string DriftTraceSha256,
    IReadOnlyList<ExecutionPositionDriftLegV1> Legs,
    string CanonicalSha256,
    byte[] CanonicalBytes);

internal static class ExecutionPositionLedgerSnapshotCanonicalizerV1
{
    internal const string Schema="wpe.execution-position-ledger/1.0";

    internal static ExecutionPositionLedgerSnapshotV1 Create(
        DateTimeOffset capturedAtUtc,
        long lastExecutionEventId,
        int executionEventCount,
        int driftObservationCount,
        int driftTrackedExecutionCount,
        bool driftIntegrityValid,
        bool driftCoverageComplete,
        string executionTraceSha256,
        string driftTraceSha256,
        IReadOnlyList<ExecutionPositionLegV1> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        capturedAtUtc=capturedAtUtc.ToUniversalTime();
        if(capturedAtUtc.Offset!=TimeSpan.Zero||lastExecutionEventId<0||executionEventCount<0||driftObservationCount<0
           ||driftTrackedExecutionCount<0||driftTrackedExecutionCount>executionEventCount||!Sha(executionTraceSha256)||!Sha(driftTraceSha256))
            throw new ArgumentException("Execution position ledger snapshot metadata is invalid.");
        if(driftCoverageComplete&&(!driftIntegrityValid||executionEventCount==0||driftTrackedExecutionCount!=executionEventCount))
            throw new ArgumentException("Execution position ledger drift coverage is inconsistent.");
        var normalized=NormalizeLegs(legs);
        var bytes=CanonicalBytes(capturedAtUtc,lastExecutionEventId,executionEventCount,driftObservationCount,driftTrackedExecutionCount,
            driftIntegrityValid,driftCoverageComplete,executionTraceSha256,driftTraceSha256,normalized);
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Schema,capturedAtUtc,lastExecutionEventId,executionEventCount,driftObservationCount,driftTrackedExecutionCount,
            driftIntegrityValid,driftCoverageComplete,executionTraceSha256,driftTraceSha256,normalized,hash,bytes);
    }

    internal static bool IsCanonical(ExecutionPositionLedgerSnapshotV1 value)
    {
        if(value is null||value.Schema!=Schema||!Sha(value.CanonicalSha256)||value.CanonicalBytes is null)return false;
        try
        {
            var expected=Create(value.CapturedAtUtc,value.LastExecutionEventId,value.ExecutionEventCount,value.DriftObservationCount,
                value.DriftTrackedExecutionCount,value.DriftIntegrityValid,value.DriftCoverageComplete,value.ExecutionTraceSha256,
                value.DriftTraceSha256,value.Legs);
            return string.Equals(expected.CanonicalSha256,value.CanonicalSha256,StringComparison.Ordinal)
                   &&CryptographicOperations.FixedTimeEquals(expected.CanonicalBytes,value.CanonicalBytes);
        }
        catch(ArgumentException){return false;}
    }

    private static ExecutionPositionLegV1[] NormalizeLegs(IReadOnlyList<ExecutionPositionLegV1> legs)
    {
        var seen=new HashSet<(string Symbol,PositionSide Side)>();
        var result=new List<ExecutionPositionLegV1>();
        foreach(var row in legs)
        {
            var symbol=(row.Symbol??string.Empty).Trim().ToUpperInvariant();
            if(string.IsNullOrWhiteSpace(symbol)||symbol.Length>80||!Enum.IsDefined(row.Side)||!seen.Add((symbol,row.Side)))
                throw new ArgumentException("Execution position ledger leg identity is invalid.");
            if(row.Quantity!=0)result.Add(new(symbol,row.Side,row.Quantity));
        }
        return result.OrderBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.Side).ToArray();
    }

    private static byte[] CanonicalBytes(
        DateTimeOffset capturedAtUtc,long lastExecutionEventId,int executionEventCount,int driftObservationCount,int driftTrackedExecutionCount,
        bool driftIntegrityValid,bool driftCoverageComplete,string executionTraceSha256,string driftTraceSha256,
        IReadOnlyList<ExecutionPositionLegV1> legs)
        =>JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema=Schema,
            capturedAtUtc,
            lastExecutionEventId,
            executionEventCount,
            driftObservationCount,
            driftTrackedExecutionCount,
            driftIntegrityValid,
            driftCoverageComplete,
            executionTraceSha256,
            driftTraceSha256,
            legs=legs.Select(x=>new{symbol=x.Symbol,side=x.Side.ToString().ToLowerInvariant(),quantity=x.Quantity.ToString("G29",CultureInfo.InvariantCulture)})
        });

    internal static bool Sha(string value)=>value is{Length:64}&&value.All(Uri.IsHexDigit);
    private static bool Symbol(string value)=>value.Length is>=5 and<=30&&value.All(c=>c is>='A' and<='Z' or>='0' and<='9');
}

internal static class ExecutionPositionDriftReconciliationCanonicalizerV1
{
    internal const string Schema="wpe.execution-position-drift-reconciliation/1.0";

    internal static ExecutionPositionDriftReconciliationV1 Create(
        ExecutionPositionLedgerSnapshotV1 snapshot,
        PositionReconciliationReportV1 report,
        DateTimeOffset linkedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);ArgumentNullException.ThrowIfNull(report);
        if(!ExecutionPositionLedgerSnapshotCanonicalizerV1.IsCanonical(snapshot)||!PositionReconciliationServiceV1.IsCanonical(report))
            throw new ArgumentException("Execution position drift inputs must be canonical.");
        if(!SameLegs(snapshot.Legs,report.LocalLegs))
            throw new ArgumentException("Position reconciliation local legs do not match the captured execution ledger snapshot.");

        linkedAtUtc=linkedAtUtc.ToUniversalTime();
        var local=snapshot.Legs.ToDictionary(x=>(x.Symbol,x.Side),x=>x.Quantity);
        var exchange=report.ExchangeLegs.ToDictionary(x=>(x.Symbol,x.Side),x=>x.Quantity);
        var legs=local.Keys.Union(exchange.Keys)
            .OrderBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.Side)
            .Select(x=>
            {
                var l=local.GetValueOrDefault(x);var e=exchange.GetValueOrDefault(x);
                return new ExecutionPositionDriftLegV1(x.Symbol,x.Side,l,e,e-l);
            }).ToArray();
        var calibratable=snapshot.DriftCoverageComplete&&snapshot.DriftIntegrityValid
            &&report.State==PositionReconciliationStateV1.Confirmed&&snapshot.ExecutionEventCount>0;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema=Schema,linkedAtUtc,
            executionLedgerSha256=snapshot.CanonicalSha256,
            positionReportId=report.ReportId,positionReportSha256=report.CanonicalSha256,
            positionState=report.State.ToString().ToLowerInvariant(),report.AllowsRiskIncrease,
            snapshot.DriftIntegrityValid,snapshot.DriftCoverageComplete,calibratable,
            snapshot.ExecutionEventCount,snapshot.DriftObservationCount,snapshot.DriftTrackedExecutionCount,
            snapshot.ExecutionTraceSha256,snapshot.DriftTraceSha256,
            legs=legs.Select(x=>new{symbol=x.Symbol,side=x.Side.ToString().ToLowerInvariant(),
                localQuantity=x.LocalQuantity.ToString("G29",CultureInfo.InvariantCulture),
                exchangeQuantity=x.ExchangeQuantity.ToString("G29",CultureInfo.InvariantCulture),
                quantityDrift=x.QuantityDrift.ToString("G29",CultureInfo.InvariantCulture)})
        });
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Schema,"execution-position-drift:"+hash,linkedAtUtc,snapshot.CanonicalSha256,report.ReportId,report.CanonicalSha256,
            report.State,report.AllowsRiskIncrease,snapshot.DriftIntegrityValid,snapshot.DriftCoverageComplete,calibratable,
            snapshot.ExecutionEventCount,snapshot.DriftObservationCount,snapshot.DriftTrackedExecutionCount,
            snapshot.ExecutionTraceSha256,snapshot.DriftTraceSha256,legs,hash,bytes);
    }

    internal static bool IsCanonical(ExecutionPositionDriftReconciliationV1 value)
    {
        if(value is null||value.Schema!=Schema||value.LinkId!="execution-position-drift:"+value.CanonicalSha256
           ||!ExecutionPositionLedgerSnapshotCanonicalizerV1.Sha(value.CanonicalSha256)
           ||!ExecutionPositionLedgerSnapshotCanonicalizerV1.Sha(value.ExecutionLedgerSha256)
           ||!ExecutionPositionLedgerSnapshotCanonicalizerV1.Sha(value.PositionReportSha256)
           ||!ExecutionPositionLedgerSnapshotCanonicalizerV1.Sha(value.ExecutionTraceSha256)
           ||!ExecutionPositionLedgerSnapshotCanonicalizerV1.Sha(value.DriftTraceSha256)
           ||value.CanonicalBytes is null)return false;
        try
        {
            var expectedBytes=JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema=Schema,linkedAtUtc=value.LinkedAtUtc.ToUniversalTime(),
                executionLedgerSha256=value.ExecutionLedgerSha256,
                positionReportId=value.PositionReportId,positionReportSha256=value.PositionReportSha256,
                positionState=value.PositionState.ToString().ToLowerInvariant(),value.AllowsRiskIncrease,
                value.DriftIntegrityValid,value.DriftCoverageComplete,calibratable=value.Calibratable,
                value.ExecutionEventCount,value.DriftObservationCount,value.DriftTrackedExecutionCount,
                value.ExecutionTraceSha256,value.DriftTraceSha256,
                legs=value.Legs.Select(x=>new{symbol=x.Symbol,side=x.Side.ToString().ToLowerInvariant(),
                    localQuantity=x.LocalQuantity.ToString("G29",CultureInfo.InvariantCulture),
                    exchangeQuantity=x.ExchangeQuantity.ToString("G29",CultureInfo.InvariantCulture),
                    quantityDrift=x.QuantityDrift.ToString("G29",CultureInfo.InvariantCulture)})
            });
            return CryptographicOperations.FixedTimeEquals(expectedBytes,value.CanonicalBytes)
                   &&string.Equals(Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant(),value.CanonicalSha256,StringComparison.Ordinal);
        }
        catch{return false;}
    }

    private static bool SameLegs(IReadOnlyList<ExecutionPositionLegV1> left,IReadOnlyList<ExecutionPositionLegV1> right)
    {
        if(left.Count!=right.Count)return false;
        var a=left.OrderBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.Side).ToArray();
        var b=right.OrderBy(x=>x.Symbol,StringComparer.Ordinal).ThenBy(x=>x.Side).ToArray();
        return a.Zip(b).All(x=>string.Equals(x.First.Symbol,x.Second.Symbol,StringComparison.Ordinal)
                               &&x.First.Side==x.Second.Side&&x.First.Quantity==x.Second.Quantity);
    }
}

public sealed partial class AgentSqliteStore
{
    private sealed record ExecutionPositionEventRow(
        long Id,string ClientOrderId,string Symbol,PositionSide Side,bool ReduceOnly,decimal Quantity,string Status,string OccurredAt,string? ExchangeUpdatedAt);

    private void EnsureExecutionPositionDriftSchema()
    {
        using var c=new SqliteConnection(_cs);c.Open();using var q=c.CreateCommand();q.CommandText="""
            CREATE TABLE IF NOT EXISTS execution_position_ledger_snapshots(
                canonical_sha256 TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                last_execution_event_id INTEGER NOT NULL,
                execution_event_count INTEGER NOT NULL,
                drift_observation_count INTEGER NOT NULL,
                drift_tracked_execution_count INTEGER NOT NULL,
                drift_integrity_valid INTEGER NOT NULL CHECK(drift_integrity_valid IN (0,1)),
                drift_coverage_complete INTEGER NOT NULL CHECK(drift_coverage_complete IN (0,1)),
                execution_trace_sha256 TEXT NOT NULL,
                drift_trace_sha256 TEXT NOT NULL,
                canonical_bytes BLOB NOT NULL);
            CREATE TRIGGER IF NOT EXISTS execution_position_ledger_snapshots_no_update BEFORE UPDATE ON execution_position_ledger_snapshots BEGIN SELECT RAISE(ABORT,'execution position ledger snapshots are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_position_ledger_snapshots_no_delete BEFORE DELETE ON execution_position_ledger_snapshots BEGIN SELECT RAISE(ABORT,'execution position ledger snapshots are append-only'); END;
            CREATE TABLE IF NOT EXISTS execution_position_drift_reconciliations(
                link_id TEXT PRIMARY KEY,
                schema TEXT NOT NULL,
                linked_at TEXT NOT NULL,
                execution_ledger_sha256 TEXT NOT NULL,
                position_report_id TEXT NOT NULL,
                position_report_sha256 TEXT NOT NULL,
                position_state TEXT NOT NULL,
                allows_risk_increase INTEGER NOT NULL CHECK(allows_risk_increase IN (0,1)),
                drift_integrity_valid INTEGER NOT NULL CHECK(drift_integrity_valid IN (0,1)),
                drift_coverage_complete INTEGER NOT NULL CHECK(drift_coverage_complete IN (0,1)),
                calibratable INTEGER NOT NULL CHECK(calibratable IN (0,1)),
                execution_event_count INTEGER NOT NULL,
                drift_observation_count INTEGER NOT NULL,
                drift_tracked_execution_count INTEGER NOT NULL,
                execution_trace_sha256 TEXT NOT NULL,
                drift_trace_sha256 TEXT NOT NULL,
                canonical_sha256 TEXT NOT NULL UNIQUE,
                canonical_bytes BLOB NOT NULL,
                UNIQUE(execution_ledger_sha256,position_report_sha256));
            CREATE INDEX IF NOT EXISTS ix_execution_position_drift_report ON execution_position_drift_reconciliations(position_report_id,linked_at);
            CREATE TRIGGER IF NOT EXISTS execution_position_drift_reconciliations_no_update BEFORE UPDATE ON execution_position_drift_reconciliations BEGIN SELECT RAISE(ABORT,'execution position drift reconciliations are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS execution_position_drift_reconciliations_no_delete BEFORE DELETE ON execution_position_drift_reconciliations BEGIN SELECT RAISE(ABORT,'execution position drift reconciliations are append-only'); END;
            """;
        q.ExecuteNonQuery();
    }

    internal async Task<ExecutionPositionLedgerSnapshotV1> GetExecutionPositionLedgerSnapshotAsync(CancellationToken ct)
    {
        var events=new List<ExecutionPositionEventRow>();var totals=new Dictionary<(string Symbol,PositionSide Side),decimal>();
        var driftRows=new List<(string ClientOrderId,int Sequence,string Phase,string CanonicalSha256,byte[] CanonicalBytes)>();
        var driftIntegrity=true;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using(var q=c.CreateCommand())
        {
            q.Transaction=tx;q.CommandText="""
                SELECT id,client_order_id,symbol,side,reduce_only,quantity,status,occurred_at,exchange_updated_at
                FROM execution_events WHERE status IN ('FILLED','PARTIALLY_FILLED') ORDER BY id
                """;
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                if(!decimal.TryParse(r.GetString(5),NumberStyles.Number,CultureInfo.InvariantCulture,out var quantity)||quantity<0
                   ||!Enum.TryParse<PositionSide>(r.GetString(3),true,out var side))
                    throw new InvalidOperationException("Execution position ledger row is invalid.");
                var row=new ExecutionPositionEventRow(r.GetInt64(0),r.GetString(1),r.GetString(2),side,r.GetInt32(4)==1,quantity,
                    r.GetString(6),r.GetString(7),r.IsDBNull(8)?null:r.GetString(8));
                events.Add(row);
                var key=(row.Symbol,row.Side);totals[key]=totals.GetValueOrDefault(key)+(row.ReduceOnly?-row.Quantity:row.Quantity);
            }
        }

        try
        {
            await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="""
                SELECT d.client_order_id,d.sequence,d.phase,d.canonical_sha256,d.canonical_bytes
                FROM execution_drift_observations d
                JOIN execution_events e ON e.client_order_id=d.client_order_id
                WHERE e.status IN ('FILLED','PARTIALLY_FILLED')
                ORDER BY d.client_order_id,d.sequence
                """;
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var hash=r.GetString(3);var bytes=(byte[])r[4];
                if(!ExecutionPositionLedgerSnapshotCanonicalizerV1.Sha(hash)
                   ||!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),hash,StringComparison.Ordinal))
                    driftIntegrity=false;
                driftRows.Add((r.GetString(0),r.GetInt32(1),r.GetString(2),hash,bytes));
            }
        }
        catch(SqliteException)
        {
            // Execution authority comes from execution_events. Missing/corrupt drift evidence disables calibration only.
            driftIntegrity=false;driftRows.Clear();
        }
        await tx.CommitAsync(ct);

        var eventTraceBytes=JsonSerializer.SerializeToUtf8Bytes(events.Select(x=>new
        {
            x.Id,x.ClientOrderId,x.Symbol,side=x.Side.ToString(),x.ReduceOnly,
            quantity=x.Quantity.ToString("G29",CultureInfo.InvariantCulture),x.Status,x.OccurredAt,x.ExchangeUpdatedAt
        }));
        var driftTraceBytes=JsonSerializer.SerializeToUtf8Bytes(driftRows.Select(x=>new{x.ClientOrderId,x.Sequence,x.Phase,x.CanonicalSha256}));
        var eventHash=Convert.ToHexString(SHA256.HashData(eventTraceBytes)).ToLowerInvariant();
        var driftHash=Convert.ToHexString(SHA256.HashData(driftTraceBytes)).ToLowerInvariant();
        var eventClients=events.Select(x=>x.ClientOrderId).ToHashSet(StringComparer.Ordinal);
        var tracked=driftRows.Where(x=>string.Equals(x.Phase,ExecutionDriftPhaseV1.ExecutionRecorded.ToString(),StringComparison.Ordinal))
            .Select(x=>x.ClientOrderId).Where(eventClients.Contains).Distinct(StringComparer.Ordinal).Count();
        var coverage=driftIntegrity&&events.Count>0&&tracked==events.Count;
        var legs=totals.Where(x=>x.Value!=0).OrderBy(x=>x.Key.Symbol,StringComparer.Ordinal).ThenBy(x=>x.Key.Side)
            .Select(x=>new ExecutionPositionLegV1(x.Key.Symbol,x.Key.Side,x.Value)).ToArray();
        return ExecutionPositionLedgerSnapshotCanonicalizerV1.Create(
            _utcNow().ToUniversalTime(),events.Count==0?0:events[^1].Id,events.Count,driftRows.Count,tracked,
            driftIntegrity,coverage,eventHash,driftHash,legs);
    }

    internal async Task<bool> SaveExecutionPositionLedgerSnapshotAsync(ExecutionPositionLedgerSnapshotV1 value,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(!ExecutionPositionLedgerSnapshotCanonicalizerV1.IsCanonical(value))throw new InvalidOperationException("Execution position ledger snapshot canonical identity is invalid.");
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
            INSERT OR IGNORE INTO execution_position_ledger_snapshots(
                canonical_sha256,schema,captured_at,last_execution_event_id,execution_event_count,drift_observation_count,
                drift_tracked_execution_count,drift_integrity_valid,drift_coverage_complete,execution_trace_sha256,drift_trace_sha256,canonical_bytes)
            VALUES($hash,$schema,$captured,$cursor,$events,$drift,$tracked,$integrity,$coverage,$executionTrace,$driftTrace,$bytes);
            SELECT changes();
            """;
        q.Parameters.AddWithValue("$hash",value.CanonicalSha256);q.Parameters.AddWithValue("$schema",value.Schema);
        q.Parameters.AddWithValue("$captured",value.CapturedAtUtc.ToString("O",CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$cursor",value.LastExecutionEventId);
        q.Parameters.AddWithValue("$events",value.ExecutionEventCount);q.Parameters.AddWithValue("$drift",value.DriftObservationCount);q.Parameters.AddWithValue("$tracked",value.DriftTrackedExecutionCount);
        q.Parameters.AddWithValue("$integrity",value.DriftIntegrityValid?1:0);q.Parameters.AddWithValue("$coverage",value.DriftCoverageComplete?1:0);
        q.Parameters.AddWithValue("$executionTrace",value.ExecutionTraceSha256);q.Parameters.AddWithValue("$driftTrace",value.DriftTraceSha256);
        q.Parameters.Add("$bytes",SqliteType.Blob).Value=value.CanonicalBytes;
        return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }

    internal async Task<bool> SaveExecutionPositionDriftReconciliationAsync(ExecutionPositionDriftReconciliationV1 value,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(!ExecutionPositionDriftReconciliationCanonicalizerV1.IsCanonical(value))throw new InvalidOperationException("Execution position drift reconciliation canonical identity is invalid.");
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
            INSERT OR IGNORE INTO execution_position_drift_reconciliations(
                link_id,schema,linked_at,execution_ledger_sha256,position_report_id,position_report_sha256,position_state,
                allows_risk_increase,drift_integrity_valid,drift_coverage_complete,calibratable,execution_event_count,
                drift_observation_count,drift_tracked_execution_count,execution_trace_sha256,drift_trace_sha256,canonical_sha256,canonical_bytes)
            VALUES($id,$schema,$linked,$ledger,$report,$reportHash,$state,$allows,$integrity,$coverage,$calibratable,$events,
                $drift,$tracked,$executionTrace,$driftTrace,$hash,$bytes);
            SELECT changes();
            """;
        q.Parameters.AddWithValue("$id",value.LinkId);q.Parameters.AddWithValue("$schema",value.Schema);q.Parameters.AddWithValue("$linked",value.LinkedAtUtc.ToString("O",CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$ledger",value.ExecutionLedgerSha256);q.Parameters.AddWithValue("$report",value.PositionReportId);q.Parameters.AddWithValue("$reportHash",value.PositionReportSha256);
        q.Parameters.AddWithValue("$state",value.PositionState.ToString());q.Parameters.AddWithValue("$allows",value.AllowsRiskIncrease?1:0);
        q.Parameters.AddWithValue("$integrity",value.DriftIntegrityValid?1:0);q.Parameters.AddWithValue("$coverage",value.DriftCoverageComplete?1:0);q.Parameters.AddWithValue("$calibratable",value.Calibratable?1:0);
        q.Parameters.AddWithValue("$events",value.ExecutionEventCount);q.Parameters.AddWithValue("$drift",value.DriftObservationCount);q.Parameters.AddWithValue("$tracked",value.DriftTrackedExecutionCount);
        q.Parameters.AddWithValue("$executionTrace",value.ExecutionTraceSha256);q.Parameters.AddWithValue("$driftTrace",value.DriftTraceSha256);q.Parameters.AddWithValue("$hash",value.CanonicalSha256);
        q.Parameters.Add("$bytes",SqliteType.Blob).Value=value.CanonicalBytes;
        return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }

    internal async Task<bool> PersistExecutionPositionDriftEvidenceSafelyAsync(
        ExecutionPositionLedgerSnapshotV1 snapshot,PositionReconciliationReportV1 report,CancellationToken ct)
    {
        try
        {
            await SaveExecutionPositionLedgerSnapshotAsync(snapshot,ct);
            var link=ExecutionPositionDriftReconciliationCanonicalizerV1.Create(snapshot,report,_utcNow().ToUniversalTime());
            await SaveExecutionPositionDriftReconciliationAsync(link,ct);
            return true;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch
        {
            // This evidence is observational. Persistence or linkage failure must not change position authority.
            return false;
        }
    }

    internal async Task<ExecutionPositionDriftReconciliationV1?> GetLatestExecutionPositionDriftReconciliationAsync(CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT canonical_bytes FROM execution_position_drift_reconciliations ORDER BY linked_at DESC,rowid DESC LIMIT 1";
        var bytes=await q.ExecuteScalarAsync(ct) as byte[];if(bytes is null)return null;
        using var doc=JsonDocument.Parse(bytes);var root=doc.RootElement;
        var legs=root.GetProperty("legs").EnumerateArray().Select(x=>new ExecutionPositionDriftLegV1(
            x.GetProperty("symbol").GetString()!,Enum.Parse<PositionSide>(x.GetProperty("side").GetString()!,true),
            decimal.Parse(x.GetProperty("localQuantity").GetString()!,CultureInfo.InvariantCulture),
            decimal.Parse(x.GetProperty("exchangeQuantity").GetString()!,CultureInfo.InvariantCulture),
            decimal.Parse(x.GetProperty("quantityDrift").GetString()!,CultureInfo.InvariantCulture))).ToArray();
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var value=new ExecutionPositionDriftReconciliationV1(
            root.GetProperty("schema").GetString()!,"execution-position-drift:"+hash,root.GetProperty("linkedAtUtc").GetDateTimeOffset().ToUniversalTime(),
            root.GetProperty("executionLedgerSha256").GetString()!,root.GetProperty("positionReportId").GetString()!,root.GetProperty("positionReportSha256").GetString()!,
            Enum.Parse<PositionReconciliationStateV1>(root.GetProperty("positionState").GetString()!,true),root.GetProperty("AllowsRiskIncrease").GetBoolean(),
            root.GetProperty("DriftIntegrityValid").GetBoolean(),root.GetProperty("DriftCoverageComplete").GetBoolean(),root.GetProperty("calibratable").GetBoolean(),
            root.GetProperty("ExecutionEventCount").GetInt32(),root.GetProperty("DriftObservationCount").GetInt32(),root.GetProperty("DriftTrackedExecutionCount").GetInt32(),
            root.GetProperty("ExecutionTraceSha256").GetString()!,root.GetProperty("DriftTraceSha256").GetString()!,legs,hash,bytes);
        return ExecutionPositionDriftReconciliationCanonicalizerV1.IsCanonical(value)?value:null;
    }
}

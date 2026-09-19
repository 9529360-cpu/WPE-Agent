using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services;

namespace WpeAgent.RuntimeServices;

/// <summary>Bounded, read-only projections of facts already persisted in the local agent SQLite database.</summary>
public sealed class RuntimeHistoricalCollectionStateStore
{
    private const string SourceName = "local-agent-sqlite";
    private static readonly Regex SensitiveValue = new("(?i)(?:bearer\\s+|api[-_]?key|secret|token|authorization|passphrase|signature|sk-)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _connectionString;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly byte[] _cursorKey=RandomNumberGenerator.GetBytes(32);

    public RuntimeHistoricalCollectionStateStore(string databasePath, Func<DateTimeOffset>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A database path is required.", nameof(databasePath));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<HistoricalCollectionPageV1<HistoricalOrderV1>> ReadOrdersAsync(HistoricalCollectionRequestV1 request, CancellationToken ct = default) =>
        ReadAsync(HistoricalCollectionKindV1.Orders, request, "execution_events", "occurred_at", TimeSpan.FromDays(30),
            "SELECT id,occurred_at,cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,avg_price,status FROM execution_events ORDER BY occurred_at DESC,id DESC LIMIT $limit OFFSET $offset",
            r => new HistoricalOrderV1(r.GetInt64(0), Instant(r.GetString(1)), Masked(r,2,"cycle"), Masked(r,3,"order"), Safe(r.GetString(4),80), Safe(r.GetString(5),20), Safe(r.GetString(6),40), r.GetInt32(7)==1, Decimal(r,8), NullableDecimal(r,9), Safe(r.GetString(10),40)), ct);

    public Task<HistoricalCollectionPageV1<HistoricalEquityPointV1>> ReadEquityAsync(HistoricalCollectionRequestV1 request, CancellationToken ct = default) =>
        ReadAsync(HistoricalCollectionKindV1.Equity, request, "equity_snapshots", "observed_at", TimeSpan.FromHours(24),
            "SELECT id,observed_at,equity,available_balance,environment,provider_id FROM equity_snapshots ORDER BY observed_at DESC,id DESC LIMIT $limit OFFSET $offset",
            r => new HistoricalEquityPointV1(r.GetInt64(0), Instant(r.GetString(1)), Decimal(r,2), Decimal(r,3), Safe(r.GetString(4),40), Safe(r.GetString(5),80)), ct);

    public Task<HistoricalCollectionPageV1<HistoricalBacktestV1>> ReadBacktestsAsync(HistoricalCollectionRequestV1 request, CancellationToken ct = default) =>
        ReadAsync(HistoricalCollectionKindV1.Backtests, request, "backtest_runs", "completed_at", TimeSpan.FromDays(30),
            "SELECT id,completed_at,strategy_id,strategy_version,symbol,status,coverage_days,trades,out_of_sample_return,max_drawdown,sharpe FROM backtest_runs ORDER BY completed_at DESC,id DESC LIMIT $limit OFFSET $offset",
            r => new HistoricalBacktestV1(Safe(r.GetString(0),120), Instant(r.GetString(1)), Safe(r.GetString(2),120), Safe(r.GetString(3),80), Safe(r.GetString(4),80), Safe(r.GetString(5),40), r.GetInt32(6), r.GetInt32(7), r.GetDouble(8), r.GetDouble(9), r.GetDouble(10)), ct);

    public Task<HistoricalCollectionPageV1<HistoricalSkillCallV1>> ReadSkillCallsAsync(HistoricalCollectionRequestV1 request, CancellationToken ct = default) =>
        ReadAsync(HistoricalCollectionKindV1.SkillCalls, request, "runtime_skill_calls", "occurred_at", TimeSpan.FromDays(7),
            "SELECT id,occurred_at,skill,status,duration_ms,mode,remote_llm,tokens,cost_usd FROM runtime_skill_calls ORDER BY occurred_at DESC,id DESC LIMIT $limit OFFSET $offset",
            r => new HistoricalSkillCallV1("runtime:"+r.GetInt64(0), Instant(r.GetString(1)), Safe(r.GetString(2),120), Safe(r.GetString(3),40), Math.Max(0,r.GetInt64(4)), Text(r,5,40), r.IsDBNull(6)?null:r.GetInt32(6)==1, r.IsDBNull(7)?null:r.GetInt32(7), NullableDecimal(r,8)), ct);

    public Task<HistoricalCollectionPageV1<HistoricalAuditEventV1>> ReadAuditEventsAsync(HistoricalCollectionRequestV1 request, CancellationToken ct = default) =>
        ReadAsync(HistoricalCollectionKindV1.AuditEvents, request, "runtime_events", "occurred_at", TimeSpan.FromDays(30),
            "SELECT event_id,occurred_at,event_type,source,correlation_id FROM runtime_events ORDER BY occurred_at DESC,sequence DESC LIMIT $limit OFFSET $offset",
            r => new HistoricalAuditEventV1(Safe(r.GetString(0),120), Instant(r.GetString(1)), Safe(r.GetString(2),80), Safe(r.GetString(3),80), Text(r,4,120), "RECORDED"), ct);


    public async Task<HistoricalCollectionPageV1<HistoricalPostTradeReviewV1>> ReadPostTradeReviewsAsync(HistoricalCollectionRequestV1 request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind=HistoricalCollectionKindV1.PostTradeReviews;
        var limit=Math.Clamp(request.Limit,1,HistoricalCollectionPageV1<HistoricalPostTradeReviewV1>.MaximumPageSize);
        if(!TryOffset(kind,request.Cursor,out var offset))return Page<HistoricalPostTradeReviewV1>(kind,RuntimeCollectionState.Error,[],null,null,"The collection cursor is invalid or expired.");
        try
        {
            await using var connection=new SqliteConnection(_connectionString);await connection.OpenAsync(ct);
            if(!await TableExists(connection,"trade_outcomes",ct))return Page<HistoricalPostTradeReviewV1>(kind,RuntimeCollectionState.Unsupported,[],null,null,"The SQLite post-trade collection is not available.");
            var updatedAt=await Latest(connection,"trade_outcomes","closed_at",ct);
            var raw=new List<RawPostTradeReview>(limit+1);
            await using(var command=connection.CreateCommand())
            {
                command.CommandText="SELECT client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,fees,fee_basis,fee_rate,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,funding_amount,funding_basis,net_pnl,return_pct,closed_at,strategy_id,strategy_version,attribution_basis FROM trade_outcomes WHERE client_order_id IS NOT NULL AND cycle_id IS NOT NULL ORDER BY closed_at DESC,id DESC LIMIT $limit OFFSET $offset";
                command.Parameters.AddWithValue("$limit",limit+1);command.Parameters.AddWithValue("$offset",offset);
                await using var reader=await command.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))
                {
                    var clientOrderId=Safe(reader.GetString(0),120);if(string.IsNullOrWhiteSpace(clientOrderId))throw new InvalidOperationException("Post-trade close identity is invalid.");
                    var cycleId=reader.GetString(1);if(string.IsNullOrWhiteSpace(cycleId))throw new InvalidOperationException("Post-trade cycle identity is invalid.");
                    var net=Decimal(reader,16);
                    raw.Add(new(
                        cycleId,
                        Safe(reader.GetString(2),80),
                        Safe(reader.GetString(3),20),
                        Decimal(reader,4),
                        Decimal(reader,5),
                        Decimal(reader,6),
                        Decimal(reader,7),
                        Safe(reader.GetString(8),80),
                        Decimal(reader,9),
                        Decimal(reader,10),
                        Decimal(reader,11),
                        Decimal(reader,12),
                        Safe(reader.GetString(13),80),
                        Decimal(reader,14),
                        Safe(reader.GetString(15),80),
                        net,
                        Decimal(reader,17),
                        net>0?"win":net<0?"loss":"flat",
                        Instant(reader.GetString(18)),
                        Text(reader,19,120),
                        Safe(reader.GetString(20),80),
                        Safe(reader.GetString(21),80)));
                }
            }
            var hasMore=raw.Count>limit;if(hasMore)raw.RemoveAt(raw.Count-1);
            var queueAvailable=await TableExists(connection,"automatic_execution_queue",ct);
            var eventsAvailable=await TableExists(connection,"automatic_execution_events",ct);
            var evidenceAvailable=await TableExists(connection,"model_off_canonical_audits",ct);
            var items=new List<HistoricalPostTradeReviewV1>(raw.Count);
            foreach(var row in raw)
            {
                var trace=await ReadPostTradeTraceAsync(connection,row.CycleId,queueAvailable,eventsAvailable,ct);
                var evidence=await ReadModelOffEvidenceAsync(connection,row.CycleId,evidenceAvailable,ct);
                items.Add(new(
                    trace.TraceId,
                    "wpe.post-trade-review/1.4",
                    row.Symbol,row.Side,row.EntryPrice,row.ExitPrice,row.Quantity,row.Fees,row.FeeBasis,row.FeeRate,
                    row.EntrySlippageAmount,row.ExitSlippageAmount,row.TotalSlippageAmount,row.SlippageBasis,
                    row.FundingAmount,row.FundingBasis,row.NetPnl,row.ReturnPct,row.Outcome,row.ClosedAtUtc,
                    row.StrategyId,row.StrategyVersion,row.AttributionBasis,
                    trace.TraceState,trace.RiskDecision,trace.ExecutionStatus,trace.ExecutionCode,trace.ExecutionAttempts,
                    trace.MarketCollectedAtUtc,trace.MarketDataVersion,evidence.State,evidence.Chain));
            }
            return Page(kind,RuntimeCollectionState.Available,items,hasMore?Cursor(kind,offset+items.Count):null,updatedAt,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return Page<HistoricalPostTradeReviewV1>(kind,RuntimeCollectionState.Error,[],null,null,"The SQLite post-trade reviews could not be read.");}
    }

    private async Task<PostTradeExecutionTrace> ReadPostTradeTraceAsync(SqliteConnection connection,string cycleId,bool queueAvailable,bool eventsAvailable,CancellationToken ct)
    {
        var traceId=SensitiveDataRedactor.MaskIdentifier(cycleId,"trade");
        if(!queueAvailable)return new(traceId,"legacy","Unavailable","Unavailable","trace.queue-unavailable",null,null,null);
        var rows=new List<(string Id,string Status,string Code,int Attempts,DateTimeOffset? MarketAt,string? MarketVersion)>(2);
        await using(var command=connection.CreateCommand())
        {
            command.CommandText="SELECT execution_id,status,last_code,attempt_count,market_collected_at,market_data_version FROM automatic_execution_queue WHERE correlation_id=$cycle ORDER BY updated_at DESC,execution_id DESC LIMIT 2";
            command.Parameters.AddWithValue("$cycle",cycleId);
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
            {
                var attempts=reader.GetInt32(3);if(attempts<0)throw new InvalidOperationException("Execution attempt count is invalid.");
                var marketAt=reader.IsDBNull(4)?null:Instant(reader.GetString(4));
                rows.Add((reader.GetString(0),Safe(reader.GetString(1),40),Safe(reader.GetString(2),120),attempts,marketAt,Text(reader,5,80)));
            }
        }
        if(rows.Count==0)return new(traceId,"legacy","Unavailable","Unavailable","trace.execution-unavailable",null,null,null);
        if(rows.Count!=1)return new(traceId,"ambiguous","Ambiguous","Ambiguous","trace.multiple-executions",null,null,null);
        var row=rows[0];
        var risk="Unavailable";
        if(eventsAvailable)
        {
            var approved=0;var blocked=0;
            await using var command=connection.CreateCommand();
            command.CommandText="SELECT to_status,COUNT(*) FROM automatic_execution_events WHERE execution_id=$id AND to_status IN ('RiskApproved','RiskBlocked') GROUP BY to_status";
            command.Parameters.AddWithValue("$id",row.Id);
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
            {
                var count=reader.GetInt32(1);if(count<0)throw new InvalidOperationException("Risk event count is invalid.");
                if(string.Equals(reader.GetString(0),"RiskApproved",StringComparison.Ordinal))approved+=count;
                else if(string.Equals(reader.GetString(0),"RiskBlocked",StringComparison.Ordinal))blocked+=count;
            }
            risk=approved>0&&blocked==0?"Approved":blocked>0&&approved==0?"Blocked":approved==0&&blocked==0?"Unavailable":"Conflicting";
        }
        return new(traceId,"available",risk,row.Status,row.Code,row.Attempts,row.MarketAt,row.MarketVersion);
    }

    private static readonly string[] EvidenceStages=["market","research","strategy","risk"];

    private async Task<PostTradeEvidenceTrace> ReadModelOffEvidenceAsync(SqliteConnection connection,string cycleId,bool tableAvailable,CancellationToken ct)
    {
        if(!tableAvailable)return new("unavailable",[]);
        var links=new List<HistoricalEvidenceLinkV1>(EvidenceStages.Length);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT output_kind,status,canonical_sha256,as_of_utc,canonical_bytes FROM model_off_canonical_audits WHERE cycle_id=$cycle AND output_kind IN ('market','research','strategy','risk') ORDER BY as_of_utc,output_kind,output_id";
        command.Parameters.AddWithValue("$cycle",cycleId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var stage=Safe(reader.GetString(0),20).ToLowerInvariant();
            if(!EvidenceStages.Contains(stage,StringComparer.Ordinal)||!seen.Add(stage))return new("ambiguous",[]);
            var status=Safe(reader.GetString(1),40);
            var hash=Hash(reader.GetString(2)).ToLowerInvariant();
            if(reader.IsDBNull(4))return new("invalid",[]);
            var bytes=(byte[])reader[4];
            if(bytes.Length is 0 or > 262144)return new("invalid",[]);
            var computed=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash),Convert.FromHexString(computed)))return new("invalid",[]);
            links.Add(new(stage,status,hash,Instant(reader.GetString(3))));
        }
        if(links.Count==0)return new("legacy",[]);
        var ordered=EvidenceStages.Select(stage=>links.SingleOrDefault(link=>link.Stage==stage)).Where(link=>link is not null).Cast<HistoricalEvidenceLinkV1>().ToArray();
        return new(ordered.Length==EvidenceStages.Length?"available":"partial",ordered);
    }

    public async Task<HistoricalCollectionPageV1<HistoricalReconciliationV1>> ReadReconciliationsAsync(HistoricalCollectionRequestV1 request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind=HistoricalCollectionKindV1.Reconciliations;
        var limit=Math.Clamp(request.Limit,1,HistoricalCollectionPageV1<HistoricalReconciliationV1>.MaximumPageSize);
        if(!TryOffset(kind,request.Cursor,out var offset))return Page<HistoricalReconciliationV1>(kind,RuntimeCollectionState.Error,[],null,null,"The collection cursor is invalid or expired.");
        try
        {
            await using var connection=new SqliteConnection(_connectionString);await connection.OpenAsync(ct);
            var sources=new List<(string Kind,string Table)>();
            foreach(var source in new[]{("position","position_reconciliation_audits"),("protection","protection_reconciliation_audits"),("externalIsolation","external_position_isolation_audits")})
                if(await TableExists(connection,source.Item2,ct))sources.Add(source);
            if(sources.Count==0)return Page<HistoricalReconciliationV1>(kind,RuntimeCollectionState.Unsupported,[],null,null,"The SQLite reconciliation audit collections are not available.");

            DateTimeOffset? updatedAt=null;
            foreach(var source in sources)
            {
                var latest=await Latest(connection,source.Table,"evaluated_at",ct);
                if(latest is not null&&(updatedAt is null||latest.Value>updatedAt.Value))updatedAt=latest;
            }

            var union=string.Join(" UNION ALL ",sources.Select(source=>$"SELECT '{source.Kind}' AS kind,report_id,schema,observed_at,evaluated_at,state,allows_risk_increase,canonical_sha256 FROM {source.Table}"));
            var items=new List<HistoricalReconciliationV1>(limit+1);
            await using var command=connection.CreateCommand();
            command.CommandText=$"SELECT kind,report_id,schema,observed_at,evaluated_at,state,allows_risk_increase,canonical_sha256 FROM ({union}) ORDER BY evaluated_at DESC,report_id DESC LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$limit",limit+1);command.Parameters.AddWithValue("$offset",offset);
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
            {
                var allows=reader.GetInt32(6);
                if(allows is not 0 and not 1)throw new InvalidOperationException("Reconciliation risk flag is invalid.");
                items.Add(new(
                    Safe(reader.GetString(0),40),
                    SensitiveDataRedactor.MaskIdentifier(reader.GetString(1),"reconciliation"),
                    Safe(reader.GetString(2),120),
                    Instant(reader.GetString(3)),
                    Instant(reader.GetString(4)),
                    Safe(reader.GetString(5),40),
                    allows==1,
                    Hash(reader.GetString(7))));
            }
            var hasMore=items.Count>limit;if(hasMore)items.RemoveAt(items.Count-1);
            return Page(kind,RuntimeCollectionState.Available,items,hasMore?Cursor(kind,offset+items.Count):null,updatedAt,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return Page<HistoricalReconciliationV1>(kind,RuntimeCollectionState.Error,[],null,null,"The SQLite reconciliation audits could not be read.");}
    }

    private async Task<HistoricalCollectionPageV1<T>> ReadAsync<T>(HistoricalCollectionKindV1 kind, HistoricalCollectionRequestV1 request, string table, string timestampColumn, TimeSpan? staleAfter, string sql, Func<SqliteDataReader,T> map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit=Math.Clamp(request.Limit,1,HistoricalCollectionPageV1<T>.MaximumPageSize);
        if(!TryOffset(kind,request.Cursor,out var offset)) return Page<T>(kind,RuntimeCollectionState.Error,[],null,null,"The collection cursor is invalid or expired.");
        try
        {
            await using var connection=new SqliteConnection(_connectionString);await connection.OpenAsync(ct);
            if(!await TableExists(connection,table,ct)) return Page<T>(kind,RuntimeCollectionState.Unsupported,[],null,null,"The SQLite collection is not available.");
            var updatedAt=await Latest(connection,table,timestampColumn,ct);
            if(staleAfter is not null&&updatedAt is not null&&_utcNow()-updatedAt>staleAfter.Value) return Page<T>(kind,RuntimeCollectionState.Stale,[],null,updatedAt,"The persisted collection is stale.");
            var items=new List<T>(limit+1);await using var command=connection.CreateCommand();command.CommandText=sql;command.Parameters.AddWithValue("$limit",limit+1);command.Parameters.AddWithValue("$offset",offset);
            await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))items.Add(map(reader));
            var hasMore=items.Count>limit;if(hasMore)items.RemoveAt(items.Count-1);
            return Page(kind,RuntimeCollectionState.Available,items,hasMore?Cursor(kind,offset+items.Count):null,updatedAt,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{return Page<T>(kind,RuntimeCollectionState.Error,[],null,null,"The SQLite collection could not be read.");}
    }

    private static HistoricalCollectionPageV1<T> Page<T>(HistoricalCollectionKindV1 kind,RuntimeCollectionState state,IReadOnlyList<T> items,string? cursor,DateTimeOffset? updated,string? message)=>new(HistoricalCollectionPageV1<T>.CurrentContractVersion,kind,state,items,cursor,updated,SourceName,message);
    private static async Task<bool> TableExists(SqliteConnection c,string table,CancellationToken ct){await using var q=c.CreateCommand();q.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";q.Parameters.AddWithValue("$name",table);return await q.ExecuteScalarAsync(ct) is not null;}
    private static async Task<DateTimeOffset?> Latest(SqliteConnection c,string table,string column,CancellationToken ct){await using var q=c.CreateCommand();q.CommandText=$"SELECT MAX({column}) FROM {table}";var value=await q.ExecuteScalarAsync(ct);return value is null||value is DBNull?null:Instant(Convert.ToString(value,CultureInfo.InvariantCulture)!);}
    private string Cursor(HistoricalCollectionKindV1 kind,int offset)
    {
        var expiresAt=_utcNow().Add(HistoricalCollectionRequestV1.CursorLifetime).ToUnixTimeSeconds();
        var payload=Encoding.UTF8.GetBytes($"v1:{kind}:{offset}:{expiresAt}");
        var signature=HMACSHA256.HashData(_cursorKey,payload);
        return Convert.ToBase64String(payload)+"."+Convert.ToBase64String(signature);
    }
    private bool TryOffset(HistoricalCollectionKindV1 kind,string? cursor,out int offset)
    {
        offset=0;if(string.IsNullOrWhiteSpace(cursor))return true;
        try
        {
            var segments=cursor.Split('.');if(segments.Length!=2)return false;
            var payload=Convert.FromBase64String(segments[0]);var supplied=Convert.FromBase64String(segments[1]);
            if(!string.Equals(Convert.ToBase64String(payload),segments[0],StringComparison.Ordinal)||!string.Equals(Convert.ToBase64String(supplied),segments[1],StringComparison.Ordinal))return false;
            var expected=HMACSHA256.HashData(_cursorKey,payload);if(!CryptographicOperations.FixedTimeEquals(expected,supplied))return false;
            var parts=Encoding.UTF8.GetString(payload).Split(':');
            return parts.Length==4&&parts[0]=="v1"&&parts[1]==kind.ToString()
                &&int.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out offset)&&offset>=0&&offset<=1_000_000
                &&long.TryParse(parts[3],NumberStyles.None,CultureInfo.InvariantCulture,out var expiresAt)&&expiresAt>_utcNow().ToUnixTimeSeconds();
        }
        catch{return false;}
    }
    private sealed record RawPostTradeReview(
        string CycleId,string Symbol,string Side,decimal EntryPrice,decimal ExitPrice,decimal Quantity,decimal Fees,string FeeBasis,decimal FeeRate,
        decimal EntrySlippageAmount,decimal ExitSlippageAmount,decimal TotalSlippageAmount,string SlippageBasis,decimal FundingAmount,string FundingBasis,
        decimal NetPnl,decimal ReturnPct,string Outcome,DateTimeOffset ClosedAtUtc,string? StrategyId,string StrategyVersion,string AttributionBasis);
    private sealed record PostTradeExecutionTrace(
        string TraceId,string TraceState,string RiskDecision,string ExecutionStatus,string ExecutionCode,int? ExecutionAttempts,DateTimeOffset? MarketCollectedAtUtc,string? MarketDataVersion);

    private sealed record PostTradeEvidenceTrace(string State,IReadOnlyList<HistoricalEvidenceLinkV1> Chain);

    private static DateTimeOffset Instant(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static decimal Decimal(SqliteDataReader r,int i)=>decimal.Parse(r.GetString(i),NumberStyles.Number,CultureInfo.InvariantCulture);
    private static decimal? NullableDecimal(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:Decimal(r,i);
    private static string Hash(string value)=>Regex.IsMatch(value,"^[A-Fa-f0-9]{64}$",RegexOptions.CultureInvariant)?value:throw new InvalidOperationException("Historical hash is invalid.");
    private static string? Text(SqliteDataReader r,int i,int max=120)=>r.IsDBNull(i)?null:Safe(r.GetString(i),max);
    private static string? Masked(SqliteDataReader r,int i,string prefix)=>r.IsDBNull(i)?null:SensitiveDataRedactor.MaskIdentifier(r.GetString(i),prefix);
    private static string Safe(string value,int max)
    {
        var singleLine=value.Replace('\r',' ').Replace('\n',' ').Trim();
        if(SensitiveValue.IsMatch(singleLine))return "[REDACTED]";
        return singleLine[..Math.Min(Math.Max(0,max),singleLine.Length)];
    }
}

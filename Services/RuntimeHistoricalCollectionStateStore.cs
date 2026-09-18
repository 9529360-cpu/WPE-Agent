using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;

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
            r => new HistoricalOrderV1(r.GetInt64(0), Instant(r.GetString(1)), Text(r,2), Text(r,3), Safe(r.GetString(4),80), Safe(r.GetString(5),20), Safe(r.GetString(6),40), r.GetInt32(7)==1, Decimal(r,8), NullableDecimal(r,9), Safe(r.GetString(10),40)), ct);

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

    public Task<HistoricalCollectionPageV1<HistoricalExecutionRealityV1>> ReadExecutionRealityAsync(HistoricalCollectionRequestV1 request, CancellationToken ct = default) =>
        ReadAsync(HistoricalCollectionKindV1.ExecutionReality, request, "execution_reality_drift", "observed_at", TimeSpan.FromDays(30),
            "SELECT observed_at,strategy_id,strategy_version,cost_model_version,symbol,state,terminal,comparable,fee_comparable,total_comparable,fill_ratio,slippage_drift_bps,fee_drift_bps,total_execution_drift_bps,observation_latency_ms,reason_code FROM execution_reality_drift ORDER BY observed_at DESC,rowid DESC LIMIT $limit OFFSET $offset",
            r =>
            {
                var priceComparable=r.GetInt32(7)==1;
                var feeComparable=r.GetInt32(8)==1;
                var totalComparable=r.GetInt32(9)==1;
                return new HistoricalExecutionRealityV1(
                    Instant(r.GetString(0)),
                    Safe(r.GetString(1),120),
                    Safe(r.GetString(2),80),
                    Safe(r.GetString(3),80),
                    Safe(r.GetString(4),80),
                    Safe(r.GetString(5),40),
                    r.GetInt32(6)==1,
                    priceComparable,
                    feeComparable,
                    totalComparable,
                    Decimal(r,10),
                    Decimal(r,11),
                    feeComparable?Decimal(r,12):null,
                    totalComparable?Decimal(r,13):null,
                    Math.Max(0,r.GetInt64(14)),
                    Safe(r.GetString(15),80));
            }, ct);

    private async Task<HistoricalCollectionPageV1<T>> ReadAsync<T>(HistoricalCollectionKindV1 kind, HistoricalCollectionRequestV1 request, string table, string timestampColumn, TimeSpan staleAfter, string sql, Func<SqliteDataReader,T> map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit=Math.Clamp(request.Limit,1,HistoricalCollectionPageV1<T>.MaximumPageSize);
        if(!TryOffset(kind,request.Cursor,out var offset)) return Page<T>(kind,RuntimeCollectionState.Error,[],null,null,"The collection cursor is invalid or expired.");
        try
        {
            await using var connection=new SqliteConnection(_connectionString);await connection.OpenAsync(ct);
            if(!await TableExists(connection,table,ct)) return Page<T>(kind,RuntimeCollectionState.Unsupported,[],null,null,"The SQLite collection is not available.");
            var updatedAt=await Latest(connection,table,timestampColumn,ct);
            if(updatedAt is not null&&_utcNow()-updatedAt>staleAfter) return Page<T>(kind,RuntimeCollectionState.Stale,[],null,updatedAt,"The persisted collection is stale.");
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
    private static DateTimeOffset Instant(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static decimal Decimal(SqliteDataReader r,int i)=>decimal.Parse(r.GetString(i),NumberStyles.Number,CultureInfo.InvariantCulture);
    private static decimal? NullableDecimal(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:Decimal(r,i);
    private static string? Text(SqliteDataReader r,int i,int max=120)=>r.IsDBNull(i)?null:Safe(r.GetString(i),max);
    private static string Safe(string value,int max)
    {
        var singleLine=value.Replace('\r',' ').Replace('\n',' ').Trim();
        if(SensitiveValue.IsMatch(singleLine))return "[REDACTED]";
        return singleLine[..Math.Min(Math.Max(0,max),singleLine.Length)];
    }
}

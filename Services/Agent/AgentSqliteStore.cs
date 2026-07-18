using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.IO;

namespace 币安量化机器人.Services.Agent;

public sealed class AgentSqliteStore
{
    private readonly string _cs;
    public AgentSqliteStore(string? path=null) { path??=Path.Combine(AppContext.BaseDirectory,"Data","agent.db");Directory.CreateDirectory(Path.GetDirectoryName(path)!);_cs=$"Data Source={path}";Initialize(); }
    private void Initialize(){using var c=new SqliteConnection(_cs);c.Open();using var q=c.CreateCommand();q.CommandText="""
    PRAGMA journal_mode=WAL;
    CREATE TABLE IF NOT EXISTS cycles(id TEXT PRIMARY KEY,started_at TEXT NOT NULL,completed_at TEXT,status TEXT,completeness INTEGER,evidence_json TEXT,brain TEXT,brain_request TEXT,brain_response TEXT,decision_json TEXT,risk_result TEXT,error TEXT);
    CREATE TABLE IF NOT EXISTS order_intents(client_order_id TEXT PRIMARY KEY,cycle_id TEXT,symbol TEXT,side TEXT,quantity TEXT,status TEXT,exchange_order_id INTEGER,updated_at TEXT,details TEXT);
    CREATE TABLE IF NOT EXISTS snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT,collected_at TEXT,account_json TEXT,positions_json TEXT,orders_json TEXT);
    CREATE TABLE IF NOT EXISTS news(duplicate_group TEXT PRIMARY KEY,source TEXT,title TEXT,url TEXT,published_at TEXT,collected_at TEXT,reliability TEXT,assets TEXT);
    CREATE TABLE IF NOT EXISTS errors(id INTEGER PRIMARY KEY AUTOINCREMENT,occurred_at TEXT,stage TEXT,message TEXT,details TEXT);
    CREATE TABLE IF NOT EXISTS agent_state(key TEXT PRIMARY KEY,value TEXT,updated_at TEXT);
    CREATE TABLE IF NOT EXISTS daily_risk(day TEXT PRIMARY KEY,equity_high TEXT NOT NULL,updated_at TEXT NOT NULL);
    """;q.ExecuteNonQuery();}
    public async Task StartCycleAsync(string id,EvidencePack e,string brain,CancellationToken ct){await Exec("INSERT OR REPLACE INTO cycles(id,started_at,status,completeness,evidence_json,brain) VALUES($i,$t,'RUNNING',$c,$e,$b)",ct,("$i",id),("$t",DateTime.UtcNow.ToString("O")),("$c",e.Completeness),("$e",JsonSerializer.Serialize(e)),("$b",brain));}
    public async Task CompleteCycleAsync(string id,DecisionPlan d,string risk,string? request,string? response,CancellationToken ct){await Exec("UPDATE cycles SET completed_at=$t,status='COMPLETED',decision_json=$d,risk_result=$r,brain_request=$q,brain_response=$b WHERE id=$i",ct,("$i",id),("$t",DateTime.UtcNow.ToString("O")),("$d",JsonSerializer.Serialize(d)),("$r",risk),("$q",request??""),("$b",response??""));}
    public async Task FailCycleAsync(string id,string stage,Exception ex,CancellationToken ct)=>await Exec("UPDATE cycles SET completed_at=$t,status='FAILED',risk_result=$s,error=$e WHERE id=$i",ct,("$i",id),("$t",DateTime.UtcNow.ToString("O")),("$s",stage),("$e",ex.ToString()));
    public async Task SaveSnapshotAsync(AccountSnapshot a,IReadOnlyList<ManagedPosition> p,IReadOnlyList<ExchangeOrder> o,CancellationToken ct)=>await Exec("INSERT INTO snapshots(collected_at,account_json,positions_json,orders_json) VALUES($t,$a,$p,$o)",ct,("$t",DateTime.UtcNow.ToString("O")),("$a",JsonSerializer.Serialize(a)),("$p",JsonSerializer.Serialize(p)),("$o",JsonSerializer.Serialize(o)));
    public async Task SaveIntentAsync(string cycle,ExecutionIntent i,string status,long? orderId,CancellationToken ct)=>await Exec("INSERT OR REPLACE INTO order_intents(client_order_id,cycle_id,symbol,side,quantity,status,exchange_order_id,updated_at,details) VALUES($id,$c,$s,$side,$q,$st,$oid,$t,$d)",ct,("$id",i.ClientOrderId),("$c",cycle),("$s",i.Symbol),("$side",i.Side.ToString()),("$q",i.Quantity.ToString(CultureInfo.InvariantCulture)),("$st",status),("$oid",orderId),("$t",DateTime.UtcNow.ToString("O")),("$d",JsonSerializer.Serialize(i)));
    public async Task<IReadOnlyList<PersistedIntent>> GetRecoverableIntentsAsync(CancellationToken ct)
    {
        var list=new List<PersistedIntent>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT cycle_id,status,exchange_order_id,details FROM order_intents WHERE status NOT IN ('PROTECTED','COMPLETED','CANCELED','REJECTED','EXPIRED','EMERGENCY_CLOSED') ORDER BY updated_at";
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var intent=JsonSerializer.Deserialize<ExecutionIntent>(r.GetString(3));if(intent is not null)list.Add(new(r.IsDBNull(0)?"RECOVERY":r.GetString(0),intent,r.IsDBNull(1)?"UNKNOWN":r.GetString(1),r.IsDBNull(2)?null:r.GetInt64(2)));}return list;
    }
    public async Task<ExecutionIntent?> GetLatestOpeningIntentAsync(string symbol,PositionSide side,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT details FROM order_intents WHERE symbol=$s AND side=$side ORDER BY updated_at DESC LIMIT 20";q.Parameters.AddWithValue("$s",symbol);q.Parameters.AddWithValue("$side",side.ToString());
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var intent=JsonSerializer.Deserialize<ExecutionIntent>(r.GetString(0));if(intent is{ReduceOnly:false,StopLoss:>0,TakeProfit:>0})return intent;}return null;
    }
    public async Task SaveNewsAsync(IEnumerable<NewsEvidence> news,CancellationToken ct)
    {
        foreach(var n in news)await Exec("INSERT OR REPLACE INTO news(duplicate_group,source,title,url,published_at,collected_at,reliability,assets) VALUES($g,$s,$t,$u,$p,$c,$r,$a)",ct,("$g",n.DuplicateGroup),("$s",n.Source),("$t",n.Title),("$u",n.Url),("$p",n.PublishedAt?.ToString("O")),("$c",n.CollectedAt.ToString("O")),("$r",n.Reliability),("$a",JsonSerializer.Serialize(n.AffectedAssets)));
    }
    public async Task<PositionSide?> GetLockedSideAsync(string symbol,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT value FROM agent_state WHERE key=$k";q.Parameters.AddWithValue("$k","locked-side:"+symbol);var value=await q.ExecuteScalarAsync(ct);return Enum.TryParse<PositionSide>(value?.ToString(),true,out var side)?side:null;
    }
    public Task SetLockedSideAsync(string symbol,PositionSide side,CancellationToken ct)=>Exec("INSERT OR REPLACE INTO agent_state(key,value,updated_at) VALUES($k,$v,$t)",ct,("$k","locked-side:"+symbol),("$v",side.ToString()),("$t",DateTime.UtcNow.ToString("O")));
    public Task ClearLockedSideAsync(string symbol,CancellationToken ct)=>Exec("DELETE FROM agent_state WHERE key=$k",ct,("$k","locked-side:"+symbol));
    public async Task ClearLockedSideIfMatchesAsync(string symbol,PositionSide side,CancellationToken ct){if(await GetLockedSideAsync(symbol,ct)==side)await ClearLockedSideAsync(symbol,ct);}
    public async Task<decimal> GetOrUpdateDailyHighAsync(DateOnly day,decimal equity,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);await using var q=c.CreateCommand();q.Transaction=(SqliteTransaction)tx;q.CommandText="SELECT equity_high FROM daily_risk WHERE day=$d";q.Parameters.AddWithValue("$d",day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));var stored=await q.ExecuteScalarAsync(ct);var high=stored is not null&&decimal.TryParse(stored.ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var value)?Math.Max(value,equity):equity;
        q.Parameters.Clear();q.CommandText="INSERT OR REPLACE INTO daily_risk(day,equity_high,updated_at) VALUES($d,$h,$t)";q.Parameters.AddWithValue("$d",day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$h",high.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$t",DateTime.UtcNow.ToString("O"));await q.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return high;
    }
    public async Task RecordErrorAsync(string stage,Exception ex,CancellationToken ct)=>await Exec("INSERT INTO errors(occurred_at,stage,message,details) VALUES($t,$s,$m,$d)",ct,("$t",DateTime.UtcNow.ToString("O")),("$s",stage),("$m",ex.Message),("$d",ex.ToString()));
    public async Task<IReadOnlyList<string>> RecentOutcomesAsync(CancellationToken ct){var list=new List<string>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT decision_json||' | '||risk_result FROM cycles WHERE status='COMPLETED' ORDER BY completed_at DESC LIMIT 5";await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(r.GetString(0));return list;}
    private async Task Exec(string sql,CancellationToken ct,params (string,object?)[] args){await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText=sql;foreach(var a in args)q.Parameters.AddWithValue(a.Item1,a.Item2??DBNull.Value);await q.ExecuteNonQueryAsync(ct);}
}

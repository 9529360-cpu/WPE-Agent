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
    CREATE TABLE IF NOT EXISTS decision_audits(cycle_id TEXT PRIMARY KEY,created_at TEXT NOT NULL,assessments_json TEXT NOT NULL,review_json TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS maturity_audits(cycle_id TEXT PRIMARY KEY,created_at TEXT NOT NULL,plan_json TEXT NOT NULL,review_json TEXT NOT NULL,risk_json TEXT NOT NULL,research_json TEXT,execution_result TEXT);
    CREATE TABLE IF NOT EXISTS execution_events(id INTEGER PRIMARY KEY AUTOINCREMENT,cycle_id TEXT,client_order_id TEXT UNIQUE,symbol TEXT,side TEXT,action TEXT,reduce_only INTEGER,quantity TEXT,avg_price TEXT,status TEXT,occurred_at TEXT);
    CREATE TABLE IF NOT EXISTS trade_outcomes(id INTEGER PRIMARY KEY AUTOINCREMENT,cycle_id TEXT,symbol TEXT,side TEXT,entry_price TEXT,exit_price TEXT,quantity TEXT,gross_pnl TEXT,fees TEXT,net_pnl TEXT,return_pct TEXT,closed_at TEXT,strategy_version TEXT);
    CREATE TABLE IF NOT EXISTS research_validations(id INTEGER PRIMARY KEY AUTOINCREMENT,created_at TEXT,symbol TEXT,strategy_version TEXT,approved INTEGER,quality_score REAL,result_json TEXT);
    CREATE TABLE IF NOT EXISTS skill_calls(id INTEGER PRIMARY KEY AUTOINCREMENT,occurred_at TEXT,skill TEXT,status TEXT,duration_ms INTEGER,input_summary TEXT,output_summary TEXT,error TEXT);
    """;q.ExecuteNonQuery();}
    public async Task StartCycleAsync(string id,EvidencePack e,string brain,CancellationToken ct){await Exec("INSERT OR REPLACE INTO cycles(id,started_at,status,completeness,evidence_json,brain) VALUES($i,$t,'RUNNING',$c,$e,$b)",ct,("$i",id),("$t",DateTime.UtcNow.ToString("O")),("$c",e.Completeness),("$e",JsonSerializer.Serialize(e)),("$b",brain));}
    public async Task CompleteCycleAsync(string id,DecisionPlan d,string risk,string? request,string? response,CancellationToken ct){await Exec("UPDATE cycles SET completed_at=$t,status='COMPLETED',decision_json=$d,risk_result=$r,brain_request=$q,brain_response=$b WHERE id=$i",ct,("$i",id),("$t",DateTime.UtcNow.ToString("O")),("$d",JsonSerializer.Serialize(d)),("$r",risk),("$q",request??""),("$b",response??""));}
    public async Task FailCycleAsync(string id,string stage,Exception ex,CancellationToken ct)=>await Exec("UPDATE cycles SET completed_at=$t,status='FAILED',risk_result=$s,error=$e WHERE id=$i",ct,("$i",id),("$t",DateTime.UtcNow.ToString("O")),("$s",stage),("$e",ex.ToString()));
    public async Task SaveSnapshotAsync(AccountSnapshot a,IReadOnlyList<ManagedPosition> p,IReadOnlyList<ExchangeOrder> o,CancellationToken ct)=>await Exec("INSERT INTO snapshots(collected_at,account_json,positions_json,orders_json) VALUES($t,$a,$p,$o)",ct,("$t",DateTime.UtcNow.ToString("O")),("$a",JsonSerializer.Serialize(a)),("$p",JsonSerializer.Serialize(p)),("$o",JsonSerializer.Serialize(o)));
    public async Task SaveIntentAsync(string cycle,ExecutionIntent i,string status,long? orderId,CancellationToken ct)=>await Exec("INSERT OR REPLACE INTO order_intents(client_order_id,cycle_id,symbol,side,quantity,status,exchange_order_id,updated_at,details) VALUES($id,$c,$s,$side,$q,$st,$oid,$t,$d)",ct,("$id",i.ClientOrderId),("$c",cycle),("$s",i.Symbol),("$side",i.Side.ToString()),("$q",i.Quantity.ToString(CultureInfo.InvariantCulture)),("$st",status),("$oid",orderId),("$t",DateTime.UtcNow.ToString("O")),("$d",JsonSerializer.Serialize(i)));
    public async Task<IReadOnlyList<PersistedIntent>> GetRecoverableIntentsAsync(CancellationToken ct)
    {
        var list=new List<PersistedIntent>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT cycle_id,status,exchange_order_id,details FROM order_intents WHERE status NOT IN ('PROTECTED','PARTIALLY_FILLED_PROTECTED','PREFLIGHT_BLOCKED','COMPLETED','CANCELED','REJECTED','EXPIRED','EMERGENCY_CLOSED') ORDER BY updated_at";
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
    public Task RecordDecisionAuditAsync(string cycle,IReadOnlyList<MarketDecisionAssessment> assessments,DecisionReview review,CancellationToken ct)=>Exec("INSERT OR REPLACE INTO decision_audits(cycle_id,created_at,assessments_json,review_json) VALUES($c,$t,$a,$r)",ct,("$c",cycle),("$t",DateTime.UtcNow.ToString("O")),("$a",JsonSerializer.Serialize(assessments)),("$r",JsonSerializer.Serialize(review)));
    public Task RecordMaturityAuditAsync(string cycle,DecisionPlan plan,DecisionReview review,IndependentRiskReview risk,ResearchValidationResult? research,string result,CancellationToken ct)=>Exec("INSERT OR REPLACE INTO maturity_audits(cycle_id,created_at,plan_json,review_json,risk_json,research_json,execution_result) VALUES($c,$t,$p,$r,$k,$s,$e)",ct,("$c",cycle),("$t",DateTime.UtcNow.ToString("O")),("$p",JsonSerializer.Serialize(plan)),("$r",JsonSerializer.Serialize(review)),("$k",JsonSerializer.Serialize(risk)),("$s",research is null?null:JsonSerializer.Serialize(research)),("$e",result));
    public Task SaveResearchAsync(ResearchValidationResult result,CancellationToken ct)=>Exec("INSERT INTO research_validations(created_at,symbol,strategy_version,approved,quality_score,result_json) VALUES($t,$s,$v,$a,$q,$j)",ct,("$t",DateTime.UtcNow.ToString("O")),("$s",result.Symbol),("$v",result.StrategyVersion),("$a",result.Approved?1:0),("$q",result.QualityScore),("$j",JsonSerializer.Serialize(result)));
    public Task RecordSkillCallAsync(string skill,string status,long duration,string input,string output,string? error,CancellationToken ct)=>Exec("INSERT INTO skill_calls(occurred_at,skill,status,duration_ms,input_summary,output_summary,error) VALUES($t,$s,$st,$d,$i,$o,$e)",ct,("$t",DateTime.UtcNow.ToString("O")),("$s",skill),("$st",status),("$d",duration),("$i",input),("$o",output),("$e",error));
    public async Task RecordExecutionAsync(string cycle,ExecutionIntent intent,ExchangeOrder order,string strategyVersion,CancellationToken ct)
    {
        var price=order.AvgPrice>0?order.AvgPrice:intent.ExpectedPrice;var quantity=order.ExecutedQuantity>0?order.ExecutedQuantity:intent.Quantity;
        await Exec("INSERT OR REPLACE INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,avg_price,status,occurred_at) VALUES($c,$id,$s,$side,$a,$r,$q,$p,$st,$t)",ct,("$c",cycle),("$id",intent.ClientOrderId),("$s",intent.Symbol),("$side",intent.Side.ToString()),("$a",intent.Action.ToString()),("$r",intent.ReduceOnly?1:0),("$q",quantity.ToString(CultureInfo.InvariantCulture)),("$p",price.ToString(CultureInfo.InvariantCulture)),("$st",order.Status),("$t",DateTime.UtcNow.ToString("O")));
        if(!intent.ReduceOnly||price<=0||quantity<=0)return;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT avg_price FROM execution_events WHERE symbol=$s AND side=$side AND reduce_only=0 AND status IN ('FILLED','PARTIALLY_FILLED') ORDER BY id DESC LIMIT 1";q.Parameters.AddWithValue("$s",intent.Symbol);q.Parameters.AddWithValue("$side",intent.Side.ToString());var raw=await q.ExecuteScalarAsync(ct);if(!decimal.TryParse(raw?.ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var entry)||entry<=0)return;
        var gross=(intent.Side==PositionSide.Long?price-entry:entry-price)*quantity;var fees=(entry+price)*quantity*.0004m;var net=gross-fees;var ret=entry*quantity>0?net/(entry*quantity):0;
        await Exec("INSERT INTO trade_outcomes(cycle_id,symbol,side,entry_price,exit_price,quantity,gross_pnl,fees,net_pnl,return_pct,closed_at,strategy_version) VALUES($c,$s,$side,$e,$x,$q,$g,$f,$n,$r,$t,$v)",ct,("$c",cycle),("$s",intent.Symbol),("$side",intent.Side.ToString()),("$e",entry.ToString(CultureInfo.InvariantCulture)),("$x",price.ToString(CultureInfo.InvariantCulture)),("$q",quantity.ToString(CultureInfo.InvariantCulture)),("$g",gross.ToString(CultureInfo.InvariantCulture)),("$f",fees.ToString(CultureInfo.InvariantCulture)),("$n",net.ToString(CultureInfo.InvariantCulture)),("$r",ret.ToString(CultureInfo.InvariantCulture)),("$t",DateTime.UtcNow.ToString("O")),("$v",strategyVersion));
    }
    public async Task<RiskHistorySnapshot> GetRiskHistoryAsync(CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);
        decimal daily;await using(var dailyQuery=c.CreateCommand()){dailyQuery.CommandText="SELECT COALESCE(SUM(CAST(net_pnl AS REAL)),0) FROM trade_outcomes WHERE closed_at >= $start";dailyQuery.Parameters.AddWithValue("$start",DateTime.UtcNow.Date.ToString("O"));var raw=await dailyQuery.ExecuteScalarAsync(ct);daily=decimal.TryParse(raw?.ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var value)?value:0;}var losses=0;await using(var q=c.CreateCommand()){q.CommandText="SELECT net_pnl FROM trade_outcomes ORDER BY id DESC LIMIT 20";await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){if(decimal.TryParse(r.GetString(0),NumberStyles.Any,CultureInfo.InvariantCulture,out var pnl)&&pnl<0)losses++;else break;}}
        await using var errors=c.CreateCommand();errors.CommandText="SELECT COUNT(*) FROM errors WHERE occurred_at >= $t AND (stage LIKE '%API%' OR stage LIKE '%EXCHANGE%' OR stage='AGENT_LOOP')";errors.Parameters.AddWithValue("$t",DateTime.UtcNow.AddMinutes(-30).ToString("O"));var api=Convert.ToInt32(await errors.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
        await using var uncertain=c.CreateCommand();uncertain.CommandText="SELECT COUNT(*) FROM order_intents WHERE status IN ('UNKNOWN','EMERGENCY_UNKNOWN','NEW','PARTIALLY_FILLED')";var unknown=Convert.ToInt32(await uncertain.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)>0;return new(daily,losses,api,unknown);
    }
    public async Task<bool> HasStateAsync(string key,CancellationToken ct){await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT 1 FROM agent_state WHERE key=$k LIMIT 1";q.Parameters.AddWithValue("$k",key);return await q.ExecuteScalarAsync(ct) is not null;}
    public Task SetStateAsync(string key,string value,CancellationToken ct)=>Exec("INSERT OR REPLACE INTO agent_state(key,value,updated_at) VALUES($k,$v,$t)",ct,("$k",key),("$v",value),("$t",DateTime.UtcNow.ToString("O")));
    public async Task<IReadOnlyList<string>> RecentOutcomesAsync(CancellationToken ct)
    {
        var groups=new Dictionary<string,(DecisionPlan Decision,string Risk,int Count)>(StringComparer.OrdinalIgnoreCase);await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT decision_json,risk_result FROM cycles WHERE status='COMPLETED' ORDER BY completed_at DESC LIMIT 20";await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            try{var d=JsonSerializer.Deserialize<DecisionPlan>(r.GetString(0));if(d is null)continue;var risk=r.GetString(1);var key=$"{d.Action}|{d.Instrument}|{Math.Round(d.Confidence,1):F1}|{risk}";if(groups.TryGetValue(key,out var old))groups[key]=(old.Decision,old.Risk,old.Count+1);else groups[key]=(d,risk,1);}catch(JsonException){}
        }
        return groups.Values.Take(6).Select(x=>{var reason=x.Decision.Reason.Length>180?x.Decision.Reason[..180]+"…":x.Decision.Reason;return $"{x.Decision.Action} {x.Decision.Instrument} confidence={x.Decision.Confidence:F2} result={x.Risk} repeats={x.Count} latestReason={reason}";}).ToArray();
    }
    public async Task<int> ConsecutiveHoldCountAsync(CancellationToken ct)
    {
        var count=0;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT decision_json FROM cycles WHERE status='COMPLETED' ORDER BY completed_at DESC LIMIT 20";await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)){try{var d=JsonSerializer.Deserialize<DecisionPlan>(r.GetString(0));if(d?.Action!=DecisionAction.Hold)break;count++;}catch(JsonException){break;}}return count;
    }
    private async Task Exec(string sql,CancellationToken ct,params (string,object?)[] args){await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText=sql;foreach(var a in args)q.Parameters.AddWithValue(a.Item1,a.Item2??DBNull.Value);await q.ExecuteNonQueryAsync(ct);}
}

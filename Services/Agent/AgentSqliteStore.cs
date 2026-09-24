using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using 币安量化机器人.Services;
using 币安量化机器人.Core.Runtime;
using WpeAgent.TradingAuthorization;
using WpeAgent.ModelOff;
using WpeAgent.AgentServices;

namespace 币安量化机器人.Services.Agent;

public sealed record IntentStatusCount(string Status,int Count);
public sealed record IntentStateSummary(int TotalCount,int RecoverableCount,int UnknownCount,DateTimeOffset? LatestUpdatedAtUtc,IReadOnlyList<IntentStatusCount> Statuses);
public sealed record ModelOffAuditPersistenceResult(bool Succeeded,bool Idempotent,string Code);
public sealed record MacroObservationPersistenceResult(bool Succeeded,bool Idempotent,int Revision,string Code);
public sealed record PersistedMacroObservation(string IndicatorId,DateTimeOffset ObservationAtUtc,int Revision,string Geography,string Frequency,string Unit,decimal Value,string SourceId,string SourceArtifactHash,DateTimeOffset FirstObservedAtUtc,DateTimeOffset? ReleasedAtUtc=null,string ReleaseTimeBasis="official-endpoint-first-observed",string? ReleaseCalendarArtifactHash=null,string? ReleaseCalendarEventId=null);
public sealed record PostTradeReviewV1(string Schema,string ClientOrderId,string CycleId,string Symbol,string Side,decimal EntryPrice,decimal ExitPrice,decimal Quantity,decimal Fees,string FeeBasis,decimal FeeRate,decimal EntrySlippageAmount,decimal ExitSlippageAmount,decimal TotalSlippageAmount,string SlippageBasis,decimal FundingAmount,string FundingBasis,decimal NetPnl,decimal ReturnPct,decimal MaeReturnPct,decimal MfeReturnPct,string ExcursionBasis,int ExcursionSamples,string ExitReason,string Outcome,DateTimeOffset ClosedAtUtc,string? StrategyId,string StrategyVersion,string AttributionBasis);
public sealed record PersistedModelOffAudit(
    string OutputId,string CycleId,string Schema,string TemplateVersion,string CanonicalSha256,
    string Status,string OutputKind,string SourcesJson,DateTimeOffset AsOfUtc,DateTimeOffset RecordedAtUtc,
    byte[] CanonicalBytes);
public sealed record PersistedModelOffHandoff(
    string HandoffId,string CycleId,string FromAgent,string ToAgent,string CanonicalOutputId,
    string CanonicalSha256,string Status,DateTimeOffset RecordedAtUtc,string HandoffSha256,byte[] CanonicalBytes);

public sealed partial class AgentSqliteStore
{
    private readonly string _cs;
    private readonly Func<DateTimeOffset> _utcNow;
    public AgentSqliteStore(string? path=null):this(path,null){}
    internal AgentSqliteStore(string? path,Func<DateTimeOffset>? utcNow) { path??=AppDataPaths.File("agent.db");Directory.CreateDirectory(Path.GetDirectoryName(path)!);_cs=$"Data Source={path}";_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);Initialize();EnsureMacroReleaseColumns();EnsureExecutionQualityColumns();EnsurePostTradeIntelligenceSchema(); }
    private void EnsureMacroReleaseColumns(){using var c=new SqliteConnection(_cs);c.Open();EnsureColumn(c,"macro_observation_revisions","released_at","TEXT");EnsureColumn(c,"macro_observation_revisions","release_time_basis","TEXT NOT NULL DEFAULT 'official-endpoint-first-observed'");EnsureColumn(c,"macro_observation_revisions","release_calendar_hash","TEXT");EnsureColumn(c,"macro_observation_revisions","release_calendar_event_id","TEXT");}
    private void EnsureExecutionQualityColumns(){using var c=new SqliteConnection(_cs);c.Open();EnsureColumn(c,"execution_events","expected_price","TEXT NOT NULL DEFAULT '0'");EnsureColumn(c,"execution_events","exchange_updated_at","TEXT");EnsureColumn(c,"trade_outcomes","entry_slippage_amount","TEXT NOT NULL DEFAULT '0'");EnsureColumn(c,"trade_outcomes","exit_slippage_amount","TEXT NOT NULL DEFAULT '0'");EnsureColumn(c,"trade_outcomes","total_slippage_amount","TEXT NOT NULL DEFAULT '0'");EnsureColumn(c,"trade_outcomes","slippage_basis","TEXT NOT NULL DEFAULT 'unavailable'");EnsureColumn(c,"trade_outcomes","close_expected_price","TEXT NOT NULL DEFAULT '0'");EnsureColumn(c,"trade_outcomes","funding_amount","TEXT NOT NULL DEFAULT '0'");EnsureColumn(c,"trade_outcomes","funding_basis","TEXT NOT NULL DEFAULT 'unavailable'");}
    private void Initialize(){using var c=new SqliteConnection(_cs);c.Open();using var q=c.CreateCommand();q.CommandText="""
    PRAGMA journal_mode=WAL;
    PRAGMA foreign_keys=ON;
    PRAGMA busy_timeout=5000;
    CREATE TABLE IF NOT EXISTS cycles(id TEXT PRIMARY KEY,started_at TEXT NOT NULL,completed_at TEXT,status TEXT,completeness INTEGER,evidence_json TEXT,brain TEXT,brain_request TEXT,brain_response TEXT,decision_json TEXT,risk_result TEXT,error TEXT);
    CREATE TABLE IF NOT EXISTS order_intents(client_order_id TEXT PRIMARY KEY,cycle_id TEXT,symbol TEXT,side TEXT,quantity TEXT,status TEXT,exchange_order_id INTEGER,updated_at TEXT,details TEXT);
    CREATE TABLE IF NOT EXISTS execution_submission_journal(submission_id TEXT PRIMARY KEY,client_order_id TEXT NOT NULL UNIQUE,provider_id TEXT NOT NULL,environment TEXT NOT NULL,submitted_at TEXT NOT NULL,result_code TEXT NOT NULL,result_hash TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS legacy_intent_isolation(client_order_id TEXT PRIMARY KEY,source_status TEXT NOT NULL,projection_status TEXT NOT NULL CHECK(projection_status='Quarantined'),reason_code TEXT NOT NULL,isolated_at TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS legacy_intent_isolation_events(id INTEGER PRIMARY KEY AUTOINCREMENT,client_order_id TEXT NOT NULL,sequence INTEGER NOT NULL,occurred_at TEXT NOT NULL,from_status TEXT NOT NULL,to_status TEXT NOT NULL,event_code TEXT NOT NULL,UNIQUE(client_order_id,sequence));
    CREATE INDEX IF NOT EXISTS ix_legacy_intent_isolation_status ON legacy_intent_isolation(projection_status,isolated_at);
    CREATE TRIGGER IF NOT EXISTS legacy_intent_isolation_events_no_update BEFORE UPDATE ON legacy_intent_isolation_events BEGIN SELECT RAISE(ABORT,'legacy intent isolation events are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS legacy_intent_isolation_events_no_delete BEFORE DELETE ON legacy_intent_isolation_events BEGIN SELECT RAISE(ABORT,'legacy intent isolation events are append-only'); END;
    CREATE TABLE IF NOT EXISTS snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT,collected_at TEXT,account_json TEXT,positions_json TEXT,orders_json TEXT);
    CREATE TABLE IF NOT EXISTS news(duplicate_group TEXT PRIMARY KEY,source TEXT,title TEXT,url TEXT,published_at TEXT,collected_at TEXT,reliability TEXT,assets TEXT);
    CREATE TABLE IF NOT EXISTS macro_observation_revisions(indicator_id TEXT NOT NULL,observation_at TEXT NOT NULL,revision INTEGER NOT NULL,geography TEXT NOT NULL,frequency TEXT NOT NULL,unit TEXT NOT NULL,value TEXT NOT NULL,source_id TEXT NOT NULL,source_artifact_hash TEXT NOT NULL,first_observed_at TEXT NOT NULL,PRIMARY KEY(indicator_id,observation_at,revision));
    CREATE INDEX IF NOT EXISTS ix_macro_observation_latest ON macro_observation_revisions(indicator_id,observation_at DESC,revision DESC);
    CREATE TRIGGER IF NOT EXISTS macro_observation_revisions_no_update BEFORE UPDATE ON macro_observation_revisions BEGIN SELECT RAISE(ABORT,'macro observations are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS macro_observation_revisions_no_delete BEFORE DELETE ON macro_observation_revisions BEGIN SELECT RAISE(ABORT,'macro observations are append-only'); END;
    CREATE TABLE IF NOT EXISTS errors(id INTEGER PRIMARY KEY AUTOINCREMENT,occurred_at TEXT,stage TEXT,message TEXT,details TEXT);
    CREATE TABLE IF NOT EXISTS agent_state(key TEXT PRIMARY KEY,value TEXT,updated_at TEXT);
    CREATE TABLE IF NOT EXISTS daily_risk(day TEXT PRIMARY KEY,equity_high TEXT NOT NULL,updated_at TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS decision_audits(cycle_id TEXT PRIMARY KEY,created_at TEXT NOT NULL,assessments_json TEXT NOT NULL,review_json TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS maturity_audits(cycle_id TEXT PRIMARY KEY,created_at TEXT NOT NULL,plan_json TEXT NOT NULL,review_json TEXT NOT NULL,risk_json TEXT NOT NULL,research_json TEXT,execution_result TEXT);
    CREATE TABLE IF NOT EXISTS model_off_canonical_audits(output_id TEXT PRIMARY KEY,cycle_id TEXT NOT NULL,schema TEXT NOT NULL,template_version TEXT NOT NULL,canonical_sha256 TEXT NOT NULL,status TEXT NOT NULL,output_kind TEXT NOT NULL,sources_json TEXT NOT NULL,as_of_utc TEXT NOT NULL,recorded_at_utc TEXT NOT NULL,canonical_bytes BLOB NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_model_off_canonical_audits_cycle ON model_off_canonical_audits(cycle_id,as_of_utc,output_id);
    CREATE TRIGGER IF NOT EXISTS model_off_canonical_audits_no_update BEFORE UPDATE ON model_off_canonical_audits BEGIN SELECT RAISE(ABORT,'model-off canonical audits are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS model_off_canonical_audits_no_delete BEFORE DELETE ON model_off_canonical_audits BEGIN SELECT RAISE(ABORT,'model-off canonical audits are append-only'); END;
    CREATE TABLE IF NOT EXISTS model_off_canonical_handoffs(handoff_id TEXT PRIMARY KEY,cycle_id TEXT NOT NULL,from_agent TEXT NOT NULL,to_agent TEXT NOT NULL,canonical_output_id TEXT NOT NULL,canonical_sha256 TEXT NOT NULL,status TEXT NOT NULL,recorded_at_utc TEXT NOT NULL,handoff_sha256 TEXT NOT NULL,canonical_bytes BLOB NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_model_off_canonical_handoffs_cycle ON model_off_canonical_handoffs(cycle_id,handoff_id);
    CREATE TRIGGER IF NOT EXISTS model_off_canonical_handoffs_no_update BEFORE UPDATE ON model_off_canonical_handoffs BEGIN SELECT RAISE(ABORT,'model-off canonical handoffs are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS model_off_canonical_handoffs_no_delete BEFORE DELETE ON model_off_canonical_handoffs BEGIN SELECT RAISE(ABORT,'model-off canonical handoffs are append-only'); END;
    CREATE TABLE IF NOT EXISTS execution_events(id INTEGER PRIMARY KEY AUTOINCREMENT,cycle_id TEXT,client_order_id TEXT UNIQUE,symbol TEXT,side TEXT,action TEXT,reduce_only INTEGER,quantity TEXT,avg_price TEXT,expected_price TEXT NOT NULL DEFAULT '0',status TEXT,occurred_at TEXT,exchange_updated_at TEXT);
    CREATE TABLE IF NOT EXISTS exchange_order_fee_evidence(evidence_id TEXT PRIMARY KEY,schema TEXT NOT NULL,provider_id TEXT NOT NULL,environment TEXT NOT NULL,symbol TEXT NOT NULL,order_id TEXT NOT NULL,client_order_id TEXT NOT NULL,fill_count INTEGER NOT NULL,executed_quantity TEXT NOT NULL,fee_amount TEXT NOT NULL,fee_asset TEXT NOT NULL,observed_at TEXT NOT NULL,state TEXT NOT NULL,canonical_sha256 TEXT NOT NULL,canonical_bytes BLOB NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_exchange_order_fee_identity ON exchange_order_fee_evidence(provider_id,environment,symbol,order_id,client_order_id,observed_at DESC);
    CREATE TRIGGER IF NOT EXISTS exchange_order_fee_evidence_no_update BEFORE UPDATE ON exchange_order_fee_evidence BEGIN SELECT RAISE(ABORT,'exchange order fee evidence is append-only'); END;
    CREATE TRIGGER IF NOT EXISTS exchange_order_fee_evidence_no_delete BEFORE DELETE ON exchange_order_fee_evidence BEGIN SELECT RAISE(ABORT,'exchange order fee evidence is append-only'); END;
    CREATE TABLE IF NOT EXISTS funding_income_events(event_id TEXT PRIMARY KEY,provider_id TEXT NOT NULL,environment TEXT NOT NULL,symbol TEXT NOT NULL,transaction_id TEXT NOT NULL,occurred_at TEXT NOT NULL,amount TEXT NOT NULL,asset TEXT NOT NULL,canonical_sha256 TEXT NOT NULL,canonical_bytes BLOB NOT NULL,UNIQUE(provider_id,environment,transaction_id));
    CREATE TABLE IF NOT EXISTS funding_observation_windows(window_id TEXT PRIMARY KEY,schema TEXT NOT NULL,provider_id TEXT NOT NULL,environment TEXT NOT NULL,symbol TEXT NOT NULL,start_utc TEXT NOT NULL,end_utc TEXT NOT NULL,observed_at TEXT NOT NULL,state TEXT NOT NULL,event_count INTEGER NOT NULL,source_artifact_sha256 TEXT NOT NULL,canonical_sha256 TEXT NOT NULL,canonical_bytes BLOB NOT NULL);
    CREATE TABLE IF NOT EXISTS funding_window_events(window_id TEXT NOT NULL REFERENCES funding_observation_windows(window_id),event_id TEXT NOT NULL REFERENCES funding_income_events(event_id),PRIMARY KEY(window_id,event_id));
    CREATE INDEX IF NOT EXISTS ix_funding_windows_lookup ON funding_observation_windows(provider_id,environment,symbol,state,end_utc DESC,observed_at DESC);
    CREATE TRIGGER IF NOT EXISTS funding_income_events_no_update BEFORE UPDATE ON funding_income_events BEGIN SELECT RAISE(ABORT,'funding income events are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS funding_income_events_no_delete BEFORE DELETE ON funding_income_events BEGIN SELECT RAISE(ABORT,'funding income events are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS funding_observation_windows_no_update BEFORE UPDATE ON funding_observation_windows BEGIN SELECT RAISE(ABORT,'funding observation windows are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS funding_observation_windows_no_delete BEFORE DELETE ON funding_observation_windows BEGIN SELECT RAISE(ABORT,'funding observation windows are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS funding_window_events_no_update BEFORE UPDATE ON funding_window_events BEGIN SELECT RAISE(ABORT,'funding window links are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS funding_window_events_no_delete BEFORE DELETE ON funding_window_events BEGIN SELECT RAISE(ABORT,'funding window links are append-only'); END;
    CREATE TABLE IF NOT EXISTS position_reconciliation_audits(report_id TEXT PRIMARY KEY,schema TEXT NOT NULL,observed_at TEXT NOT NULL,evaluated_at TEXT NOT NULL,state TEXT NOT NULL,allows_risk_increase INTEGER NOT NULL,canonical_sha256 TEXT NOT NULL,canonical_bytes BLOB NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_position_reconciliation_time ON position_reconciliation_audits(evaluated_at DESC);
    CREATE TRIGGER IF NOT EXISTS position_reconciliation_audits_no_update BEFORE UPDATE ON position_reconciliation_audits BEGIN SELECT RAISE(ABORT,'position reconciliation audits are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS position_reconciliation_audits_no_delete BEFORE DELETE ON position_reconciliation_audits BEGIN SELECT RAISE(ABORT,'position reconciliation audits are append-only'); END;
    CREATE TABLE IF NOT EXISTS external_position_isolation_audits(report_id TEXT PRIMARY KEY,schema TEXT NOT NULL,observed_at TEXT NOT NULL,evaluated_at TEXT NOT NULL,state TEXT NOT NULL,allows_risk_increase INTEGER NOT NULL,canonical_sha256 TEXT NOT NULL,canonical_bytes BLOB NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_external_position_isolation_time ON external_position_isolation_audits(evaluated_at DESC);
    CREATE TRIGGER IF NOT EXISTS external_position_isolation_audits_no_update BEFORE UPDATE ON external_position_isolation_audits BEGIN SELECT RAISE(ABORT,'external position isolation audits are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS external_position_isolation_audits_no_delete BEFORE DELETE ON external_position_isolation_audits BEGIN SELECT RAISE(ABORT,'external position isolation audits are append-only'); END;
    CREATE TABLE IF NOT EXISTS protection_reconciliation_audits(report_id TEXT PRIMARY KEY,schema TEXT NOT NULL,observed_at TEXT NOT NULL,evaluated_at TEXT NOT NULL,state TEXT NOT NULL,allows_risk_increase INTEGER NOT NULL,canonical_sha256 TEXT NOT NULL,canonical_bytes BLOB NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_protection_reconciliation_time ON protection_reconciliation_audits(evaluated_at DESC);
    CREATE TRIGGER IF NOT EXISTS protection_reconciliation_audits_no_update BEFORE UPDATE ON protection_reconciliation_audits BEGIN SELECT RAISE(ABORT,'protection reconciliation audits are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS protection_reconciliation_audits_no_delete BEFORE DELETE ON protection_reconciliation_audits BEGIN SELECT RAISE(ABORT,'protection reconciliation audits are append-only'); END;
    CREATE TABLE IF NOT EXISTS trade_outcomes(id INTEGER PRIMARY KEY AUTOINCREMENT,client_order_id TEXT,cycle_id TEXT,symbol TEXT,side TEXT,entry_price TEXT,exit_price TEXT,quantity TEXT,gross_pnl TEXT,fees TEXT,fee_basis TEXT NOT NULL DEFAULT 'estimated-static-rate',fee_rate TEXT NOT NULL DEFAULT '0.0004',entry_slippage_amount TEXT NOT NULL DEFAULT '0',exit_slippage_amount TEXT NOT NULL DEFAULT '0',total_slippage_amount TEXT NOT NULL DEFAULT '0',slippage_basis TEXT NOT NULL DEFAULT 'unavailable',close_expected_price TEXT NOT NULL DEFAULT '0',funding_amount TEXT NOT NULL DEFAULT '0',funding_basis TEXT NOT NULL DEFAULT 'unavailable',net_pnl TEXT,return_pct TEXT,closed_at TEXT,strategy_id TEXT,strategy_version TEXT,attribution_basis TEXT NOT NULL DEFAULT 'legacy-version-only');
    CREATE TABLE IF NOT EXISTS skill_calls(id INTEGER PRIMARY KEY AUTOINCREMENT,occurred_at TEXT,skill TEXT,status TEXT,duration_ms INTEGER,input_summary TEXT,output_summary TEXT,error TEXT);
    CREATE TABLE IF NOT EXISTS runtime_skill_calls(id INTEGER PRIMARY KEY AUTOINCREMENT,source_skill_call_id INTEGER UNIQUE,occurred_at TEXT NOT NULL,skill TEXT NOT NULL,status TEXT NOT NULL,duration_ms INTEGER NOT NULL,mode TEXT,remote_llm INTEGER,tokens INTEGER,cost_usd TEXT,context_chars INTEGER,input_tokens INTEGER,output_tokens INTEGER,cache_hit INTEGER,llm_outcome TEXT,token_source TEXT);
    CREATE INDEX IF NOT EXISTS ix_runtime_skill_calls_occurred ON runtime_skill_calls(occurred_at DESC);
    CREATE TABLE IF NOT EXISTS realtime_events(id INTEGER PRIMARY KEY AUTOINCREMENT,occurred_at TEXT,event_type TEXT,symbol TEXT,status TEXT,summary TEXT,payload_hash TEXT);
    CREATE INDEX IF NOT EXISTS ix_realtime_events_time ON realtime_events(occurred_at);
    CREATE TABLE IF NOT EXISTS news_documents(id INTEGER PRIMARY KEY AUTOINCREMENT,duplicate_group TEXT UNIQUE,source TEXT,title TEXT,url TEXT,published_at TEXT,collected_at TEXT,reliability TEXT,assets TEXT,body_summary TEXT,confidence REAL,corroborating_sources INTEGER,event_type TEXT,is_breaking INTEGER,sentiment REAL);
    CREATE VIRTUAL TABLE IF NOT EXISTS news_search USING fts5(title,body_summary,source,event_type,content='news_documents',content_rowid='id');
    CREATE TRIGGER IF NOT EXISTS news_documents_ai AFTER INSERT ON news_documents BEGIN INSERT INTO news_search(rowid,title,body_summary,source,event_type) VALUES(new.id,new.title,new.body_summary,new.source,new.event_type); END;
    CREATE TRIGGER IF NOT EXISTS news_documents_au AFTER UPDATE ON news_documents BEGIN INSERT INTO news_search(news_search,rowid,title,body_summary,source,event_type) VALUES('delete',old.id,old.title,old.body_summary,old.source,old.event_type); INSERT INTO news_search(rowid,title,body_summary,source,event_type) VALUES(new.id,new.title,new.body_summary,new.source,new.event_type); END;
    CREATE TABLE IF NOT EXISTS historical_candles(symbol TEXT NOT NULL,interval TEXT NOT NULL,open_time TEXT NOT NULL,open TEXT,high TEXT,low TEXT,close TEXT,volume TEXT,quote_volume TEXT,trades INTEGER,taker_buy_volume TEXT,PRIMARY KEY(symbol,interval,open_time));
    CREATE INDEX IF NOT EXISTS ix_historical_symbol_time ON historical_candles(symbol,interval,open_time);
    CREATE TABLE IF NOT EXISTS portfolio_risk_audits(cycle_id TEXT PRIMARY KEY,created_at TEXT NOT NULL,result_json TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS workflow_runs(run_id TEXT NOT NULL,cycle_id TEXT NOT NULL,status TEXT NOT NULL,current_node TEXT NOT NULL,state_json TEXT NOT NULL,started_at TEXT NOT NULL,updated_at TEXT NOT NULL,error TEXT,PRIMARY KEY(run_id,cycle_id));
    CREATE INDEX IF NOT EXISTS ix_workflow_runs_status ON workflow_runs(status,updated_at);
    CREATE TABLE IF NOT EXISTS workflow_checkpoints(id INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL,cycle_id TEXT NOT NULL,node TEXT NOT NULL,phase TEXT NOT NULL,attempt INTEGER NOT NULL,state_json TEXT NOT NULL,created_at TEXT NOT NULL,UNIQUE(run_id,cycle_id,node,phase,attempt));
    CREATE INDEX IF NOT EXISTS ix_workflow_checkpoints_cycle ON workflow_checkpoints(run_id,cycle_id,id);
    CREATE TABLE IF NOT EXISTS runtime_events(event_id TEXT PRIMARY KEY,sequence INTEGER NOT NULL,correlation_id TEXT NOT NULL,causation_id TEXT,event_type TEXT NOT NULL,source TEXT NOT NULL,payload_json TEXT NOT NULL,occurred_at TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_runtime_events_correlation ON runtime_events(correlation_id,sequence);
    CREATE INDEX IF NOT EXISTS ix_cycles_completed_at ON cycles(completed_at DESC);
    CREATE INDEX IF NOT EXISTS ix_decision_audits_created_at ON decision_audits(created_at DESC);
    CREATE INDEX IF NOT EXISTS ix_maturity_audits_created_at ON maturity_audits(created_at DESC);
    CREATE INDEX IF NOT EXISTS ix_execution_events_occurred_at ON execution_events(occurred_at DESC);
    CREATE INDEX IF NOT EXISTS ix_skill_calls_occurred_at ON skill_calls(occurred_at DESC);
    CREATE INDEX IF NOT EXISTS ix_workflow_checkpoints_created_at ON workflow_checkpoints(created_at DESC);
    CREATE TABLE IF NOT EXISTS runtime_leases(name TEXT PRIMARY KEY,owner_id TEXT NOT NULL,expires_at TEXT NOT NULL,heartbeat_at TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS backtest_runs(id TEXT PRIMARY KEY,strategy_id TEXT NOT NULL,strategy_version TEXT NOT NULL,symbol TEXT NOT NULL,status TEXT NOT NULL,completed_at TEXT NOT NULL,coverage_days INTEGER NOT NULL,trades INTEGER NOT NULL,out_of_sample_return REAL NOT NULL,max_drawdown REAL NOT NULL,sharpe REAL NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_backtest_runs_completed ON backtest_runs(completed_at DESC);
    CREATE TABLE IF NOT EXISTS equity_snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT,observed_at TEXT NOT NULL,equity TEXT NOT NULL,available_balance TEXT NOT NULL,environment TEXT NOT NULL,provider_id TEXT NOT NULL);
    CREATE UNIQUE INDEX IF NOT EXISTS ux_equity_snapshots_source_time ON equity_snapshots(provider_id,environment,observed_at);
    CREATE INDEX IF NOT EXISTS ix_equity_snapshots_observed ON equity_snapshots(observed_at DESC);
    CREATE TABLE IF NOT EXISTS trading_approval_requests(request_id TEXT PRIMARY KEY,mode TEXT NOT NULL,correlation_id TEXT NOT NULL,intent_hash TEXT NOT NULL,artifact_hash TEXT,user_id TEXT NOT NULL,device_id TEXT NOT NULL,session_id TEXT NOT NULL,issued_at TEXT NOT NULL,expires_at TEXT NOT NULL,revoked_at TEXT,consumed_at TEXT);
    CREATE INDEX IF NOT EXISTS ix_trading_approval_requests_state ON trading_approval_requests(consumed_at,revoked_at,expires_at);
    CREATE TABLE IF NOT EXISTS trading_approval_receipts(receipt_id TEXT PRIMARY KEY,request_id TEXT NOT NULL,correlation_id TEXT NOT NULL,intent_hash TEXT NOT NULL,artifact_hash TEXT,user_id TEXT NOT NULL,device_id TEXT NOT NULL,session_id TEXT NOT NULL,approved INTEGER NOT NULL CHECK(approved IN (0,1)),issued_at TEXT NOT NULL,expires_at TEXT NOT NULL,revoked_at TEXT,consumed_at TEXT);
    CREATE INDEX IF NOT EXISTS ix_trading_approval_receipts_request ON trading_approval_receipts(request_id,consumed_at,revoked_at,expires_at);
    CREATE TABLE IF NOT EXISTS trading_review_execution_queue(
        request_id TEXT PRIMARY KEY REFERENCES trading_approval_requests(request_id),
        contract_version INTEGER NOT NULL,
        artifact_bytes BLOB NOT NULL,
        artifact_hash TEXT NOT NULL,
        intent_hash TEXT NOT NULL,
        provider_id TEXT NOT NULL,
        environment TEXT NOT NULL,
        strategy_id TEXT NOT NULL,
        strategy_version TEXT NOT NULL,
        market_collected_at TEXT NOT NULL,
        market_data_version TEXT NOT NULL,
        created_at TEXT NOT NULL,
        expires_at TEXT NOT NULL,
        status TEXT NOT NULL CHECK(status IN ('Pending','Approved','Rejected','Revoked','Expired','Claimed','Executing','Reconciling','Succeeded','ArtifactInvalid','StrategyInvalid','MarketStale','PolicyBlocked','FailedTerminal')),
        attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count>=0),
        lease_owner TEXT,
        lease_expires_at TEXT,
        last_code TEXT NOT NULL,
        updated_at TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_trading_review_queue_state ON trading_review_execution_queue(status,expires_at,updated_at,request_id);
    CREATE TABLE IF NOT EXISTS trading_review_queue_events(
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        request_id TEXT NOT NULL REFERENCES trading_review_execution_queue(request_id),
        sequence INTEGER NOT NULL,
        occurred_at TEXT NOT NULL,
        from_status TEXT,
        to_status TEXT NOT NULL,
        event_code TEXT NOT NULL,
        actor_kind TEXT NOT NULL,
        reason TEXT NOT NULL DEFAULT '',
        UNIQUE(request_id,sequence));
    CREATE INDEX IF NOT EXISTS ix_trading_review_events_request ON trading_review_queue_events(request_id,sequence);
    CREATE TRIGGER IF NOT EXISTS trading_review_events_no_update BEFORE UPDATE ON trading_review_queue_events BEGIN SELECT RAISE(ABORT,'review queue events are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS trading_review_events_no_delete BEFORE DELETE ON trading_review_queue_events BEGIN SELECT RAISE(ABORT,'review queue events are append-only'); END;
    CREATE TABLE IF NOT EXISTS automatic_execution_queue(
        execution_id TEXT PRIMARY KEY,
        correlation_id TEXT NOT NULL,
        contract_version INTEGER NOT NULL,
        artifact_bytes BLOB NOT NULL,
        artifact_hash TEXT NOT NULL,
        intent_hash TEXT NOT NULL,
        risk_receipt_bytes BLOB,
        risk_receipt_hash TEXT,
        provider_id TEXT NOT NULL,
        environment TEXT NOT NULL CHECK(environment='Testnet'),
        strategy_id TEXT NOT NULL,
        strategy_version TEXT NOT NULL,
        market_collected_at TEXT NOT NULL,
        market_data_version TEXT NOT NULL,
        created_at TEXT NOT NULL,
        expires_at TEXT NOT NULL,
        status TEXT NOT NULL CHECK(status IN ('Proposed','RiskApproved','RiskBlocked','Claimed','Executing','Reconciling','Succeeded','CapabilityUnavailable','MarketStale','StrategyInvalid','ArtifactInvalid','PolicyBlocked','UnknownOutcome','FailedTerminal')),
        attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count>=0),
        lease_owner TEXT,
        lease_expires_at TEXT,
        last_code TEXT NOT NULL,
        updated_at TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_automatic_execution_state ON automatic_execution_queue(status,updated_at,execution_id);
    CREATE TABLE IF NOT EXISTS automatic_execution_events(
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        execution_id TEXT NOT NULL REFERENCES automatic_execution_queue(execution_id),
        sequence INTEGER NOT NULL,
        occurred_at TEXT NOT NULL,
        from_status TEXT,
        to_status TEXT NOT NULL,
        event_code TEXT NOT NULL,
        actor_kind TEXT NOT NULL,
        UNIQUE(execution_id,sequence));
    CREATE INDEX IF NOT EXISTS ix_automatic_execution_events ON automatic_execution_events(execution_id,sequence);
    CREATE TRIGGER IF NOT EXISTS automatic_execution_events_no_update BEFORE UPDATE ON automatic_execution_events BEGIN SELECT RAISE(ABORT,'automatic execution events are append-only'); END;
    CREATE TRIGGER IF NOT EXISTS automatic_execution_events_no_delete BEFORE DELETE ON automatic_execution_events BEGIN SELECT RAISE(ABORT,'automatic execution events are append-only'); END;
    CREATE TABLE IF NOT EXISTS trading_authorization_mode_audits(change_id TEXT PRIMARY KEY,old_mode TEXT NOT NULL,new_mode TEXT NOT NULL,user_id TEXT NOT NULL,device_id TEXT NOT NULL,changed_at TEXT NOT NULL,reason TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_trading_authorization_mode_audits_time ON trading_authorization_mode_audits(changed_at DESC);
    INSERT OR IGNORE INTO workflow_runs(run_id,cycle_id,status,current_node,state_json,started_at,updated_at,error)
        SELECT 'legacy',id,'RUNNING','Observation','{}',started_at,started_at,'Migrated from pre-checkpoint runtime' FROM cycles WHERE status='RUNNING';
    """;q.ExecuteNonQuery();EnsureColumn(c,"trading_review_queue_events","reason","TEXT NOT NULL DEFAULT ''");EnsureColumn(c,"trading_approval_requests","artifact_hash","TEXT");EnsureColumn(c,"trading_approval_receipts","artifact_hash","TEXT");EnsureColumn(c,"trade_outcomes","client_order_id","TEXT");EnsureColumn(c,"trade_outcomes","fee_basis","TEXT NOT NULL DEFAULT 'estimated-static-rate'");EnsureColumn(c,"trade_outcomes","fee_rate","TEXT NOT NULL DEFAULT '0.0004'");EnsureColumn(c,"trade_outcomes","strategy_id","TEXT");EnsureColumn(c,"trade_outcomes","attribution_basis","TEXT NOT NULL DEFAULT 'legacy-version-only'");EnsureColumn(c,"runtime_skill_calls","context_chars","INTEGER");EnsureColumn(c,"runtime_skill_calls","input_tokens","INTEGER");EnsureColumn(c,"runtime_skill_calls","output_tokens","INTEGER");EnsureColumn(c,"runtime_skill_calls","cache_hit","INTEGER");EnsureColumn(c,"runtime_skill_calls","llm_outcome","TEXT");EnsureColumn(c,"runtime_skill_calls","token_source","TEXT");using var outcomeIndex=c.CreateCommand();outcomeIndex.CommandText="CREATE UNIQUE INDEX IF NOT EXISTS ux_trade_outcomes_client_order ON trade_outcomes(client_order_id) WHERE client_order_id IS NOT NULL";outcomeIndex.ExecuteNonQuery();}
    public async Task StartCycleAsync(string id,EvidencePack e,string brain,CancellationToken ct){await Exec("INSERT OR REPLACE INTO cycles(id,started_at,status,completeness,evidence_json,brain) VALUES($i,$t,'RUNNING',$c,$e,$b)",ct,("$i",id),("$t",DateTime.UtcNow.ToString("O")),("$c",e.Completeness),("$e",JsonSerializer.Serialize(e)),("$b",brain));}
    public async Task<ModelOffAuditPersistenceResult> SaveModelOffCanonicalAuditAsync(ModelOffAgentOutputV1 output,ModelOffCanonicalDocumentV1 document,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);ArgumentNullException.ThrowIfNull(document);
        if(!Enum.IsDefined(output.Agent)||!Enum.IsDefined(output.Status)||!Enum.IsDefined(output.Uncertainty.Level))throw new InvalidOperationException("Canonical audit enum values must be known.");
        if(string.IsNullOrWhiteSpace(output.TemplateVersion))throw new InvalidOperationException("Canonical audit template version is required.");
        var canonical=ModelOffCanonicalSerializerV1.Serialize(output);
        if(document.Utf8Bytes.Length==0||!CryptographicOperations.FixedTimeEquals(canonical.Utf8Bytes,document.Utf8Bytes)||!string.Equals(canonical.Sha256,document.Sha256,StringComparison.Ordinal))throw new InvalidOperationException("Canonical audit document bytes or hash do not match the output.");
        var asOf=output.Sources.Select(x=>x.AsOfUtc).Where(x=>x.HasValue).Select(x=>x!.Value).DefaultIfEmpty().Max();
        if(asOf==default)throw new InvalidOperationException("Canonical audit source as-of time is required.");
        var sources=JsonSerializer.Serialize(output.Sources.OrderBy(x=>x.SourceId,StringComparer.Ordinal).Select(x=>new{x.SourceId,Kind=x.Kind.ToString().ToLowerInvariant(),AsOfUtc=x.AsOfUtc!.Value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),Status=x.Status.ToString().ToLowerInvariant(),x.ArtifactHash}));
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using(var q=c.CreateCommand()){q.Transaction=(SqliteTransaction)tx;q.CommandText="INSERT OR IGNORE INTO model_off_canonical_audits(output_id,cycle_id,schema,template_version,canonical_sha256,status,output_kind,sources_json,as_of_utc,recorded_at_utc,canonical_bytes) VALUES($o,$c,$s,$v,$h,$st,$k,$src,$a,$r,$b)";q.Parameters.AddWithValue("$o",output.OutputId);q.Parameters.AddWithValue("$c",output.CycleId);q.Parameters.AddWithValue("$s",ModelOffAgentOutputV1.Schema);q.Parameters.AddWithValue("$v",output.TemplateVersion);q.Parameters.AddWithValue("$h",canonical.Sha256);q.Parameters.AddWithValue("$st",output.Status.ToString().ToLowerInvariant());q.Parameters.AddWithValue("$k",output.Agent.ToString().ToLowerInvariant());q.Parameters.AddWithValue("$src",sources);q.Parameters.AddWithValue("$a",asOf.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$r",_utcNow().ToUniversalTime().ToString("O",CultureInfo.InvariantCulture));q.Parameters.Add("$b",SqliteType.Blob).Value=canonical.Utf8Bytes;var inserted=await q.ExecuteNonQueryAsync(ct);if(inserted==1){await tx.CommitAsync(ct);return new(true,false,"audit.persisted");}}
        await using(var q=c.CreateCommand()){q.Transaction=(SqliteTransaction)tx;q.CommandText="SELECT cycle_id,canonical_sha256,canonical_bytes FROM model_off_canonical_audits WHERE output_id=$o";q.Parameters.AddWithValue("$o",output.OutputId);await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new InvalidOperationException("Canonical audit identity disappeared during persistence.");var identical=string.Equals(r.GetString(0),output.CycleId,StringComparison.Ordinal)&&string.Equals(r.GetString(1),canonical.Sha256,StringComparison.Ordinal)&&CryptographicOperations.FixedTimeEquals((byte[])r[2],canonical.Utf8Bytes);await tx.RollbackAsync(ct);return identical?new(true,true,"audit.idempotent"):new(false,false,"audit.identity-conflict");}
    }
    public async Task<IReadOnlyList<PersistedModelOffAudit>> GetModelOffCanonicalAuditsAsync(string cycleId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(cycleId))throw new ArgumentException("Cycle id is required.",nameof(cycleId));
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT output_id,cycle_id,schema,template_version,canonical_sha256,status,output_kind,sources_json,as_of_utc,recorded_at_utc,canonical_bytes FROM model_off_canonical_audits WHERE cycle_id=$c ORDER BY as_of_utc,output_id";q.Parameters.AddWithValue("$c",cycleId);await using var r=await q.ExecuteReaderAsync(ct);var rows=new List<PersistedModelOffAudit>();while(await r.ReadAsync(ct))rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),DateTimeOffset.Parse(r.GetString(8),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),DateTimeOffset.Parse(r.GetString(9),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),(byte[])r[10]));return rows;
    }
    public async Task<IReadOnlyList<PersistedModelOffHandoff>> GetModelOffCanonicalHandoffsAsync(string cycleId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(cycleId))throw new ArgumentException("Cycle id is required.",nameof(cycleId));
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT handoff_id,cycle_id,from_agent,to_agent,canonical_output_id,canonical_sha256,status,recorded_at_utc,handoff_sha256,canonical_bytes FROM model_off_canonical_handoffs WHERE cycle_id=$c ORDER BY handoff_id";q.Parameters.AddWithValue("$c",cycleId);await using var r=await q.ExecuteReaderAsync(ct);var rows=new List<PersistedModelOffHandoff>();while(await r.ReadAsync(ct))rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),DateTimeOffset.Parse(r.GetString(7),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),r.GetString(8),(byte[])r[9]));return rows;
    }
    public async Task<ModelOffAuditPersistenceResult> SaveModelOffProductionCycleAsync(IReadOnlyList<(ModelOffAgentOutputV1 Output,ModelOffCanonicalDocumentV1 Document)> outputs,IReadOnlyList<ModelOffHandoffV1> handoffs,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outputs);ArgumentNullException.ThrowIfNull(handoffs);
        var roles=Enum.GetValues<ModelOffAgentV1>();
        if(outputs.Count!=roles.Length||outputs.Select(x=>x.Output.Agent).Distinct().Count()!=roles.Length||roles.Any(role=>outputs.Count(x=>x.Output.Agent==role)!=1)||handoffs.Count!=roles.Length-1)return new(false,false,"audit.production-cycle-shape-invalid");
        var cycle=outputs[0].Output.CycleId;
        if(string.IsNullOrWhiteSpace(cycle)||outputs.Any(x=>!string.Equals(x.Output.CycleId,cycle,StringComparison.Ordinal))||handoffs.Any(x=>!string.Equals(x.CycleId,cycle,StringComparison.Ordinal)))return new(false,false,"audit.production-cycle-identity-invalid");
        var ordered=outputs.OrderBy(x=>x.Output.Agent).ToArray();
        for(var i=0;i<handoffs.Count;i++)
        {
            var handoff=handoffs[i];var source=ordered[i];
            if(handoff.FromAgent!=roles[i]||handoff.ToAgent!=roles[i+1]||!string.Equals(handoff.CanonicalOutputId,source.Output.OutputId,StringComparison.Ordinal)||!string.Equals(handoff.CanonicalSha256,source.Document.Sha256,StringComparison.Ordinal))return new(false,false,"audit.production-cycle-handoff-invalid");
        }
        var prepared=new List<(ModelOffAgentOutputV1 Output,ModelOffCanonicalDocumentV1 Document,string Sources,DateTimeOffset AsOf)>();
        try
        {
            foreach(var pair in ordered)
            {
                var canonical=ModelOffCanonicalSerializerV1.Serialize(pair.Output);
                if(pair.Document.Utf8Bytes.Length==0||!CryptographicOperations.FixedTimeEquals(canonical.Utf8Bytes,pair.Document.Utf8Bytes)||!string.Equals(canonical.Sha256,pair.Document.Sha256,StringComparison.Ordinal))return new(false,false,"audit.production-cycle-canonical-invalid");
                var asOf=pair.Output.Sources.Select(x=>x.AsOfUtc).Where(x=>x.HasValue).Select(x=>x!.Value).DefaultIfEmpty().Max();if(asOf==default)return new(false,false,"audit.production-cycle-source-time-invalid");
                var sources=JsonSerializer.Serialize(pair.Output.Sources.OrderBy(x=>x.SourceId,StringComparer.Ordinal).Select(x=>new{x.SourceId,Kind=x.Kind.ToString().ToLowerInvariant(),AsOfUtc=x.AsOfUtc!.Value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),Status=x.Status.ToString().ToLowerInvariant(),x.ArtifactHash}));
                prepared.Add((pair.Output,pair.Document,sources,asOf));
            }
        }
        catch(InvalidOperationException){return new(false,false,"audit.production-cycle-canonical-invalid");}
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);var allIdempotent=true;var recorded=DbInstant(_utcNow());
        foreach(var pair in prepared)
        {
            await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT OR IGNORE INTO model_off_canonical_audits(output_id,cycle_id,schema,template_version,canonical_sha256,status,output_kind,sources_json,as_of_utc,recorded_at_utc,canonical_bytes) VALUES($o,$c,$s,$v,$h,$st,$k,$src,$a,$r,$b)";AddParameters(q,("$o",pair.Output.OutputId),("$c",cycle),("$s",ModelOffAgentOutputV1.Schema),("$v",pair.Output.TemplateVersion),("$h",pair.Document.Sha256),("$st",pair.Output.Status.ToString().ToLowerInvariant()),("$k",pair.Output.Agent.ToString().ToLowerInvariant()),("$src",pair.Sources),("$a",DbInstant(pair.AsOf)),("$r",recorded));q.Parameters.Add("$b",SqliteType.Blob).Value=pair.Document.Utf8Bytes;
            if(await q.ExecuteNonQueryAsync(ct)==1){allIdempotent=false;continue;}
            await using var check=c.CreateCommand();check.Transaction=tx;check.CommandText="SELECT cycle_id,canonical_sha256,canonical_bytes FROM model_off_canonical_audits WHERE output_id=$id";check.Parameters.AddWithValue("$id",pair.Output.OutputId);await using var reader=await check.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct)||!string.Equals(reader.GetString(0),cycle,StringComparison.Ordinal)||!string.Equals(reader.GetString(1),pair.Document.Sha256,StringComparison.Ordinal)||!CryptographicOperations.FixedTimeEquals((byte[])reader[2],pair.Document.Utf8Bytes)){await tx.RollbackAsync(ct);return new(false,false,"audit.identity-conflict");}
        }
        foreach(var handoff in handoffs)
        {
            var document=ModelOffCanonicalSerializerV1.SerializeHandoff(handoff);await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT OR IGNORE INTO model_off_canonical_handoffs(handoff_id,cycle_id,from_agent,to_agent,canonical_output_id,canonical_sha256,status,recorded_at_utc,handoff_sha256,canonical_bytes) VALUES($i,$c,$f,$t,$o,$h,$s,$r,$hh,$b)";AddParameters(q,("$i",handoff.HandoffId),("$c",cycle),("$f",handoff.FromAgent.ToString().ToLowerInvariant()),("$t",handoff.ToAgent.ToString().ToLowerInvariant()),("$o",handoff.CanonicalOutputId),("$h",handoff.CanonicalSha256),("$s",handoff.Status.ToString().ToLowerInvariant()),("$r",recorded),("$hh",document.Sha256));q.Parameters.Add("$b",SqliteType.Blob).Value=document.Utf8Bytes;
            if(await q.ExecuteNonQueryAsync(ct)==1){allIdempotent=false;continue;}
            await using var check=c.CreateCommand();check.Transaction=tx;check.CommandText="SELECT cycle_id,handoff_sha256,canonical_bytes FROM model_off_canonical_handoffs WHERE handoff_id=$id";check.Parameters.AddWithValue("$id",handoff.HandoffId);await using var reader=await check.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct)||!string.Equals(reader.GetString(0),cycle,StringComparison.Ordinal)||!string.Equals(reader.GetString(1),document.Sha256,StringComparison.Ordinal)||!CryptographicOperations.FixedTimeEquals((byte[])reader[2],document.Utf8Bytes)){await tx.RollbackAsync(ct);return new(false,false,"audit.handoff-identity-conflict");}
        }
        await tx.CommitAsync(ct);return new(true,allIdempotent,allIdempotent?"audit.production-cycle-idempotent":"audit.production-cycle-persisted");
    }
    public async Task<bool> HasModelOffCanonicalAuditAsync(string outputId,CancellationToken ct)
    {
        if(!QueueToken(outputId,240))return false;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);
        await using var q=c.CreateCommand();q.CommandText="SELECT 1 FROM model_off_canonical_audits WHERE output_id=$id LIMIT 1";q.Parameters.AddWithValue("$id",outputId);
        return await q.ExecuteScalarAsync(ct) is not null;
    }
    public Task CompleteCycleAsync(string id,DecisionPlan d,string risk,string? request,string? response,CancellationToken ct)=>Exec("UPDATE cycles SET completed_at=$t,status='COMPLETED',decision_json=$d,risk_result=$r,brain_request=$q,brain_response=$b WHERE id=$i",ct,("$i",id),("$t",DateTime.UtcNow.ToString("O")),("$d",JsonSerializer.Serialize(d)),("$r",risk),("$q",request??""),("$b",response??""));
    public async Task FailCycleAsync(string id,string stage,Exception ex,CancellationToken ct)=>await Exec("UPDATE cycles SET completed_at=$t,status='FAILED',risk_result=$s,error=$e WHERE id=$i",ct,("$i",id),("$t",DateTime.UtcNow.ToString("O")),("$s",SensitiveDataRedactor.ForLog(stage,120)),("$e",SensitiveDataRedactor.Redact(ex.ToString())));
    public async Task SaveSnapshotAsync(AccountSnapshot a,IReadOnlyList<ManagedPosition> p,IReadOnlyList<ExchangeOrder> o,CancellationToken ct)=>await Exec("INSERT INTO snapshots(collected_at,account_json,positions_json,orders_json) VALUES($t,$a,$p,$o)",ct,("$t",DateTime.UtcNow.ToString("O")),("$a",JsonSerializer.Serialize(a)),("$p",JsonSerializer.Serialize(p)),("$o",JsonSerializer.Serialize(o)));
    public async Task SaveIntentAsync(string cycle,ExecutionIntent i,string status,string? orderId,CancellationToken ct)=>await Exec("INSERT OR REPLACE INTO order_intents(client_order_id,cycle_id,symbol,side,quantity,status,exchange_order_id,updated_at,details) VALUES($id,$c,$s,$side,$q,$st,$oid,$t,$d)",ct,("$id",i.ClientOrderId),("$c",cycle),("$s",i.Symbol),("$side",i.Side.ToString()),("$q",i.Quantity.ToString(CultureInfo.InvariantCulture)),("$st",status),("$oid",orderId),("$t",DateTime.UtcNow.ToString("O")),("$d",JsonSerializer.Serialize(i)));
    public async Task<TradingReviewQueueMutationResult> SaveTradingReviewQueueAsync(
        TradingApprovalRequest request,
        DurableReviewExecutionArtifactV1 artifact,
        CancellationToken ct)
    {
        if(!ValidApprovalRequest(request)||request.Mode!=TradingAuthorizationMode.Review)
            return new(false,"review.request-invalid");
        byte[] artifactBytes;DurableReviewArtifactHashes hashes;
        try{artifactBytes=DurableReviewArtifactCanonicalizer.Serialize(artifact);hashes=DurableReviewArtifactCanonicalizer.ComputeHashes(artifact);}
        catch(ArgumentException){return new(false,"review.artifact-invalid");}
        if(!FixedHashEquals(request.IntentHash,hashes.IntentHash)||request.IssuedAtUtc.ToUniversalTime()!=artifact.CreatedAtUtc||request.ExpiresAtUtc.ToUniversalTime()!=artifact.ExpiresAtUtc)
            return new(false,"review.request-artifact-mismatch");
        var now=_utcNow().ToUniversalTime();
        try
        {
            await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
            await using(var insertRequest=c.CreateCommand())
            {
                insertRequest.Transaction=tx;insertRequest.CommandText="""
                    INSERT INTO trading_approval_requests(request_id,mode,correlation_id,intent_hash,artifact_hash,user_id,device_id,session_id,issued_at,expires_at,revoked_at,consumed_at)
                    VALUES($request,'Review',$correlation,$hash,$artifactHash,$user,$device,$session,$issued,$expires,NULL,NULL)
                    """;
                AddParameters(insertRequest,("$request",request.RequestId),("$correlation",request.CorrelationId),("$hash",hashes.IntentHash),("$artifactHash",hashes.ArtifactHash),("$user",request.UserId),("$device",request.DeviceId),("$session",request.SessionId),("$issued",DbInstant(artifact.CreatedAtUtc)),("$expires",DbInstant(artifact.ExpiresAtUtc)));
                if(await insertRequest.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"review.request-not-persisted");}
            }
            await using(var insertQueue=c.CreateCommand())
            {
                insertQueue.Transaction=tx;insertQueue.CommandText="""
                    INSERT INTO trading_review_execution_queue(
                        request_id,contract_version,artifact_bytes,artifact_hash,intent_hash,provider_id,environment,
                        strategy_id,strategy_version,market_collected_at,market_data_version,created_at,expires_at,
                        status,attempt_count,lease_owner,lease_expires_at,last_code,updated_at)
                    VALUES($request,$version,$artifact,$artifactHash,$intentHash,$provider,$environment,$strategy,$strategyVersion,
                        $marketAt,$marketVersion,$created,$expires,'Pending',0,NULL,NULL,'review.pending',$updated)
                    """;
                AddParameters(insertQueue,("$request",request.RequestId),("$version",artifact.ContractVersion),("$artifact",artifactBytes),("$artifactHash",hashes.ArtifactHash),("$intentHash",hashes.IntentHash),("$provider",artifact.ProviderId),("$environment",artifact.Environment),("$strategy",artifact.StrategyId),("$strategyVersion",artifact.StrategyVersion),("$marketAt",DbInstant(artifact.MarketCollectedAtUtc)),("$marketVersion",artifact.MarketDataVersion),("$created",DbInstant(artifact.CreatedAtUtc)),("$expires",DbInstant(artifact.ExpiresAtUtc)),("$updated",DbInstant(now)));
                if(await insertQueue.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"review.queue-not-persisted");}
            }
            await AppendTradingReviewEventAsync(c,tx,request.RequestId,null,TradingReviewQueueStatus.Pending,"review.pending","system",now,ct);
            await tx.CommitAsync(ct);return new(true,"review.queue-persisted");
        }
        catch(SqliteException){return new(false,"review.queue-persistence-failed");}
    }

    public async Task<PersistedTradingReviewQueueItem?> GetTradingReviewQueueItemAsync(string requestId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(requestId))return null;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=QueueReadCommand(c,"WHERE queue.request_id=$request",1,0);q.Parameters.AddWithValue("$request",requestId);
        await using var r=await q.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadTradingReviewQueueItem(r):null;
    }

    public async Task<IReadOnlyList<PersistedTradingReviewQueueItem>> GetTradingReviewQueueAsync(TradingReviewQueueStatus? status,int limit,int offset,CancellationToken ct)
    {
        var list=new List<PersistedTradingReviewQueueItem>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);
        await using var q=QueueReadCommand(c,"WHERE ($status IS NULL OR queue.status=$status)",Math.Clamp(limit,1,100),Math.Clamp(offset,0,10_000));q.Parameters.AddWithValue("$status",status is null?DBNull.Value:status.Value.ToString());
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(ReadTradingReviewQueueItem(r));return list;
    }

    internal async Task<TradingReviewQueueMutationResult> TryApproveTradingReviewWithReceiptAsync(
        string requestId,string userId,string deviceId,string sessionId,string providerId,string reason,CancellationToken ct)
    {
        var safeReason=SensitiveDataRedactor.ForLog(reason,240);
        if(string.IsNullOrWhiteSpace(requestId)||!QueueIdentity(userId)||!QueueIdentity(deviceId)||!QueueIdentity(sessionId)||!QueueToken(providerId,64)||string.IsNullOrWhiteSpace(safeReason))return new(false,"review.approval-context-invalid");
        var now=_utcNow().ToUniversalTime();
        try
        {
            await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
            PersistedTradingReviewQueueItem item;
            await using(var read=QueueReadCommand(c,"WHERE queue.request_id=$request",1,0))
            {
                read.Transaction=tx;read.Parameters.AddWithValue("$request",requestId);await using var r=await read.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)){await tx.RollbackAsync(ct);return new(false,"review.not-found");}item=ReadTradingReviewQueueItem(r);
            }
            if(item.Status!=TradingReviewQueueStatus.Pending){await tx.RollbackAsync(ct);return new(false,"review.not-pending");}
            if(!item.ArtifactValid||item.Artifact is null){await tx.RollbackAsync(ct);return new(false,"review.artifact-invalid");}
            var artifact=item.Artifact;
            if(artifact.CreatedAtUtc>now){await tx.RollbackAsync(ct);return new(false,"review.not-yet-valid");}
            if(artifact.ExpiresAtUtc<=now)
            {
                await using var expire=c.CreateCommand();expire.Transaction=tx;expire.CommandText="UPDATE trading_review_execution_queue SET status='Expired',last_code='review.expired',updated_at=$now WHERE request_id=$request AND status='Pending'";AddParameters(expire,("$now",DbInstant(now)),("$request",requestId));
                if(await expire.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"review.not-pending");}
                await AppendTradingReviewEventAsync(c,tx,requestId,TradingReviewQueueStatus.Pending,TradingReviewQueueStatus.Expired,"review.expired","system",now,ct);await tx.CommitAsync(ct);return new(false,"review.expired");
            }
            if(!string.Equals(artifact.Environment,"Testnet",StringComparison.Ordinal)) {await tx.RollbackAsync(ct);return new(false,"review.testnet-required");}
            if(!string.Equals(artifact.ProviderId,providerId,StringComparison.OrdinalIgnoreCase)){await tx.RollbackAsync(ct);return new(false,"review.provider-mismatch");}
            await using(var request=c.CreateCommand())
            {
                request.Transaction=tx;request.CommandText="SELECT mode,intent_hash,artifact_hash,user_id,device_id,session_id,issued_at,expires_at,revoked_at,consumed_at FROM trading_approval_requests WHERE request_id=$request";request.Parameters.AddWithValue("$request",requestId);await using var r=await request.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)||r.GetString(0)!="Review"||!FixedHashEquals(r.GetString(1),item.IntentHash)||r.IsDBNull(2)||!FixedHashEquals(r.GetString(2),item.ArtifactHash)||!string.Equals(r.GetString(3),userId,StringComparison.Ordinal)||!string.Equals(r.GetString(4),deviceId,StringComparison.Ordinal)||!string.Equals(r.GetString(5),sessionId,StringComparison.Ordinal)||Instant(r.GetString(6))!=artifact.CreatedAtUtc||Instant(r.GetString(7))!=artifact.ExpiresAtUtc||!r.IsDBNull(8)||!r.IsDBNull(9)){await tx.RollbackAsync(ct);return new(false,"review.approval-context-mismatch");}
            }
            var receiptId=Guid.NewGuid().ToString("N");
            await using(var receipt=c.CreateCommand())
            {
                receipt.Transaction=tx;receipt.CommandText="""
                    INSERT INTO trading_approval_receipts(receipt_id,request_id,correlation_id,intent_hash,artifact_hash,user_id,device_id,session_id,approved,issued_at,expires_at,revoked_at,consumed_at)
                    SELECT $receipt,request.request_id,request.correlation_id,request.intent_hash,request.artifact_hash,request.user_id,request.device_id,request.session_id,1,$issued,request.expires_at,NULL,NULL
                    FROM trading_approval_requests request JOIN trading_review_execution_queue queue ON queue.request_id=request.request_id
                    WHERE request.request_id=$request AND queue.status='Pending' AND request.revoked_at IS NULL AND request.consumed_at IS NULL
                    """;AddParameters(receipt,("$receipt",receiptId),("$issued",DbInstant(now)),("$request",requestId));
                if(await receipt.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"review.receipt-not-persisted");}
            }
            await using(var approve=c.CreateCommand())
            {
                approve.Transaction=tx;approve.CommandText="UPDATE trading_review_execution_queue SET status='Approved',last_code='review.approved',updated_at=$now WHERE request_id=$request AND status='Pending' AND expires_at>$now";AddParameters(approve,("$now",DbInstant(now)),("$request",requestId));
                if(await approve.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"review.not-pending");}
            }
            await AppendTradingReviewEventAsync(c,tx,requestId,TradingReviewQueueStatus.Pending,TradingReviewQueueStatus.Approved,"review.approved","desktop",now,ct,safeReason);await tx.CommitAsync(ct);return new(true,"review.approved");
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(SqliteException){return new(false,"review.approval-persistence-failed");}
    }

    internal Task<TradingReviewQueueMutationResult> TryApproveTradingReviewAsync(string requestId,CancellationToken ct)
        =>TryTradingReviewTransitionAsync(requestId,TradingReviewQueueStatus.Pending,TradingReviewQueueStatus.Approved,null,"review.approved","desktop",ct);
    internal Task<TradingReviewQueueMutationResult> TryRejectTradingReviewAsync(string requestId,CancellationToken ct)
        =>TryTradingReviewTransitionAsync(requestId,TradingReviewQueueStatus.Pending,TradingReviewQueueStatus.Rejected,null,"review.rejected","desktop",ct);
    internal Task<TradingReviewQueueMutationResult> TryRevokeTradingReviewAsync(string requestId,TradingReviewQueueStatus expected,CancellationToken ct)
        =>expected is TradingReviewQueueStatus.Pending or TradingReviewQueueStatus.Approved or TradingReviewQueueStatus.Claimed
            ?TryTradingReviewTransitionAsync(requestId,expected,TradingReviewQueueStatus.Revoked,null,"review.revoked","desktop",ct)
            :Task.FromResult(new TradingReviewQueueMutationResult(false,"review.transition-invalid"));
    internal Task<TradingReviewQueueMutationResult> TryRejectTradingReviewAsync(string requestId,string reason,CancellationToken ct)
        =>TryTradingReviewTransitionAsync(requestId,TradingReviewQueueStatus.Pending,TradingReviewQueueStatus.Rejected,null,"review.rejected","desktop",ct,reason:reason);
    internal Task<TradingReviewQueueMutationResult> TryRevokeTradingReviewAsync(string requestId,TradingReviewQueueStatus expected,string reason,CancellationToken ct)
        =>expected is TradingReviewQueueStatus.Pending or TradingReviewQueueStatus.Approved or TradingReviewQueueStatus.Claimed
            ?TryTradingReviewTransitionAsync(requestId,expected,TradingReviewQueueStatus.Revoked,null,"review.revoked","desktop",ct,reason:reason)
            :Task.FromResult(new TradingReviewQueueMutationResult(false,"review.transition-invalid"));

    public async Task<TradingReviewQueueMutationResult> TryExpireTradingReviewAsync(string requestId,TradingReviewQueueStatus expected,CancellationToken ct)
    {
        if(expected is not (TradingReviewQueueStatus.Pending or TradingReviewQueueStatus.Approved or TradingReviewQueueStatus.Claimed))return new(false,"review.transition-invalid");
        return await TryTradingReviewTransitionAsync(requestId,expected,TradingReviewQueueStatus.Expired,null,"review.expired","system",ct,requireExpired:true);
    }

    public async Task<TradingReviewQueueClaimResult> TryClaimTradingReviewAsync(string requestId,string leaseOwner,TimeSpan leaseLifetime,CancellationToken ct)
    {
        if(!QueueToken(leaseOwner,96)||leaseLifetime<=TimeSpan.Zero||leaseLifetime>TimeSpan.FromMinutes(5))return new(false,"review.claim-invalid",0);
        var now=_utcNow().ToUniversalTime();
        try
        {
            await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
            TradingReviewQueueStatus current;int attempts;DateTimeOffset? leaseExpires;
            await using(var read=QueueReadCommand(c,"WHERE queue.request_id=$request",1,0))
            {
                read.Transaction=tx;read.Parameters.AddWithValue("$request",requestId);
                await using var r=await read.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)){await tx.RollbackAsync(ct);return new(false,"review.not-claimable",0);}
                var item=ReadTradingReviewQueueItem(r);if(!item.ArtifactValid||item.Artifact is null){await tx.RollbackAsync(ct);return new(false,"review.artifact-invalid",0);}
                current=item.Status;attempts=item.AttemptCount;leaseExpires=item.LeaseExpiresAtUtc;var expires=item.Artifact.ExpiresAtUtc;
                if(expires<=now||current!=TradingReviewQueueStatus.Approved&&!(current==TradingReviewQueueStatus.Claimed&&leaseExpires<=now)){await tx.RollbackAsync(ct);return new(false,"review.not-claimable",attempts);}
            }
            var nextAttempts=checked(attempts+1);var nextLease=now.Add(leaseLifetime);
            await using(var update=c.CreateCommand())
            {
                update.Transaction=tx;update.CommandText="UPDATE trading_review_execution_queue SET status='Claimed',attempt_count=$attempts,lease_owner=$owner,lease_expires_at=$lease,last_code='review.claimed',updated_at=$now WHERE request_id=$request AND status=$status";
                AddParameters(update,("$attempts",nextAttempts),("$owner",leaseOwner),("$lease",DbInstant(nextLease)),("$now",DbInstant(now)),("$request",requestId),("$status",current.ToString()));
                if(await update.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"review.not-claimable",attempts);}
            }
            await AppendTradingReviewEventAsync(c,tx,requestId,current,TradingReviewQueueStatus.Claimed,"review.claimed","processor",now,ct);await tx.CommitAsync(ct);return new(true,"review.claimed",nextAttempts);
        }
        catch(SqliteException){return new(false,"review.claim-failed",0);}
    }

    public async Task<TradingReviewQueueClaimResult> TryClaimTradingReviewReconciliationAsync(string requestId,string leaseOwner,TimeSpan leaseLifetime,CancellationToken ct)
    {
        if(!QueueToken(leaseOwner,96)||leaseLifetime<=TimeSpan.Zero||leaseLifetime>TimeSpan.FromMinutes(5))return new(false,"review.reconcile-claim-invalid",0);var now=_utcNow().ToUniversalTime();
        try
        {
            await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);PersistedTradingReviewQueueItem item;
            await using(var read=QueueReadCommand(c,"WHERE queue.request_id=$request",1,0)){read.Transaction=tx;read.Parameters.AddWithValue("$request",requestId);await using var r=await read.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)){await tx.RollbackAsync(ct);return new(false,"review.not-reconcilable",0);}item=ReadTradingReviewQueueItem(r);}
            if(!item.ArtifactValid||item.Artifact is null){await tx.RollbackAsync(ct);return new(false,"review.artifact-invalid",item.AttemptCount);}
            if(item.Status!=TradingReviewQueueStatus.Executing||item.LeaseExpiresAtUtc is not null&&item.LeaseExpiresAtUtc>now){await tx.RollbackAsync(ct);return new(false,"review.not-reconcilable",item.AttemptCount);}
            var attempts=checked(item.AttemptCount+1);await using(var update=c.CreateCommand()){update.Transaction=tx;update.CommandText="UPDATE trading_review_execution_queue SET status='Reconciling',attempt_count=$attempts,lease_owner=$owner,lease_expires_at=$lease,last_code='review.reconciling',updated_at=$now WHERE request_id=$request AND status='Executing'";AddParameters(update,("$attempts",attempts),("$owner",leaseOwner),("$lease",DbInstant(now.Add(leaseLifetime))),("$now",DbInstant(now)),("$request",requestId));if(await update.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"review.not-reconcilable",item.AttemptCount);}}
            await AppendTradingReviewEventAsync(c,tx,requestId,TradingReviewQueueStatus.Executing,TradingReviewQueueStatus.Reconciling,"review.reconciling","processor",now,ct);await tx.CommitAsync(ct);return new(true,"review.reconciling",attempts);
        }
        catch(SqliteException){return new(false,"review.reconcile-claim-failed",0);}
    }

    public Task<TradingReviewQueueMutationResult> TryTransitionTradingReviewAsync(string requestId,TradingReviewQueueStatus expected,TradingReviewQueueStatus next,string? leaseOwner,string eventCode,CancellationToken ct)
    {
        if(!AllowedProcessorQueueTransition(expected,next)||!QueueToken(eventCode,120))return Task.FromResult(new TradingReviewQueueMutationResult(false,"review.transition-invalid"));
        return TryTradingReviewTransitionAsync(requestId,expected,next,leaseOwner,eventCode,"processor",ct);
    }

    public async Task<IReadOnlyList<PersistedTradingReviewQueueEvent>> GetTradingReviewQueueEventsAsync(string requestId,int limit,CancellationToken ct)
    {
        var list=new List<PersistedTradingReviewQueueEvent>();if(string.IsNullOrWhiteSpace(requestId))return list;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT sequence,occurred_at,from_status,to_status,event_code,actor_kind,reason FROM trading_review_queue_events WHERE request_id=$request ORDER BY sequence LIMIT $limit";q.Parameters.AddWithValue("$request",requestId);q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(new(requestId,r.GetInt32(0),Instant(r.GetString(1)),r.IsDBNull(2)?null:Enum.Parse<TradingReviewQueueStatus>(r.GetString(2)),Enum.Parse<TradingReviewQueueStatus>(r.GetString(3)),r.GetString(4),r.GetString(5),r.GetString(6)));return list;
    }

    public async Task<AutomaticExecutionMutationResult> SaveAutomaticExecutionAsync(string executionId,DurableExecutionArtifactV2 artifact,CancellationToken ct)
    {
        if(!QueueToken(executionId,120))return new(false,"automatic.execution-id-invalid");
        byte[] bytes;DurableExecutionArtifactHashesV2 hashes;
        try{bytes=DurableExecutionArtifactCanonicalizerV2.Serialize(artifact);hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);}catch(ArgumentException){return new(false,"automatic.artifact-invalid");}
        var now=_utcNow().ToUniversalTime();
        try
        {
            await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
            await using(var q=c.CreateCommand())
            {
                q.Transaction=tx;q.CommandText="""
                    INSERT INTO automatic_execution_queue(execution_id,correlation_id,contract_version,artifact_bytes,artifact_hash,intent_hash,risk_receipt_bytes,risk_receipt_hash,provider_id,environment,strategy_id,strategy_version,market_collected_at,market_data_version,created_at,expires_at,status,attempt_count,lease_owner,lease_expires_at,last_code,updated_at)
                    VALUES($id,$correlation,$version,$bytes,$artifactHash,$intentHash,NULL,NULL,$provider,'Testnet',$strategy,$strategyVersion,$marketAt,$marketVersion,$created,$expires,'Proposed',0,NULL,NULL,'automatic.proposed',$now)
                    """;
                AddParameters(q,("$id",executionId),("$correlation",artifact.CorrelationId),("$version",artifact.ContractVersion),("$bytes",bytes),("$artifactHash",hashes.ArtifactHash),("$intentHash",hashes.IntentHash),("$provider",artifact.ProviderId),("$strategy",artifact.StrategyId),("$strategyVersion",artifact.StrategyVersion),("$marketAt",DbInstant(artifact.MarketCollectedAtUtc)),("$marketVersion",artifact.MarketDataVersion),("$created",DbInstant(artifact.CreatedAtUtc)),("$expires",DbInstant(artifact.ExpiresAtUtc)),("$now",DbInstant(now)));
                if(await q.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"automatic.not-persisted");}
            }
            await AppendAutomaticExecutionEventAsync(c,tx,executionId,null,AutomaticExecutionQueueStatus.Proposed,"automatic.proposed","producer",now,ct);await tx.CommitAsync(ct);return new(true,"automatic.persisted");
        }
        catch(SqliteException){return new(false,"automatic.persistence-failed");}
    }

    public async Task<AutomaticExecutionMutationResult> RecordAutomaticRiskDecisionAsync(string executionId,DeterministicRiskReceipt receipt,CancellationToken ct)
    {
        if(!QueueToken(executionId,120))return new(false,"automatic.execution-id-invalid");var now=_utcNow().ToUniversalTime();var item=await GetAutomaticExecutionAsync(executionId,ct);
        if(item is null||item.Status!=AutomaticExecutionQueueStatus.Proposed)return new(false,"automatic.not-proposed");
        var valid=ValidAutomaticRiskReceipt(receipt,item,now);var next=valid?AutomaticExecutionQueueStatus.RiskApproved:AutomaticExecutionQueueStatus.RiskBlocked;var code=valid?"automatic.risk-approved":"automatic.risk-blocked";
        byte[] bytes;try{bytes=DurableExecutionArtifactCanonicalizerV2.SerializeRiskReceipt(receipt);}catch(ArgumentException){return new(false,"automatic.risk-receipt-invalid");}
        var hash=DurableReviewArtifactCanonicalizer.Sha256Hex(bytes);
        try
        {
            await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
            await using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="UPDATE automatic_execution_queue SET risk_receipt_bytes=$bytes,risk_receipt_hash=$hash,status=$next,last_code=$code,updated_at=$now WHERE execution_id=$id AND status='Proposed'";AddParameters(q,("$bytes",bytes),("$hash",hash),("$next",next.ToString()),("$code",code),("$now",DbInstant(now)),("$id",executionId));if(await q.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"automatic.not-proposed");}}
            await AppendAutomaticExecutionEventAsync(c,tx,executionId,AutomaticExecutionQueueStatus.Proposed,next,code,"risk-gate",now,ct);await tx.CommitAsync(ct);return new(true,code);
        }
        catch(SqliteException){return new(false,"automatic.persistence-failed");}
    }

    public async Task<PersistedAutomaticExecution?> GetAutomaticExecutionAsync(string executionId,CancellationToken ct)
    {
        if(!QueueToken(executionId,120))return null;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=AutomaticQueueReadCommand(c,"WHERE execution_id=$id",1);q.Parameters.AddWithValue("$id",executionId);await using var r=await q.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadAutomaticExecution(r):null;
    }

    public async Task<IReadOnlyList<PersistedAutomaticExecution>> GetAutomaticExecutionQueueAsync(AutomaticExecutionQueueStatus status,int limit,CancellationToken ct)
    {
        var result=new List<PersistedAutomaticExecution>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=AutomaticQueueReadCommand(c,"WHERE status=$status",Math.Clamp(limit,1,100));q.Parameters.AddWithValue("$status",status.ToString());await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result.Add(ReadAutomaticExecution(r));return result;
    }

    public async Task<bool> HasAutomaticExecutionBlockingRepeatAsync(string strategyId,CancellationToken ct)
    {
        if(!QueueToken(strategyId,96))return true;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT 1
            FROM automatic_execution_queue
            WHERE strategy_id=$strategy
              AND status IN ('Proposed','RiskApproved','Claimed','Executing','Reconciling','Succeeded','UnknownOutcome')
            LIMIT 1
            """;
        q.Parameters.AddWithValue("$strategy",strategyId);
        return await q.ExecuteScalarAsync(ct) is not null;
    }

    internal async Task<IReadOnlyList<AutomaticExecutionObservationCandidate>> GetAutomaticExecutionObservationPageAsync(long afterEventId,int limit,CancellationToken ct)
    {
        if(afterEventId<0)throw new ArgumentOutOfRangeException(nameof(afterEventId));var result=new List<AutomaticExecutionObservationCandidate>();
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
            SELECT q.execution_id,q.correlation_id,q.contract_version,q.artifact_bytes,q.artifact_hash,q.intent_hash,
                   q.risk_receipt_bytes,q.risk_receipt_hash,q.provider_id,q.environment,q.strategy_id,q.strategy_version,
                   q.market_collected_at,q.market_data_version,q.created_at,q.expires_at,q.status,q.attempt_count,
                   q.lease_owner,q.lease_expires_at,q.last_code,q.updated_at,e.event_cursor
            FROM automatic_execution_queue q
            JOIN (SELECT execution_id,MAX(id) AS event_cursor FROM automatic_execution_events GROUP BY execution_id) e
              ON e.execution_id=q.execution_id
            WHERE e.event_cursor>$after
            ORDER BY e.event_cursor,q.execution_id
            LIMIT $limit
            """;q.Parameters.AddWithValue("$after",afterEventId);q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result.Add(new(ReadAutomaticExecution(r),r.GetInt64(22)));return result;
    }

    internal async Task<IReadOnlyList<AutomaticExecutionObservationCandidate>> GetAutomaticExecutionEvidenceRetryPageAsync(long afterEventId,int limit,CancellationToken ct)
    {
        if(afterEventId<0)throw new ArgumentOutOfRangeException(nameof(afterEventId));var result=new List<AutomaticExecutionObservationCandidate>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
            SELECT q.execution_id,q.correlation_id,q.contract_version,q.artifact_bytes,q.artifact_hash,q.intent_hash,
                   q.risk_receipt_bytes,q.risk_receipt_hash,q.provider_id,q.environment,q.strategy_id,q.strategy_version,
                   q.market_collected_at,q.market_data_version,q.created_at,q.expires_at,q.status,q.attempt_count,
                   q.lease_owner,q.lease_expires_at,q.last_code,q.updated_at,e.event_cursor
            FROM automatic_execution_queue q
            JOIN (SELECT execution_id,MAX(id) AS event_cursor,MAX(sequence) AS event_sequence FROM automatic_execution_events GROUP BY execution_id) e
              ON e.execution_id=q.execution_id
            WHERE q.status='Succeeded'
              AND e.event_cursor>$after
              AND NOT EXISTS(
                  SELECT 1 FROM model_off_canonical_audits a
                  WHERE a.output_id=q.execution_id||'-observation-'||e.event_sequence||'-succeeded-confirmed-execution')
            ORDER BY e.event_cursor,q.execution_id
            LIMIT $limit
            """;q.Parameters.AddWithValue("$after",afterEventId);q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result.Add(new(ReadAutomaticExecution(r),r.GetInt64(22)));return result;
    }

    public async Task<AutomaticExecutionClaimResult> TryClaimAutomaticExecutionAsync(string executionId,string leaseOwner,TimeSpan leaseLifetime,CancellationToken ct)
        =>await TryClaimAutomaticAsync(executionId,leaseOwner,leaseLifetime,AutomaticExecutionQueueStatus.RiskApproved,AutomaticExecutionQueueStatus.Claimed,"automatic.claimed",ct);

    public async Task<AutomaticExecutionClaimResult> TryClaimAutomaticReconciliationAsync(string executionId,string leaseOwner,TimeSpan leaseLifetime,CancellationToken ct)
    {
        var item=await GetAutomaticExecutionAsync(executionId,ct);if(item is null||item.Status is not (AutomaticExecutionQueueStatus.Executing or AutomaticExecutionQueueStatus.UnknownOutcome)||item.LeaseExpiresAtUtc is not null&&item.LeaseExpiresAtUtc>_utcNow().ToUniversalTime())return new(false,"automatic.not-reconcilable",item?.AttemptCount??0);
        return await TryClaimAutomaticAsync(executionId,leaseOwner,leaseLifetime,item.Status,AutomaticExecutionQueueStatus.Reconciling,"automatic.reconciling",ct);
    }

    public async Task<AutomaticExecutionMutationResult> TryTransitionAutomaticExecutionAsync(string executionId,AutomaticExecutionQueueStatus expected,AutomaticExecutionQueueStatus next,string leaseOwner,string code,CancellationToken ct)
    {
        if(!AllowedAutomaticTransition(expected,next)||!QueueToken(leaseOwner,96)||!QueueToken(code,120))return new(false,"automatic.transition-invalid");var now=_utcNow().ToUniversalTime();
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
        var keep=next is AutomaticExecutionQueueStatus.Executing or AutomaticExecutionQueueStatus.UnknownOutcome;
        await using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="UPDATE automatic_execution_queue SET status=$next,lease_owner=CASE WHEN $keep=1 THEN lease_owner ELSE NULL END,lease_expires_at=CASE WHEN $keep=1 THEN lease_expires_at ELSE NULL END,last_code=$code,updated_at=$now WHERE execution_id=$id AND status=$expected AND lease_owner=$owner";AddParameters(q,("$next",next.ToString()),("$keep",keep?1:0),("$code",code),("$now",DbInstant(now)),("$id",executionId),("$expected",expected.ToString()),("$owner",leaseOwner));if(await q.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"automatic.transition-not-applied");}}
        await AppendAutomaticExecutionEventAsync(c,tx,executionId,expected,next,code,"processor",now,ct);await tx.CommitAsync(ct);return new(true,code);
    }

    public async Task<IReadOnlyList<PersistedAutomaticExecutionEvent>> GetAutomaticExecutionEventsAsync(string executionId,int limit,CancellationToken ct)
    {
        var result=new List<PersistedAutomaticExecutionEvent>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT sequence,occurred_at,from_status,to_status,event_code,actor_kind FROM automatic_execution_events WHERE execution_id=$id ORDER BY sequence LIMIT $limit";q.Parameters.AddWithValue("$id",executionId);q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result.Add(new(executionId,r.GetInt32(0),Instant(r.GetString(1)),r.IsDBNull(2)?null:Enum.Parse<AutomaticExecutionQueueStatus>(r.GetString(2)),Enum.Parse<AutomaticExecutionQueueStatus>(r.GetString(3)),r.GetString(4),r.GetString(5)));return result;
    }
    public async Task<TradingApprovalPersistenceResult> SaveTradingApprovalRequestAsync(TradingApprovalRequest request,CancellationToken ct)
    {
        if(!ValidApprovalRequest(request))return new(false,"approval.request-invalid");
        var rows=await ExecRows("""
            INSERT OR IGNORE INTO trading_approval_requests(request_id,mode,correlation_id,intent_hash,artifact_hash,user_id,device_id,session_id,issued_at,expires_at,revoked_at,consumed_at)
            VALUES($request,$mode,$correlation,$hash,$artifactHash,$user,$device,$session,$issued,$expires,$revoked,$consumed)
            """,ct,
            ("$request",request.RequestId),("$mode",request.Mode.ToString()),("$correlation",request.CorrelationId),("$hash",request.IntentHash),
            ("$artifactHash",request.ArtifactHash),
            ("$user",request.UserId),("$device",request.DeviceId),("$session",request.SessionId),("$issued",DbInstant(request.IssuedAtUtc)),
            ("$expires",DbInstant(request.ExpiresAtUtc)),("$revoked",DbInstant(request.RevokedAtUtc)),("$consumed",DbInstant(request.ConsumedAtUtc)));
        return rows==1?new(true,"approval.request-persisted"):new(false,"approval.request-not-persisted");
    }
    public async Task<TradingApprovalPersistenceResult> SaveTradingApprovalReceiptAsync(string requestId,TradingApprovalReceipt receipt,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(requestId)||!ValidApprovalReceipt(receipt))return new(false,"approval.receipt-invalid");
        var rows=await ExecRows("""
            INSERT OR IGNORE INTO trading_approval_receipts(receipt_id,request_id,correlation_id,intent_hash,artifact_hash,user_id,device_id,session_id,approved,issued_at,expires_at,revoked_at,consumed_at)
            SELECT $receipt,$request,$correlation,$hash,$artifactHash,$user,$device,$session,$approved,$issued,$expires,$revoked,$consumed
            FROM trading_approval_requests request
            WHERE request.request_id=$request
              AND request.correlation_id=$correlation AND request.intent_hash=$hash
              AND ($artifactHash IS NULL OR request.artifact_hash=$artifactHash)
              AND request.user_id=$user AND request.device_id=$device AND request.session_id=$session
              AND request.revoked_at IS NULL AND request.consumed_at IS NULL
              AND request.issued_at<=$issued AND request.expires_at>=$expires
            """,ct,
            ("$receipt",receipt.ReceiptId),("$request",requestId),("$correlation",receipt.CorrelationId),("$hash",receipt.IntentHash),
            ("$artifactHash",receipt.ArtifactHash),
            ("$user",receipt.UserId),("$device",receipt.DeviceId),("$session",receipt.SessionId),("$approved",receipt.Approved?1:0),
            ("$issued",DbInstant(receipt.IssuedAtUtc)),("$expires",DbInstant(receipt.ExpiresAtUtc)),
            ("$revoked",DbInstant(receipt.RevokedAtUtc)),("$consumed",DbInstant(receipt.ConsumedAtUtc)));
        return rows==1?new(true,"approval.receipt-persisted"):new(false,"approval.receipt-not-persisted");
    }
    public async Task<TradingApprovalRequest?> GetTradingApprovalRequestAsync(string requestId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(requestId))return null;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT mode,correlation_id,intent_hash,user_id,device_id,session_id,issued_at,expires_at,revoked_at,consumed_at,artifact_hash FROM trading_approval_requests WHERE request_id=$request";q.Parameters.AddWithValue("$request",requestId);await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
        var mode=Enum.TryParse<TradingAuthorizationMode>(r.GetString(0),out var parsed)&&Enum.IsDefined(parsed)?parsed:TradingAuthorizationMode.Review;
        return new(requestId,mode,r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),Instant(r.GetString(6)),Instant(r.GetString(7)),NullableInstant(r,8),NullableInstant(r,9),r.IsDBNull(10)?null:r.GetString(10));
    }
    public async Task<PersistedTradingApprovalReceipt?> GetTradingApprovalReceiptAsync(string receiptId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(receiptId))return null;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT request_id,correlation_id,intent_hash,user_id,device_id,session_id,approved,issued_at,expires_at,revoked_at,consumed_at,artifact_hash FROM trading_approval_receipts WHERE receipt_id=$receipt";q.Parameters.AddWithValue("$receipt",receiptId);await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
        return new(r.GetString(0),new(receiptId,r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6)==1,Instant(r.GetString(7)),Instant(r.GetString(8)),NullableInstant(r,9),NullableInstant(r,10),r.IsDBNull(11)?null:r.GetString(11)));
    }
    public async Task<PersistedTradingApprovalReceipt?> GetTradingApprovalReceiptForRequestAsync(string requestId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(requestId))return null;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT receipt_id,correlation_id,intent_hash,user_id,device_id,session_id,approved,issued_at,expires_at,revoked_at,consumed_at,artifact_hash FROM trading_approval_receipts WHERE request_id=$request ORDER BY issued_at DESC,receipt_id DESC LIMIT 1";q.Parameters.AddWithValue("$request",requestId);await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
        return new(requestId,new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6)==1,Instant(r.GetString(7)),Instant(r.GetString(8)),NullableInstant(r,9),NullableInstant(r,10),r.IsDBNull(11)?null:r.GetString(11)));
    }
    public async Task<TradingApprovalConsumptionResult> TryConsumeTradingApprovalAsync(TradingApprovalConsumption consumption,CancellationToken ct)
    {
        if(!ValidApprovalConsumption(consumption))return new(false,"approval.consume-invalid");
        var consumedAtUtc=_utcNow().ToUniversalTime();
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var receiptUpdate=c.CreateCommand();receiptUpdate.Transaction=tx;receiptUpdate.CommandText="""
            UPDATE trading_approval_receipts SET consumed_at=$now
            WHERE receipt_id=$receipt AND request_id=$request AND approved=1
              AND consumed_at IS NULL AND revoked_at IS NULL AND issued_at<=$now AND expires_at>$now
              AND correlation_id=$correlation AND intent_hash=$hash
              AND ($artifactHash IS NULL OR artifact_hash=$artifactHash)
              AND user_id=$user AND device_id=$device AND session_id=$session
              AND EXISTS(
                  SELECT 1 FROM trading_approval_requests request
                  WHERE request.request_id=$request AND request.mode='Review'
                    AND request.consumed_at IS NULL AND request.revoked_at IS NULL
                    AND request.issued_at<=$now AND request.expires_at>$now
                    AND request.correlation_id=$correlation AND request.intent_hash=$hash
                    AND ($artifactHash IS NULL OR request.artifact_hash=$artifactHash)
                    AND request.user_id=$user AND request.device_id=$device AND request.session_id=$session)
              AND ($artifactHash IS NULL OR EXISTS(SELECT 1 FROM trading_review_execution_queue queue WHERE queue.request_id=$request AND queue.status='Executing' AND queue.artifact_hash=$artifactHash))
            """;
        AddApprovalConsumptionParameters(receiptUpdate,consumption,consumedAtUtc);var receiptRows=await receiptUpdate.ExecuteNonQueryAsync(ct);
        if(receiptRows!=1){await tx.RollbackAsync(ct);return new(false,"approval.not-consumable");}
        await using var requestUpdate=c.CreateCommand();requestUpdate.Transaction=tx;requestUpdate.CommandText="UPDATE trading_approval_requests SET consumed_at=$now WHERE request_id=$request AND consumed_at IS NULL AND revoked_at IS NULL AND ($artifactHash IS NULL OR artifact_hash=$artifactHash)";requestUpdate.Parameters.AddWithValue("$now",DbInstant(consumedAtUtc));requestUpdate.Parameters.AddWithValue("$request",consumption.RequestId);requestUpdate.Parameters.AddWithValue("$artifactHash",consumption.ArtifactHash is null?DBNull.Value:consumption.ArtifactHash);var requestRows=await requestUpdate.ExecuteNonQueryAsync(ct);
        if(requestRows!=1){await tx.RollbackAsync(ct);return new(false,"approval.not-consumable");}
        await tx.CommitAsync(ct);return new(true,"approval.consumed");
    }
    public async Task RecordTradingAuthorizationModeChangeAsync(TradingAuthorizationModeChangeAudit audit,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if(string.IsNullOrWhiteSpace(audit.ChangeId)||!Enum.IsDefined(audit.OldMode)||!Enum.IsDefined(audit.NewMode)||
           string.IsNullOrWhiteSpace(audit.UserId)||string.IsNullOrWhiteSpace(audit.DeviceId)||string.IsNullOrWhiteSpace(audit.Reason))
            throw new ArgumentException("Trading authorization mode audit is incomplete.",nameof(audit));
        await Exec("INSERT INTO trading_authorization_mode_audits(change_id,old_mode,new_mode,user_id,device_id,changed_at,reason) VALUES($id,$old,$new,$user,$device,$at,$reason)",ct,
            ("$id",audit.ChangeId),("$old",audit.OldMode.ToString()),("$new",audit.NewMode.ToString()),
            ("$user",SensitiveDataRedactor.ForLog(audit.UserId,120)),("$device",SensitiveDataRedactor.ForLog(audit.DeviceId,120)),
            ("$at",DbInstant(audit.ChangedAtUtc)),("$reason",SensitiveDataRedactor.ForLog(audit.Reason,240)));
    }
    public async Task<IReadOnlyList<TradingAuthorizationModeChangeAudit>> GetTradingAuthorizationModeChangesAsync(int limit,CancellationToken ct)
    {
        var list=new List<TradingAuthorizationModeChangeAudit>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT change_id,old_mode,new_mode,user_id,device_id,changed_at,reason FROM trading_authorization_mode_audits ORDER BY changed_at DESC LIMIT $limit";q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))
        {
            if(!Enum.TryParse<TradingAuthorizationMode>(r.GetString(1),out var oldMode)||!Enum.IsDefined(oldMode)||!Enum.TryParse<TradingAuthorizationMode>(r.GetString(2),out var newMode)||!Enum.IsDefined(newMode))continue;
            list.Add(new(r.GetString(0),oldMode,newMode,r.GetString(3),r.GetString(4),Instant(r.GetString(5)),r.GetString(6)));
        }
        return list;
    }
    public async Task<IReadOnlyList<PersistedTradingApprovalSummary>> GetPendingTradingApprovalSummariesAsync(int limit,int offset,CancellationToken ct)
    {
        var list=new List<PersistedTradingApprovalSummary>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT request.request_id,request.issued_at,request.expires_at,request.revoked_at,
                   cycle.decision_json,maturity.risk_json
            FROM trading_approval_requests request
            LEFT JOIN cycles cycle ON cycle.id=request.correlation_id
            LEFT JOIN maturity_audits maturity ON maturity.cycle_id=request.correlation_id
            WHERE request.mode='Review' AND request.consumed_at IS NULL
            ORDER BY request.issued_at DESC,request.request_id DESC
            LIMIT $limit OFFSET $offset
            """;
        q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));q.Parameters.AddWithValue("$offset",Math.Clamp(offset,0,10_000));
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))
        {
            DecisionPlan? decision=null;IndependentRiskReview? risk=null;
            try{if(!r.IsDBNull(4))decision=JsonSerializer.Deserialize<DecisionPlan>(r.GetString(4));}catch(JsonException){}
            try{if(!r.IsDBNull(5))risk=JsonSerializer.Deserialize<IndependentRiskReview>(r.GetString(5));}catch(JsonException){}
            list.Add(new(r.GetString(0),Instant(r.GetString(1)),Instant(r.GetString(2)),NullableInstant(r,3),decision,risk));
        }
        return list;
    }
    public async Task<IReadOnlyList<PersistedIntent>> GetRecoverableIntentsAsync(CancellationToken ct)
    {
        var list=new List<PersistedIntent>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT cycle_id,status,exchange_order_id,details FROM order_intents WHERE status NOT IN ('PROTECTED','PROTECTED_PARTIAL','PARTIALLY_FILLED_PROTECTED','PREFLIGHT_BLOCKED','COMPLETED','COMPLETED_PARTIAL','CANCELED','REJECTED','EXPIRED','EMERGENCY_CLOSED','LegacyUnresolved','Quarantined') ORDER BY updated_at";
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var intent=JsonSerializer.Deserialize<ExecutionIntent>(r.GetString(3));if(intent is not null)list.Add(new(r.IsDBNull(0)?"RECOVERY":r.GetString(0),intent,r.IsDBNull(1)?"UNKNOWN":r.GetString(1),r.IsDBNull(2)?null:r.GetValue(2).ToString()));}return list;
    }
    public async Task<IReadOnlyList<PersistedIntent>> GetIntentsByCycleAsync(string cycleId,int limit,CancellationToken ct)
    {
        var list=new List<PersistedIntent>();if(!QueueToken(cycleId,120))return list;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT cycle_id,status,exchange_order_id,details FROM order_intents WHERE cycle_id=$cycle ORDER BY client_order_id LIMIT $limit";
        q.Parameters.AddWithValue("$cycle",cycleId);q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))
        {
            try{var intent=JsonSerializer.Deserialize<ExecutionIntent>(r.GetString(3));if(intent is not null)list.Add(new(r.GetString(0),intent,r.IsDBNull(1)?"UNKNOWN":r.GetString(1),r.IsDBNull(2)?null:r.GetValue(2).ToString()));}
            catch(JsonException){/* Malformed rows are withheld and cause correlation to fail closed. */}
        }
        return list;
    }
    public async Task<IReadOnlyList<PersistedIntent>> GetProtectedOpeningIntentsAsync(CancellationToken ct)
    {
        var list=new List<PersistedIntent>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT cycle_id,status,exchange_order_id,details FROM order_intents WHERE status IN ('PROTECTED','PROTECTED_PARTIAL','PARTIALLY_FILLED_PROTECTED') ORDER BY updated_at DESC,client_order_id DESC LIMIT 200";
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))
        {
            try
            {
                var intent=JsonSerializer.Deserialize<ExecutionIntent>(r.GetString(3));
                if(intent is{ReduceOnly:false})list.Add(new(r.IsDBNull(0)?"PROTECTION":r.GetString(0),intent,r.IsDBNull(1)?"UNKNOWN":r.GetString(1),r.IsDBNull(2)?null:r.GetValue(2).ToString()));
            }
            catch(JsonException){/* Malformed protected rows are withheld so reconciliation stays fail-closed. */}
        }
        return list;
    }
    public async Task<IntentStateSummary> GetIntentStateSummaryAsync(CancellationToken ct)
    {
        var statuses=new List<IntentStatusCount>();var total=0;var recoverable=0;var unknown=0;DateTimeOffset? latest=null;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT COALESCE(status,'UNKNOWN'),COUNT(*) FROM order_intents GROUP BY COALESCE(status,'UNKNOWN') ORDER BY COALESCE(status,'UNKNOWN')";
        await using(var r=await q.ExecuteReaderAsync(ct))while(await r.ReadAsync(ct)){var status=r.GetString(0);var count=r.GetInt32(1);statuses.Add(new(status,count));total+=count;if(status.Contains("UNKNOWN",StringComparison.Ordinal))unknown+=count;}
        await using var recoverableCommand=c.CreateCommand();recoverableCommand.CommandText="SELECT COUNT(*) FROM order_intents WHERE status NOT IN ('PROTECTED','PROTECTED_PARTIAL','PARTIALLY_FILLED_PROTECTED','PREFLIGHT_BLOCKED','COMPLETED','COMPLETED_PARTIAL','CANCELED','REJECTED','EXPIRED','EMERGENCY_CLOSED','LegacyUnresolved','Quarantined')";recoverable=Convert.ToInt32(await recoverableCommand.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
        await using var latestCommand=c.CreateCommand();latestCommand.CommandText="SELECT MAX(updated_at) FROM order_intents";var value=await latestCommand.ExecuteScalarAsync(ct);if(value is string text&&DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed))latest=parsed.ToUniversalTime();
        return new(total,recoverable,unknown,latest,statuses);
    }
    public async Task<ExecutionIntent?> GetLatestOpeningIntentAsync(string symbol,PositionSide side,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT oi.details,ee.exchange_updated_at,ee.occurred_at FROM order_intents oi LEFT JOIN execution_events ee ON ee.client_order_id=oi.client_order_id AND ee.reduce_only=0 AND ee.status IN ('FILLED','PARTIALLY_FILLED') WHERE oi.symbol=$s AND oi.side=$side AND oi.status IN ('PROTECTED','PROTECTED_PARTIAL','PARTIALLY_FILLED_PROTECTED') ORDER BY CASE WHEN ee.client_order_id IS NULL THEN 1 ELSE 0 END,COALESCE(ee.exchange_updated_at,ee.occurred_at,oi.updated_at) DESC,oi.client_order_id DESC LIMIT 50";
        q.Parameters.AddWithValue("$s",symbol);q.Parameters.AddWithValue("$side",side.ToString());
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var intent=JsonSerializer.Deserialize<ExecutionIntent>(r.GetString(0));if(intent is{ReduceOnly:false,StopLoss:>0,TakeProfit:>0})return intent;}return null;
    }
    public async Task<ExecutionIntent?> GetLatestOpeningIntentAsync(string symbol,PositionSide side,DateTimeOffset asOfUtc,CancellationToken ct)
    {
        asOfUtc=asOfUtc.ToUniversalTime();
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT oi.details,ee.exchange_updated_at,ee.occurred_at FROM order_intents oi JOIN execution_events ee ON ee.client_order_id=oi.client_order_id WHERE oi.symbol=$s AND oi.side=$side AND oi.status IN ('PROTECTED','PROTECTED_PARTIAL','PARTIALLY_FILLED_PROTECTED') AND ee.reduce_only=0 AND ee.status IN ('FILLED','PARTIALLY_FILLED') ORDER BY COALESCE(ee.exchange_updated_at,ee.occurred_at) DESC,oi.client_order_id DESC LIMIT 50";
        q.Parameters.AddWithValue("$s",symbol);q.Parameters.AddWithValue("$side",side.ToString());
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))
        {
            var eventAt=ExecutionEventTime(r,1,2);
            if(eventAt is null||eventAt.Value>asOfUtc)continue;
            var intent=JsonSerializer.Deserialize<ExecutionIntent>(r.GetString(0));
            if(intent is{ReduceOnly:false,StopLoss:>0,TakeProfit:>0})return intent;
        }
        return null;
    }
    public async Task<string?> GetOrderIntentStatusAsync(string clientOrderId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(clientOrderId))return null;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT status FROM order_intents WHERE client_order_id=$id";q.Parameters.AddWithValue("$id",clientOrderId);return (await q.ExecuteScalarAsync(ct))?.ToString();
    }
    public async Task<bool> HasExecutionEventAsync(string clientOrderId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(clientOrderId))return false;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT 1 FROM execution_events WHERE client_order_id=$id LIMIT 1";q.Parameters.AddWithValue("$id",clientOrderId);return await q.ExecuteScalarAsync(ct) is not null;
    }
    public async Task<DateTimeOffset?> GetExecutionEventObservedAtAsync(string clientOrderId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(clientOrderId))return null;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT exchange_updated_at,occurred_at FROM execution_events WHERE client_order_id=$id LIMIT 1";q.Parameters.AddWithValue("$id",clientOrderId);
        await using var r=await q.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ExecutionEventTime(r,0,1):null;
    }
    public async Task<DateTimeOffset?> GetLatestExecutionEventObservedAtAsync(string symbol,PositionSide side,DateTimeOffset asOfUtc,CancellationToken ct)
    {
        asOfUtc=asOfUtc.ToUniversalTime();
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT exchange_updated_at,occurred_at FROM execution_events WHERE symbol=$symbol COLLATE NOCASE AND side=$side AND status IN ('FILLED','PARTIALLY_FILLED') ORDER BY COALESCE(exchange_updated_at,occurred_at) DESC,id DESC LIMIT 100";
        q.Parameters.AddWithValue("$symbol",symbol);q.Parameters.AddWithValue("$side",side.ToString());
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var eventAt=ExecutionEventTime(r,0,1);
            if(eventAt is not null&&eventAt.Value<=asOfUtc)return eventAt;
        }
        return null;
    }
    public async Task<IReadOnlyList<PersistedIntent>> GetLegacyIntentIsolationCandidatesAsync(CancellationToken ct)
    {
        var list=new List<PersistedIntent>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT cycle_id,status,exchange_order_id,details FROM order_intents WHERE status='INTENT' ORDER BY updated_at,client_order_id";await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){try{var intent=JsonSerializer.Deserialize<ExecutionIntent>(r.GetString(3));if(intent is not null)list.Add(new(r.IsDBNull(0)?"LEGACY":r.GetString(0),intent,"INTENT",r.IsDBNull(2)?null:r.GetValue(2).ToString()));}catch(JsonException){}}return list;
    }
    public async Task<bool> HasExecutionSubmissionJournalAsync(string clientOrderId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(clientOrderId))return true;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT EXISTS(SELECT 1 FROM execution_submission_journal WHERE client_order_id=$id)";q.Parameters.AddWithValue("$id",clientOrderId);return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }
    public async Task<LegacyIntentIsolationMutationResult> TryQuarantineLegacyIntentAsync(string clientOrderId,string reasonCode,CancellationToken ct)
    {
        if(!QueueToken(clientOrderId,120)||!QueueToken(reasonCode,120))return new(false,"legacy-isolation.invalid");var now=_utcNow().ToUniversalTime();
        try
        {
            await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
            await using(var projection=c.CreateCommand()){projection.Transaction=tx;projection.CommandText="INSERT INTO legacy_intent_isolation(client_order_id,source_status,projection_status,reason_code,isolated_at) SELECT client_order_id,'INTENT','Quarantined',$reason,$now FROM order_intents WHERE client_order_id=$id AND status='INTENT' AND NOT EXISTS(SELECT 1 FROM execution_submission_journal journal WHERE journal.client_order_id=$id)";AddParameters(projection,("$reason",reasonCode),("$now",DbInstant(now)),("$id",clientOrderId));if(await projection.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"legacy-isolation.not-applicable");}}
            await using(var update=c.CreateCommand()){update.Transaction=tx;update.CommandText="UPDATE order_intents SET status='LegacyUnresolved',updated_at=$now WHERE client_order_id=$id AND status='INTENT'";AddParameters(update,("$now",DbInstant(now)),("$id",clientOrderId));if(await update.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"legacy-isolation.not-applicable");}}
            await using(var audit=c.CreateCommand()){audit.Transaction=tx;audit.CommandText="INSERT INTO legacy_intent_isolation_events(client_order_id,sequence,occurred_at,from_status,to_status,event_code) SELECT $id,COALESCE(MAX(sequence),0)+1,$now,'INTENT','Quarantined',$reason FROM legacy_intent_isolation_events WHERE client_order_id=$id";AddParameters(audit,("$id",clientOrderId),("$now",DbInstant(now)),("$reason",reasonCode));if(await audit.ExecuteNonQueryAsync(ct)!=1)throw new SqliteException("legacy isolation audit append failed",1);}
            await tx.CommitAsync(ct);return new(true,"legacy-isolation.quarantined");
        }
        catch(SqliteException){return new(false,"legacy-isolation.persistence-failed");}
    }
    public async Task<PersistedLegacyIntentIsolation?> GetLegacyIntentIsolationAsync(string clientOrderId,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT source_status,projection_status,reason_code,isolated_at FROM legacy_intent_isolation WHERE client_order_id=$id";q.Parameters.AddWithValue("$id",clientOrderId);await using var r=await q.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(clientOrderId,r.GetString(0),r.GetString(1),r.GetString(2),Instant(r.GetString(3))):null;
    }
    public async Task<IReadOnlyList<PersistedLegacyIntentIsolationEvent>> GetLegacyIntentIsolationEventsAsync(string clientOrderId,CancellationToken ct)
    {
        var list=new List<PersistedLegacyIntentIsolationEvent>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT sequence,occurred_at,from_status,to_status,event_code FROM legacy_intent_isolation_events WHERE client_order_id=$id ORDER BY sequence";q.Parameters.AddWithValue("$id",clientOrderId);await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(new(clientOrderId,r.GetInt32(0),Instant(r.GetString(1)),r.GetString(2),r.GetString(3),r.GetString(4)));return list;
    }
    public async Task SaveNewsAsync(IEnumerable<NewsEvidence> news,CancellationToken ct)
    {
        foreach(var n in news){await Exec("INSERT OR REPLACE INTO news(duplicate_group,source,title,url,published_at,collected_at,reliability,assets) VALUES($g,$s,$t,$u,$p,$c,$r,$a)",ct,("$g",n.DuplicateGroup),("$s",n.Source),("$t",n.Title),("$u",n.Url),("$p",n.PublishedAt?.ToString("O")),("$c",n.CollectedAt.ToString("O")),("$r",n.Reliability),("$a",JsonSerializer.Serialize(n.AffectedAssets)));await Exec("INSERT INTO news_documents(duplicate_group,source,title,url,published_at,collected_at,reliability,assets,body_summary,confidence,corroborating_sources,event_type,is_breaking,sentiment) VALUES($g,$s,$t,$u,$p,$c,$r,$a,$b,$q,$n,$e,$i,$m) ON CONFLICT(duplicate_group) DO UPDATE SET source=excluded.source,title=excluded.title,url=excluded.url,published_at=excluded.published_at,collected_at=excluded.collected_at,reliability=excluded.reliability,assets=excluded.assets,body_summary=excluded.body_summary,confidence=excluded.confidence,corroborating_sources=excluded.corroborating_sources,event_type=excluded.event_type,is_breaking=excluded.is_breaking,sentiment=excluded.sentiment",ct,("$g",n.DuplicateGroup),("$s",n.Source),("$t",n.Title),("$u",n.Url),("$p",n.PublishedAt?.ToString("O")),("$c",n.CollectedAt.ToString("O")),("$r",n.Reliability),("$a",JsonSerializer.Serialize(n.AffectedAssets)),("$b",n.BodySummary),("$q",n.Confidence),("$n",n.CorroboratingSources),("$e",n.EventType),("$i",n.IsBreaking?1:0),("$m",n.Sentiment));}
    }

    public async Task<MacroObservationPersistenceResult> SaveMacroObservationAsync(BlsMacroObservationV1 observation,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observation);var facts=observation.Facts;var source=observation.Source;
        if(facts.ValueKind!=JsonValueKind.Object||source.Kind!=ModelOffSourceKindV1.Macro||source.Status!=ModelOffSourceStatusV1.Available||source.AsOfUtc is null||source.AsOfUtc.Value.Offset!=TimeSpan.Zero||string.IsNullOrWhiteSpace(source.SourceId)||string.IsNullOrWhiteSpace(source.ArtifactHash)||source.ArtifactHash.Length!=64||!source.ArtifactHash.All(Uri.IsHexDigit))return new(false,false,0,"macro.persistence.invalid");
        string schema,indicator,geography,frequency,unit,basis;string? calendarHash,calendarEventId;DateTimeOffset observed,released;decimal value;
        try{schema=facts.GetProperty("schema").GetString()??"";indicator=facts.GetProperty("indicatorId").GetString()??"";geography=facts.GetProperty("geography").GetString()??"";frequency=facts.GetProperty("frequency").GetString()??"";unit=facts.GetProperty("unit").GetString()??"";observed=facts.GetProperty("observationAtUtc").GetDateTimeOffset();released=facts.GetProperty("releasedAtUtc").GetDateTimeOffset();basis=facts.GetProperty("releaseTimeBasis").GetString()??"";calendarHash=facts.TryGetProperty("releaseCalendarArtifactHash",out var hashProperty)&&hashProperty.ValueKind==JsonValueKind.String?hashProperty.GetString():null;calendarEventId=facts.TryGetProperty("releaseCalendarEventId",out var eventProperty)&&eventProperty.ValueKind==JsonValueKind.String?eventProperty.GetString():null;value=facts.GetProperty("value").GetDecimal();}catch{return new(false,false,0,"macro.persistence.invalid");}
        var formal=basis=="official-release-calendar";var firstObserved=basis=="official-endpoint-first-observed";
        if(schema!="wpe.macro-facts/1.0"||string.IsNullOrWhiteSpace(indicator)||string.IsNullOrWhiteSpace(geography)||string.IsNullOrWhiteSpace(frequency)||string.IsNullOrWhiteSpace(unit)||observed.Offset!=TimeSpan.Zero||released.Offset!=TimeSpan.Zero||observed>released||source.AsOfUtc.Value<released||
           !(formal||firstObserved)||formal&&(!ValidSha256(calendarHash)||!SafeMacroIdentity(calendarEventId))||firstObserved&&(calendarHash is not null||calendarEventId is not null))return new(false,false,0,"macro.persistence.invalid");
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);var revision=1;
        await using(var read=c.CreateCommand()){read.Transaction=tx;read.CommandText="SELECT revision,value,geography,frequency,unit,release_time_basis,released_at,release_calendar_hash,release_calendar_event_id FROM macro_observation_revisions WHERE indicator_id=$id AND observation_at=$at ORDER BY revision DESC LIMIT 1";AddParameters(read,("$id",indicator),("$at",DbInstant(observed)));await using var r=await read.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)){revision=r.GetInt32(0);var same=decimal.Parse(r.GetString(1),CultureInfo.InvariantCulture)==value&&r.GetString(2)==geography&&r.GetString(3)==frequency&&r.GetString(4)==unit&&r.GetString(5)==basis&&!r.IsDBNull(6)&&Instant(r.GetString(6))==released&&NullableText(r,7)==calendarHash&&NullableText(r,8)==calendarEventId;if(same){await tx.RollbackAsync(ct);return new(true,true,revision,"macro.persistence.idempotent");}revision=checked(revision+1);}}
        await using(var insert=c.CreateCommand()){insert.Transaction=tx;insert.CommandText="INSERT INTO macro_observation_revisions(indicator_id,observation_at,revision,geography,frequency,unit,value,source_id,source_artifact_hash,first_observed_at,released_at,release_time_basis,release_calendar_hash,release_calendar_event_id) VALUES($id,$at,$revision,$geo,$frequency,$unit,$value,$source,$hash,$seen,$released,$basis,$calendarHash,$calendarEvent)";AddParameters(insert,("$id",indicator),("$at",DbInstant(observed)),("$revision",revision),("$geo",geography),("$frequency",frequency),("$unit",unit),("$value",value.ToString(CultureInfo.InvariantCulture)),("$source",source.SourceId),("$hash",source.ArtifactHash),("$seen",DbInstant(source.AsOfUtc.Value)),("$released",DbInstant(released)),("$basis",basis),("$calendarHash",calendarHash),("$calendarEvent",calendarEventId));await insert.ExecuteNonQueryAsync(ct);}
        await tx.CommitAsync(ct);return new(true,false,revision,revision==1?"macro.persistence.inserted":"macro.persistence.revised");
    }

    public async Task<IReadOnlyList<PersistedMacroObservation>> GetMacroObservationRevisionsAsync(string indicatorId,DateTimeOffset observationAtUtc,CancellationToken ct)
    {
        var result=new List<PersistedMacroObservation>();if(string.IsNullOrWhiteSpace(indicatorId)||observationAtUtc.Offset!=TimeSpan.Zero)return result;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT revision,geography,frequency,unit,value,source_id,source_artifact_hash,first_observed_at,released_at,release_time_basis,release_calendar_hash,release_calendar_event_id FROM macro_observation_revisions WHERE indicator_id=$id AND observation_at=$at ORDER BY revision";AddParameters(q,("$id",indicatorId),("$at",DbInstant(observationAtUtc)));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result.Add(ReadMacro(r,indicatorId,observationAtUtc,0));return result;
    }

    public async Task<IReadOnlyList<PersistedMacroObservation>> GetLatestMacroObservationsAsync(int limit,CancellationToken ct)
    {
        var result=new List<PersistedMacroObservation>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="""
            SELECT m.indicator_id,m.observation_at,m.revision,m.geography,m.frequency,m.unit,m.value,m.source_id,m.source_artifact_hash,m.first_observed_at,m.released_at,m.release_time_basis,m.release_calendar_hash,m.release_calendar_event_id
            FROM macro_observation_revisions m
            WHERE (m.observation_at,m.revision)=(SELECT x.observation_at,x.revision FROM macro_observation_revisions x WHERE x.indicator_id=m.indicator_id ORDER BY x.observation_at DESC,x.revision DESC LIMIT 1)
            ORDER BY m.indicator_id LIMIT $limit
            """;q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,32));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result.Add(ReadMacro(r,r.GetString(0),Instant(r.GetString(1)),2));return result;
    }
    public async Task<IReadOnlyList<NewsEvidence>> SearchNewsAsync(string query,int limit,CancellationToken ct)
    {
        var list=new List<NewsEvidence>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT d.source,d.title,d.url,d.published_at,d.collected_at,d.reliability,d.duplicate_group,d.assets,d.body_summary,d.confidence,d.corroborating_sources,d.event_type,d.is_breaking,d.sentiment FROM news_search f JOIN news_documents d ON d.id=f.rowid WHERE news_search MATCH $q ORDER BY rank LIMIT $l";q.Parameters.AddWithValue("$q",query);q.Parameters.AddWithValue("$l",Math.Clamp(limit,1,50));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var assets=JsonSerializer.Deserialize<string[]>(r.GetString(7))??[];list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),DateTime.TryParse(r.IsDBNull(3)?null:r.GetString(3),out var p)?p:null,DateTime.Parse(r.GetString(4)),r.GetString(5),r.GetString(6),assets,r.GetString(8),r.GetDouble(9),r.GetInt32(10),r.GetString(11),r.GetInt32(12)==1,r.GetDouble(13)));}return list;
    }
    public async Task UpsertHistoricalCandlesAsync(string symbol,string interval,IReadOnlyList<CandleEvidence> candles,CancellationToken ct)
    {
        if(candles.Count==0)return;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);foreach(var x in candles){await using var q=c.CreateCommand();q.Transaction=(SqliteTransaction)tx;q.CommandText="INSERT OR REPLACE INTO historical_candles(symbol,interval,open_time,open,high,low,close,volume,quote_volume,trades,taker_buy_volume) VALUES($s,$i,$t,$o,$h,$l,$c,$v,$q,$n,$b)";q.Parameters.AddWithValue("$s",symbol);q.Parameters.AddWithValue("$i",interval);q.Parameters.AddWithValue("$t",x.OpenTime.ToString("O"));q.Parameters.AddWithValue("$o",x.Open.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$h",x.High.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$l",x.Low.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$c",x.Close.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$v",x.Volume.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$q",x.QuoteVolume.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$n",x.Trades);q.Parameters.AddWithValue("$b",x.TakerBuyVolume.ToString(CultureInfo.InvariantCulture));await q.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<CandleEvidence>> LoadHistoricalCandlesAsync(string symbol,string interval,int limit,CancellationToken ct)
    {
        var list=new List<CandleEvidence>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT open_time,open,high,low,close,volume,quote_volume,trades,taker_buy_volume FROM historical_candles WHERE symbol=$s AND interval=$i ORDER BY open_time DESC LIMIT $l";q.Parameters.AddWithValue("$s",symbol);q.Parameters.AddWithValue("$i",interval);q.Parameters.AddWithValue("$l",Math.Clamp(limit,1,100000));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){decimal V(int n)=>decimal.Parse(r.GetString(n),CultureInfo.InvariantCulture);list.Add(new(DateTime.Parse(r.GetString(0),null,System.Globalization.DateTimeStyles.RoundtripKind),V(1),V(2),V(3),V(4),V(5),V(6),r.GetInt64(7),V(8)));}list.Reverse();return list;
    }
    public async Task<DateTime?> GetLatestHistoricalCandleAsync(string symbol,string interval,CancellationToken ct){await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT MAX(open_time) FROM historical_candles WHERE symbol=$s AND interval=$i";q.Parameters.AddWithValue("$s",symbol);q.Parameters.AddWithValue("$i",interval);var value=await q.ExecuteScalarAsync(ct);return DateTime.TryParse(value?.ToString(),null,System.Globalization.DateTimeStyles.RoundtripKind,out var result)?result:null;}
    public Task SavePortfolioRiskAsync(string cycle,PortfolioRiskAssessment result,CancellationToken ct)=>Exec("INSERT OR REPLACE INTO portfolio_risk_audits(cycle_id,created_at,result_json) VALUES($c,$t,$j)",ct,("$c",cycle),("$t",DateTime.UtcNow.ToString("O")),("$j",JsonSerializer.Serialize(result)));
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
    public async Task RecordErrorAsync(string stage,Exception ex,CancellationToken ct)=>await Exec("INSERT INTO errors(occurred_at,stage,message,details) VALUES($t,$s,$m,$d)",ct,("$t",DateTime.UtcNow.ToString("O")),("$s",SensitiveDataRedactor.ForLog(stage,120)),("$m",SensitiveDataRedactor.ForLog(ex.Message)),("$d",SensitiveDataRedactor.Redact(ex.ToString())));
    public Task RecordDecisionAuditAsync(string cycle,DecisionReview review,CancellationToken ct)=>Exec("INSERT OR REPLACE INTO decision_audits(cycle_id,created_at,assessments_json,review_json) VALUES($c,$t,$a,$r)",ct,("$c",cycle),("$t",DateTime.UtcNow.ToString("O")),("$a","[]"),("$r",JsonSerializer.Serialize(review)));
    public Task RecordMaturityAuditAsync(string cycle,DecisionPlan plan,DecisionReview review,IndependentRiskReview risk,string result,CancellationToken ct)=>Exec("INSERT OR REPLACE INTO maturity_audits(cycle_id,created_at,plan_json,review_json,risk_json,research_json,execution_result) VALUES($c,$t,$p,$r,$k,$s,$e)",ct,("$c",cycle),("$t",DateTime.UtcNow.ToString("O")),("$p",JsonSerializer.Serialize(plan)),("$r",JsonSerializer.Serialize(review)),("$k",JsonSerializer.Serialize(risk)),("$s",null),("$e",result));
    public async Task RecordSkillCallAsync(string skill,string status,long duration,string input,string output,string? error,CancellationToken ct,string? mode=null,bool? remoteLlmUsed=null,int? tokens=null,decimal? costUsd=null,int? contextChars=null,int? inputTokens=null,int? outputTokens=null,bool? cacheHit=null,string? llmOutcome=null,string? tokenSource=null)
    {
        var occurred=DateTime.UtcNow;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);long sourceId;
        await using(var q=c.CreateCommand()){q.Transaction=(SqliteTransaction)tx;q.CommandText="INSERT INTO skill_calls(occurred_at,skill,status,duration_ms,input_summary,output_summary,error) VALUES($t,$s,$st,$d,$i,$o,$e); SELECT last_insert_rowid();";q.Parameters.AddWithValue("$t",occurred.ToString("O"));q.Parameters.AddWithValue("$s",SensitiveDataRedactor.ForLog(skill,120));q.Parameters.AddWithValue("$st",SensitiveDataRedactor.ForLog(status,40));q.Parameters.AddWithValue("$d",Math.Max(0,duration));q.Parameters.AddWithValue("$i",SensitiveDataRedactor.ForLog(input));q.Parameters.AddWithValue("$o",SensitiveDataRedactor.ForLog(output));q.Parameters.AddWithValue("$e",error is null?DBNull.Value:SensitiveDataRedactor.ForLog(error));sourceId=Convert.ToInt64(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);}
        await using(var q=c.CreateCommand()){q.Transaction=(SqliteTransaction)tx;q.CommandText="INSERT INTO runtime_skill_calls(source_skill_call_id,occurred_at,skill,status,duration_ms,mode,remote_llm,tokens,cost_usd,context_chars,input_tokens,output_tokens,cache_hit,llm_outcome,token_source) VALUES($i,$t,$s,$st,$d,$m,$r,$n,$c,$chars,$input,$output,$cache,$outcome,$source)";q.Parameters.AddWithValue("$i",sourceId);q.Parameters.AddWithValue("$t",occurred.ToString("O"));q.Parameters.AddWithValue("$s",SensitiveDataRedactor.ForLog(skill,120));q.Parameters.AddWithValue("$st",SensitiveDataRedactor.ForLog(status,40));q.Parameters.AddWithValue("$d",Math.Max(0,duration));q.Parameters.AddWithValue("$m",mode is null?DBNull.Value:SensitiveDataRedactor.ForLog(mode,40));q.Parameters.AddWithValue("$r",remoteLlmUsed is null?DBNull.Value:remoteLlmUsed.Value?1:0);q.Parameters.AddWithValue("$n",(object?)tokens??DBNull.Value);q.Parameters.AddWithValue("$c",costUsd is null?DBNull.Value:costUsd.Value.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$chars",contextChars is null?DBNull.Value:Math.Max(0,contextChars.Value));q.Parameters.AddWithValue("$input",inputTokens is null?DBNull.Value:Math.Max(0,inputTokens.Value));q.Parameters.AddWithValue("$output",outputTokens is null?DBNull.Value:Math.Max(0,outputTokens.Value));q.Parameters.AddWithValue("$cache",cacheHit is null?DBNull.Value:cacheHit.Value?1:0);q.Parameters.AddWithValue("$outcome",llmOutcome is null?DBNull.Value:SensitiveDataRedactor.ForLog(llmOutcome,40));q.Parameters.AddWithValue("$source",tokenSource is null?DBNull.Value:SensitiveDataRedactor.ForLog(tokenSource,20));await q.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<PersistedRuntimeSkillCall>> GetRecentRuntimeSkillCallsAsync(int limit,CancellationToken ct)
    {
        var list=new List<PersistedRuntimeSkillCall>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT 'runtime:'||id,occurred_at,skill,status,duration_ms,mode,remote_llm,tokens,cost_usd,context_chars,input_tokens,output_tokens,cache_hit,llm_outcome,token_source FROM runtime_skill_calls UNION ALL SELECT 'legacy:'||s.id,s.occurred_at,s.skill,s.status,s.duration_ms,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL FROM skill_calls s WHERE NOT EXISTS(SELECT 1 FROM runtime_skill_calls r WHERE r.source_skill_call_id=s.id) ORDER BY occurred_at DESC LIMIT $l";q.Parameters.AddWithValue("$l",Math.Clamp(limit,1,500));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){decimal? cost=null;if(!r.IsDBNull(8)&&decimal.TryParse(r.GetString(8),NumberStyles.Number,CultureInfo.InvariantCulture,out var parsed))cost=parsed;list.Add(new(r.GetString(0),DateTime.Parse(r.GetString(1),null,DateTimeStyles.RoundtripKind).ToUniversalTime(),r.GetString(2),r.GetString(3),Math.Max(0,r.GetInt64(4)),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetInt32(6)==1,r.IsDBNull(7)?null:r.GetInt32(7),cost,r.IsDBNull(9)?null:r.GetInt32(9),r.IsDBNull(10)?null:r.GetInt32(10),r.IsDBNull(11)?null:r.GetInt32(11),r.IsDBNull(12)?null:r.GetInt32(12)==1,r.IsDBNull(13)?null:r.GetString(13),r.IsDBNull(14)?null:r.GetString(14)));}return list;
    }
    public async Task<AgentOperationsEvidence> GetAgentOperationsEvidenceAsync(CancellationToken ct)
    {
        var activities=new Dictionary<string,PersistedAgentActivity>(StringComparer.OrdinalIgnoreCase);
        foreach(var call in await GetRecentRuntimeSkillCallsAsync(300,ct))
        {
            var role=RoleForSkill(call.Skill);if(role is null||activities.ContainsKey(role))continue;
            var status=call.Status.Contains("FAIL",StringComparison.OrdinalIgnoreCase)||call.Status.Contains("ERROR",StringComparison.OrdinalIgnoreCase)?"degraded":"waiting";
            activities[role]=new(role,status,call.OccurredAtUtc,$"Skill {SafeAuditToken(call.Skill,"unknown")} {SafeAuditToken(call.Status,"UNKNOWN")}",NormalizeAgentMode(call.Mode));
        }
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);
        // Canonical model-off audits are acceptance evidence, not production worker telemetry.
        // They remain queryable through the audit surfaces and must never override live operations.
        await using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT current_node,updated_at FROM workflow_runs WHERE status='RUNNING' ORDER BY updated_at DESC LIMIT 20";
            await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))
            {
                var at=DateTime.Parse(r.GetString(1),null,DateTimeStyles.RoundtripKind).ToUniversalTime();if(DateTime.UtcNow-at>TimeSpan.FromMinutes(5))continue;
                var role=RoleForNode(r.GetString(0));if(role is null)continue;activities[role]=new(role,"running",at,$"Workflow {SafeAuditToken(r.GetString(0),"unknown")} running",activities.GetValueOrDefault(role)?.Mode??"Local Only");
            }
        }
        var handoffs=new List<PersistedAgentHandoff>();
        string? activeRunId=null;
        await using(var heartbeat=c.CreateCommand())
        {
            heartbeat.CommandText="SELECT occurred_at,payload_json FROM runtime_events WHERE event_type='runtime.heartbeat' ORDER BY occurred_at DESC LIMIT 1";
            await using var r=await heartbeat.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct))
            {
                var at=DateTime.Parse(r.GetString(0),null,DateTimeStyles.RoundtripKind).ToUniversalTime();
                if(DateTime.UtcNow-at<=TimeSpan.FromSeconds(30))
                {
                    try{using var doc=JsonDocument.Parse(r.GetString(1));var root=doc.RootElement;if((!root.TryGetProperty("LeaseRenewed",out var renewed)&&!root.TryGetProperty("leaseRenewed",out renewed))||renewed.ValueKind!=JsonValueKind.True)activeRunId=null;else if(root.TryGetProperty("RunId",out var run)||root.TryGetProperty("runId",out run))activeRunId=run.GetString();}catch(JsonException){}
                }
            }
        }
        if(!string.IsNullOrWhiteSpace(activeRunId))await using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT event_id,occurred_at,payload_json FROM runtime_events WHERE event_type='workflow.node.entered' ORDER BY sequence DESC LIMIT 100";
            await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))
            {
                try
                {
                    using var doc=JsonDocument.Parse(r.GetString(2));var root=doc.RootElement;
                    if((!root.TryGetProperty("RunId",out var run)&&!root.TryGetProperty("runId",out run))||!string.Equals(run.GetString(),activeRunId,StringComparison.Ordinal))continue;
                    if(!root.TryGetProperty("Previous",out var previous)&&!root.TryGetProperty("previous",out previous))continue;
                    if(!root.TryGetProperty("Node",out var node)&&!root.TryGetProperty("node",out node))continue;
                    var source=RoleForNode(NodeName(previous));var target=RoleForNode(NodeName(node));if(source is null||target is null||source==target)continue;
                    handoffs.Add(new(SafeAuditToken(r.GetString(0),"handoff"),DateTime.Parse(r.GetString(1),null,DateTimeStyles.RoundtripKind).ToUniversalTime(),source,target,"entered"));
                }
                catch(JsonException) { }
            }
        }
        var latest=activities.Values.Select(x=>x.OccurredAtUtc).Concat(handoffs.Select(x=>x.OccurredAtUtc)).DefaultIfEmpty(DateTime.UtcNow).Max();
        return new(activities.Values.ToArray(),handoffs,latest);
    }

    private static string? RoleForSkill(string skill)=>skill switch
    {
        "EvidenceCollector" or "DataQuality" or "MarketRegime" or "NewsResearch" or "HistoricalData"=>"market",
        "BrainPlanner" or "DeterministicPlan" or "DecisionCritic" or "DecisionReviewer"=>"decision",
        "PortfolioRisk" or "IndependentRiskManager" or "RiskAndPositionPlanner"=>"risk",
        "ReliableOrderExecutor" or "PositionManagement" or "EmergencyClose"=>"execution",
        "ProtectionRecovery" or "ProtectionAudit"=>"recovery",
        "RuntimeMonitor"=>"audit",
        _=>null
    };
    private static string? RoleForNode(string node)=>node.ToUpperInvariant() switch{"BOOT" or "OBSERVATION"=>"market","RESEARCH" or "PLANNER" or "AGGREGATION" or "CRITIC" or "REVIEWER"=>"decision","POSITIONMANAGEMENT" or "RISK"=>"risk","EXECUTION"=>"execution","RECOVERY" or "SAFETYEXECUTION"=>"recovery","AUDIT" or "REFLECTION" or "WAITING" or "PAUSED"=>"audit",_=>null};
    private static string NodeName(JsonElement value)=>value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var number)&&Enum.IsDefined(typeof(WorkflowNode),number)?((WorkflowNode)number).ToString():value.ValueKind==JsonValueKind.String?value.GetString()??string.Empty:string.Empty;
    private static string NormalizeAgentMode(string? mode)=>"Local Only";
    public Task RecordRealtimeEventAsync(RealtimeAgentEvent value,CancellationToken ct)=>Exec("INSERT INTO realtime_events(occurred_at,event_type,symbol,status,summary,payload_hash) VALUES($t,$e,$s,$st,$m,$h)",ct,("$t",value.OccurredAt.ToString("O")),("$e",value.EventType),("$s",value.Symbol),("$st",value.Status),("$m",value.Summary),("$h",value.PayloadHash));
    public async Task<bool> SaveExchangeOrderFeeEvidenceAsync(ExchangeOrderFeeEvidenceV1 evidence,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);if(!ExchangeOrderFeeEvidenceCanonicalizerV1.IsCanonical(evidence))throw new InvalidOperationException("Exchange order fee evidence canonical identity is invalid.");if(evidence.ObservedAtUtc.Offset!=TimeSpan.Zero||Math.Abs((_utcNow().ToUniversalTime()-evidence.ObservedAtUtc).TotalMinutes)>5)throw new InvalidOperationException("Exchange order fee evidence is stale or future-dated.");
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO exchange_order_fee_evidence(evidence_id,schema,provider_id,environment,symbol,order_id,client_order_id,fill_count,executed_quantity,fee_amount,fee_asset,observed_at,state,canonical_sha256,canonical_bytes) VALUES($id,$schema,$provider,$environment,$symbol,$order,$client,$fills,$quantity,$fee,$asset,$observed,$state,$hash,$bytes); SELECT changes();";q.Parameters.AddWithValue("$id",evidence.CanonicalSha256);q.Parameters.AddWithValue("$schema",evidence.Schema);q.Parameters.AddWithValue("$provider",evidence.ProviderId);q.Parameters.AddWithValue("$environment",evidence.Environment);q.Parameters.AddWithValue("$symbol",evidence.Symbol);q.Parameters.AddWithValue("$order",evidence.OrderId);q.Parameters.AddWithValue("$client",evidence.ClientOrderId);q.Parameters.AddWithValue("$fills",evidence.FillCount);q.Parameters.AddWithValue("$quantity",evidence.ExecutedQuantity.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$fee",evidence.FeeAmount.ToString(CultureInfo.InvariantCulture));q.Parameters.AddWithValue("$asset",evidence.FeeAsset);q.Parameters.AddWithValue("$observed",evidence.ObservedAtUtc.ToString("O"));q.Parameters.AddWithValue("$state",evidence.State.ToString());q.Parameters.AddWithValue("$hash",evidence.CanonicalSha256);q.Parameters.Add("$bytes",SqliteType.Blob).Value=evidence.CanonicalBytes;var inserted=Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;if(inserted)return true;
        return false;
    }
    public async Task<DateTimeOffset?> GetUnambiguousFundingObservationStartAsync(string symbol,PositionSide side,decimal closingQuantity,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(symbol)||closingQuantity<=0)return null;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);decimal same=0,opposite=0;DateTimeOffset? opened=null;var ambiguous=false;
        await using var q=c.CreateCommand();q.CommandText="SELECT side,reduce_only,quantity,exchange_updated_at FROM execution_events WHERE symbol=$symbol AND status IN ('FILLED','PARTIALLY_FILLED') ORDER BY id";q.Parameters.AddWithValue("$symbol",symbol);await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var quantity=Decimal(r.GetString(2));if(quantity<=0)return null;var rowSide=Enum.Parse<PositionSide>(r.GetString(0),true);var reduce=r.GetInt32(1)==1;
            if(rowSide==side)
            {
                if(!reduce){if(same==0){opened=r.IsDBNull(3)?null:DateTimeOffset.Parse(r.GetString(3),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();ambiguous=opposite>0;}same+=quantity;}
                else{if(quantity>same)return null;ambiguous=true;same-=quantity;if(same==0){opened=null;ambiguous=false;}}
            }
            else{opposite+=reduce?-quantity:quantity;if(opposite<0)return null;if(same>0&&opposite>0)ambiguous=true;}
        }
        return same==closingQuantity&&opposite==0&&!ambiguous?opened:null;
    }
    public async Task<bool> SaveFundingObservationAsync(FundingObservationResultV1 evidence,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);var now=_utcNow().ToUniversalTime();if(!FundingEvidenceCanonicalizerV1.IsCanonical(evidence,now))throw new InvalidOperationException("Funding observation canonical identity is invalid.");
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        foreach(var item in evidence.Events)
        {
            await using var insert=c.CreateCommand();insert.Transaction=(SqliteTransaction)tx;insert.CommandText="INSERT OR IGNORE INTO funding_income_events(event_id,provider_id,environment,symbol,transaction_id,occurred_at,amount,asset,canonical_sha256,canonical_bytes) VALUES($id,$provider,$environment,$symbol,$transaction,$occurred,$amount,$asset,$hash,$bytes)";insert.Parameters.AddWithValue("$id",item.CanonicalSha256);insert.Parameters.AddWithValue("$provider",item.ProviderId);insert.Parameters.AddWithValue("$environment",item.Environment);insert.Parameters.AddWithValue("$symbol",item.Symbol);insert.Parameters.AddWithValue("$transaction",item.TransactionId);insert.Parameters.AddWithValue("$occurred",item.OccurredAtUtc.ToString("O",CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$amount",item.Amount.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$asset",item.Asset);insert.Parameters.AddWithValue("$hash",item.CanonicalSha256);insert.Parameters.Add("$bytes",SqliteType.Blob).Value=item.CanonicalBytes;var inserted=await insert.ExecuteNonQueryAsync(ct);
            if(inserted==0){await using var existing=c.CreateCommand();existing.Transaction=(SqliteTransaction)tx;existing.CommandText="SELECT canonical_sha256,canonical_bytes FROM funding_income_events WHERE provider_id=$provider AND environment=$environment AND transaction_id=$transaction";existing.Parameters.AddWithValue("$provider",item.ProviderId);existing.Parameters.AddWithValue("$environment",item.Environment);existing.Parameters.AddWithValue("$transaction",item.TransactionId);await using var row=await existing.ExecuteReaderAsync(ct);if(!await row.ReadAsync(ct)||row.GetString(0)!=item.CanonicalSha256||!CryptographicOperations.FixedTimeEquals((byte[])row[1],item.CanonicalBytes))throw new InvalidOperationException("Funding transaction identity conflict.");}
        }
        var w=evidence.Window;await using var window=c.CreateCommand();window.Transaction=(SqliteTransaction)tx;window.CommandText="INSERT OR IGNORE INTO funding_observation_windows(window_id,schema,provider_id,environment,symbol,start_utc,end_utc,observed_at,state,event_count,source_artifact_sha256,canonical_sha256,canonical_bytes) VALUES($id,$schema,$provider,$environment,$symbol,$start,$end,$observed,$state,$count,$source,$hash,$bytes)";window.Parameters.AddWithValue("$id",w.CanonicalSha256);window.Parameters.AddWithValue("$schema",w.Schema);window.Parameters.AddWithValue("$provider",w.ProviderId);window.Parameters.AddWithValue("$environment",w.Environment);window.Parameters.AddWithValue("$symbol",w.Symbol);window.Parameters.AddWithValue("$start",w.StartUtc.ToString("O",CultureInfo.InvariantCulture));window.Parameters.AddWithValue("$end",w.EndUtc.ToString("O",CultureInfo.InvariantCulture));window.Parameters.AddWithValue("$observed",w.ObservedAtUtc.ToString("O",CultureInfo.InvariantCulture));window.Parameters.AddWithValue("$state",w.State.ToString());window.Parameters.AddWithValue("$count",w.EventCount);window.Parameters.AddWithValue("$source",w.SourceArtifactSha256);window.Parameters.AddWithValue("$hash",w.CanonicalSha256);window.Parameters.Add("$bytes",SqliteType.Blob).Value=w.CanonicalBytes;var added=await window.ExecuteNonQueryAsync(ct)==1;
        foreach(var item in evidence.Events){await using var link=c.CreateCommand();link.Transaction=(SqliteTransaction)tx;link.CommandText="INSERT OR IGNORE INTO funding_window_events(window_id,event_id) VALUES($window,$event)";link.Parameters.AddWithValue("$window",w.CanonicalSha256);link.Parameters.AddWithValue("$event",item.CanonicalSha256);await link.ExecuteNonQueryAsync(ct);}
        await tx.CommitAsync(ct);return added;
    }
    public async Task RecordExecutionAsync(string cycle,ExecutionIntent intent,ExchangeOrder order,string strategyVersion,CancellationToken ct)
    {
        var price=order.AvgPrice>0?order.AvgPrice:intent.ExpectedPrice;var quantity=order.ExecutedQuantity>0?order.ExecutedQuantity:intent.Quantity;
        var attribution=await ResolvePostTradeAttributionAsync(cycle,strategyVersion,ct);
        if(intent.ReduceOnly&&string.Equals(order.Status,"FILLED",StringComparison.OrdinalIgnoreCase)&&await AcceptIdenticalPostTradeReplayAsync(intent.ClientOrderId,cycle,intent.Symbol,intent.Side.ToString(),price,quantity,intent.ExpectedPrice,attribution.StrategyVersion,ct))return;
        await Exec("INSERT OR REPLACE INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,avg_price,expected_price,status,occurred_at,exchange_updated_at) VALUES($c,$id,$s,$side,$a,$r,$q,$p,$expected,$st,$t,$exchange)",ct,("$c",cycle),("$id",intent.ClientOrderId),("$s",intent.Symbol),("$side",intent.Side.ToString()),("$a",intent.Action.ToString()),("$r",intent.ReduceOnly?1:0),("$q",quantity.ToString(CultureInfo.InvariantCulture)),("$p",price.ToString(CultureInfo.InvariantCulture)),("$expected",intent.ExpectedPrice.ToString(CultureInfo.InvariantCulture)),("$st",order.Status),("$t",_utcNow().ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)),("$exchange",new DateTimeOffset(order.UpdatedAt.ToUniversalTime()).ToString("O",CultureInfo.InvariantCulture)));
        if(!intent.ReduceOnly||!string.Equals(order.Status,"FILLED",StringComparison.OrdinalIgnoreCase)||price<=0||quantity<=0)return;
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);var entry=await WeightedEntryPriceAsync(c,intent.Symbol,intent.Side.ToString(),intent.ClientOrderId,quantity,ct);if(entry is null)return;
        const decimal estimatedFeeRate=.0004m;var reported=await ReportedFeesForCloseAsync(c,intent.Symbol,intent.Side.ToString(),intent.ClientOrderId,quantity,ct);var feeBasis=reported is null?"estimated-static-rate":"exchange-reported-usdt";var feeRate=reported is null?estimatedFeeRate:0m;var slippage=await SlippageForCloseAsync(c,intent.Symbol,intent.Side.ToString(),intent.ClientOrderId,quantity,price,intent.ExpectedPrice,ct);var closedAt=_utcNow().ToUniversalTime();var funding=await FundingForCloseAsync(c,intent.Symbol,intent.Side.ToString(),intent.ClientOrderId,quantity,closedAt,ct);var excursion=await GetTradeExcursionAsync(c,intent.Symbol,intent.Side,intent.ClientOrderId,quantity,entry.Value,ct);var exitReason=ResolveExitReason(intent,order);var gross=(intent.Side==PositionSide.Long?price-entry.Value:entry.Value-price)*quantity;var fees=reported??(entry.Value+price)*quantity*estimatedFeeRate;var net=gross-fees+funding.Amount;var ret=entry.Value*quantity>0?net/(entry.Value*quantity):0;
        await using var insert=c.CreateCommand();insert.CommandText="INSERT OR IGNORE INTO trade_outcomes(client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,gross_pnl,fees,fee_basis,fee_rate,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,close_expected_price,funding_amount,funding_basis,net_pnl,return_pct,mae_return_pct,mfe_return_pct,excursion_basis,excursion_samples,exit_reason,closed_at,strategy_id,strategy_version,attribution_basis) VALUES($id,$c,$s,$side,$e,$x,$q,$g,$f,$fb,$fr,$es,$xs,$ts,$sb,$expected,$funding,$fundingBasis,$n,$r,$mae,$mfe,$excursionBasis,$excursionSamples,$exitReason,$t,$sid,$v,$ab); SELECT changes();";insert.Parameters.AddWithValue("$id",intent.ClientOrderId);insert.Parameters.AddWithValue("$c",cycle);insert.Parameters.AddWithValue("$s",intent.Symbol);insert.Parameters.AddWithValue("$side",intent.Side.ToString());insert.Parameters.AddWithValue("$e",entry.Value.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$x",price.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$q",quantity.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$g",gross.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$f",fees.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$fb",feeBasis);insert.Parameters.AddWithValue("$fr",feeRate.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$es",slippage.Entry.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$xs",slippage.Exit.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$ts",slippage.Total.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$sb",slippage.Basis);insert.Parameters.AddWithValue("$expected",intent.ExpectedPrice.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$funding",funding.Amount.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$fundingBasis",funding.Basis);insert.Parameters.AddWithValue("$n",net.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$r",ret.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$mae",excursion.MaeReturnPct.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$mfe",excursion.MfeReturnPct.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$excursionBasis",excursion.Basis);insert.Parameters.AddWithValue("$excursionSamples",excursion.SampleCount);insert.Parameters.AddWithValue("$exitReason",exitReason);insert.Parameters.AddWithValue("$t",closedAt.ToString("O"));insert.Parameters.AddWithValue("$sid",(object?)attribution.StrategyId??DBNull.Value);insert.Parameters.AddWithValue("$v",attribution.StrategyVersion);insert.Parameters.AddWithValue("$ab",attribution.Basis);var inserted=Convert.ToInt32(await insert.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
        if(!inserted){await EnsureIdenticalTradeOutcomeAsync(c,intent.ClientOrderId,cycle,intent.Symbol,intent.Side.ToString(),entry.Value,price,quantity,fees,slippage,intent.ExpectedPrice,funding,net,ret,excursion,exitReason,attribution.StrategyVersion,ct);return;}

    }
    private static DateTimeOffset? ExecutionEventTime(SqliteDataReader reader,int exchangeUpdatedIndex,int occurredIndex)
    {
        foreach(var index in new[]{exchangeUpdatedIndex,occurredIndex})
        {
            if(reader.IsDBNull(index))continue;
            if(DateTimeOffset.TryParse(reader.GetString(index),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var value))
                return value.ToUniversalTime();
        }
        return null;
    }

    private const string ExecutionPositionRetiredThroughPrefix="execution-position-retired-through:";
    private static string ExecutionPositionRetiredThroughKey(string symbol,PositionSide side)=>
        ExecutionPositionRetiredThroughPrefix+symbol.Trim().ToUpperInvariant()+":"+side.ToString().ToLowerInvariant();

    public Task<IReadOnlyList<ExecutionPositionLegV1>> GetExecutionPositionLedgerAsync(CancellationToken ct)=>
        GetExecutionPositionLedgerCoreAsync(null,ct);

    public Task<IReadOnlyList<ExecutionPositionLegV1>> GetExecutionPositionLedgerAsync(DateTimeOffset asOfUtc,CancellationToken ct)=>
        GetExecutionPositionLedgerCoreAsync(asOfUtc.ToUniversalTime(),ct);

    private async Task<IReadOnlyList<ExecutionPositionLegV1>> GetExecutionPositionLedgerCoreAsync(DateTimeOffset? asOfUtc,CancellationToken ct)
    {
        var retiredThrough=new Dictionary<string,DateTimeOffset>(StringComparer.Ordinal);
        var totals=new Dictionary<(string Symbol,PositionSide Side),decimal>();
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);
        await using(var retirement=c.CreateCommand())
        {
            retirement.CommandText="SELECT key,value FROM agent_state WHERE key LIKE $prefix ORDER BY key";
            retirement.Parameters.AddWithValue("$prefix",ExecutionPositionRetiredThroughPrefix+"%");
            await using var rr=await retirement.ExecuteReaderAsync(ct);
            while(await rr.ReadAsync(ct))
            {
                if(!DateTimeOffset.TryParse(rr.GetString(1),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var cutoff))
                    throw new InvalidOperationException("Execution position retirement cutoff is invalid.");
                retiredThrough[rr.GetString(0)]=cutoff.ToUniversalTime();
            }
        }
        await using var q=c.CreateCommand();
        q.CommandText="SELECT symbol,side,reduce_only,quantity,exchange_updated_at,occurred_at FROM execution_events WHERE status IN ('FILLED','PARTIALLY_FILLED') ORDER BY symbol,side,id";
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var eventAt=ExecutionEventTime(r,4,5);
            if(asOfUtc is not null&&eventAt is not null&&eventAt.Value>asOfUtc.Value)continue;
            var symbol=r.GetString(0);var side=Enum.Parse<PositionSide>(r.GetString(1),true);
            if(eventAt is not null&&retiredThrough.TryGetValue(ExecutionPositionRetiredThroughKey(symbol,side),out var cutoff)&&eventAt.Value<=cutoff)continue;
            if(!decimal.TryParse(r.GetString(3),NumberStyles.Number,CultureInfo.InvariantCulture,out var quantity)||quantity<0)
                throw new InvalidOperationException("Execution position ledger quantity is invalid.");
            var key=(symbol,side);var current=totals.GetValueOrDefault(key);var reduceOnly=r.GetInt32(2)==1;
            var retirementKey=ExecutionPositionRetiredThroughKey(symbol,side);
            if(reduceOnly&&current==0&&eventAt is not null&&retiredThrough.TryGetValue(retirementKey,out var retiredAt)&&eventAt.Value>retiredAt)
                continue;
            totals[key]=current+(reduceOnly?-quantity:quantity);
        }
        return totals.OrderBy(x=>x.Key.Symbol,StringComparer.Ordinal).ThenBy(x=>x.Key.Side).Select(x=>new ExecutionPositionLegV1(x.Key.Symbol,x.Key.Side,x.Value)).ToArray();
    }

    public async Task<bool> RetireExecutionPositionLedgerAsync(string symbol,PositionSide side,DateTimeOffset observedAtUtc,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(symbol)||observedAtUtc==default)throw new ArgumentException("Execution position retirement identity is invalid.");
        observedAtUtc=observedAtUtc.ToUniversalTime();var key=ExecutionPositionRetiredThroughKey(symbol,side);
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
        DateTimeOffset? current=null;
        await using(var read=c.CreateCommand())
        {
            read.Transaction=tx;read.CommandText="SELECT value FROM agent_state WHERE key=$key";read.Parameters.AddWithValue("$key",key);
            var value=await read.ExecuteScalarAsync(ct);
            if(value is not null&&value is not DBNull)
            {
                if(!DateTimeOffset.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed))
                    throw new InvalidOperationException("Execution position retirement cutoff is invalid.");
                current=parsed.ToUniversalTime();
            }
        }
        if(current is not null&&current.Value>=observedAtUtc){await tx.RollbackAsync(ct);return false;}
        var eligible=false;
        await using(var evidence=c.CreateCommand())
        {
            evidence.Transaction=tx;evidence.CommandText="SELECT exchange_updated_at,occurred_at FROM execution_events WHERE symbol=$symbol COLLATE NOCASE AND side=$side AND status IN ('FILLED','PARTIALLY_FILLED') ORDER BY id";
            evidence.Parameters.AddWithValue("$symbol",symbol);evidence.Parameters.AddWithValue("$side",side.ToString());
            await using var er=await evidence.ExecuteReaderAsync(ct);
            while(await er.ReadAsync(ct))
            {
                var eventAt=ExecutionEventTime(er,0,1);
                if(eventAt is not null&&eventAt.Value<=observedAtUtc){eligible=true;break;}
            }
        }
        if(!eligible){await tx.RollbackAsync(ct);return false;}
        await using(var write=c.CreateCommand())
        {
            write.Transaction=tx;write.CommandText="INSERT OR REPLACE INTO agent_state(key,value,updated_at) VALUES($key,$value,$updated)";
            write.Parameters.AddWithValue("$key",key);write.Parameters.AddWithValue("$value",observedAtUtc.ToString("O",CultureInfo.InvariantCulture));write.Parameters.AddWithValue("$updated",_utcNow().ToUniversalTime().ToString("O",CultureInfo.InvariantCulture));
            await write.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);return true;
    }
    public async Task<bool> SavePositionReconciliationAsync(PositionReconciliationReportV1 report,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);if(!PositionReconciliationServiceV1.IsCanonical(report))throw new InvalidOperationException("Position reconciliation canonical identity is invalid.");await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO position_reconciliation_audits(report_id,schema,observed_at,evaluated_at,state,allows_risk_increase,canonical_sha256,canonical_bytes) VALUES($id,$schema,$observed,$evaluated,$state,$allows,$hash,$bytes); SELECT changes();";q.Parameters.AddWithValue("$id",report.ReportId);q.Parameters.AddWithValue("$schema",report.Schema);q.Parameters.AddWithValue("$observed",report.ObservedAtUtc.ToString("O"));q.Parameters.AddWithValue("$evaluated",report.EvaluatedAtUtc.ToString("O"));q.Parameters.AddWithValue("$state",report.State.ToString());q.Parameters.AddWithValue("$allows",report.AllowsRiskIncrease?1:0);q.Parameters.AddWithValue("$hash",report.CanonicalSha256);q.Parameters.Add("$bytes",SqliteType.Blob).Value=report.CanonicalBytes;return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }
    public async Task<bool> SaveExternalPositionIsolationAsync(ExternalPositionIsolationReportV1 report,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);if(!ExternalPositionIsolationServiceV1.IsCanonical(report))throw new InvalidOperationException("External position isolation canonical identity is invalid.");await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO external_position_isolation_audits(report_id,schema,observed_at,evaluated_at,state,allows_risk_increase,canonical_sha256,canonical_bytes) VALUES($id,$schema,$observed,$evaluated,$state,$allows,$hash,$bytes); SELECT changes();";q.Parameters.AddWithValue("$id",report.ReportId);q.Parameters.AddWithValue("$schema",report.Schema);q.Parameters.AddWithValue("$observed",report.ObservedAtUtc.ToString("O"));q.Parameters.AddWithValue("$evaluated",report.EvaluatedAtUtc.ToString("O"));q.Parameters.AddWithValue("$state",report.State.ToString());q.Parameters.AddWithValue("$allows",report.AllowsRiskIncrease?1:0);q.Parameters.AddWithValue("$hash",report.CanonicalSha256);q.Parameters.Add("$bytes",SqliteType.Blob).Value=report.CanonicalBytes;return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }
    public async Task<bool> SaveProtectionReconciliationAsync(ProtectionReconciliationReportV1 report,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);if(!ProtectionReconciliationServiceV1.IsCanonical(report))throw new InvalidOperationException("Protection reconciliation canonical identity is invalid.");await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT OR IGNORE INTO protection_reconciliation_audits(report_id,schema,observed_at,evaluated_at,state,allows_risk_increase,canonical_sha256,canonical_bytes) VALUES($id,$schema,$observed,$evaluated,$state,$allows,$hash,$bytes); SELECT changes();";q.Parameters.AddWithValue("$id",report.ReportId);q.Parameters.AddWithValue("$schema",report.Schema);q.Parameters.AddWithValue("$observed",report.ObservedAtUtc.ToString("O"));q.Parameters.AddWithValue("$evaluated",report.EvaluatedAtUtc.ToString("O"));q.Parameters.AddWithValue("$state",report.State.ToString());q.Parameters.AddWithValue("$allows",report.AllowsRiskIncrease?1:0);q.Parameters.AddWithValue("$hash",report.CanonicalSha256);q.Parameters.Add("$bytes",SqliteType.Blob).Value=report.CanonicalBytes;return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }
    public async Task<IReadOnlyList<PostTradeReviewV1>> GetRecentPostTradeReviewsAsync(int limit,CancellationToken ct)
    {
        var rows=new List<PostTradeReviewV1>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,fees,fee_basis,fee_rate,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,funding_amount,funding_basis,net_pnl,return_pct,mae_return_pct,mfe_return_pct,excursion_basis,excursion_samples,exit_reason,closed_at,strategy_id,strategy_version,attribution_basis FROM trade_outcomes WHERE client_order_id IS NOT NULL ORDER BY closed_at DESC,id DESC LIMIT $limit";q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var net=Decimal(r.GetString(16));rows.Add(new("wpe.post-trade-review/1.5",r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),Decimal(r.GetString(4)),Decimal(r.GetString(5)),Decimal(r.GetString(6)),Decimal(r.GetString(7)),r.GetString(8),Decimal(r.GetString(9)),Decimal(r.GetString(10)),Decimal(r.GetString(11)),Decimal(r.GetString(12)),r.GetString(13),Decimal(r.GetString(14)),r.GetString(15),net,Decimal(r.GetString(17)),Decimal(r.GetString(18)),Decimal(r.GetString(19)),r.GetString(20),r.GetInt32(21),r.GetString(22),net>0?"win":net<0?"loss":"flat",DateTimeOffset.Parse(r.GetString(23),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),r.IsDBNull(24)?null:r.GetString(24),r.GetString(25),r.GetString(26)));}return rows;
    }
    private async Task<(string? StrategyId,string StrategyVersion,string Basis)> ResolvePostTradeAttributionAsync(string cycle,string fallbackVersion,CancellationToken ct)
    {
        var matches=new List<(string Id,string Version)>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT DISTINCT strategy_id,strategy_version FROM automatic_execution_queue WHERE correlation_id=$cycle";q.Parameters.AddWithValue("$cycle",cycle);await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))matches.Add((r.GetString(0),r.GetString(1)));return matches.Count switch{1=>(matches[0].Id,matches[0].Version,"automatic-artifact"),0=>(null,fallbackVersion,"legacy-version-only"),_=>(null,fallbackVersion,"conflicting-correlation")};
    }
    private static async Task EnsureIdenticalTradeOutcomeAsync(SqliteConnection c,string clientOrderId,string cycle,string symbol,string side,decimal entry,decimal exit,decimal quantity,decimal fees,(decimal Entry,decimal Exit,decimal Total,string Basis) slippage,decimal closeExpectedPrice,(decimal Amount,string Basis) funding,decimal net,decimal returnPct,TradeExcursionSummary excursion,string exitReason,string strategyVersion,CancellationToken ct)
    {
        await using var q=c.CreateCommand();q.CommandText="SELECT cycle_id,symbol,side,entry_price,exit_price,quantity,fees,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,close_expected_price,funding_amount,funding_basis,net_pnl,return_pct,mae_return_pct,mfe_return_pct,excursion_basis,excursion_samples,exit_reason,strategy_version FROM trade_outcomes WHERE client_order_id=$id";q.Parameters.AddWithValue("$id",clientOrderId);await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)||!string.Equals(r.GetString(0),cycle,StringComparison.Ordinal)||!string.Equals(r.GetString(1),symbol,StringComparison.Ordinal)||!string.Equals(r.GetString(2),side,StringComparison.Ordinal)||Decimal(r.GetString(3))!=entry||Decimal(r.GetString(4))!=exit||Decimal(r.GetString(5))!=quantity||Decimal(r.GetString(6))!=fees||Decimal(r.GetString(7))!=slippage.Entry||Decimal(r.GetString(8))!=slippage.Exit||Decimal(r.GetString(9))!=slippage.Total||r.GetString(10)!=slippage.Basis||Decimal(r.GetString(11))!=closeExpectedPrice||Decimal(r.GetString(12))!=funding.Amount||r.GetString(13)!=funding.Basis||Decimal(r.GetString(14))!=net||Decimal(r.GetString(15))!=returnPct||Decimal(r.GetString(16))!=excursion.MaeReturnPct||Decimal(r.GetString(17))!=excursion.MfeReturnPct||r.GetString(18)!=excursion.Basis||r.GetInt32(19)!=excursion.SampleCount||r.GetString(20)!=exitReason||!string.Equals(r.GetString(21),strategyVersion,StringComparison.Ordinal))throw new InvalidOperationException("Post-trade outcome identity conflict.");
    }
    private async Task<bool> AcceptIdenticalPostTradeReplayAsync(string clientOrderId,string cycle,string symbol,string side,decimal exit,decimal quantity,decimal closeExpectedPrice,string strategyVersion,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT cycle_id,symbol,side,exit_price,quantity,close_expected_price,strategy_version FROM trade_outcomes WHERE client_order_id=$id";q.Parameters.AddWithValue("$id",clientOrderId);await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return false;if(!string.Equals(r.GetString(0),cycle,StringComparison.Ordinal)||!string.Equals(r.GetString(1),symbol,StringComparison.Ordinal)||!string.Equals(r.GetString(2),side,StringComparison.Ordinal)||Decimal(r.GetString(3))!=exit||Decimal(r.GetString(4))!=quantity||Decimal(r.GetString(5))!=closeExpectedPrice||!string.Equals(r.GetString(6),strategyVersion,StringComparison.Ordinal))throw new InvalidOperationException("Post-trade outcome identity conflict.");return true;
    }
    private static async Task<(decimal Amount,string Basis)> FundingForCloseAsync(SqliteConnection c,string symbol,string side,string closeClientOrderId,decimal closingQuantity,DateTimeOffset evaluatedAt,CancellationToken ct)
    {
        decimal same=0,opposite=0;DateTimeOffset? opened=null;DateTimeOffset? closed=null;bool ambiguous=false,found=false;
        await using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT client_order_id,side,reduce_only,quantity,exchange_updated_at FROM execution_events WHERE symbol=$symbol AND status IN ('FILLED','PARTIALLY_FILLED') ORDER BY id";q.Parameters.AddWithValue("$symbol",symbol);await using var rows=await q.ExecuteReaderAsync(ct);
            while(await rows.ReadAsync(ct))
            {
                var id=rows.GetString(0);var rowSide=rows.GetString(1);var reduce=rows.GetInt32(2)==1;var quantity=Decimal(rows.GetString(3));if(quantity<=0)return(0,"unavailable");
                if(rowSide==side)
                {
                    if(!reduce){if(same==0){opened=rows.IsDBNull(4)?null:DateTimeOffset.Parse(rows.GetString(4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();ambiguous=opposite>0;}same+=quantity;}
                    else
                    {
                        if(id==closeClientOrderId){found=true;closed=rows.IsDBNull(4)?null:DateTimeOffset.Parse(rows.GetString(4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();if(quantity!=closingQuantity||same!=closingQuantity)ambiguous=true;}
                        else if(same>0)ambiguous=true;
                        if(quantity>same)return(0,"unavailable");same-=quantity;
                    }
                }
                else{opposite+=reduce?-quantity:quantity;if(opposite<0)return(0,"unavailable");if(opened is not null&&opposite>0)ambiguous=true;}
                if(found)break;
                if(same==0){opened=null;ambiguous=false;}
            }
        }
        if(!found||ambiguous||same!=0||opposite!=0||opened is null||closed is null||closed>evaluatedAt.AddMinutes(1))return(0,"unavailable");
        string? windowId=null;await using(var window=c.CreateCommand())
        {
            window.CommandText="SELECT window_id FROM funding_observation_windows WHERE symbol=$symbol AND environment='Testnet' AND state='Available' AND start_utc<=$opened AND end_utc>=$closed AND observed_at>=$closed AND observed_at<=$fresh ORDER BY observed_at DESC,window_id DESC LIMIT 1";window.Parameters.AddWithValue("$symbol",symbol);window.Parameters.AddWithValue("$opened",opened.Value.ToString("O",CultureInfo.InvariantCulture));window.Parameters.AddWithValue("$closed",closed.Value.ToString("O",CultureInfo.InvariantCulture));window.Parameters.AddWithValue("$fresh",closed.Value.AddMinutes(5).ToString("O",CultureInfo.InvariantCulture));windowId=await window.ExecuteScalarAsync(ct) as string;
        }
        if(windowId is null)return(0,"unavailable");decimal total=0;await using(var events=c.CreateCommand())
        {
            events.CommandText="SELECT e.amount,e.asset FROM funding_window_events l JOIN funding_income_events e ON e.event_id=l.event_id WHERE l.window_id=$window ORDER BY e.occurred_at,e.transaction_id";events.Parameters.AddWithValue("$window",windowId);await using var rows=await events.ExecuteReaderAsync(ct);while(await rows.ReadAsync(ct)){if(rows.GetString(1)!="USDT")return(0,"unavailable");total+=Decimal(rows.GetString(0));}
        }
        return(total,"exchange-reported-window");
    }
    private static async Task<(decimal Entry,decimal Exit,decimal Total,string Basis)> SlippageForCloseAsync(SqliteConnection c,string symbol,string side,string closeClientOrderId,decimal closingQuantity,decimal closeFillPrice,decimal closeExpectedPrice,CancellationToken ct)
    {
        if(closeExpectedPrice<=0)return(0,0,0,"unavailable");decimal openQuantity=0,openSlippagePool=0;await using var q=c.CreateCommand();q.CommandText="SELECT reduce_only,quantity,avg_price,expected_price FROM execution_events WHERE symbol=$symbol AND side=$side AND client_order_id<>$close AND status IN ('FILLED','PARTIALLY_FILLED') ORDER BY id";q.Parameters.AddWithValue("$symbol",symbol);q.Parameters.AddWithValue("$side",side);q.Parameters.AddWithValue("$close",closeClientOrderId);await using var rows=await q.ExecuteReaderAsync(ct);while(await rows.ReadAsync(ct)){var quantity=Decimal(rows.GetString(1));if(quantity<=0)return(0,0,0,"unavailable");if(rows.GetInt32(0)==0){var fill=Decimal(rows.GetString(2));var expected=Decimal(rows.GetString(3));if(fill<=0||expected<=0)return(0,0,0,"unavailable");openQuantity+=quantity;openSlippagePool+=(side==nameof(PositionSide.Long)?fill-expected:expected-fill)*quantity;continue;}if(quantity>openQuantity)return(0,0,0,"unavailable");var perUnit=openQuantity>0?openSlippagePool/openQuantity:0;openQuantity-=quantity;openSlippagePool-=perUnit*quantity;}if(openQuantity<closingQuantity||openQuantity<=0)return(0,0,0,"unavailable");var entry=openSlippagePool/openQuantity*closingQuantity;var exit=(side==nameof(PositionSide.Long)?closeExpectedPrice-closeFillPrice:closeFillPrice-closeExpectedPrice)*closingQuantity;return(entry,exit,entry+exit,"intent-expected-vs-fill");
    }
    private static async Task<decimal?> ReportedFeesForCloseAsync(SqliteConnection c,string symbol,string side,string closeClientOrderId,decimal closingQuantity,CancellationToken ct)
    {
        decimal? closeFee=null;
        await using(var close=c.CreateCommand())
        {
            close.CommandText="SELECT fee_amount,fee_asset,state FROM exchange_order_fee_evidence WHERE client_order_id=$id AND symbol=$symbol ORDER BY observed_at DESC,evidence_id DESC LIMIT 1";close.Parameters.AddWithValue("$id",closeClientOrderId);close.Parameters.AddWithValue("$symbol",symbol);await using var r=await close.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)&&r.GetString(2)=="Confirmed"&&r.GetString(1)=="USDT")closeFee=Decimal(r.GetString(0));
        }
        if(closeFee is null)return null;
        decimal openQuantity=0,openFeePool=0;await using var q=c.CreateCommand();q.CommandText="SELECT e.reduce_only,e.quantity,f.fee_amount,f.fee_asset,f.state FROM execution_events e LEFT JOIN exchange_order_fee_evidence f ON f.evidence_id=(SELECT evidence_id FROM exchange_order_fee_evidence x WHERE x.client_order_id=e.client_order_id AND x.symbol=e.symbol ORDER BY x.observed_at DESC,x.evidence_id DESC LIMIT 1) WHERE e.symbol=$symbol AND e.side=$side AND e.client_order_id<>$close AND e.status IN ('FILLED','PARTIALLY_FILLED') ORDER BY e.id";q.Parameters.AddWithValue("$symbol",symbol);q.Parameters.AddWithValue("$side",side);q.Parameters.AddWithValue("$close",closeClientOrderId);await using var rows=await q.ExecuteReaderAsync(ct);while(await rows.ReadAsync(ct)){var quantity=Decimal(rows.GetString(1));if(quantity<=0)return null;if(rows.GetInt32(0)==0){if(rows.IsDBNull(2)||rows.GetString(4)!="Confirmed"||rows.GetString(3)!="USDT")return null;var fee=Decimal(rows.GetString(2));if(fee<0)return null;openQuantity+=quantity;openFeePool+=fee;continue;}if(quantity>openQuantity)return null;var feePerUnit=openQuantity>0?openFeePool/openQuantity:0;openQuantity-=quantity;openFeePool-=feePerUnit*quantity;}if(openQuantity<closingQuantity||openQuantity<=0)return null;return openFeePool/openQuantity*closingQuantity+closeFee.Value;
    }
    private static async Task<decimal?> WeightedEntryPriceAsync(SqliteConnection c,string symbol,string side,string excludedClientOrderId,decimal closingQuantity,CancellationToken ct)
    {
        decimal openQuantity=0,openCost=0;await using var q=c.CreateCommand();q.CommandText="SELECT reduce_only,quantity,avg_price FROM execution_events WHERE symbol=$s AND side=$side AND client_order_id<>$id AND status IN ('FILLED','PARTIALLY_FILLED') ORDER BY id";q.Parameters.AddWithValue("$s",symbol);q.Parameters.AddWithValue("$side",side);q.Parameters.AddWithValue("$id",excludedClientOrderId);await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var quantity=Decimal(r.GetString(1));var price=Decimal(r.GetString(2));if(quantity<=0||price<=0)return null;if(r.GetInt32(0)==0){openQuantity+=quantity;openCost+=quantity*price;continue;}if(quantity>openQuantity)return null;var average=openQuantity>0?openCost/openQuantity:0;openQuantity-=quantity;openCost-=average*quantity;}return openQuantity>=closingQuantity&&openQuantity>0?openCost/openQuantity:null;
    }
    private static decimal Decimal(string value)=>decimal.Parse(value,NumberStyles.Number,CultureInfo.InvariantCulture);
    public async Task<RiskHistorySnapshot> GetRiskHistoryAsync(CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);
        decimal daily;await using(var dailyQuery=c.CreateCommand()){dailyQuery.CommandText="SELECT COALESCE(SUM(CAST(net_pnl AS REAL)),0) FROM trade_outcomes WHERE closed_at >= $start AND (cycle_id IS NULL OR cycle_id NOT LIKE 'SMOKE-%') AND (client_order_id IS NULL OR client_order_id NOT LIKE 'WPE-SMOKE-%')";dailyQuery.Parameters.AddWithValue("$start",DateTime.UtcNow.Date.ToString("O"));var raw=await dailyQuery.ExecuteScalarAsync(ct);daily=decimal.TryParse(raw?.ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var value)?value:0;}
        await using var errors=c.CreateCommand();errors.CommandText="SELECT COUNT(*) FROM errors WHERE occurred_at >= $t AND (stage LIKE '%API%' OR stage LIKE '%EXCHANGE%' OR stage='AGENT_LOOP')";errors.Parameters.AddWithValue("$t",DateTime.UtcNow.AddMinutes(-30).ToString("O"));var api=Convert.ToInt32(await errors.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
        await using var uncertain=c.CreateCommand();uncertain.CommandText="SELECT COUNT(*) FROM order_intents WHERE status IN ('UNKNOWN','EMERGENCY_UNKNOWN','NEW','PARTIALLY_FILLED')";var unknown=Convert.ToInt32(await uncertain.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)>0;return new(daily,api,unknown);
    }
    public async Task<bool> HasStateAsync(string key,CancellationToken ct){await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT 1 FROM agent_state WHERE key=$k LIMIT 1";q.Parameters.AddWithValue("$k",key);return await q.ExecuteScalarAsync(ct) is not null;}
    public Task SetStateAsync(string key,string value,CancellationToken ct)=>Exec("INSERT OR REPLACE INTO agent_state(key,value,updated_at) VALUES($k,$v,$t)",ct,("$k",key),("$v",value),("$t",DateTime.UtcNow.ToString("O")));
    public async Task<string?> GetStateAsync(string key,CancellationToken ct)
    {
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT value FROM agent_state WHERE key=$k";q.Parameters.AddWithValue("$k",key);var value=await q.ExecuteScalarAsync(ct);return value is null||value is DBNull?null:Convert.ToString(value);
    }
    public Task BeginWorkflowRunAsync(string runId,string cycleId,string stateJson,CancellationToken ct)=>Exec("INSERT INTO workflow_runs(run_id,cycle_id,status,current_node,state_json,started_at,updated_at) VALUES($r,$c,'RUNNING',$n,$s,$t,$t)",ct,("$r",runId),("$c",cycleId),("$n",WorkflowNode.Observation.ToString()),("$s",stateJson),("$t",DateTime.UtcNow.ToString("O")));
    public async Task SaveWorkflowCheckpointAsync(WorkflowCheckpoint checkpoint,CancellationToken ct)
    {
        await Exec("INSERT OR REPLACE INTO workflow_checkpoints(run_id,cycle_id,node,phase,attempt,state_json,created_at) VALUES($r,$c,$n,$p,$a,$s,$t)",ct,("$r",checkpoint.RunId),("$c",checkpoint.CycleId),("$n",checkpoint.Node.ToString()),("$p",checkpoint.Phase.ToString()),("$a",checkpoint.Attempt),("$s",checkpoint.StateJson),("$t",checkpoint.CreatedAtUtc.ToString("O")));
        await Exec("UPDATE workflow_runs SET current_node=$n,state_json=$s,updated_at=$t WHERE run_id=$r AND cycle_id=$c",ct,("$r",checkpoint.RunId),("$c",checkpoint.CycleId),("$n",checkpoint.Node.ToString()),("$s",checkpoint.StateJson),("$t",checkpoint.CreatedAtUtc.ToString("O")));
    }
    public Task CompleteWorkflowRunAsync(string runId,string cycleId,string status,string? error,CancellationToken ct)=>Exec("UPDATE workflow_runs SET status=$s,updated_at=$t,error=$e WHERE run_id=$r AND cycle_id=$c",ct,("$r",runId),("$c",cycleId),("$s",SensitiveDataRedactor.ForLog(status,40)),("$t",DateTime.UtcNow.ToString("O")),("$e",error is null?null:SensitiveDataRedactor.Redact(error)));
    public async Task<IReadOnlyList<WorkflowRecovery>> GetInterruptedWorkflowsAsync(CancellationToken ct)
    {
        var list=new List<WorkflowRecovery>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT run_id,cycle_id,current_node,state_json,updated_at FROM workflow_runs WHERE status IN ('RUNNING','RECOVERY_PENDING') ORDER BY updated_at";await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)){var node=Enum.TryParse<WorkflowNode>(r.GetString(2),true,out var parsed)?parsed:WorkflowNode.Observation;list.Add(new(r.GetString(0),r.GetString(1),node,CheckpointPhase.Entered,r.GetString(3),DateTime.Parse(r.GetString(4),null,DateTimeStyles.RoundtripKind),node==WorkflowNode.Execution?"RECONCILE_EXECUTION":"RESTART_OBSERVATION"));}return list;
    }
    public async Task MarkWorkflowRecoveredAsync(WorkflowRecovery recovery,string action,CancellationToken ct)
    {
        var state=JsonSerializer.Serialize(new{recovery.LastNode,action,recoveredAtUtc=DateTime.UtcNow});
        await SaveWorkflowCheckpointAsync(new(recovery.RunId,recovery.CycleId,recovery.LastNode,CheckpointPhase.Recovered,state,DateTime.UtcNow),ct);
        await CompleteWorkflowRunAsync(recovery.RunId,recovery.CycleId,"RECOVERED",action,ct);
        await Exec("UPDATE cycles SET completed_at=$t,status='RECOVERED',risk_result=$a WHERE id=$c AND status='RUNNING'",ct,("$c",recovery.CycleId),("$t",DateTime.UtcNow.ToString("O")),("$a",action));
    }
    public Task RecordRuntimeEventAsync(AgentRuntimeEvent value,CancellationToken ct)=>Exec("INSERT OR IGNORE INTO runtime_events(event_id,sequence,correlation_id,causation_id,event_type,source,payload_json,occurred_at) VALUES($i,$q,$c,$a,$e,$s,$p,$t)",ct,("$i",value.EventId),("$q",value.Sequence),("$c",value.CorrelationId),("$a",value.CausationId),("$e",value.EventType),("$s",value.Source),("$p",value.PayloadJson),("$t",value.OccurredAtUtc.ToString("O")));
    public async Task<IReadOnlyList<string>> GetRecentRuntimeEventsAsync(int limit,CancellationToken ct)
    {
        return await GetRecentRuntimeEventsAsync(limit, string.Empty, ct);
    }
    public async Task<IReadOnlyList<string>> GetRecentRuntimeEventsAsync(int limit,string? filter,CancellationToken ct)
    {
        var list=new List<string>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT occurred_at,event_type,source,sequence,payload_json FROM runtime_events WHERE ($f='' OR event_type LIKE $like OR source LIKE $like OR payload_json LIKE $like) ORDER BY sequence DESC LIMIT $l";
        var value=(filter??string.Empty).Trim();q.Parameters.AddWithValue("$f",value);q.Parameters.AddWithValue("$like",$"%{value}%");q.Parameters.AddWithValue("$l",Math.Clamp(limit,1,500));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var payload=r.IsDBNull(4)?string.Empty:r.GetString(4);if(payload.Length>220)payload=payload[..220]+"...";list.Add($"{r.GetString(0)}  #{r.GetInt64(3)}  {r.GetString(1)}  [{r.GetString(2)}]\n{payload}");}return list;
    }
    public async Task<IReadOnlyList<PersistedRuntimeAuditEvent>> GetRecentAuditEventsAsync(int limit,CancellationToken ct)
    {
        var list=new List<PersistedRuntimeAuditEvent>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="""
        SELECT event_id,occurred_at,category,source,correlation_id,status,item_a,item_b,duration_ms
        FROM (
            SELECT 'decision:'||d.cycle_id event_id,d.created_at occurred_at,'decision' category,'DecisionGovernance' source,d.cycle_id correlation_id,COALESCE(c.status,'RECORDED') status,'' item_a,'' item_b,0 duration_ms
            FROM decision_audits d LEFT JOIN cycles c ON c.id=d.cycle_id
            UNION ALL
            SELECT 'risk:'||m.cycle_id,m.created_at,'risk','IndependentRiskManager',m.cycle_id,'RECORDED','','',0
            FROM maturity_audits m
            UNION ALL
            SELECT 'execution:'||CAST(e.id AS TEXT),e.occurred_at,'execution','ReliableOrderExecutor',e.cycle_id,e.status,e.action,e.symbol,0
            FROM execution_events e
            UNION ALL
            SELECT 'recovery:'||CAST(w.id AS TEXT),w.created_at,'recovery','AgentRuntimeSupervisor',w.cycle_id,w.phase,w.node,'',0
            FROM workflow_checkpoints w WHERE w.phase='Recovered'
            UNION ALL
            SELECT 'system:skill:'||CAST(s.id AS TEXT),s.occurred_at,'system','SkillExecutionGuard','',s.status,s.skill,'',s.duration_ms
            FROM skill_calls s
        ) ORDER BY occurred_at DESC,event_id DESC LIMIT $l
        """;
        q.Parameters.AddWithValue("$l",Math.Clamp(limit,1,200));await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var category=r.GetString(2);var a=SafeAuditToken(r.IsDBNull(6)?null:r.GetString(6),"unknown");var b=SafeAuditToken(r.IsDBNull(7)?null:r.GetString(7),"unknown");
            var summary=category switch
            {
                "decision"=>"Decision audit recorded.",
                "risk"=>"Independent risk review recorded.",
                "execution"=>$"{a} {b} execution recorded.",
                "recovery"=>$"{a} recovery checkpoint recorded.",
                _=>$"{a} completed in {Math.Max(0,r.IsDBNull(8)?0:r.GetInt64(8))} ms."
            };
            list.Add(new(
                SafeAuditToken(r.GetString(0),"audit-event"),
                DateTime.Parse(r.GetString(1),null,DateTimeStyles.RoundtripKind).ToUniversalTime(),
                category,
                SafeAuditToken(r.GetString(3),"WPE"),
                r.IsDBNull(4)||string.IsNullOrWhiteSpace(r.GetString(4))?null:SafeAuditToken(r.GetString(4),"correlation"),
                SafeAuditToken(r.IsDBNull(5)?null:r.GetString(5),"UNKNOWN"),
                summary));
        }
        return list;
    }

    private static string SafeAuditToken(string? value,string fallback)
    {
        if(string.IsNullOrWhiteSpace(value))return fallback;
        var safe=new string(value.Where(ch=>char.IsLetterOrDigit(ch)||ch is '-' or '_' or '.' or ':' or '/').Take(80).ToArray());
        return string.IsNullOrWhiteSpace(safe)?fallback:safe;
    }
    public async Task<IReadOnlyList<string>> GetWorkflowTimelineAsync(int limit,CancellationToken ct)
    {
        return await GetWorkflowTimelineAsync(limit, string.Empty, ct);
    }
    public async Task<IReadOnlyList<string>> GetWorkflowTimelineAsync(int limit,string? filter,CancellationToken ct)
    {
        var list=new List<string>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT created_at,cycle_id,node,phase,attempt,state_json FROM workflow_checkpoints ORDER BY id DESC LIMIT $l";q.Parameters.AddWithValue("$l",Math.Clamp(limit,1,300));await using var r=await q.ExecuteReaderAsync(ct);var value=(filter??string.Empty).Trim();while(await r.ReadAsync(ct)){var state=r.IsDBNull(5)?string.Empty:r.GetString(5);var line=$"{r.GetString(0)}  [{r.GetString(2)} / {r.GetString(3)}]  cycle={r.GetString(1)}  attempt={r.GetInt32(4)}\n{SummarizeWorkflowState(state)}";if(value.Length==0||line.Contains(value,StringComparison.OrdinalIgnoreCase))list.Add(line);}return list;
    }
    private static string SummarizeWorkflowState(string state)
    {
        if (string.IsNullOrWhiteSpace(state)) return "state: <empty>";
        try
        {
            using var doc=JsonDocument.Parse(state);var root=doc.RootElement;var parts=new List<string>();
            foreach(var key in new[]{"status","result","tool","toolId","durationMs","costUsd","promptHash","error"})
                if(root.TryGetProperty(key,out var value)) parts.Add($"{key}: {value.ToString()}");
            if(parts.Count>0)return string.Join(" | ",parts);
        }
        catch(JsonException) { }
        return state.Length>240?state[..240]+"...":state;
    }
    public async Task<IReadOnlyList<string>> GetMemoryExplorerAsync(int limit,CancellationToken ct)
    {
        var list=new List<string>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT completed_at,'DECISION' AS source,COALESCE(decision_json,risk_result,error,'') AS content FROM cycles WHERE completed_at IS NOT NULL UNION ALL SELECT collected_at,'NEWS',title||' · '||body_summary FROM news_documents ORDER BY completed_at DESC LIMIT $l";q.Parameters.AddWithValue("$l",Math.Clamp(limit,1,300));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var content=r.IsDBNull(2)?string.Empty:r.GetString(2);if(content.Length>260)content=content[..260]+"...";list.Add($"{r.GetString(0)}  [{r.GetString(1)}]\n{content}");}return list;
    }
    public async Task<IReadOnlyList<string>> GetMemoryExplorerAsync(int limit,string? filter,CancellationToken ct)
    {
        var all=await GetMemoryExplorerAsync(Math.Clamp(limit,1,300),ct);var value=(filter??string.Empty).Trim();
        return value.Length==0?all:all.Where(x=>x.Contains(value,StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public async Task<IReadOnlyDictionary<string,long>> GetMemorySourceCountsAsync(CancellationToken ct)
    {
        var result=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);
        foreach(var pair in new[]{("DECISION","SELECT COUNT(*) FROM cycles WHERE completed_at IS NOT NULL"),("NEWS","SELECT COUNT(*) FROM news_documents")})
        {await using var q=c.CreateCommand();q.CommandText=pair.Item2;result[pair.Item1]=Convert.ToInt64(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);}
        return result;
    }
    public async Task<IReadOnlyList<(string Source,int References)>> GetMemoryReferenceCountsAsync(CancellationToken ct)
    {
        var rows=await GetMemoryExplorerAsync(300,ct);return rows.GroupBy(x=>{var nl=x.IndexOf('\n');var body=nl>=0?x[(nl+1)..]:x;return body.Trim();},StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1).OrderByDescending(g=>g.Count()).Take(10).Select(g=>(g.Key[..Math.Min(80,g.Key.Length)],g.Count())).ToArray();
    }
    public async Task<bool> TryAcquireRuntimeLeaseAsync(string name,string ownerId,TimeSpan ttl,CancellationToken ct)
    {
        var now=DateTime.UtcNow;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="INSERT INTO runtime_leases(name,owner_id,expires_at,heartbeat_at) VALUES($n,$o,$e,$h) ON CONFLICT(name) DO UPDATE SET owner_id=excluded.owner_id,expires_at=excluded.expires_at,heartbeat_at=excluded.heartbeat_at WHERE runtime_leases.owner_id=$o OR runtime_leases.expires_at<$h; SELECT changes();";q.Parameters.AddWithValue("$n",name);q.Parameters.AddWithValue("$o",ownerId);q.Parameters.AddWithValue("$e",now.Add(ttl).ToString("O"));q.Parameters.AddWithValue("$h",now.ToString("O"));return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)>0;
    }
    public async Task<bool> RenewRuntimeLeaseAsync(string name,string ownerId,TimeSpan ttl,CancellationToken ct)
    {
        var now=DateTime.UtcNow;await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="UPDATE runtime_leases SET expires_at=$e,heartbeat_at=$h WHERE name=$n AND owner_id=$o; SELECT changes();";q.Parameters.AddWithValue("$n",name);q.Parameters.AddWithValue("$o",ownerId);q.Parameters.AddWithValue("$e",now.Add(ttl).ToString("O"));q.Parameters.AddWithValue("$h",now.ToString("O"));return Convert.ToInt32(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)>0;
    }
    public Task ReleaseRuntimeLeaseAsync(string name,string ownerId,CancellationToken ct)=>Exec("DELETE FROM runtime_leases WHERE name=$n AND owner_id=$o",ct,("$n",name),("$o",ownerId));
    public Task SaveBacktestRunAsync(PersistedBacktestRun run,CancellationToken ct)=>Exec("INSERT OR REPLACE INTO backtest_runs(id,strategy_id,strategy_version,symbol,status,completed_at,coverage_days,trades,out_of_sample_return,max_drawdown,sharpe) VALUES($i,$s,$v,$m,$t,$c,$d,$n,$r,$x,$h)",ct,("$i",run.Id),("$s",run.StrategyId),("$v",run.StrategyVersion),("$m",run.Symbol),("$t",run.Status),("$c",run.CompletedAtUtc.ToString("O")),("$d",run.CoverageDays),("$n",run.Trades),("$r",run.OutOfSampleReturn),("$x",run.MaxDrawdown),("$h",run.Sharpe));
    public async Task<IReadOnlyList<PersistedBacktestRun>> GetRecentBacktestRunsAsync(int limit,CancellationToken ct)
    {
        var list=new List<PersistedBacktestRun>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText="SELECT id,strategy_id,strategy_version,symbol,status,completed_at,coverage_days,trades,out_of_sample_return,max_drawdown,sharpe FROM backtest_runs ORDER BY completed_at DESC LIMIT $l";q.Parameters.AddWithValue("$l",Math.Clamp(limit,1,500));await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),DateTime.Parse(r.GetString(5),null,DateTimeStyles.RoundtripKind),r.GetInt32(6),r.GetInt32(7),r.GetDouble(8),r.GetDouble(9),r.GetDouble(10)));return list;
    }
    public async Task<bool> SaveEquitySnapshotAsync(PersistedEquitySnapshot value,CancellationToken ct)
    {
        if(value.Equity<0||value.AvailableBalance<0)throw new ArgumentOutOfRangeException(nameof(value),"Equity values must be non-negative finite decimals.");
        if(string.IsNullOrWhiteSpace(value.ProviderId)||string.IsNullOrWhiteSpace(value.Environment))throw new ArgumentException("Equity source identity is required.",nameof(value));
        var observed=value.ObservedAtUtc.ToUniversalTime();
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using(var latest=c.CreateCommand())
        {
            latest.Transaction=(SqliteTransaction)tx;
            latest.CommandText="SELECT observed_at,equity,available_balance FROM equity_snapshots WHERE provider_id=$p AND environment=$n ORDER BY observed_at DESC LIMIT 1";
            latest.Parameters.AddWithValue("$p",value.ProviderId.Trim());latest.Parameters.AddWithValue("$n",value.Environment.Trim());
            await using var reader=await latest.ExecuteReaderAsync(ct);
            if(await reader.ReadAsync(ct)&&DateTime.TryParse(reader.GetString(0),null,DateTimeStyles.RoundtripKind,out var previousAt)&&
               decimal.TryParse(reader.GetString(1),NumberStyles.Number,CultureInfo.InvariantCulture,out var previousEquity)&&
               decimal.TryParse(reader.GetString(2),NumberStyles.Number,CultureInfo.InvariantCulture,out var previousAvailable)&&
               observed-previousAt.ToUniversalTime()<TimeSpan.FromMinutes(5)&&previousEquity==value.Equity&&previousAvailable==value.AvailableBalance)
            {await tx.RollbackAsync(ct);return false;}
        }
        await using(var insert=c.CreateCommand())
        {
            insert.Transaction=(SqliteTransaction)tx;insert.CommandText="INSERT OR IGNORE INTO equity_snapshots(observed_at,equity,available_balance,environment,provider_id) VALUES($t,$e,$a,$n,$p)";
            insert.Parameters.AddWithValue("$t",observed.ToString("O"));insert.Parameters.AddWithValue("$e",value.Equity.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$a",value.AvailableBalance.ToString(CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$n",value.Environment.Trim());insert.Parameters.AddWithValue("$p",value.ProviderId.Trim());
            if(await insert.ExecuteNonQueryAsync(ct)==0){await tx.RollbackAsync(ct);return false;}
        }
        await using(var pruneAge=c.CreateCommand()){pruneAge.Transaction=(SqliteTransaction)tx;pruneAge.CommandText="DELETE FROM equity_snapshots WHERE observed_at<$t";pruneAge.Parameters.AddWithValue("$t",DateTime.UtcNow.AddDays(-90).ToString("O"));await pruneAge.ExecuteNonQueryAsync(ct);}
        await using(var pruneCount=c.CreateCommand()){pruneCount.Transaction=(SqliteTransaction)tx;pruneCount.CommandText="DELETE FROM equity_snapshots WHERE id IN (SELECT id FROM equity_snapshots WHERE provider_id=$p AND environment=$n ORDER BY observed_at DESC LIMIT -1 OFFSET 10000)";pruneCount.Parameters.AddWithValue("$p",value.ProviderId.Trim());pruneCount.Parameters.AddWithValue("$n",value.Environment.Trim());await pruneCount.ExecuteNonQueryAsync(ct);}
        await tx.CommitAsync(ct);return true;
    }
    public async Task<IReadOnlyList<PersistedEquitySnapshot>> GetRecentEquitySnapshotsAsync(DateTime sinceUtc,int limit,CancellationToken ct)
    {
        var list=new List<PersistedEquitySnapshot>();await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();
        q.CommandText="SELECT observed_at,equity,available_balance,environment,provider_id FROM equity_snapshots WHERE observed_at>=$s ORDER BY observed_at DESC,id DESC LIMIT $l";q.Parameters.AddWithValue("$s",sinceUtc.ToUniversalTime().ToString("O"));q.Parameters.AddWithValue("$l",Math.Clamp(limit,1,2000));
        await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))
        {
            if(!DateTime.TryParse(r.GetString(0),null,DateTimeStyles.RoundtripKind,out var time)||!decimal.TryParse(r.GetString(1),NumberStyles.Number,CultureInfo.InvariantCulture,out var equity)||!decimal.TryParse(r.GetString(2),NumberStyles.Number,CultureInfo.InvariantCulture,out var available)||equity<0||available<0)continue;
            list.Add(new(time.ToUniversalTime(),equity,available,r.GetString(3),r.GetString(4)));
        }
        list.Reverse();return list;
    }
    private static string? Token(string? value){if(string.IsNullOrWhiteSpace(value))return null;var safe=new string(value.Where(ch=>char.IsLetterOrDigit(ch)||ch is '-' or '_' or '.' or ':').Take(80).ToArray());return string.IsNullOrWhiteSpace(safe)?null:safe;}
    private async Task<TradingReviewQueueMutationResult> TryTradingReviewTransitionAsync(
        string requestId,
        TradingReviewQueueStatus expected,
        TradingReviewQueueStatus next,
        string? leaseOwner,
        string eventCode,
        string actorKind,
        CancellationToken ct,
        bool requireExpired=false,
        string reason="")
    {
        if(string.IsNullOrWhiteSpace(requestId)||!AllowedQueueTransition(expected,next)||!QueueToken(eventCode,120)||!QueueToken(actorKind,32))return new(false,"review.transition-invalid");
        var processing=expected is TradingReviewQueueStatus.Claimed or TradingReviewQueueStatus.Executing or TradingReviewQueueStatus.Reconciling&&next is not (TradingReviewQueueStatus.Revoked or TradingReviewQueueStatus.Expired);
        if(processing&&!QueueToken(leaseOwner,96))return new(false,"review.transition-invalid");
        var now=_utcNow().ToUniversalTime();
        try
        {
            await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
            await using(var read=QueueReadCommand(c,"WHERE queue.request_id=$request",1,0))
            {
                read.Transaction=tx;read.Parameters.AddWithValue("$request",requestId);
                await using var r=await read.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)){await tx.RollbackAsync(ct);return new(false,"review.transition-not-applied");}
                var item=ReadTradingReviewQueueItem(r);var requiresValidArtifact=next is TradingReviewQueueStatus.Approved or TradingReviewQueueStatus.Executing or TradingReviewQueueStatus.Reconciling or TradingReviewQueueStatus.Succeeded;
                if(requiresValidArtifact&&!item.ArtifactValid){await tx.RollbackAsync(ct);return new(false,"review.artifact-invalid");}
                if(item.Status!=expected||requireExpired&&(item.Artifact is null||item.Artifact.ExpiresAtUtc>now)||processing&&!string.Equals(item.LeaseOwner,leaseOwner,StringComparison.Ordinal)){await tx.RollbackAsync(ct);return new(false,"review.transition-not-applied");}
            }
            var keepLease=next is TradingReviewQueueStatus.Executing or TradingReviewQueueStatus.Reconciling;
            await using(var update=c.CreateCommand())
            {
                update.Transaction=tx;update.CommandText="UPDATE trading_review_execution_queue SET status=$next,lease_owner=CASE WHEN $keep=1 THEN lease_owner ELSE NULL END,lease_expires_at=CASE WHEN $keep=1 THEN lease_expires_at ELSE NULL END,last_code=$code,updated_at=$now WHERE request_id=$request AND status=$expected";
                AddParameters(update,("$next",next.ToString()),("$keep",keepLease?1:0),("$code",eventCode),("$now",DbInstant(now)),("$request",requestId),("$expected",expected.ToString()));
                if(await update.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"review.transition-not-applied");}
            }
            if(next==TradingReviewQueueStatus.Revoked)
            {
                await using var revoke=c.CreateCommand();revoke.Transaction=tx;revoke.CommandText="UPDATE trading_approval_requests SET revoked_at=$now WHERE request_id=$request AND revoked_at IS NULL AND consumed_at IS NULL";AddParameters(revoke,("$now",DbInstant(now)),("$request",requestId));
                if(await revoke.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"review.transition-not-applied");}
                await using var revokeReceipts=c.CreateCommand();revokeReceipts.Transaction=tx;revokeReceipts.CommandText="UPDATE trading_approval_receipts SET revoked_at=$now WHERE request_id=$request AND revoked_at IS NULL AND consumed_at IS NULL";AddParameters(revokeReceipts,("$now",DbInstant(now)),("$request",requestId));await revokeReceipts.ExecuteNonQueryAsync(ct);
            }
            var safeReason=SensitiveDataRedactor.ForLog(reason,240);await AppendTradingReviewEventAsync(c,tx,requestId,expected,next,eventCode,actorKind,now,ct,safeReason);await tx.CommitAsync(ct);return new(true,eventCode);
        }
        catch(SqliteException){return new(false,"review.transition-failed");}
    }

    private static bool AllowedQueueTransition(TradingReviewQueueStatus from,TradingReviewQueueStatus to)=>from switch
    {
        TradingReviewQueueStatus.Pending=>to is TradingReviewQueueStatus.Approved or TradingReviewQueueStatus.Rejected or TradingReviewQueueStatus.Revoked or TradingReviewQueueStatus.Expired or TradingReviewQueueStatus.ArtifactInvalid or TradingReviewQueueStatus.PolicyBlocked,
        TradingReviewQueueStatus.Approved=>to is TradingReviewQueueStatus.Claimed or TradingReviewQueueStatus.Revoked or TradingReviewQueueStatus.Expired or TradingReviewQueueStatus.ArtifactInvalid or TradingReviewQueueStatus.StrategyInvalid or TradingReviewQueueStatus.MarketStale or TradingReviewQueueStatus.PolicyBlocked or TradingReviewQueueStatus.FailedTerminal,
        TradingReviewQueueStatus.Claimed=>to is TradingReviewQueueStatus.Executing or TradingReviewQueueStatus.Revoked or TradingReviewQueueStatus.Expired or TradingReviewQueueStatus.ArtifactInvalid or TradingReviewQueueStatus.StrategyInvalid or TradingReviewQueueStatus.MarketStale or TradingReviewQueueStatus.PolicyBlocked or TradingReviewQueueStatus.FailedTerminal,
        TradingReviewQueueStatus.Executing=>to is TradingReviewQueueStatus.Reconciling or TradingReviewQueueStatus.Succeeded or TradingReviewQueueStatus.ArtifactInvalid or TradingReviewQueueStatus.StrategyInvalid or TradingReviewQueueStatus.MarketStale or TradingReviewQueueStatus.PolicyBlocked or TradingReviewQueueStatus.FailedTerminal,
        TradingReviewQueueStatus.Reconciling=>to is TradingReviewQueueStatus.Succeeded or TradingReviewQueueStatus.ArtifactInvalid or TradingReviewQueueStatus.StrategyInvalid or TradingReviewQueueStatus.MarketStale or TradingReviewQueueStatus.PolicyBlocked or TradingReviewQueueStatus.FailedTerminal,
        _=>false
    };
    private static bool AllowedProcessorQueueTransition(TradingReviewQueueStatus from,TradingReviewQueueStatus to)
        =>AllowedQueueTransition(from,to)&&to is not (TradingReviewQueueStatus.Approved or TradingReviewQueueStatus.Rejected or TradingReviewQueueStatus.Revoked or TradingReviewQueueStatus.Expired or TradingReviewQueueStatus.Claimed);

    private async Task<AutomaticExecutionClaimResult> TryClaimAutomaticAsync(string executionId,string leaseOwner,TimeSpan leaseLifetime,AutomaticExecutionQueueStatus expected,AutomaticExecutionQueueStatus next,string code,CancellationToken ct)
    {
        if(!QueueToken(executionId,120)||!QueueToken(leaseOwner,96)||leaseLifetime<=TimeSpan.Zero||leaseLifetime>TimeSpan.FromMinutes(5))return new(false,"automatic.claim-invalid",0);var now=_utcNow().ToUniversalTime();
        await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await EnableQueuePragmasAsync(c,ct);await using var tx=(SqliteTransaction)await c.BeginTransactionAsync(ct);
        var current=await GetAutomaticExecutionInTransactionAsync(c,tx,executionId,ct);if(current is null||current.Status!=expected||!current.ArtifactValid||expected==AutomaticExecutionQueueStatus.RiskApproved&&!current.RiskReceiptValid){await tx.RollbackAsync(ct);return new(false,"automatic.not-claimable",current?.AttemptCount??0);}
        var attempts=checked(current.AttemptCount+1);await using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="UPDATE automatic_execution_queue SET status=$next,attempt_count=$attempts,lease_owner=$owner,lease_expires_at=$lease,last_code=$code,updated_at=$now WHERE execution_id=$id AND status=$expected";AddParameters(q,("$next",next.ToString()),("$attempts",attempts),("$owner",leaseOwner),("$lease",DbInstant(now.Add(leaseLifetime))),("$code",code),("$now",DbInstant(now)),("$id",executionId),("$expected",expected.ToString()));if(await q.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return new(false,"automatic.not-claimable",current.AttemptCount);}}
        await AppendAutomaticExecutionEventAsync(c,tx,executionId,expected,next,code,"processor",now,ct);await tx.CommitAsync(ct);return new(true,code,attempts);
    }

    private static bool AllowedAutomaticTransition(AutomaticExecutionQueueStatus from,AutomaticExecutionQueueStatus to)=>from switch
    {
        AutomaticExecutionQueueStatus.Claimed=>to is AutomaticExecutionQueueStatus.Executing or AutomaticExecutionQueueStatus.CapabilityUnavailable or AutomaticExecutionQueueStatus.MarketStale or AutomaticExecutionQueueStatus.StrategyInvalid or AutomaticExecutionQueueStatus.ArtifactInvalid or AutomaticExecutionQueueStatus.PolicyBlocked or AutomaticExecutionQueueStatus.FailedTerminal,
        AutomaticExecutionQueueStatus.Executing=>to is AutomaticExecutionQueueStatus.Succeeded or AutomaticExecutionQueueStatus.UnknownOutcome or AutomaticExecutionQueueStatus.FailedTerminal,
        AutomaticExecutionQueueStatus.Reconciling=>to is AutomaticExecutionQueueStatus.Succeeded or AutomaticExecutionQueueStatus.UnknownOutcome or AutomaticExecutionQueueStatus.FailedTerminal or AutomaticExecutionQueueStatus.ArtifactInvalid,
        _=>false
    };

    private static SqliteCommand AutomaticQueueReadCommand(SqliteConnection connection,string where,int limit)
    {
        var q=connection.CreateCommand();q.CommandText=$"SELECT execution_id,correlation_id,contract_version,artifact_bytes,artifact_hash,intent_hash,risk_receipt_bytes,risk_receipt_hash,provider_id,environment,strategy_id,strategy_version,market_collected_at,market_data_version,created_at,expires_at,status,attempt_count,lease_owner,lease_expires_at,last_code,updated_at FROM automatic_execution_queue {where} ORDER BY created_at,execution_id LIMIT $limit";q.Parameters.AddWithValue("$limit",limit);return q;
    }

    private static async Task<PersistedAutomaticExecution?> GetAutomaticExecutionInTransactionAsync(SqliteConnection c,SqliteTransaction tx,string executionId,CancellationToken ct)
    {
        await using var q=AutomaticQueueReadCommand(c,"WHERE execution_id=$id",1);q.Transaction=tx;q.Parameters.AddWithValue("$id",executionId);await using var r=await q.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadAutomaticExecution(r):null;
    }

    private static PersistedAutomaticExecution ReadAutomaticExecution(SqliteDataReader r)
    {
        var bytes=(byte[])r.GetValue(3);var valid=DurableExecutionArtifactCanonicalizerV2.TryDeserializeCanonical(bytes,out var artifact,out _);var artifactHash=r.GetString(4);var intentHash=r.GetString(5);
        if(valid&&artifact is not null){var hashes=DurableExecutionArtifactCanonicalizerV2.ComputeHashes(artifact);valid=FixedHashEquals(artifactHash,hashes.ArtifactHash)&&FixedHashEquals(intentHash,hashes.IntentHash)&&r.GetString(1)==artifact.CorrelationId&&r.GetInt32(2)==artifact.ContractVersion&&r.GetString(8)==artifact.ProviderId&&r.GetString(9)==artifact.Environment&&r.GetString(10)==artifact.StrategyId&&r.GetString(11)==artifact.StrategyVersion&&Instant(r.GetString(12))==artifact.MarketCollectedAtUtc&&r.GetString(13)==artifact.MarketDataVersion&&Instant(r.GetString(14))==artifact.CreatedAtUtc&&Instant(r.GetString(15))==artifact.ExpiresAtUtc;}
        DeterministicRiskReceipt? receipt=null;var receiptValid=false;if(!r.IsDBNull(6)&&!r.IsDBNull(7)){var receiptBytes=(byte[])r.GetValue(6);receiptValid=DurableExecutionArtifactCanonicalizerV2.TryDeserializeRiskReceipt(receiptBytes,out receipt)&&FixedHashEquals(r.GetString(7),DurableReviewArtifactCanonicalizer.Sha256Hex(receiptBytes));}
        var status=Enum.TryParse<AutomaticExecutionQueueStatus>(r.GetString(16),out var parsed)&&Enum.IsDefined(parsed)?parsed:AutomaticExecutionQueueStatus.ArtifactInvalid;
        return new(r.GetString(0),valid?artifact:null,valid?bytes:null,valid?artifactHash:string.Empty,valid?intentHash:string.Empty,receiptValid?receipt:null,status,r.GetInt32(17),r.IsDBNull(18)?null:r.GetString(18),NullableInstant(r,19),r.GetString(20),Instant(r.GetString(21)),valid,receiptValid);
    }

    private static bool ValidAutomaticRiskReceipt(DeterministicRiskReceipt receipt,PersistedAutomaticExecution item,DateTimeOffset now)
        =>item.ArtifactValid&&item.Artifact is not null&&receipt.Approved&&receipt.RevokedAtUtc is null&&QueueToken(receipt.ReceiptId,120)&&receipt.IssuedAtUtc.Offset==TimeSpan.Zero&&receipt.ExpiresAtUtc.Offset==TimeSpan.Zero&&receipt.IssuedAtUtc<=now&&receipt.ExpiresAtUtc>now&&receipt.ExpiresAtUtc>receipt.IssuedAtUtc&&receipt.ExpiresAtUtc-receipt.IssuedAtUtc<=TimeSpan.FromMinutes(2)&&string.Equals(receipt.CorrelationId,item.Artifact.CorrelationId,StringComparison.Ordinal)&&FixedHashEquals(receipt.IntentHash,item.IntentHash)&&receipt.ArtifactHash is not null&&FixedHashEquals(receipt.ArtifactHash,item.ArtifactHash);

    private static async Task AppendAutomaticExecutionEventAsync(SqliteConnection c,SqliteTransaction tx,string executionId,AutomaticExecutionQueueStatus? from,AutomaticExecutionQueueStatus to,string code,string actor,DateTimeOffset now,CancellationToken ct)
    {
        await using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT INTO automatic_execution_events(execution_id,sequence,occurred_at,from_status,to_status,event_code,actor_kind) SELECT $id,COALESCE(MAX(sequence),0)+1,$at,$from,$to,$code,$actor FROM automatic_execution_events WHERE execution_id=$id";AddParameters(q,("$id",executionId),("$at",DbInstant(now)),("$from",from?.ToString()),("$to",to.ToString()),("$code",code),("$actor",actor));if(await q.ExecuteNonQueryAsync(ct)!=1)throw new SqliteException("automatic event append failed",1);
    }

    private static SqliteCommand QueueReadCommand(SqliteConnection connection,string where,int limit,int offset)
    {
        var command=connection.CreateCommand();command.CommandText=$"""
            SELECT queue.request_id,queue.contract_version,queue.artifact_bytes,queue.artifact_hash,queue.intent_hash,
                   queue.provider_id,queue.environment,queue.strategy_id,queue.strategy_version,queue.market_collected_at,
                   queue.market_data_version,queue.created_at,queue.expires_at,queue.status,queue.attempt_count,
                   queue.lease_owner,queue.lease_expires_at,queue.last_code,queue.updated_at,request.intent_hash
            FROM trading_review_execution_queue queue
            JOIN trading_approval_requests request ON request.request_id=queue.request_id
            {where}
            ORDER BY queue.created_at,queue.request_id
            LIMIT $limit OFFSET $offset
            """;command.Parameters.AddWithValue("$limit",limit);command.Parameters.AddWithValue("$offset",offset);return command;
    }

    private static PersistedTradingReviewQueueItem ReadTradingReviewQueueItem(SqliteDataReader reader)
    {
        var bytes=(byte[])reader.GetValue(2);var storedArtifactHash=reader.GetString(3);var storedIntentHash=reader.GetString(4);var valid=DurableReviewArtifactCanonicalizer.TryDeserializeCanonical(bytes,out var artifact,out _);
        if(valid&&artifact is not null)
        {
            var hashes=DurableReviewArtifactCanonicalizer.ComputeHashes(artifact);
            valid=FixedHashEquals(storedArtifactHash,hashes.ArtifactHash)&&FixedHashEquals(storedIntentHash,hashes.IntentHash)&&FixedHashEquals(reader.GetString(19),hashes.IntentHash)&&
                  reader.GetInt32(1)==artifact.ContractVersion&&reader.GetString(5)==artifact.ProviderId&&reader.GetString(6)==artifact.Environment&&reader.GetString(7)==artifact.StrategyId&&reader.GetString(8)==artifact.StrategyVersion&&
                  Instant(reader.GetString(9))==artifact.MarketCollectedAtUtc&&reader.GetString(10)==artifact.MarketDataVersion&&Instant(reader.GetString(11))==artifact.CreatedAtUtc&&Instant(reader.GetString(12))==artifact.ExpiresAtUtc;
        }
        var parsedStatus=Enum.TryParse<TradingReviewQueueStatus>(reader.GetString(13),out var status)&&Enum.IsDefined(status)?status:TradingReviewQueueStatus.ArtifactInvalid;
        var lastCode=reader.GetString(17);if(!QueueToken(lastCode,120))lastCode="review.diagnostic-invalid";
        return new(reader.GetString(0),valid?artifact:null,valid?bytes:null,valid?storedArtifactHash:string.Empty,valid?storedIntentHash:string.Empty,parsedStatus,reader.GetInt32(14),reader.IsDBNull(15)?null:reader.GetString(15),NullableInstant(reader,16),lastCode,Instant(reader.GetString(18)),valid,"review.artifact-"+(valid?"valid":"invalid"));
    }

    private static async Task AppendTradingReviewEventAsync(SqliteConnection connection,SqliteTransaction transaction,string requestId,TradingReviewQueueStatus? from,TradingReviewQueueStatus to,string eventCode,string actorKind,DateTimeOffset occurredAt,CancellationToken ct,string reason="")
    {
        await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="""
            INSERT INTO trading_review_queue_events(request_id,sequence,occurred_at,from_status,to_status,event_code,actor_kind,reason)
            SELECT $request,COALESCE(MAX(sequence),0)+1,$at,$from,$to,$code,$actor,$reason
            FROM trading_review_queue_events WHERE request_id=$request
            """;AddParameters(command,("$request",requestId),("$at",DbInstant(occurredAt)),("$from",from?.ToString()),("$to",to.ToString()),("$code",eventCode),("$actor",actorKind),("$reason",reason));
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new SqliteException("review event append failed",1);
    }

    private static async Task EnableQueuePragmasAsync(SqliteConnection connection,CancellationToken ct){await using var command=connection.CreateCommand();command.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";await command.ExecuteNonQueryAsync(ct);}
    private static void EnsureColumn(SqliteConnection connection,string table,string column,string definition)
    {
        using var read=connection.CreateCommand();read.CommandText=$"PRAGMA table_info({table})";using var reader=read.ExecuteReader();while(reader.Read())if(string.Equals(reader.GetString(1),column,StringComparison.OrdinalIgnoreCase))return;reader.Close();using var alter=connection.CreateCommand();alter.CommandText=$"ALTER TABLE {table} ADD COLUMN {column} {definition}";alter.ExecuteNonQuery();
    }
    private static void AddParameters(SqliteCommand command,params (string Name,object? Value)[] values){foreach(var value in values)command.Parameters.AddWithValue(value.Name,value.Value??DBNull.Value);}
    private static bool FixedHashEquals(string left,string right)
    {
        if(left.Length!=64||right.Length!=64)return false;
        try{return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left),Convert.FromHexString(right));}catch(FormatException){return false;}
    }
    private static bool QueueToken(string? value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=maximum&&value.All(character=>char.IsAsciiLetterOrDigit(character)||character is '-' or '_' or '.' or ':' or '#');
    private static bool QueueIdentity(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=120&&value.All(character=>!char.IsControl(character));
    private static bool ValidApprovalRequest(TradingApprovalRequest value)=>!string.IsNullOrWhiteSpace(value.RequestId)&&Enum.IsDefined(value.Mode)&&!string.IsNullOrWhiteSpace(value.CorrelationId)&&!string.IsNullOrWhiteSpace(value.IntentHash)&&!string.IsNullOrWhiteSpace(value.UserId)&&!string.IsNullOrWhiteSpace(value.DeviceId)&&!string.IsNullOrWhiteSpace(value.SessionId)&&value.ExpiresAtUtc>value.IssuedAtUtc;
    private static bool ValidApprovalReceipt(TradingApprovalReceipt value)=>!string.IsNullOrWhiteSpace(value.ReceiptId)&&!string.IsNullOrWhiteSpace(value.CorrelationId)&&!string.IsNullOrWhiteSpace(value.IntentHash)&&!string.IsNullOrWhiteSpace(value.UserId)&&!string.IsNullOrWhiteSpace(value.DeviceId)&&!string.IsNullOrWhiteSpace(value.SessionId)&&value.ExpiresAtUtc>value.IssuedAtUtc;
    private static bool ValidApprovalConsumption(TradingApprovalConsumption value)=>!string.IsNullOrWhiteSpace(value.RequestId)&&!string.IsNullOrWhiteSpace(value.ReceiptId)&&!string.IsNullOrWhiteSpace(value.CorrelationId)&&!string.IsNullOrWhiteSpace(value.IntentHash)&&!string.IsNullOrWhiteSpace(value.UserId)&&!string.IsNullOrWhiteSpace(value.DeviceId)&&!string.IsNullOrWhiteSpace(value.SessionId)&&(value.ArtifactHash is null||value.ArtifactHash.Length==64);
    private static void AddApprovalConsumptionParameters(SqliteCommand command,TradingApprovalConsumption value,DateTimeOffset consumedAtUtc){command.Parameters.AddWithValue("$now",DbInstant(consumedAtUtc));command.Parameters.AddWithValue("$receipt",value.ReceiptId);command.Parameters.AddWithValue("$request",value.RequestId);command.Parameters.AddWithValue("$correlation",value.CorrelationId);command.Parameters.AddWithValue("$hash",value.IntentHash);command.Parameters.AddWithValue("$artifactHash",value.ArtifactHash is null?DBNull.Value:value.ArtifactHash);command.Parameters.AddWithValue("$user",value.UserId);command.Parameters.AddWithValue("$device",value.DeviceId);command.Parameters.AddWithValue("$session",value.SessionId);}
    private static string DbInstant(DateTimeOffset value)=>value.ToUniversalTime().ToString("O");
    private static object? DbInstant(DateTimeOffset? value)=>value is null?null:DbInstant(value.Value);
    private static DateTimeOffset Instant(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? NullableInstant(SqliteDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:Instant(reader.GetString(ordinal));
    private static string? NullableText(SqliteDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:reader.GetString(ordinal);
    private static bool ValidSha256(string? value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);
    private static bool SafeMacroIdentity(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=160&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '.' or '_' or '-' or '@');
    private static PersistedMacroObservation ReadMacro(SqliteDataReader reader,string indicator,DateTimeOffset observed,int offset)=>new(indicator,observed,reader.GetInt32(offset),reader.GetString(offset+1),reader.GetString(offset+2),reader.GetString(offset+3),decimal.Parse(reader.GetString(offset+4),CultureInfo.InvariantCulture),reader.GetString(offset+5),reader.GetString(offset+6),Instant(reader.GetString(offset+7)),NullableInstant(reader,offset+8),reader.GetString(offset+9),NullableText(reader,offset+10),NullableText(reader,offset+11));
    private async Task<int> ExecRows(string sql,CancellationToken ct,params (string,object?)[] args){await using var c=new SqliteConnection(_cs);await c.OpenAsync(ct);await using var q=c.CreateCommand();q.CommandText=sql;foreach(var a in args)q.Parameters.AddWithValue(a.Item1,a.Item2??DBNull.Value);return await q.ExecuteNonQueryAsync(ct);}
    private async Task Exec(string sql,CancellationToken ct,params (string,object?)[] args){_ = await ExecRows(sql,ct,args);}
}

public sealed record PersistedRuntimeAuditEvent(string Id,DateTime TimeUtc,string Category,string Source,string? CorrelationId,string Status,string Summary);
public sealed record PersistedEquitySnapshot(DateTime ObservedAtUtc,decimal Equity,decimal AvailableBalance,string Environment,string ProviderId);
public sealed record PersistedRuntimeSkillCall(string Id,DateTime OccurredAtUtc,string Skill,string Status,long DurationMs,string? Mode,bool? RemoteLlmUsed,int? Tokens,decimal? CostUsd,int? ContextCharacters=null,int? InputTokens=null,int? OutputTokens=null,bool? CacheHit=null,string? LlmOutcome=null,string? TokenSource=null);
public sealed record PersistedAgentActivity(string RoleId,string Status,DateTime OccurredAtUtc,string Activity,string Mode);
public sealed record PersistedTradingApprovalSummary(string RequestId,DateTimeOffset CreatedAtUtc,DateTimeOffset ExpiresAtUtc,DateTimeOffset? RevokedAtUtc,DecisionPlan? Decision,IndependentRiskReview? Risk);
public sealed record TradingReviewQueueMutationResult(bool Succeeded,string Code);
public sealed record TradingReviewQueueClaimResult(bool Claimed,string Code,int AttemptCount);
public sealed record PersistedTradingReviewQueueItem(
    string RequestId,
    DurableReviewExecutionArtifactV1? Artifact,
    byte[]? CanonicalArtifactBytes,
    string ArtifactHash,
    string IntentHash,
    TradingReviewQueueStatus Status,
    int AttemptCount,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAtUtc,
    string LastCode,
    DateTimeOffset UpdatedAtUtc,
    bool ArtifactValid,
    string DiagnosticCode);
public sealed record PersistedTradingReviewQueueEvent(string RequestId,int Sequence,DateTimeOffset OccurredAtUtc,TradingReviewQueueStatus? FromStatus,TradingReviewQueueStatus ToStatus,string EventCode,string ActorKind,string Reason);
public sealed record AutomaticExecutionMutationResult(bool Succeeded,string Code);
public sealed record AutomaticExecutionClaimResult(bool Claimed,string Code,int AttemptCount);
public sealed record PersistedAutomaticExecution(
    string ExecutionId,
    DurableExecutionArtifactV2? Artifact,
    byte[]? CanonicalArtifactBytes,
    string ArtifactHash,
    string IntentHash,
    DeterministicRiskReceipt? RiskReceipt,
    AutomaticExecutionQueueStatus Status,
    int AttemptCount,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAtUtc,
    string LastCode,
    DateTimeOffset UpdatedAtUtc,
    bool ArtifactValid,
    bool RiskReceiptValid);
public sealed record PersistedAutomaticExecutionEvent(string ExecutionId,int Sequence,DateTimeOffset OccurredAtUtc,AutomaticExecutionQueueStatus? FromStatus,AutomaticExecutionQueueStatus ToStatus,string EventCode,string ActorKind);
internal sealed record AutomaticExecutionObservationCandidate(PersistedAutomaticExecution Item,long EventCursor);
public sealed record LegacyIntentIsolationMutationResult(bool Succeeded,string Code);
public sealed record PersistedLegacyIntentIsolation(string ClientOrderId,string SourceStatus,string ProjectionStatus,string ReasonCode,DateTimeOffset IsolatedAtUtc);
public sealed record PersistedLegacyIntentIsolationEvent(string ClientOrderId,int Sequence,DateTimeOffset OccurredAtUtc,string FromStatus,string ToStatus,string EventCode);
public sealed record PersistedAgentHandoff(string Id,DateTime OccurredAtUtc,string SourceRoleId,string TargetRoleId,string Result);
public sealed record AgentOperationsEvidence(IReadOnlyList<PersistedAgentActivity> Activities,IReadOnlyList<PersistedAgentHandoff> Handoffs,DateTime UpdatedAtUtc);

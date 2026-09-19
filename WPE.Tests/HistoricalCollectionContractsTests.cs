using Microsoft.Data.Sqlite;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;

namespace WPE.Tests;

public sealed class HistoricalCollectionContractsTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-history-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");
    private static readonly DateTimeOffset Now=new(2026,7,21,12,0,0,TimeSpan.Zero);

    [Fact]
    public async Task OrdersAreBoundedPaginatedAndComeFromSqlite()
    {
        await InitializeAsync();
        for(var i=0;i<3;i++)await ExecuteAsync("INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,avg_price,status,occurred_at) VALUES($c,$o,'BTCUSDT','Long','OPEN',0,'1','100','FILLED',$t)",( "$c",$"cycle-{i}"),("$o",$"order-{i}"),("$t",Now.AddMinutes(-i).ToString("O")));
        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);
        var first=await store.ReadOrdersAsync(new(2));var second=await store.ReadOrdersAsync(new(2,first.NextCursor));
        var wrongCollection=await store.ReadEquityAsync(new(2,first.NextCursor));
        Assert.Equal(RuntimeCollectionState.Available,first.State);Assert.Equal(2,first.Items.Count);Assert.NotNull(first.NextCursor);Assert.Single(second.Items);Assert.Matches("^cycle#[A-F0-9]{12}$",first.Items[0].CorrelationId!);Assert.Matches("^order#[A-F0-9]{12}$",first.Items[0].ClientOrderId!);Assert.Equal("local-agent-sqlite",first.Source);Assert.Equal(RuntimeCollectionState.Error,wrongCollection.State);Assert.Empty(wrongCollection.Items);var json=System.Text.Json.JsonSerializer.Serialize(first);Assert.DoesNotContain("cycle-0",json,StringComparison.Ordinal);Assert.DoesNotContain("order-0",json,StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoricalSnapshotStoreRefreshesChangedCollectionsAndPeriodicallyRevalidatesUnchangedOnes()
    {
        await InitializeAsync();
        var clock=Now;
        await ExecuteAsync("INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,avg_price,status,occurred_at) VALUES('cycle-1','order-1','BTCUSDT','Long','OPEN',0,'1','100','FILLED',$t)",("$t",Now.ToString("O")));
        await ExecuteAsync("INSERT INTO equity_snapshots(observed_at,equity,available_balance,environment,provider_id) VALUES($t,'10','9','Testnet','binance')",("$t",Now.ToString("O")));
        var store=new RuntimeHistoricalCollectionsSnapshotStore(DatabasePath,()=>clock);

        await store.RefreshAsync(default);
        Assert.Single(store.Read().Orders.Items);
        Assert.Equal(10m,Assert.Single(store.Read().Equity.Items).Equity);

        // Production historical writers append/replace with a new row/time. This direct in-place edit deliberately
        // keeps the lightweight vector unchanged so the test can prove unchanged collections are not fully reread.
        await ExecuteAsync("UPDATE equity_snapshots SET equity='not-decimal' WHERE observed_at=$t",("$t",Now.ToString("O")));
        await ExecuteAsync("INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,avg_price,status,occurred_at) VALUES('cycle-2','order-2','ETHUSDT','Long','OPEN',0,'1','200','FILLED',$t)",("$t",Now.AddSeconds(1).ToString("O")));
        clock=Now.AddSeconds(2);
        await store.RefreshAsync(default);

        Assert.Equal(2,store.Read().Orders.Items.Count);
        Assert.Equal(RuntimeCollectionState.Available,store.Read().Equity.State);
        Assert.Equal(10m,Assert.Single(store.Read().Equity.Items).Equity);

        clock=Now.Add(RuntimeHistoricalCollectionsSnapshotStore.FullRevalidationInterval).AddMilliseconds(1);
        await store.RefreshAsync(default);
        Assert.Equal(RuntimeCollectionState.Error,store.Read().Equity.State);
        Assert.Empty(store.Read().Equity.Items);
    }

    [Fact]
    public async Task HistoricalSnapshotStoreRetriesErrorEvenWhenChangeVectorIsUnchanged()
    {
        await InitializeAsync();
        var clock=Now;
        await ExecuteAsync("INSERT INTO equity_snapshots(observed_at,equity,available_balance,environment,provider_id) VALUES($t,'not-decimal','9','Testnet','binance')",("$t",Now.ToString("O")));
        var store=new RuntimeHistoricalCollectionsSnapshotStore(DatabasePath,()=>clock);

        await store.RefreshAsync(default);
        Assert.Equal(RuntimeCollectionState.Error,store.Read().Equity.State);

        await ExecuteAsync("UPDATE equity_snapshots SET equity='10' WHERE observed_at=$t",("$t",Now.ToString("O")));
        clock=Now.AddSeconds(2);
        await store.RefreshAsync(default);

        Assert.Equal(RuntimeCollectionState.Available,store.Read().Equity.State);
        Assert.Equal(10m,Assert.Single(store.Read().Equity.Items).Equity);
    }

    [Fact]
    public async Task HistoricalChangeVectorRoutesOnlyItsOwningCollectionGroups()
    {
        await InitializeAsync();
        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);
        var baseline=await store.ReadChangeVectorAsync();

        await ExecuteAsync("INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,status,occurred_at) VALUES('cycle-vector','order-vector','BTCUSDT','Long','OPEN',0,'1','FILLED',$t)",("$t",Now.ToString("O")));
        var orderChange=await store.ReadChangeVectorAsync();
        Assert.NotEqual(baseline.Orders,orderChange.Orders);
        Assert.Equal(baseline.Equity,orderChange.Equity);
        Assert.Equal(baseline.PostTradeReviews,orderChange.PostTradeReviews);
        Assert.Equal(baseline.Reconciliations,orderChange.Reconciliations);

        await ExecuteAsync("INSERT INTO automatic_execution_queue(execution_id,correlation_id,status,last_code,attempt_count,market_collected_at,market_data_version,updated_at) VALUES('execution-vector','cycle-vector','Succeeded','automatic.succeeded',1,$t,'market-v1',$t)",("$t",Now.AddSeconds(1).ToString("O")));
        var executionChange=await store.ReadChangeVectorAsync();
        Assert.Equal(orderChange.Orders,executionChange.Orders);
        Assert.NotEqual(orderChange.PostTradeReviews,executionChange.PostTradeReviews);
        Assert.Equal(orderChange.Reconciliations,executionChange.Reconciliations);

        await ExecuteAsync("INSERT INTO position_reconciliation_audits(report_id,schema,observed_at,evaluated_at,state,allows_risk_increase,canonical_sha256,canonical_bytes) VALUES('recon-vector','schema/1.0',$t,$t,'Confirmed',1,$hash,X'01')",("$t",Now.AddSeconds(2).ToString("O")),("$hash",new string('a',64)));
        var reconciliationChange=await store.ReadChangeVectorAsync();
        Assert.Equal(executionChange.PostTradeReviews,reconciliationChange.PostTradeReviews);
        Assert.NotEqual(executionChange.Reconciliations,reconciliationChange.Reconciliations);

        await ExecuteAsync("CREATE TABLE model_off_canonical_audits(output_id TEXT PRIMARY KEY,cycle_id TEXT,schema TEXT,template_version TEXT,canonical_sha256 TEXT,status TEXT,output_kind TEXT,sources_json TEXT,as_of_utc TEXT,recorded_at_utc TEXT,canonical_bytes BLOB); INSERT INTO model_off_canonical_audits VALUES('output-vector','cycle-vector','schema','template',$hash,'succeeded','strategy','[]',$t,$t,X'01')",("$hash",new string('b',64)),("$t",Now.AddSeconds(3).ToString("O")));
        var evidenceChange=await store.ReadChangeVectorAsync();
        Assert.NotEqual(reconciliationChange.PostTradeReviews,evidenceChange.PostTradeReviews);
        Assert.Equal(reconciliationChange.Reconciliations,evidenceChange.Reconciliations);
    }

    [Fact]
    public async Task PageSizeIsCappedAtOneHundred()
    {
        await InitializeAsync();
        for(var i=0;i<101;i++)await ExecuteAsync("INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,status,occurred_at) VALUES($c,$o,'BTCUSDT','Long','OPEN',0,'1','FILLED',$t)",( "$c",$"cycle-{i}"),("$o",$"order-{i}"),("$t",Now.ToString("O")));
        var page=await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadOrdersAsync(new(500));
        Assert.Equal(100,page.Items.Count);Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task TamperedExpiredAndCrossCollectionCursorsFailClosed()
    {
        await InitializeAsync();for(var i=0;i<2;i++)await ExecuteAsync("INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,status,occurred_at) VALUES($c,$o,'BTCUSDT','Long','OPEN',0,'1','FILLED',$t)",( "$c",$"cycle-{i}"),("$o",$"order-{i}"),("$t",Now.ToString("O")));
        var clock=Now;var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>clock);var first=await store.ReadOrdersAsync(new(1));var cursor=Assert.IsType<string>(first.NextCursor);
        var tampered=cursor[..^2]+(cursor[^2]=='A'?'B':'A')+cursor[^1];
        var tamperedPage=await store.ReadOrdersAsync(new(1,tampered));var crossCollection=await store.ReadEquityAsync(new(1,cursor));
        clock=Now.Add(HistoricalCollectionRequestV1.CursorLifetime).AddSeconds(1);var expired=await store.ReadOrdersAsync(new(1,cursor));
        Assert.Equal(RuntimeCollectionState.Error,tamperedPage.State);Assert.Empty(tamperedPage.Items);Assert.Null(tamperedPage.NextCursor);
        Assert.Equal(RuntimeCollectionState.Error,crossCollection.State);Assert.Empty(crossCollection.Items);Assert.Null(crossCollection.NextCursor);
        Assert.Equal(RuntimeCollectionState.Error,expired.State);Assert.Empty(expired.Items);Assert.Null(expired.NextCursor);
    }

    [Fact]
    public async Task InvalidCursorAndStaleDataFailClosedWithoutItems()
    {
        await InitializeAsync();await ExecuteAsync("INSERT INTO equity_snapshots(observed_at,equity,available_balance,environment,provider_id) VALUES($t,'10','9','Testnet','binance')",("$t",Now.AddDays(-2).ToString("O")));
        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);
        var invalid=await store.ReadOrdersAsync(new(10,"not-base64"));var stale=await store.ReadEquityAsync(new());
        Assert.Equal(RuntimeCollectionState.Error,invalid.State);Assert.Empty(invalid.Items);Assert.Equal(RuntimeCollectionState.Stale,stale.State);Assert.Empty(stale.Items);
    }

    [Fact]
    public async Task UnsupportedStaleAndReadErrorNeverReturnItemsOrCursor()
    {
        Directory.CreateDirectory(_directory);await ExecuteAsync("CREATE TABLE equity_snapshots(id INTEGER PRIMARY KEY,observed_at TEXT,equity TEXT,available_balance TEXT,environment TEXT,provider_id TEXT); INSERT INTO equity_snapshots VALUES(1,$t,'not-decimal','9','Testnet','binance')",("$t",Now.ToString("O")));
        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);var unsupported=await store.ReadOrdersAsync(new());var error=await store.ReadEquityAsync(new());
        await ExecuteAsync("UPDATE equity_snapshots SET observed_at=$t,equity='10'",("$t",Now.AddDays(-2).ToString("O")));var stale=await store.ReadEquityAsync(new());
        Assert.Equal(RuntimeCollectionState.Unsupported,unsupported.State);Assert.Empty(unsupported.Items);Assert.Null(unsupported.NextCursor);
        Assert.Equal(RuntimeCollectionState.Error,error.State);Assert.Empty(error.Items);Assert.Null(error.NextCursor);
        Assert.Equal(RuntimeCollectionState.Stale,stale.State);Assert.Empty(stale.Items);Assert.Null(stale.NextCursor);
    }

    [Fact]
    public async Task PostTradeAndReconciliationHistoryProjectDurableAccountingWithoutCanonicalPayloads()
    {
        await InitializeAsync();
        await ExecuteAsync("INSERT INTO trade_outcomes(client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,fees,fee_basis,fee_rate,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,funding_amount,funding_basis,net_pnl,return_pct,closed_at,strategy_id,strategy_version,attribution_basis) VALUES('close-1','cycle-1','BTCUSDT','Long','100','110','1','.05','exchange-reported-usdt','0','.10','.20','.30','intent-expected-vs-fill','1.25','exchange-reported-window','11.20','.112',$t,'trend-alpha','2.1.0','automatic-artifact')",("$t",Now.AddYears(-2).ToString("O")));
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[]{1})).ToLowerInvariant();
        foreach(var row in new[]{("position_reconciliation_audits","Confirmed",1),("protection_reconciliation_audits","Protected",1),("external_position_isolation_audits","Clear",1)})
            await ExecuteAsync($"INSERT INTO {row.Item1}(report_id,schema,observed_at,evaluated_at,state,allows_risk_increase,canonical_sha256,canonical_bytes) VALUES($id,'schema/1.0',$t,$t,$state,$allows,$hash,X'01')",("$id",row.Item1),("$t",Now.ToString("O")),("$state",row.Item2),("$allows",row.Item3),("$hash",hash));

        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);
        var reviews=await store.ReadPostTradeReviewsAsync(new());
        var reconciliations=await store.ReadReconciliationsAsync(new());

        Assert.Equal(RuntimeCollectionState.Available,reviews.State);
        var review=Assert.Single(reviews.Items);
        Assert.Equal("wpe.post-trade-review/1.4",review.Schema);Assert.Equal("trend-alpha",review.StrategyId);Assert.Equal("2.1.0",review.StrategyVersion);Assert.Equal("automatic-artifact",review.AttributionBasis);
        Assert.Matches("^trade#[A-F0-9]{12}$",review.TraceId);Assert.Equal("legacy",review.TraceState);Assert.Equal("Unavailable",review.RiskDecision);Assert.Equal("Unavailable",review.ExecutionStatus);Assert.Equal("trace.execution-unavailable",review.ExecutionCode);Assert.Null(review.ExecutionAttempts);Assert.Equal("unavailable",review.EvidenceState);Assert.Empty(review.EvidenceChain);
        Assert.Equal(.05m,review.Fees);Assert.Equal(1.25m,review.FundingAmount);Assert.Equal(.30m,review.TotalSlippageAmount);Assert.Equal(11.20m,review.NetPnl);Assert.Equal("win",review.Outcome);
        var reviewJson=System.Text.Json.JsonSerializer.Serialize(reviews);Assert.DoesNotContain("close-1",reviewJson,StringComparison.Ordinal);Assert.DoesNotContain("cycle-1",reviewJson,StringComparison.Ordinal);
        Assert.Equal(RuntimeCollectionState.Available,reconciliations.State);Assert.Equal(3,reconciliations.Items.Count);Assert.All(reconciliations.Items,item=>Assert.True(item.AllowsRiskIncrease));Assert.All(reconciliations.Items,item=>Assert.Equal(hash,item.CanonicalSha256));
        Assert.Equal(new[]{"externalIsolation","position","protection"},reconciliations.Items.Select(item=>item.Kind).OrderBy(value=>value,StringComparer.Ordinal).ToArray());
        var json=System.Text.Json.JsonSerializer.Serialize(reconciliations);Assert.DoesNotContain("canonical_bytes",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("AQ==",json,StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTradeTraceCorrelatesRiskAndExecutionWithoutExposingRawIdentifiers()
    {
        await InitializeAsync();
        const string cycle="cycle-trace-secret";const string execution="execution-trace-secret";
        await ExecuteAsync("INSERT INTO trade_outcomes(client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,fees,fee_basis,fee_rate,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,funding_amount,funding_basis,net_pnl,return_pct,closed_at,strategy_id,strategy_version,attribution_basis) VALUES('close-trace',$cycle,'ETHUSDT','Short','200','190','2','.20','exchange-reported-usdt','0','-.10','.15','.05','intent-expected-vs-fill','-.40','exchange-reported-window','19.40','.0485',$t,'mean-reversion-eth','3.0.0','automatic-artifact')",("$cycle",cycle),("$t",Now.ToString("O")));
        await ExecuteAsync("INSERT INTO automatic_execution_queue(execution_id,correlation_id,status,last_code,attempt_count,market_collected_at,market_data_version,updated_at) VALUES($id,$cycle,'Succeeded','automatic.succeeded',1,$market,'provider-market-v1',$now)",("$id",execution),("$cycle",cycle),("$market",Now.AddSeconds(-20).ToString("O")),("$now",Now.ToString("O")));
        await ExecuteAsync("INSERT INTO automatic_execution_events(execution_id,sequence,occurred_at,from_status,to_status,event_code,actor_kind) VALUES($id,1,$t,'Proposed','RiskApproved','automatic.risk-approved','risk')",("$id",execution),("$t",Now.AddSeconds(-10).ToString("O")));

        var review=Assert.Single((await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadPostTradeReviewsAsync(new())).Items);

        Assert.Matches("^trade#[A-F0-9]{12}$",review.TraceId);Assert.Equal("available",review.TraceState);Assert.Equal("Approved",review.RiskDecision);Assert.Equal("Succeeded",review.ExecutionStatus);Assert.Equal("automatic.succeeded",review.ExecutionCode);Assert.Equal(1,review.ExecutionAttempts);Assert.Equal("provider-market-v1",review.MarketDataVersion);Assert.NotNull(review.MarketCollectedAtUtc);
        var json=System.Text.Json.JsonSerializer.Serialize(review);Assert.DoesNotContain(cycle,json,StringComparison.Ordinal);Assert.DoesNotContain(execution,json,StringComparison.Ordinal);Assert.DoesNotContain("close-trace",json,StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTradeTraceProjectsOnlyVerifiedCanonicalUpstreamEvidence()
    {
        await InitializeAsync();
        const string cycle="cycle-evidence";
        await ExecuteAsync("CREATE TABLE model_off_canonical_audits(output_id TEXT PRIMARY KEY,cycle_id TEXT,schema TEXT,template_version TEXT,canonical_sha256 TEXT,status TEXT,output_kind TEXT,sources_json TEXT,as_of_utc TEXT,recorded_at_utc TEXT,canonical_bytes BLOB)");
        await ExecuteAsync("INSERT INTO trade_outcomes(client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,fees,fee_basis,fee_rate,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,funding_amount,funding_basis,net_pnl,return_pct,closed_at,strategy_id,strategy_version,attribution_basis) VALUES('close-evidence',$cycle,'BTCUSDT','Long','100','105','1','.05','exchange-reported-usdt','0','0','0','0','unavailable','0','unavailable','4.95','.0495',$t,'trend-evidence','4.0.0','automatic-artifact')",("$cycle",cycle),("$t",Now.ToString("O")));
        var roles=new[]{"market","research","strategy","risk"};
        for(var i=0;i<roles.Length;i++)
        {
            var bytes=new byte[]{(byte)(i+1),(byte)(i+10)};var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            await ExecuteAsync("INSERT INTO model_off_canonical_audits(output_id,cycle_id,schema,template_version,canonical_sha256,status,output_kind,sources_json,as_of_utc,recorded_at_utc,canonical_bytes) VALUES($id,$cycle,'schema','template',$hash,'succeeded',$kind,'[]',$at,$at,$bytes)",("$id","output-"+roles[i]),("$cycle",cycle),("$hash",hash),("$kind",roles[i]),("$at",Now.AddSeconds(i).ToString("O")),("$bytes",bytes));
        }

        var review=Assert.Single((await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadPostTradeReviewsAsync(new())).Items);

        Assert.Equal("available",review.EvidenceState);Assert.Equal(roles,review.EvidenceChain.Select(x=>x.Stage).ToArray());Assert.All(review.EvidenceChain,x=>Assert.Equal("succeeded",x.Status));Assert.All(review.EvidenceChain,x=>Assert.Matches("^[a-f0-9]{64}$",x.CanonicalSha256));
        var json=System.Text.Json.JsonSerializer.Serialize(review);Assert.DoesNotContain("canonical_bytes",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("sources_json",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("output-market",json,StringComparison.Ordinal);
    }

    [Fact]
    public async Task TamperedCanonicalEvidenceIsMarkedInvalidWithoutInventingATrace()
    {
        await InitializeAsync();
        const string cycle="cycle-evidence-tampered";
        await ExecuteAsync("CREATE TABLE model_off_canonical_audits(output_id TEXT PRIMARY KEY,cycle_id TEXT,schema TEXT,template_version TEXT,canonical_sha256 TEXT,status TEXT,output_kind TEXT,sources_json TEXT,as_of_utc TEXT,recorded_at_utc TEXT,canonical_bytes BLOB)");
        await ExecuteAsync("INSERT INTO trade_outcomes(client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,fees,fee_basis,fee_rate,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,funding_amount,funding_basis,net_pnl,return_pct,closed_at,strategy_id,strategy_version,attribution_basis) VALUES('close-evidence-tampered',$cycle,'BTCUSDT','Long','100','105','1','.05','estimated-static-rate','.0004','0','0','0','unavailable','0','unavailable','4.95','.0495',$t,NULL,'legacy-v1','legacy-version-only')",("$cycle",cycle),("$t",Now.ToString("O")));
        await ExecuteAsync("INSERT INTO model_off_canonical_audits VALUES('output-risk',$cycle,'schema','template',$hash,'succeeded','risk','[]',$t,$t,$bytes)",("$cycle",cycle),("$hash",new string('a',64)),("$t",Now.ToString("O")),("$bytes",new byte[]{1,2,3}));

        var review=Assert.Single((await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadPostTradeReviewsAsync(new())).Items);

        Assert.Equal("invalid",review.EvidenceState);Assert.Empty(review.EvidenceChain);
    }

    [Fact]
    public async Task BatchedTradeTraceKeepsRiskAndEvidenceIsolatedPerCycle()
    {
        await InitializeAsync();
        await ExecuteAsync("CREATE TABLE model_off_canonical_audits(output_id TEXT PRIMARY KEY,cycle_id TEXT,schema TEXT,template_version TEXT,canonical_sha256 TEXT,status TEXT,output_kind TEXT,sources_json TEXT,as_of_utc TEXT,recorded_at_utc TEXT,canonical_bytes BLOB)");
        foreach(var row in new[]{("cycle-a","close-a","BTCUSDT","trend-a"),("cycle-b","close-b","ETHUSDT","trend-b")})
            await ExecuteAsync("INSERT INTO trade_outcomes(client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,fees,fee_basis,fee_rate,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,funding_amount,funding_basis,net_pnl,return_pct,closed_at,strategy_id,strategy_version,attribution_basis) VALUES($close,$cycle,$symbol,'Long','100','105','1','.05','exchange-reported-usdt','0','0','0','0','unavailable','0','unavailable','4.95','.0495',$t,$strategy,'4.0.0','automatic-artifact')",("$close",row.Item2),("$cycle",row.Item1),("$symbol",row.Item3),("$t",Now.ToString("O")),("$strategy",row.Item4));
        await ExecuteAsync("INSERT INTO automatic_execution_queue VALUES('execution-a','cycle-a','Succeeded','automatic.succeeded',1,$t,'market-a',$t)",("$t",Now.ToString("O")));
        await ExecuteAsync("INSERT INTO automatic_execution_queue VALUES('execution-b','cycle-b','FailedTerminal','automatic.gateway-rejected',2,$t,'market-b',$t)",("$t",Now.ToString("O")));
        await ExecuteAsync("INSERT INTO automatic_execution_events(execution_id,sequence,occurred_at,from_status,to_status,event_code,actor_kind) VALUES('execution-a',1,$t,'Proposed','RiskApproved','automatic.risk-approved','risk')",("$t",Now.ToString("O")));
        await ExecuteAsync("INSERT INTO automatic_execution_events(execution_id,sequence,occurred_at,from_status,to_status,event_code,actor_kind) VALUES('execution-b',1,$t,'Proposed','RiskBlocked','automatic.risk-blocked','risk')",("$t",Now.ToString("O")));
        var aBytes=new byte[]{1,2};var bBytes=new byte[]{3,4};
        var aHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(aBytes)).ToLowerInvariant();var bHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bBytes)).ToLowerInvariant();
        await ExecuteAsync("INSERT INTO model_off_canonical_audits VALUES('evidence-a','cycle-a','schema','template',$hash,'succeeded','strategy','[]',$t,$t,$bytes)",("$hash",aHash),("$t",Now.ToString("O")),("$bytes",aBytes));
        await ExecuteAsync("INSERT INTO model_off_canonical_audits VALUES('evidence-b','cycle-b','schema','template',$hash,'blocked','risk','[]',$t,$t,$bytes)",("$hash",bHash),("$t",Now.ToString("O")),("$bytes",bBytes));

        var page=await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadPostTradeReviewsAsync(new());
        Assert.Equal(RuntimeCollectionState.Available,page.State);Assert.Equal(2,page.Items.Count);
        var btc=Assert.Single(page.Items.Where(x=>x.Symbol=="BTCUSDT"));var eth=Assert.Single(page.Items.Where(x=>x.Symbol=="ETHUSDT"));
        Assert.Equal("Approved",btc.RiskDecision);Assert.Equal("Succeeded",btc.ExecutionStatus);Assert.Equal("market-a",btc.MarketDataVersion);Assert.Equal("partial",btc.EvidenceState);Assert.Equal("strategy",Assert.Single(btc.EvidenceChain).Stage);
        Assert.Equal("Blocked",eth.RiskDecision);Assert.Equal("FailedTerminal",eth.ExecutionStatus);Assert.Equal("market-b",eth.MarketDataVersion);Assert.Equal("partial",eth.EvidenceState);Assert.Equal("risk",Assert.Single(eth.EvidenceChain).Stage);
        Assert.NotEqual(btc.TraceId,eth.TraceId);Assert.NotEqual(btc.EvidenceChain[0].CanonicalSha256,eth.EvidenceChain[0].CanonicalSha256);
    }

    [Fact]
    public async Task AmbiguousExecutionCorrelationNeverGuessesATrace()
    {
        await InitializeAsync();
        const string cycle="cycle-ambiguous";
        await ExecuteAsync("INSERT INTO trade_outcomes(client_order_id,cycle_id,symbol,side,entry_price,exit_price,quantity,fees,fee_basis,fee_rate,entry_slippage_amount,exit_slippage_amount,total_slippage_amount,slippage_basis,funding_amount,funding_basis,net_pnl,return_pct,closed_at,strategy_id,strategy_version,attribution_basis) VALUES('close-ambiguous',$cycle,'BTCUSDT','Long','100','101','1','.01','estimated-static-rate','.0004','0','0','0','unavailable','0','unavailable','.99','.0099',$t,NULL,'legacy-v1','conflicting-correlation')",("$cycle",cycle),("$t",Now.ToString("O")));
        foreach(var execution in new[]{"execution-a","execution-b"})await ExecuteAsync("INSERT INTO automatic_execution_queue(execution_id,correlation_id,status,last_code,attempt_count,market_collected_at,market_data_version,updated_at) VALUES($id,$cycle,'Succeeded','automatic.succeeded',1,$t,'market-v1',$t)",("$id",execution),("$cycle",cycle),("$t",Now.ToString("O")));

        var review=Assert.Single((await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadPostTradeReviewsAsync(new())).Items);

        Assert.Equal("ambiguous",review.TraceState);Assert.Equal("Ambiguous",review.RiskDecision);Assert.Equal("Ambiguous",review.ExecutionStatus);Assert.Equal("trace.multiple-executions",review.ExecutionCode);Assert.Null(review.ExecutionAttempts);Assert.Null(review.MarketCollectedAtUtc);Assert.Null(review.MarketDataVersion);
    }

    [Fact]
    public async Task TamperedReconciliationCanonicalPayloadFailsClosed()
    {
        await InitializeAsync();
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[]{1})).ToLowerInvariant();
        await ExecuteAsync("INSERT INTO protection_reconciliation_audits(report_id,schema,observed_at,evaluated_at,state,allows_risk_increase,canonical_sha256,canonical_bytes) VALUES('tampered','schema/1.0',$t,$t,'Protected',1,$hash,X'02')",("$t",Now.ToString("O")),("$hash",hash));

        var page=await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadReconciliationsAsync(new());

        Assert.Equal(RuntimeCollectionState.Error,page.State);Assert.Empty(page.Items);Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task MalformedReconciliationHashFailsClosed()
    {
        await InitializeAsync();
        await ExecuteAsync("INSERT INTO position_reconciliation_audits(report_id,schema,observed_at,evaluated_at,state,allows_risk_increase,canonical_sha256,canonical_bytes) VALUES('bad','schema/1.0',$t,$t,'Confirmed',1,'not-a-hash',X'01')",("$t",Now.ToString("O")));
        var page=await new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now).ReadReconciliationsAsync(new());
        Assert.Equal(RuntimeCollectionState.Error,page.State);Assert.Empty(page.Items);Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task MissingTableIsUnsupportedAndAuditPayloadIsNeverProjected()
    {
        Directory.CreateDirectory(_directory);await ExecuteAsync("CREATE TABLE runtime_events(event_id TEXT,sequence INTEGER,correlation_id TEXT,event_type TEXT,source TEXT,payload_json TEXT,occurred_at TEXT)");
        const string secret="sk-secret-token-123456";await ExecuteAsync("INSERT INTO runtime_events VALUES('e1',1,$c,$type,$source,$p,$t)",( "$c",secret),("$type","token="+secret),("$source","Bearer "+secret),("$p",secret),("$t",Now.ToString("O")));
        var store=new RuntimeHistoricalCollectionStateStore(DatabasePath,()=>Now);var audit=await store.ReadAuditEventsAsync(new());var backtests=await store.ReadBacktestsAsync(new());
        var item=Assert.Single(audit.Items);var json=System.Text.Json.JsonSerializer.Serialize(audit);Assert.DoesNotContain(secret,json);Assert.DoesNotContain("payload_json",json,StringComparison.OrdinalIgnoreCase);Assert.Equal("[REDACTED]",item.Category);Assert.Equal("[REDACTED]",item.Source);Assert.Matches("^correlation#[A-F0-9]{12}$",item.CorrelationId!);Assert.Equal(RuntimeCollectionState.Unsupported,backtests.State);Assert.Empty(backtests.Items);
    }

    private async Task InitializeAsync(){Directory.CreateDirectory(_directory);await ExecuteAsync("""
        CREATE TABLE execution_events(id INTEGER PRIMARY KEY AUTOINCREMENT,cycle_id TEXT,client_order_id TEXT,symbol TEXT,side TEXT,action TEXT,reduce_only INTEGER,quantity TEXT,avg_price TEXT,status TEXT,occurred_at TEXT);
        CREATE TABLE equity_snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT,observed_at TEXT,equity TEXT,available_balance TEXT,environment TEXT,provider_id TEXT);
        CREATE TABLE runtime_skill_calls(id INTEGER PRIMARY KEY AUTOINCREMENT,occurred_at TEXT,skill TEXT,status TEXT,duration_ms INTEGER,mode TEXT,remote_llm INTEGER,tokens INTEGER,cost_usd TEXT);
        CREATE TABLE backtest_runs(id TEXT PRIMARY KEY,strategy_id TEXT,strategy_version TEXT,symbol TEXT,status TEXT,completed_at TEXT,coverage_days INTEGER,trades INTEGER,out_of_sample_return REAL,max_drawdown REAL,sharpe REAL);
        CREATE TABLE runtime_events(event_id TEXT,sequence INTEGER,correlation_id TEXT,event_type TEXT,source TEXT,payload_json TEXT,occurred_at TEXT);
        CREATE TABLE trade_outcomes(id INTEGER PRIMARY KEY AUTOINCREMENT,client_order_id TEXT,cycle_id TEXT,symbol TEXT,side TEXT,entry_price TEXT,exit_price TEXT,quantity TEXT,fees TEXT,fee_basis TEXT,fee_rate TEXT,entry_slippage_amount TEXT,exit_slippage_amount TEXT,total_slippage_amount TEXT,slippage_basis TEXT,funding_amount TEXT,funding_basis TEXT,net_pnl TEXT,return_pct TEXT,closed_at TEXT,strategy_id TEXT,strategy_version TEXT,attribution_basis TEXT);
        CREATE TABLE automatic_execution_queue(execution_id TEXT PRIMARY KEY,correlation_id TEXT,status TEXT,last_code TEXT,attempt_count INTEGER,market_collected_at TEXT,market_data_version TEXT,updated_at TEXT);
        CREATE TABLE automatic_execution_events(id INTEGER PRIMARY KEY AUTOINCREMENT,execution_id TEXT,sequence INTEGER,occurred_at TEXT,from_status TEXT,to_status TEXT,event_code TEXT,actor_kind TEXT);
        CREATE TABLE position_reconciliation_audits(report_id TEXT PRIMARY KEY,schema TEXT,observed_at TEXT,evaluated_at TEXT,state TEXT,allows_risk_increase INTEGER,canonical_sha256 TEXT,canonical_bytes BLOB);
        CREATE TABLE protection_reconciliation_audits(report_id TEXT PRIMARY KEY,schema TEXT,observed_at TEXT,evaluated_at TEXT,state TEXT,allows_risk_increase INTEGER,canonical_sha256 TEXT,canonical_bytes BLOB);
        CREATE TABLE external_position_isolation_audits(report_id TEXT PRIMARY KEY,schema TEXT,observed_at TEXT,evaluated_at TEXT,state TEXT,allows_risk_increase INTEGER,canonical_sha256 TEXT,canonical_bytes BLOB);
        """);}
    private async Task ExecuteAsync(string sql,params (string Name,object Value)[] values){Directory.CreateDirectory(_directory);await using var c=new SqliteConnection($"Data Source={DatabasePath}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;foreach(var value in values)q.Parameters.AddWithValue(value.Name,value.Value);await q.ExecuteNonQueryAsync();}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}

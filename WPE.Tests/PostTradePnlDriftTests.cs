using Microsoft.Data.Sqlite;
using WpeAgent.TradingAuthorization;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PostTradePnlDriftTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "wpe-post-trade-pnl-drift-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory,"agent.db");
    private static readonly DateTimeOffset Now = new(2026,9,19,6,0,0,TimeSpan.Zero);
    private const string StrategyId = "strategy-pnl";
    private const string StrategyVersion = "v1";

    [Fact]
    public async Task AutomaticClosePersistsGrossPnlDriftFromExactSimulationPath()
    {
        var store=Store();
        var open=Intent("open",false,1m,100m);
        var close=Intent("close",true,1m,110m);
        await Bind(store,"cycle-open",open);
        await Bind(store,"cycle-close",close);
        await SaveSimulation(store,"cycle-open",open,1m,100m,0.04m,ExecutionSimulationFillStateV1.Filled);
        await SaveSimulation(store,"cycle-close",close,1m,111m,0.0444m,ExecutionSimulationFillStateV1.Filled);

        await store.RecordExecutionAsync(
            "cycle-open",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);

        var fact=Assert.Single(await store.GetRecentPostTradePnlDriftAsync(10,default));

        Assert.Equal(PostTradePnlDriftStateV1.GrossComparable,fact.State);
        Assert.Equal(100m,fact.ActualEntryPrice);
        Assert.Equal(110m,fact.ActualExitPrice);
        Assert.Equal(10m,fact.ActualGrossPnl);
        Assert.Equal(100m,fact.SimulatedEntryPrice);
        Assert.Equal(111m,fact.SimulatedExitPrice);
        Assert.Equal(11m,fact.SimulatedGrossPnl);
        Assert.Equal(-1m,fact.ObservedMinusSimulatedGrossPnl);
        Assert.False(fact.FeeAdjustedPnlComparable);
        Assert.Null(fact.ObservedMinusSimulatedFeeAdjustedPnl);
        Assert.False(fact.NetPnlComparable);
        Assert.Null(fact.ObservedMinusSimulatedNetPnl);
        Assert.Equal("gross-pnl-comparable-fee-or-funding-unavailable",fact.ReasonCode);
        Assert.True(PostTradePnlDriftCanonicalizerV1.IsCanonical(fact));

        var proof=await store.GetPostTradeEntryLedgerProofAsync("close",default);
        Assert.NotNull(proof);
        Assert.Equal(proof!.CanonicalSha256,fact.EntryLedgerSha256);
        Assert.Single(fact.EntrySimulationSources);
    }

    [Fact]
    public async Task ReportedActualFeesEnableFeeAdjustedPnlDeltaButFundingStaysUnmodeled()
    {
        var store=Store();
        var open=Intent("open-fee",false,1m,100m);
        var close=Intent("close-fee",true,1m,110m);
        await Bind(store,"cycle-open-fee",open);
        await Bind(store,"cycle-close-fee",close);
        await SaveSimulation(store,"cycle-open-fee",open,1m,100m,0.05m,ExecutionSimulationFillStateV1.Filled);
        await SaveSimulation(store,"cycle-close-fee",close,1m,111m,0.055m,ExecutionSimulationFillStateV1.Filled);
        await SeedFee("open-fee",0.04m,Now.AddMinutes(-4));
        await SeedFee("close-fee",0.044m,Now.AddSeconds(-1));

        await store.RecordExecutionAsync(
            "cycle-open-fee",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close-fee",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);

        var fact=Assert.Single(await store.GetRecentPostTradePnlDriftAsync(10,default));

        Assert.Equal(PostTradePnlDriftStateV1.FeeAdjustedComparable,fact.State);
        Assert.Equal("exchange-reported-usdt",fact.ActualFeeBasis);
        Assert.Equal(0.084m,fact.ActualFees);
        Assert.True(fact.SimulatedFeeEvidenceComplete);
        Assert.Equal(0.105m,fact.SimulatedFees);
        Assert.True(fact.FeeAdjustedPnlComparable);
        Assert.Equal(-0.979m,fact.ObservedMinusSimulatedFeeAdjustedPnl);
        Assert.False(fact.NetPnlComparable);
        Assert.Equal("fee-adjusted-pnl-comparable-funding-unmodeled",fact.ReasonCode);
    }

    [Fact]
    public async Task SimulationQuantityPathDivergenceFailsClosedWithoutPnlFact()
    {
        var store=Store();
        var open=Intent("open-partial",false,1m,100m);
        var close=Intent("close-partial",true,1m,110m);
        await Bind(store,"cycle-open-partial",open);
        await Bind(store,"cycle-close-partial",close);
        await SaveSimulation(store,"cycle-open-partial",open,0.5m,100m,0.02m,ExecutionSimulationFillStateV1.Partial);
        await SaveSimulation(store,"cycle-close-partial",close,1m,110m,0.04m,ExecutionSimulationFillStateV1.Filled);

        await store.RecordExecutionAsync(
            "cycle-open-partial",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close-partial",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);

        Assert.Empty(await store.GetRecentPostTradePnlDriftAsync(10,default));
        Assert.Null(await store.TryBuildAndSavePostTradePnlDriftAsync("close-partial",default));
    }

    [Fact]
    public async Task PersistedPnlDriftIsAppendOnlyAndSourceTamperingFailsRestartReplay()
    {
        var store=Store();
        var open=Intent("open-tamper",false,1m,100m);
        var close=Intent("close-tamper",true,1m,110m);
        await Bind(store,"cycle-open-tamper",open);
        await Bind(store,"cycle-close-tamper",close);
        await SaveSimulation(store,"cycle-open-tamper",open,1m,100m,0.04m,ExecutionSimulationFillStateV1.Filled);
        await SaveSimulation(store,"cycle-close-tamper",close,1m,110m,0.044m,ExecutionSimulationFillStateV1.Filled);
        await store.RecordExecutionAsync(
            "cycle-open-tamper",open,Order(open,"FILLED",1m,100m,Now.AddMinutes(-5)),"fallback",default);
        await store.RecordExecutionAsync(
            "cycle-close-tamper",close,Order(close,"FILLED",1m,110m,Now.AddSeconds(-1)),"fallback",default);

        Assert.Single(await store.GetRecentPostTradePnlDriftAsync(10,default));

        await using(var connection=new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            foreach(var sql in new[]
            {
                "UPDATE post_trade_pnl_drift SET state='GrossComparable'",
                "DELETE FROM post_trade_pnl_drift"
            })
            {
                await using var command=connection.CreateCommand();
                command.CommandText=sql;
                await Assert.ThrowsAsync<SqliteException>(()=>command.ExecuteNonQueryAsync());
            }

            await using var tamper=connection.CreateCommand();
            tamper.CommandText="UPDATE trade_outcomes SET gross_pnl='999' WHERE client_order_id='close-tamper'";
            Assert.Equal(1,await tamper.ExecuteNonQueryAsync());
        }

        var restarted=new AgentSqliteStore(Database,()=>Now);
        await Assert.ThrowsAsync<InvalidOperationException>(
            ()=>restarted.GetRecentPostTradePnlDriftAsync(10,default));
    }

    private AgentSqliteStore Store()=>new(Database,()=>Now);

    private async Task Bind(AgentSqliteStore store,string cycle,ExecutionIntent intent)
    {
        var artifact=new DurableExecutionArtifactV2(
            DurableExecutionArtifactV2.Version,
            cycle,
            [new DurableExecutionIntentSnapshotV1(
                0,
                intent.Symbol,
                intent.Side.ToString(),
                intent.Quantity,
                intent.ReduceOnly,
                intent.StopLoss,
                intent.TakeProfit,
                intent.ClientOrderId,
                "automatic.risk-approved",
                intent.Action.ToString(),
                intent.OrderType.ToString(),
                intent.LimitPrice,
                intent.ExpectedPrice)],
            1,
            true,
            "binance",
            "Testnet",
            StrategyId,
            StrategyVersion,
            Now.AddSeconds(-20),
            "market-v1",
            Now.AddSeconds(-10),
            Now.AddMinutes(5));
        Assert.True((await store.SaveAutomaticExecutionAsync(cycle,artifact,default)).Succeeded);
    }

    private static async Task SaveSimulation(
        AgentSqliteStore store,
        string cycle,
        ExecutionIntent intent,
        decimal executedQuantity,
        decimal averagePrice,
        decimal fee,
        ExecutionSimulationFillStateV1 state)
    {
        var fill=ExecutionSimulationFillCanonicalizerV1.Create(
            cycle,
            intent.ClientOrderId,
            StrategyId,
            StrategyVersion,
            "cost-v1",
            "simulation-v1",
            "venue-v1",
            intent.Symbol,
            intent.Side,
            intent.ReduceOnly,
            intent.OrderType,
            intent.Quantity,
            state,
            executedQuantity,
            averagePrice,
            fee,
            executedQuantity>0?ExecutionSimulationFeeRoleV1.Taker:ExecutionSimulationFeeRoleV1.Unavailable,
            false,
            0,
            Now.AddMinutes(-2),
            Now.AddMinutes(-1),
            "test");
        Assert.True((await store.SaveExecutionSimulationFillAsync(fill,default)).Succeeded);
    }

    private async Task SeedFee(string clientOrderId,decimal fee,DateTimeOffset observedAt)
    {
        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="""
            INSERT INTO exchange_order_fee_evidence(
                evidence_id,schema,provider_id,environment,symbol,order_id,client_order_id,
                fill_count,executed_quantity,fee_amount,fee_asset,observed_at,state,
                canonical_sha256,canonical_bytes)
            VALUES(
                $id,'test','binance','Testnet','BTCUSDT',$order,$client,
                1,'1',$fee,'USDT',$observed,'Confirmed',$hash,$bytes);
            """;
        command.Parameters.AddWithValue("$id","fee-"+clientOrderId);
        command.Parameters.AddWithValue("$order","venue-"+clientOrderId);
        command.Parameters.AddWithValue("$client",clientOrderId);
        command.Parameters.AddWithValue("$fee",fee.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$observed",observedAt.ToString("O"));
        command.Parameters.AddWithValue("$hash",new string(clientOrderId.Contains("close",StringComparison.Ordinal)?'b':'a',64));
        command.Parameters.Add("$bytes",SqliteType.Blob).Value=new byte[]{1};
        await command.ExecuteNonQueryAsync();
    }

    private static ExecutionIntent Intent(
        string clientOrderId,
        bool reduceOnly,
        decimal quantity,
        decimal expectedPrice)=>
        new(
            "BTCUSDT",
            PositionSide.Long,
            quantity,
            reduceOnly,
            90m,
            170m,
            clientOrderId,
            "test",
            reduceOnly?DecisionAction.CloseLong:DecisionAction.OpenLong,
            ExecutionOrderType.Market,
            0m,
            expectedPrice);

    private static ExchangeOrder Order(
        ExecutionIntent intent,
        string status,
        decimal quantity,
        decimal price,
        DateTimeOffset updatedAt)=>
        new(
            intent.Symbol,
            "venue-"+intent.ClientOrderId,
            intent.ClientOrderId,
            status,
            quantity,
            price,
            "MARKET",
            intent.Side,
            intent.ReduceOnly,
            updatedAt.UtcDateTime);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))
            Directory.Delete(_directory,true);
    }
}

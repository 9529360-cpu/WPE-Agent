using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ExecutionLedgerTests:IDisposable
{
    private readonly string _db=Path.Combine(Path.GetTempPath(),"wpe-ledger-"+Guid.NewGuid().ToString("N")+".db");

    [Fact]
    public async Task RecentExecutionLedgerShowsAllSymbolsAndJoinsExchangeReportedFees()
    {
        var store=new AgentSqliteStore(_db);
        await using(var c=new SqliteConnection($"Data Source={_db}"))
        {
            await c.OpenAsync();
            await using var q=c.CreateCommand();
            q.CommandText="""
                INSERT INTO execution_events(cycle_id,client_order_id,symbol,side,action,reduce_only,quantity,avg_price,expected_price,status,occurred_at,exchange_updated_at)
                VALUES
                ('c1','open-btc','BTCUSDT','Long','OpenLong',0,'0.0091','84945','84945','FILLED','2026-09-27T10:15:25Z','2026-09-27T10:15:25Z'),
                ('c2','close-grass','GRASSUSDT','Long','CloseLong',1,'26','0.5949','0.5950','FILLED','2026-09-27T09:47:49Z','2026-09-27T09:47:49Z');
                INSERT INTO exchange_order_fee_evidence(evidence_id,schema,provider_id,environment,symbol,order_id,client_order_id,fill_count,executed_quantity,fee_amount,fee_asset,observed_at,state,canonical_sha256,canonical_bytes)
                VALUES
                ('f1','wpe.exchange-order-fee-evidence/1.0','binance-futures','Testnet','BTCUSDT','28605600324','open-btc',1,'0.0091','0.30919980','USDT','2026-09-27T10:15:25Z','Confirmed','abc',X'01'),
                ('f2','wpe.exchange-order-fee-evidence/1.0','binance-futures','Testnet','GRASSUSDT','293240806','close-grass',1,'26','0.00773370','USDT','2026-09-27T09:47:49Z','Confirmed','def',X'02');
                """;
            await q.ExecuteNonQueryAsync();
        }

        var rows=await store.GetRecentExecutionLedgerAsync(20,default);

        Assert.Equal(2,rows.Count);
        Assert.Equal("BTCUSDT",rows[0].Symbol);
        Assert.Equal("买入开多",rows[0].Direction);
        Assert.Equal(0.30919980m,rows[0].Fee);
        Assert.Equal("28605600324",rows[0].OrderId);
        Assert.Equal("GRASSUSDT",rows[1].Symbol);
        Assert.Equal("卖出平多",rows[1].Direction);
        Assert.Equal(0.00773370m,rows[1].Fee);
    }

    public void Dispose()
    {
        try{if(File.Exists(_db))File.Delete(_db);}catch{}
        try{if(File.Exists(_db+"-wal"))File.Delete(_db+"-wal");}catch{}
        try{if(File.Exists(_db+"-shm"))File.Delete(_db+"-shm");}catch{}
    }
}

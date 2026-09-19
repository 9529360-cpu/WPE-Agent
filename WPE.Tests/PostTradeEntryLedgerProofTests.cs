using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class PostTradeEntryLedgerProofTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "wpe-post-trade-entry-ledger-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "agent.db");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MovingWeightedLedgerReplaysOpenAndPriorReductionExactly()
    {
        var events = new[]
        {
            Event(0,"open-a",false,2m,100m,Now.AddMinutes(-40)),
            Event(1,"open-b",false,1m,130m,Now.AddMinutes(-30)),
            Event(2,"reduce-a",true,1m,115m,Now.AddMinutes(-20)),
            Event(3,"open-c",false,1m,140m,Now.AddMinutes(-10))
        };

        var proof = PostTradeEntryLedgerProofCanonicalizerV1.Create(
            "close-final",
            "cycle-close-final",
            "BTCUSDT",
            "Long",
            1.5m,
            events);

        Assert.Equal(3m, proof.PreCloseOpenQuantity);
        Assert.Equal(360m, proof.PreCloseOpenCost);
        Assert.Equal(120m, proof.WeightedEntryPrice);
        Assert.Equal(1.5m, proof.PostCloseRemainingQuantity);
        Assert.Equal(4, proof.Events.Count);
        Assert.True(PostTradeEntryLedgerProofCanonicalizerV1.IsCanonical(proof));
    }

    [Fact]
    public void ReductionConsumesAtMovingAverageRatherThanFifoPrice()
    {
        var proof = PostTradeEntryLedgerProofCanonicalizerV1.Create(
            "close-final",
            "cycle-close-final",
            "BTCUSDT",
            "Long",
            1m,
            [
                Event(0,"open-a",false,1m,100m,Now.AddMinutes(-30)),
                Event(1,"open-b",false,1m,200m,Now.AddMinutes(-20)),
                Event(2,"reduce-a",true,1m,180m,Now.AddMinutes(-10))
            ]);

        Assert.Equal(1m, proof.PreCloseOpenQuantity);
        Assert.Equal(150m, proof.PreCloseOpenCost);
        Assert.Equal(150m, proof.WeightedEntryPrice);
    }

    [Fact]
    public void ImpossibleLedgerOrTamperingFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PostTradeEntryLedgerProofCanonicalizerV1.Create(
                "close",
                "cycle-close",
                "BTCUSDT",
                "Long",
                1m,
                [
                    Event(0,"open",false,0.5m,100m,Now.AddMinutes(-2)),
                    Event(1,"reduce",true,1m,101m,Now.AddMinutes(-1))
                ]));

        var valid = PostTradeEntryLedgerProofCanonicalizerV1.Create(
            "close",
            "cycle-close",
            "BTCUSDT",
            "Long",
            0.5m,
            [Event(0,"open",false,1m,100m,Now.AddMinutes(-1))]);

        Assert.False(PostTradeEntryLedgerProofCanonicalizerV1.IsCanonical(
            valid with { WeightedEntryPrice = 999m }));
        Assert.False(PostTradeEntryLedgerProofCanonicalizerV1.IsCanonical(
            valid with { CanonicalSha256 = new string('0',64) }));
    }

    [Fact]
    public async Task RealPostTradeAccountingPersistsExactLedgerProofAndUsesItsEntryPrice()
    {
        var store = Store();

        await store.RecordExecutionAsync(
            "cycle-open-a",
            Intent("open-a",false,2m,100m),
            Order("open-a","FILLED",2m,100m,Now.AddMinutes(-40)),
            "strategy-v1",
            default);
        await store.RecordExecutionAsync(
            "cycle-open-b",
            Intent("open-b",false,1m,130m),
            Order("open-b","FILLED",1m,130m,Now.AddMinutes(-30)),
            "strategy-v1",
            default);
        await store.RecordExecutionAsync(
            "cycle-reduce-a",
            Intent("reduce-a",true,1m,115m),
            Order("reduce-a","FILLED",1m,115m,Now.AddMinutes(-20)),
            "strategy-v1",
            default);
        await store.RecordExecutionAsync(
            "cycle-open-c",
            Intent("open-c",false,1m,140m),
            Order("open-c","FILLED",1m,140m,Now.AddMinutes(-10)),
            "strategy-v1",
            default);
        await store.RecordExecutionAsync(
            "cycle-close-final",
            Intent("close-final",true,1.5m,150m),
            Order("close-final","FILLED",1.5m,150m,Now.AddSeconds(-1)),
            "strategy-v1",
            default);

        var proof = await new AgentSqliteStore(Database,() => Now)
            .GetPostTradeEntryLedgerProofAsync("close-final",default);
        Assert.NotNull(proof);
        Assert.True(PostTradeEntryLedgerProofCanonicalizerV1.IsCanonical(proof!));
        Assert.Equal(120m, proof!.WeightedEntryPrice);
        Assert.Equal(3m, proof.PreCloseOpenQuantity);
        Assert.Equal(1.5m, proof.PostCloseRemainingQuantity);
        Assert.Equal(
            new[] { "open-a","open-b","reduce-a","open-c" },
            proof.Events.Select(x => x.ClientOrderId).ToArray());

        var review = (await store.GetRecentPostTradeReviewsAsync(10,default))
            .Single(x => x.ClientOrderId == "close-final");
        Assert.Equal(proof.WeightedEntryPrice, review.EntryPrice);
        Assert.Equal(150m, review.ExitPrice);
        Assert.Equal(1.5m, review.Quantity);
    }

    [Fact]
    public async Task ExecutionReplayPreservesLedgerOrderUsedByLaterCloseProof()
    {
        var store = Store();
        var opening = Intent("open",false,2m,100m);

        await store.RecordExecutionAsync(
            "cycle-open",
            opening,
            Order("open","PARTIALLY_FILLED",2m,100m,Now.AddMinutes(-30)),
            "strategy-v1",
            default);
        var originalId = await ExecutionEventId("open");

        await store.RecordExecutionAsync(
            "cycle-reduce",
            Intent("reduce",true,1m,105m),
            Order("reduce","FILLED",1m,105m,Now.AddMinutes(-20)),
            "strategy-v1",
            default);

        await store.RecordExecutionAsync(
            "cycle-open",
            opening,
            Order("open","FILLED",2m,101m,Now.AddMinutes(-10)),
            "strategy-v1",
            default);
        Assert.Equal(originalId,await ExecutionEventId("open"));

        await store.RecordExecutionAsync(
            "cycle-close",
            Intent("close",true,1m,110m),
            Order("close","FILLED",1m,110m,Now.AddMinutes(-1)),
            "strategy-v1",
            default);

        var proof = await store.GetPostTradeEntryLedgerProofAsync("close",default);
        Assert.NotNull(proof);
        Assert.Equal(new[] { "open","reduce" },proof!.Events.Select(x => x.ClientOrderId).ToArray());
        Assert.Equal(101m,proof.WeightedEntryPrice);
    }

    [Fact]
    public async Task IdenticalCloseReplayKeepsOneImmutableProof()
    {
        var store = Store();
        await store.RecordExecutionAsync(
            "cycle-open",
            Intent("open",false,1m,100m),
            Order("open","FILLED",1m,100m,Now.AddMinutes(-5)),
            "strategy-v1",
            default);

        var closeIntent = Intent("close",true,1m,110m);
        var closeOrder = Order("close","FILLED",1m,110m,Now.AddSeconds(-1));
        await store.RecordExecutionAsync("cycle-close",closeIntent,closeOrder,"strategy-v1",default);
        var first = await store.GetPostTradeEntryLedgerProofAsync("close",default);

        await store.RecordExecutionAsync("cycle-close",closeIntent,closeOrder,"strategy-v1",default);
        var second = await new AgentSqliteStore(Database,() => Now)
            .GetPostTradeEntryLedgerProofAsync("close",default);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.CanonicalSha256, second!.CanonicalSha256);

        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM post_trade_entry_ledger_proofs WHERE close_client_order_id='close'";
        Assert.Equal(1,Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ProofLedgerIsAppendOnlyAndMetadataMismatchFailsClosed()
    {
        var store = Store();
        await store.RecordExecutionAsync(
            "cycle-open",
            Intent("open",false,1m,100m),
            Order("open","FILLED",1m,100m,Now.AddMinutes(-5)),
            "strategy-v1",
            default);
        await store.RecordExecutionAsync(
            "cycle-close",
            Intent("close",true,1m,110m),
            Order("close","FILLED",1m,110m,Now.AddSeconds(-1)),
            "strategy-v1",
            default);

        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
            {
                "UPDATE post_trade_entry_ledger_proofs SET weighted_entry_price='999'",
                "DELETE FROM post_trade_entry_ledger_proofs"
            })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            }
        }

        var proof = await store.GetPostTradeEntryLedgerProofAsync("close",default);
        Assert.NotNull(proof);
        Assert.Equal(100m,proof!.WeightedEntryPrice);
    }

    [Fact]
    public void CanonicalBytesRoundTripAfterRestartParsing()
    {
        var proof = PostTradeEntryLedgerProofCanonicalizerV1.Create(
            "close",
            "cycle-close",
            "BTCUSDT",
            "Long",
            0.25m,
            [Event(0,"open",false,1m,100m,Now.AddMinutes(-1))]);

        Assert.True(PostTradeEntryLedgerProofCanonicalizerV1.TryDeserialize(
            proof.CanonicalBytes,
            proof.CanonicalSha256,
            out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(proof.CanonicalSha256,parsed!.CanonicalSha256);
        Assert.Equal(proof.CanonicalBytes,parsed.CanonicalBytes);
    }

    private AgentSqliteStore Store() => new(Database,() => Now);

    private static ExecutionIntent Intent(
        string clientOrderId,
        bool reduceOnly,
        decimal quantity,
        decimal expectedPrice) =>
        new(
            "BTCUSDT",
            PositionSide.Long,
            quantity,
            reduceOnly,
            90m,
            170m,
            clientOrderId,
            "test",
            reduceOnly ? DecisionAction.CloseLong : DecisionAction.OpenLong,
            ExecutionOrderType.Market,
            0m,
            expectedPrice);

    private static ExchangeOrder Order(
        string clientOrderId,
        string status,
        decimal quantity,
        decimal price,
        DateTimeOffset updatedAt) =>
        new(
            "BTCUSDT",
            "venue-" + clientOrderId,
            clientOrderId,
            status,
            quantity,
            price,
            "MARKET",
            PositionSide.Long,
            false,
            updatedAt.UtcDateTime);

    private static PostTradeEntryLedgerEventV1 Event(
        int sequence,
        string clientOrderId,
        bool reduceOnly,
        decimal quantity,
        decimal averagePrice,
        DateTimeOffset occurredAt) =>
        new(
            sequence,
            "cycle-" + clientOrderId,
            clientOrderId,
            reduceOnly,
            quantity,
            averagePrice,
            averagePrice,
            "FILLED",
            occurredAt,
            occurredAt);

    private async Task<long> ExecutionEventId(string clientOrderId)
    {
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM execution_events WHERE client_order_id=$client";
        command.Parameters.AddWithValue("$client",clientOrderId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory,true);
    }
}

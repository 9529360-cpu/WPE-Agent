using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class OrderFaultInjectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentStatus _originalStatus = ServiceLocator.SystemState.Status;

    public OrderFaultInjectionTests()
    {
        ServiceLocator.SystemState.Status = AgentStatus.Running;
    }

    [Fact]
    public async Task PartialOpeningFill_IsProtectedAndStopsThePlan()
    {
        var exchange = new ScriptedExchange
        {
            PlaceMarket = request => Order(request, "CANCELED", 0.004m)
        };
        var (executor, store) = CreateExecutor(exchange);
        var intent = Opening("partial-open", 0.01m);

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            executor.ExecutePlanAsync("cycle-1", [intent, Opening("must-not-run", 0.01m)], 10, true, CancellationToken.None));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Single(exchange.MarketRequests);
        Assert.Single(exchange.ProtectionRequests);
        Assert.Empty(await store.GetRecoverableIntentsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PartialRiskReduction_StopsThePlanWithoutAddingProtection()
    {
        var exchange = new ScriptedExchange
        {
            PlaceMarket = request => Order(request, "CANCELED", 0.004m)
        };
        var (executor, store) = CreateExecutor(exchange);
        var intent = Opening("partial-close", 0.01m) with { ReduceOnly = true, Action = DecisionAction.CloseLong };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecutePlanAsync("cycle-1", [intent, Opening("must-not-run", 0.01m)], 10, true, CancellationToken.None));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Single(exchange.MarketRequests);
        Assert.Empty(exchange.ProtectionRequests);
        Assert.Empty(await store.GetRecoverableIntentsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProtectionFailure_EmergencyClosesOnlyTheConfirmedFill()
    {
        var exchange = new ScriptedExchange
        {
            PlaceMarket = request => request.ReduceOnly
                ? Order(request, "FILLED", request.Quantity)
                : Order(request, "FILLED", 0.006m),
            ProtectionFailure = new InvalidOperationException("protection rejected")
        };
        var (executor, store) = CreateExecutor(exchange);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("cycle-1", Opening("protect-fails", 0.01m), 10, true, CancellationToken.None));

        Assert.Contains("ProtectionEmergency", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, exchange.MarketRequests.Count);
        var emergency = exchange.MarketRequests[1];
        Assert.True(emergency.ReduceOnly);
        Assert.Equal(0.006m, emergency.Quantity);
        Assert.EndsWith("-E", emergency.ClientOrderId, StringComparison.Ordinal);
        Assert.Empty(await store.GetRecoverableIntentsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProtectionFailure_WithUnconfirmedEmergencyClose_RemainsRecoverable()
    {
        var exchange = new ScriptedExchange
        {
            PlaceMarket = request => request.ReduceOnly
                ? Order(request, "REJECTED", 0m)
                : Order(request, "FILLED", request.Quantity),
            ProtectionFailure = new InvalidOperationException("protection rejected")
        };
        var (executor, store) = CreateExecutor(exchange);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("cycle-1", Opening("emergency-unknown", 0.01m), 10, true, CancellationToken.None));

        Assert.Contains("ProtectionEmergencyUnknown", error.Message, StringComparison.Ordinal);
        var pending = Assert.Single(await store.GetRecoverableIntentsAsync(CancellationToken.None));
        Assert.Equal("EMERGENCY_UNKNOWN", pending.Status);
    }

    [Fact]
    public async Task FillArrivingDuringTimeoutPolling_DoesNotSendCancel()
    {
        var exchange = new ScriptedExchange
        {
            PlaceMarket = request => Order(request, "NEW", 0m),
            FindAfterPlacement = request => Order(request, "FILLED", request.Quantity)
        };
        var (executor, _) = CreateExecutor(exchange);

        var result = await executor.ExecuteAsync("cycle-1", Opening("fill-race", 0.01m), 10, true, CancellationToken.None);

        Assert.Contains("Execution.Protected", result, StringComparison.Ordinal);
        Assert.Equal(0, exchange.CancelRequests);
    }

    [Fact]
    public async Task TimeoutAfterExchangeAcceptedOrder_RequeriesInsteadOfSubmittingAgain()
    {
        var exchange = new ScriptedExchange { ThrowTimeoutAfterAccept = true };
        var (executor, _) = CreateExecutor(exchange);

        var result = await executor.ExecuteAsync("cycle-1", Opening("accepted-timeout", 0.01m), 10, true, CancellationToken.None);

        Assert.Contains("Execution.Protected", result, StringComparison.Ordinal);
        Assert.Single(exchange.MarketRequests);
        Assert.True(exchange.FindRequests >= 2);
    }

    [Fact]
    public async Task OrderTimeout_CancelsAndUsesVirtualTime()
    {
        var exchange = new TimeoutExchange();
        var scheduler = new VirtualPollScheduler();
        var (executor, _) = CreateExecutor(exchange, scheduler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("cycle-1", Opening("times-out", 0.01m), 10, true, CancellationToken.None));

        Assert.Contains("Execution.Unconfirmed", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, exchange.CancelAttempts);
        Assert.True(scheduler.DelayCalls >= 2);
    }

    [Fact]
    public async Task CancelFailure_RemainsUnknownAndRecoverable()
    {
        var exchange = new TimeoutExchange { CancelFailure = true };
        var scheduler = new VirtualPollScheduler();
        var (executor, store) = CreateExecutor(exchange, scheduler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("cycle-1", Opening("cancel-fails", 0.01m), 10, true, CancellationToken.None));

        Assert.Contains("Execution.Unconfirmed", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, exchange.CancelAttempts);
        Assert.Equal("UNKNOWN", Assert.Single(await store.GetRecoverableIntentsAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task PartialOpeningFillWithoutTerminalCancelAck_RemainsRecoverableUntilTerminalObservation()
    {
        var exchange = new TimeoutExchange
        {
            InitialExecutedQuantity = 0.004m,
            RemainPartialAfterCancel = true,
            TerminalExecutedQuantity = 0.006m
        };
        var scheduler = new VirtualPollScheduler();
        var (executor, store) = CreateExecutor(exchange, scheduler);
        var intent = Opening("partial-open-unacked-cancel", 0.01m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("cycle-1", intent, 10, true, CancellationToken.None));

        var pending = Assert.Single(await store.GetRecoverableIntentsAsync(CancellationToken.None));
        Assert.Equal("PROTECTED_PARTIAL_PENDING", pending.Status);
        Assert.Single(exchange.ProtectionRequests);

        var firstRecovery = await executor.RecoverPendingAsync(CancellationToken.None);
        Assert.False(firstRecovery.SafeToIncreaseRisk);
        Assert.Equal("PROTECTED_PARTIAL_PENDING", Assert.Single(await store.GetRecoverableIntentsAsync(CancellationToken.None)).Status);
        Assert.Single(exchange.ProtectionRequests);

        exchange.RemainPartialAfterCancel = false;
        var settled = await executor.RecoverPendingAsync(CancellationToken.None);

        Assert.True(settled.SafeToIncreaseRisk);
        Assert.Empty(await store.GetRecoverableIntentsAsync(CancellationToken.None));
        Assert.Equal(2, exchange.ProtectionRequests.Count);
        Assert.Equal("PROTECTED_PARTIAL", await store.GetOrderIntentStatusAsync(intent.ClientOrderId, CancellationToken.None));
        var position=Assert.Single(await store.GetExecutionPositionLedgerAsync(CancellationToken.None));
        Assert.Equal(0.006m,position.Quantity);
    }

    [Fact]
    public async Task PartialCloseWithoutTerminalCancelAck_RemainsRecoverableAndDoesNotCancelProtection()
    {
        var exchange = new TimeoutExchange { InitialExecutedQuantity = 0.004m, RemainPartialAfterCancel = true };
        var scheduler = new VirtualPollScheduler();
        var (executor, store) = CreateExecutor(exchange, scheduler);
        var intent = Opening("partial-close-unacked-cancel", 0.01m) with { ReduceOnly = true, Action = DecisionAction.CloseLong };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("cycle-1", intent, 10, true, CancellationToken.None));

        var pending = Assert.Single(await store.GetRecoverableIntentsAsync(CancellationToken.None));
        Assert.Equal("COMPLETED_PARTIAL_PENDING", pending.Status);
        Assert.Empty(exchange.ProtectionRequests);

        exchange.RemainPartialAfterCancel = false;
        var settled = await executor.RecoverPendingAsync(CancellationToken.None);

        Assert.False(settled.SafeToIncreaseRisk);
        Assert.Empty(await store.GetRecoverableIntentsAsync(CancellationToken.None));
        Assert.Equal("COMPLETED_PARTIAL", await store.GetOrderIntentStatusAsync(intent.ClientOrderId, CancellationToken.None));
        Assert.Empty(exchange.ProtectionRequests);
    }

    [Fact]
    public async Task ReduceOnlyRecoveryPartialOpenNeverMasqueradesAsTerminalPartial()
    {
        var exchange = new TimeoutExchange { InitialExecutedQuantity = 0.004m, RemainPartialAfterCancel = true };
        var scheduler = new VirtualPollScheduler();
        var (executor, store) = CreateExecutor(exchange, scheduler);
        var intent = Opening("recovery-partial-open", 0.01m) with { ReduceOnly = true, Action = DecisionAction.CloseLong, OrderType = ExecutionOrderType.Market };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteReduceOnlyRecoveryAsync("cycle-recovery", intent, CancellationToken.None));

        var pending = Assert.Single(await store.GetRecoverableIntentsAsync(CancellationToken.None));
        Assert.Equal("COMPLETED_PARTIAL_PENDING", pending.Status);
    }

    [Fact]
    public async Task CanceledObservation_FollowedByLateFill_IsHandledAsFilled()
    {
        var exchange = new TimeoutExchange { FillAfterFirstPostCancelQuery = true };
        var scheduler = new VirtualPollScheduler();
        var (executor, store) = CreateExecutor(exchange, scheduler);

        var result = await executor.ExecuteAsync("cycle-1", Opening("late-fill", 0.01m), 10, true, CancellationToken.None);

        Assert.Contains("Execution.Protected", result, StringComparison.Ordinal);
        Assert.Equal(1, exchange.CancelAttempts);
        Assert.True(exchange.PostCancelQueries >= 2);
        Assert.Single(exchange.ProtectionRequests);
        Assert.Empty(await store.GetRecoverableIntentsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentSameClientOrderId_ConvergesOnSingleAcceptedExchangeOrder()
    {
        var exchange = new ConcurrentIdempotentExchange();
        var (executor, _) = CreateExecutor(exchange);
        var intent = Opening("concurrent-client-id", 0.01m);

        var results = await Task.WhenAll(
            executor.ExecuteAsync("cycle-a", intent, 10, true, CancellationToken.None),
            executor.ExecuteAsync("cycle-b", intent, 10, true, CancellationToken.None));

        Assert.All(results, result => Assert.Contains("Execution.Protected", result, StringComparison.Ordinal));
        Assert.Equal(1, exchange.AcceptedOrders);
        Assert.Equal(1, exchange.PlaceAttempts);
    }

    public void Dispose()
    {
        ServiceLocator.SystemState.Status = _originalStatus;
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private (ReliableOrderExecutor Executor, AgentSqliteStore Store) CreateExecutor(IExchangeAdapter exchange,IOrderPollScheduler? scheduler=null)
    {
        var store = new AgentSqliteStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        return (scheduler is null?new ReliableOrderExecutor(exchange,store):new ReliableOrderExecutor(exchange,store,scheduler), store);
    }

    private static ExecutionIntent Opening(string clientOrderId, decimal quantity) => new(
        "BTCUSDT", PositionSide.Long, quantity, false, 49_000m, 51_000m, clientOrderId, "fault injection", DecisionAction.OpenLong);

    private static ExchangeOrder Order(MarketRequest request, string status, decimal executedQuantity) => new(
        request.Symbol, "42", request.ClientOrderId, status, executedQuantity, 50_000m, "MARKET", request.Side, false, DateTime.UtcNow);

    private sealed record MarketRequest(string Symbol, PositionSide Side, decimal Quantity, string ClientOrderId, bool ReduceOnly);

    private class ScriptedExchange : ExchangeStub
    {
        private readonly Dictionary<string, ExchangeOrder> _orders = new(StringComparer.Ordinal);
        public Func<MarketRequest, ExchangeOrder>? PlaceMarket { get; init; }
        public Func<MarketRequest, ExchangeOrder>? FindAfterPlacement { get; init; }
        public Exception? ProtectionFailure { get; init; }
        public bool ThrowTimeoutAfterAccept { get; init; }
        public List<MarketRequest> MarketRequests { get; } = [];
        public List<string> ProtectionRequests { get; } = [];
        public int FindRequests { get; private set; }
        public int CancelRequests { get; private set; }

        public override Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct)
        {
            var request = new MarketRequest(symbol, side, quantity, clientOrderId, reduceOnly);
            MarketRequests.Add(request);
            var order = PlaceMarket?.Invoke(request) ?? Order(request, "FILLED", quantity);
            _orders[clientOrderId] = order;
            if (ThrowTimeoutAfterAccept)
                throw new TimeoutException("response lost after exchange accepted order");
            return Task.FromResult(order);
        }

        public override Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct)
        {
            FindRequests++;
            if (!_orders.TryGetValue(clientOrderId, out var order))
                return Task.FromResult<ExchangeOrder?>(null);
            if (FindAfterPlacement is not null)
            {
                var request = MarketRequests.Single(x => x.ClientOrderId == clientOrderId);
                order = FindAfterPlacement(request);
                _orders[clientOrderId] = order;
            }
            return Task.FromResult<ExchangeOrder?>(order);
        }

        public override Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct)
        {
            ProtectionRequests.Add(groupId);
            if (ProtectionFailure is not null)
                throw ProtectionFailure;
            return Task.FromResult(new ExchangeOrder(symbol,"100", groupId + "-SL", "NEW", 0, 0, "STOP_MARKET", sideToClose, true, DateTime.UtcNow));
        }

        public override Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct)
        {
            CancelRequests++;
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrentIdempotentExchange : ExchangeStub
    {
        private readonly ConcurrentDictionary<string, ExchangeOrder> _orders = new(StringComparer.Ordinal);
        private int _placeAttempts;
        private int _acceptedOrders;
        public int PlaceAttempts => _placeAttempts;
        public int AcceptedOrders => _acceptedOrders;

        public override Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct)
        {
            if (_orders.TryGetValue(clientOrderId, out var existing))
                return Task.FromResult<ExchangeOrder?>(existing);
            return Task.FromResult<ExchangeOrder?>(null);
        }

        public override Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct)
        {
            Interlocked.Increment(ref _placeAttempts);
            var order = new ExchangeOrder(symbol,"42", clientOrderId, "FILLED", quantity, 50_000m, "MARKET", side, false, DateTime.UtcNow);
            if (!_orders.TryAdd(clientOrderId, order))
                throw new TimeoutException("duplicate request raced with accepted order");
            Interlocked.Increment(ref _acceptedOrders);
            return Task.FromResult(order);
        }

        public override Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct) =>
            Task.FromResult(new ExchangeOrder(symbol,"100", groupId + "-SL", "NEW", 0, 0, "STOP_MARKET", sideToClose, true, DateTime.UtcNow));
    }

    private sealed class TimeoutExchange : ExchangeStub
    {
        private ExchangeOrder? _order;
        private bool _cancelAttempted;
        public bool CancelFailure { get; init; }
        public bool FillAfterFirstPostCancelQuery { get; init; }
        public decimal InitialExecutedQuantity { get; init; }
        public decimal TerminalExecutedQuantity { get; init; }
        public bool RemainPartialAfterCancel { get; set; }
        public int CancelAttempts { get; private set; }
        public int PostCancelQueries { get; private set; }
        public List<string> ProtectionRequests { get; } = [];

        public override Task<ExchangeOrder> PlaceMarketAsync(string symbol,PositionSide side,decimal quantity,string clientOrderId,bool reduceOnly,CancellationToken ct)
        {
            var partial=InitialExecutedQuantity>0;
            _order=new ExchangeOrder(symbol,"42",clientOrderId,partial?"PARTIALLY_FILLED":"NEW",InitialExecutedQuantity,partial?50_000m:0m,"MARKET",side,false,DateTime.UtcNow);return Task.FromResult(_order);
        }

        public override Task<ExchangeOrder?> FindOrderAsync(string symbol,string clientOrderId,CancellationToken ct)
        {
            if(_order is null)return Task.FromResult<ExchangeOrder?>(null);
            if(!_cancelAttempted)return Task.FromResult<ExchangeOrder?>(_order);
            PostCancelQueries++;
            if(FillAfterFirstPostCancelQuery&&PostCancelQueries>=2)_order=_order with{Status="FILLED",ExecutedQuantity=0.01m,AvgPrice=50_000m};
            else if(RemainPartialAfterCancel)_order=_order with{Status="PARTIALLY_FILLED"};
            else if(!CancelFailure)_order=_order with
            {
                Status="CANCELED",
                ExecutedQuantity=TerminalExecutedQuantity>0?TerminalExecutedQuantity:_order.ExecutedQuantity,
                AvgPrice=TerminalExecutedQuantity>0?50_000m:_order.AvgPrice
            };
            return Task.FromResult<ExchangeOrder?>(_order);
        }

        public override Task CancelOrderAsync(string symbol,string orderId,CancellationToken ct)
        {
            CancelAttempts++;_cancelAttempted=true;if(CancelFailure)throw new InvalidOperationException("cancel transport failure");return Task.CompletedTask;
        }

        public override Task<ExchangeOrder> PlaceProtectionAsync(string symbol,PositionSide sideToClose,decimal stopLoss,decimal takeProfit,string groupId,CancellationToken ct)
        {
            ProtectionRequests.Add(groupId);return Task.FromResult(new ExchangeOrder(symbol,"100",groupId+"-SL","NEW",0,0,"STOP_MARKET",sideToClose,true,DateTime.UtcNow));
        }
    }

    private sealed class VirtualPollScheduler : IOrderPollScheduler
    {
        public TimeSpan OrderTimeout => TimeSpan.FromSeconds(2);
        public TimeSpan PollInterval => TimeSpan.FromSeconds(1);
        public TimeSpan PostCancelWindow => TimeSpan.FromSeconds(3);
        public DateTimeOffset UtcNow { get; private set; } = new(2026,7,20,0,0,0,TimeSpan.Zero);
        public int DelayCalls { get; private set; }
        public ValueTask DelayAsync(CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();DelayCalls++;UtcNow+=PollInterval;return ValueTask.CompletedTask;}
    }

    private abstract class ExchangeStub : IExchangeAdapter
    {
        public ExchangeEnvironment Environment => ExchangeEnvironment.Testnet;
        public virtual Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<ExchangeOrder> PlaceLimitAsync(string symbol,PositionSide side,decimal quantity,decimal price,string clientOrderId,bool reduceOnly,CancellationToken ct) => PlaceMarketAsync(symbol,side,quantity,clientOrderId,reduceOnly,ct);
        public virtual Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct) => Task.CompletedTask;
        public Task SetLeverageAsync(string symbol, int leverage, CancellationToken ct) => Task.CompletedTask;
        public Task SetMarginModeAsync(string symbol, bool isolated, CancellationToken ct) => Task.CompletedTask;
        public Task SetHedgeModeAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExchangeOrder>>(Array.Empty<ExchangeOrder>());
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<TradingRule> GetRulesAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol, CancellationToken ct) => Task.FromResult(HealthyMarket(symbol));
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol,string interval,int limit,CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static MarketEvidence HealthyMarket(string symbol)=>new(symbol,50_000m,49_000m,51_000m,50,0,0,0,new(0,0,0,0,0,0,0),DateTime.UtcNow){Quality=new(){QualityScore=100,LiquidityScore=1,SpreadBps=1,AtrPercent=.01}};
    }
}

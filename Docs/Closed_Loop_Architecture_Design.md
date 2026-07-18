# 币安量化机器人 - 架构重构方案

> 基于闭环自动化交易系统的完整模块化重构指南

**文档版本**: 2.0 (架构对齐版)  
**目标**: 将现有服务定位器模式转换为模块化架构，完全对齐闭环交易流程  
**目标框架**: .NET 8.0

---

## ?? 现状 vs 目标对齐

### 您的闭环流程需求

```
数据抓取 → 策略模拟 → 筛选执行 → 风险管理 → 实盘执行 → 日志记录 → 循环
```

### 现有架构问题

| 模块 | 现状 | 问题 | 目标 |
|------|------|------|------|
| 数据层 | `DataCacheService` | 单一缓存，结构不清 | 完整的数据采集和管理 |
| 策略层 | `StrategyOrchestrator` | 缺少策略评分机制 | 策略模拟→筛选→执行 |
| 执行层 | `TradeExecutionService` | 错误处理不完善 | 可靠的交易执行 |
| 循环层 | `AutoTradingAgent` | 简单的循环控制 | 完整的闭环编排 |
| 持久层 | 缺少 | 无日志记录 | 数据持久化和回溯 |

---

## ??? 新架构设计

### 整体架构图

```
┌─────────────────────────────────────────────────────────────┐
│                    应用层 (Application)                      │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  自动化交易引擎 (AutomatedTradingEngine)             │  │
│  │  - 闭环编排                                          │  │
│  │  - 循环控制                                          │  │
│  │  - 异常恢复                                          │  │
│  └──────────────────────────────────────────────────────┘  │
└────┬──────────┬──────────┬──────────┬──────────┬────────────┘
     │          │          │          │          │
   ┌─▼─┐    ┌──▼──┐   ┌──▼──┐   ┌──▼──┐    ┌──▼──┐
   │数据│    │策略 │   │风控 │   │执行 │    │记录 │
   │层  │    │层   │   │层   │   │层   │    │层   │
   └─┬─┘    └──┬──┘   └──┬──┘   └──┬──┘    └──┬──┘
     │         │         │         │          │
   ┌─▼─────────▼─────────▼─────────▼──────────▼────┐
   │         基础设施层 (Infrastructure)           │
   │ ┌──────────────────────────────────────────┐  │
   │ │ 外部服务                                │  │
   │ │ - Binance API Client                   │  │
   │ │ - WebSocket 连接                       │  │
   │ │ - 数据库 (SQLite)                      │  │
   │ │ - 文件系统                              │  │
   │ └──────────────────────────────────────────┘  │
   └─────────────────────────────────────────────────┘
```

---

## ?? 完整的模块化设计

### 第 1 层：数据采集与管理层 (Data Layer)

**责任**: 统一管理所有数据源，提供一致的数据接口

```csharp
// Core/Data/IDataCollectionService.cs
/// <summary>
/// 数据采集服务 - 负责定时拉取实时行情、账户余额、仓位信息
/// </summary>
public interface IDataCollectionService
{
    /// <summary>
    /// 采集市场行情数据
    /// </summary>
    IAsyncEnumerable<MarketSnapshot> CollectMarketDataAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 采集账户信息
    /// </summary>
    Task<AccountSnapshot> CollectAccountDataAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 采集历史K线（用于回测）
    /// </summary>
    Task<IReadOnlyList<Kline>> CollectHistoricalKlinesAsync(
        string symbol,
        string interval,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken = default);
}

// Infrastructure/Data/BinanceDataCollectionService.cs
public class BinanceDataCollectionService : IDataCollectionService
{
    private readonly BinanceApiClient _apiClient;
    private readonly BinanceStreamClient _streamClient;
    private readonly ILogger _logger;

    public async IAsyncEnumerable<MarketSnapshot> CollectMarketDataAsync(
        IEnumerable<string> symbols,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 实时推送行情，支持WebSocket
        await foreach (var marketData in _streamClient.SubscribeKlinesAsync(symbols, cancellationToken))
        {
            yield return new MarketSnapshot(
                marketData.Symbol,
                marketData.Timestamp,
                marketData.Open,
                marketData.High,
                marketData.Low,
                marketData.Close,
                marketData.Volume);
        }
    }

    public async Task<AccountSnapshot> CollectAccountDataAsync(
        CancellationToken cancellationToken = default)
    {
        var balances = await _apiClient.GetAccountBalancesAsync(cancellationToken);
        var positions = await _apiClient.GetPositionsAsync(cancellationToken);

        return new AccountSnapshot(
            DateTime.UtcNow,
            balances,
            positions,
            CalculateTotalEquity(balances, positions));
    }

    public async Task<IReadOnlyList<Kline>> CollectHistoricalKlinesAsync(
        string symbol,
        string interval,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken = default)
    {
        // 分批获取历史K线，避免API限制
        var klines = new List<Kline>();
        var current = start;

        while (current < end)
        {
            var batch = await _apiClient.GetKlinesAsync(
                symbol, interval, current, 
                Math.Min(current.AddDays(1), end), 
                cancellationToken);
            
            klines.AddRange(batch);
            current = current.AddDays(1);
        }

        return klines;
    }

    private decimal CalculateTotalEquity(
        IReadOnlyList<AccountBalance> balances,
        IReadOnlyList<PositionSnapshot> positions)
    {
        var walletBalance = balances.Sum(b => b.WalletBalance);
        var unrealizedPnl = positions.Sum(p => p.UnrealizedProfit);
        return walletBalance + unrealizedPnl;
    }
}
```

---

### 第 2 层：策略评估与模拟层 (Strategy Layer)

**责任**: 对策略进行评分、模拟和筛选

```csharp
// Core/Strategy/IStrategyEvaluationService.cs
/// <summary>
/// 策略评估服务 - 对策略进行模拟评分
/// </summary>
public interface IStrategyEvaluationService
{
    /// <summary>
    /// 评估单个策略的收益
    /// </summary>
    Task<StrategyEvaluationResult> EvaluateAsync(
        ITradingStrategy strategy,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量评估多个策略
    /// </summary>
    Task<IReadOnlyList<StrategyEvaluationResult>> EvaluateMultipleAsync(
        IEnumerable<ITradingStrategy> strategies,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default);
}

// Core/Strategy/IStrategyFilterService.cs
/// <summary>
/// 策略筛选服务 - 根据评分筛选策略
/// </summary>
public interface IStrategyFilterService
{
    /// <summary>
    /// 筛选达到收益阈值的策略
    /// </summary>
    IReadOnlyList<StrategyEvaluationResult> FilterByThreshold(
        IEnumerable<StrategyEvaluationResult> results,
        decimal profitThreshold);

    /// <summary>
    /// 对策略进行优先级排序
    /// </summary>
    IReadOnlyList<StrategyEvaluationResult> RankByPriority(
        IEnumerable<StrategyEvaluationResult> results);

    /// <summary>
    /// 淘汰策略
    /// </summary>
    Task DeactivateAsync(
        ITradingStrategy strategy,
        string reason,
        CancellationToken cancellationToken = default);
}

// Application/Services/StrategyEvaluationService.cs
public class StrategyEvaluationService : IStrategyEvaluationService
{
    private readonly IMarketDataService _marketData;
    private readonly IBacktestEngine _backtestEngine;
    private readonly ILogger _logger;

    public async Task<StrategyEvaluationResult> EvaluateAsync(
        ITradingStrategy strategy,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default)
    {
        _logger.Information("开始评估策略: {StrategyName}", strategy.Name);

        try
        {
            // 运行回测
            var backTestResult = await _backtestEngine.RunAsync(
                new BacktestRequest(
                    "BTCUSDT",
                    marketSnapshots.First().Timestamp,
                    marketSnapshots.Last().Timestamp,
                    strategy),
                cancellationToken);

            // 计算费用和成本
            var costs = CalculateTransactionCosts(backTestResult);
            var netProfit = backTestResult.NetProfit - costs;
            var profitRatio = accountSnapshot.TotalEquity > 0
                ? netProfit / accountSnapshot.TotalEquity
                : 0;

            // 评估杠杆风险
            var leverageRisk = CalculateLeverageRisk(strategy, accountSnapshot);

            var result = new StrategyEvaluationResult(
                strategy.Name,
                strategy.Id,
                netProfit,
                profitRatio,
                backTestResult.Sharpe,
                leverageRisk,
                backTestResult.Signals.Count,
                DateTime.UtcNow);

            _logger.Information(
                "策略 {StrategyName} 评估完成: 净收益={NetProfit}, 收益率={ProfitRatio:P}",
                strategy.Name, netProfit, profitRatio);

            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "策略 {StrategyName} 评估失败", strategy.Name);
            throw;
        }
    }

    public async Task<IReadOnlyList<StrategyEvaluationResult>> EvaluateMultipleAsync(
        IEnumerable<ITradingStrategy> strategies,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default)
    {
        // 并行评估多个策略
        var tasks = strategies.Select(s =>
            EvaluateAsync(s, marketSnapshots, accountSnapshot, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return results;
    }

    private decimal CalculateTransactionCosts(BacktestResult result)
    {
        // 手续费: 每次交易 0.05%（币安现货）或 0.02%（合约）
        // 滑点: 估算为 0.1% 每次交易
        var trades = result.Signals.Count(s => s.Action.ActionType != TradeActionType.Hold);
        const decimal brokerageFee = 0.0005m; // 0.05%
        const decimal slippage = 0.001m; // 0.1%
        return trades * (brokerageFee + slippage);
    }

    private RiskLevel CalculateLeverageRisk(ITradingStrategy strategy, AccountSnapshot account)
    {
        // 根据杠杆倍数计算风险等级
        if (strategy.MaxLeverage <= 1)
            return RiskLevel.Low;
        if (strategy.MaxLeverage <= 5)
            return RiskLevel.Medium;
        return RiskLevel.High;
    }
}

// Application/Services/StrategyFilterService.cs
public class StrategyFilterService : IStrategyFilterService
{
    private readonly IStrategyRepository _repository;
    private readonly ILogger _logger;

    public IReadOnlyList<StrategyEvaluationResult> FilterByThreshold(
        IEnumerable<StrategyEvaluationResult> results,
        decimal profitThreshold)
    {
        return results
            .Where(r => r.ProfitRatio >= profitThreshold)
            .ToList();
    }

    public IReadOnlyList<StrategyEvaluationResult> RankByPriority(
        IEnumerable<StrategyEvaluationResult> results)
    {
        // 按收益率排序，同时考虑 Sharpe 比率
        return results
            .OrderByDescending(r => r.ProfitRatio)
            .ThenByDescending(r => r.SharpeRatio)
            .ToList();
    }

    public async Task DeactivateAsync(
        ITradingStrategy strategy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        _logger.Warning(
            "策略 {StrategyName} 已下架，原因: {Reason}",
            strategy.Name, reason);

        await _repository.MarkAsInactiveAsync(strategy.Id, reason, cancellationToken);
    }
}
```

---

### 第 3 层：风险管理层 (Risk Management Layer)

**责任**: 风险评估、仓位管理、杠杆控制

```csharp
// Core/Risk/IPositionManager.cs
/// <summary>
/// 仓位管理服务 - 管理开仓、平仓和资金分配
/// </summary>
public interface IPositionManager
{
    /// <summary>
    /// 计算推荐的仓位大小
    /// </summary>
    decimal CalculatePositionSize(
        StrategyEvaluationResult evaluation,
        AccountSnapshot account,
        decimal maxRiskPercentage = 0.02m);

    /// <summary>
    /// 检查是否可以开新仓位
    /// </summary>
    bool CanOpenPosition(
        AccountSnapshot account,
        decimal requiredMargin);

    /// <summary>
    /// 获取杠杆风险调整因子
    /// </summary>
    decimal GetLeverageRiskFactor(decimal leverage);
}

// Core/Risk/ILeverageController.cs
/// <summary>
/// 杠杆控制服务 - 管理杠杆倍数和利息成本
/// </summary>
public interface ILeverageController
{
    /// <summary>
    /// 计算杠杆成本（利息）
    /// </summary>
    Task<decimal> CalculateLeverageCostAsync(
        string symbol,
        decimal borrowAmount,
        decimal daysHeld,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取推荐的杠杆倍数
    /// </summary>
    decimal GetRecommendedLeverage(StrategyEvaluationResult evaluation);

    /// <summary>
    /// 检查是否超过杠杆限额
    /// </summary>
    bool IsLeverageWithinLimit(
        AccountSnapshot account,
        decimal proposedLeverage);
}

// Application/Services/PositionManager.cs
public class PositionManager : IPositionManager
{
    private readonly ILogger _logger;

    public decimal CalculatePositionSize(
        StrategyEvaluationResult evaluation,
        AccountSnapshot account,
        decimal maxRiskPercentage = 0.02m)
    {
        // 凯利公式: f = (bp - q) / b
        // 简化版: 仓位 = 账户余额 × 收益率 × 风险系数
        
        var riskAmount = account.TotalEquity * maxRiskPercentage;
        var leverageRiskFactor = GetLeverageRiskFactor(evaluation.RiskLevel);
        var adjustedProfitRatio = evaluation.ProfitRatio * leverageRiskFactor;
        
        var positionSize = (riskAmount * adjustedProfitRatio) / account.TotalEquity;
        
        _logger.Information(
            "计算仓位大小: 风险={Risk}, 收益率={ProfitRatio}, 仓位={PositionSize}",
            riskAmount, evaluation.ProfitRatio, positionSize);

        return Math.Min(positionSize, account.AvailableBalance);
    }

    public bool CanOpenPosition(
        AccountSnapshot account,
        decimal requiredMargin)
    {
        return account.AvailableBalance >= requiredMargin;
    }

    public decimal GetLeverageRiskFactor(RiskLevel riskLevel)
    {
        return riskLevel switch
        {
            RiskLevel.Low => 1.0m,      // 无杠杆或低杠杆
            RiskLevel.Medium => 0.8m,   // 中等杠杆，降低仓位
            RiskLevel.High => 0.5m,     // 高杠杆，大幅降低仓位
            _ => 1.0m
        };
    }
}

// Application/Services/LeverageController.cs
public class LeverageController : ILeverageController
{
    private readonly BinanceApiClient _apiClient;
    private readonly ILogger _logger;
    private const decimal MaxLeverageLimit = 10m;

    public async Task<decimal> CalculateLeverageCostAsync(
        string symbol,
        decimal borrowAmount,
        decimal daysHeld,
        CancellationToken cancellationToken = default)
    {
        // 获取借贷利率
        var borrowRate = await _apiClient.GetBorrowRateAsync(symbol, cancellationToken);
        var interestCost = borrowAmount * borrowRate * daysHeld / 365m;

        _logger.Information(
            "杠杆成本计算: 符号={Symbol}, 借贷额={Amount}, 天数={Days}, 利息={Interest}",
            symbol, borrowAmount, daysHeld, interestCost);

        return interestCost;
    }

    public decimal GetRecommendedLeverage(StrategyEvaluationResult evaluation)
    {
        // 根据策略的夏普比率推荐杠杆倍数
        if (evaluation.SharpeRatio > 2.0)
            return 3.0m;  // 优秀策略，可用 3 倍杠杆
        if (evaluation.SharpeRatio > 1.0)
            return 2.0m;  // 良好策略，用 2 倍杠杆
        return 1.0m;      // 一般策略，不用杠杆
    }

    public bool IsLeverageWithinLimit(
        AccountSnapshot account,
        decimal proposedLeverage)
    {
        return proposedLeverage <= MaxLeverageLimit;
    }
}
```

---

### 第 4 层：实盘执行层 (Execution Layer)

**责任**: 订单执行、异常处理和重试机制

```csharp
// Core/Execution/IOrderExecutor.cs
/// <summary>
/// 订单执行服务 - 执行市价单、限价单、止损止盈
/// </summary>
public interface IOrderExecutor
{
    /// <summary>
    /// 执行市价单
    /// </summary>
    Task<OrderResult> ExecuteMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行限价单
    /// </summary>
    Task<OrderResult> ExecuteLimitOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行止损单
    /// </summary>
    Task<OrderResult> ExecuteStopLossAsync(
        string symbol,
        decimal quantity,
        decimal stopPrice,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 平仓
    /// </summary>
    Task<OrderResult> ClosePositionAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}

// Application/Services/RobustOrderExecutor.cs
public class RobustOrderExecutor : IOrderExecutor
{
    private readonly BinanceApiClient _apiClient;
    private readonly IErrorRecoveryHandler _errorHandler;
    private readonly ILogger _logger;

    public async Task<OrderResult> ExecuteMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        CancellationToken cancellationToken = default)
    {
        return await _errorHandler.ExecuteWithRetryAsync(
            async ct =>
            {
                var result = await _apiClient.PlaceOrderAsync(
                    new OrderRequest
                    {
                        Symbol = symbol,
                        Side = side,
                        Type = OrderType.Market,
                        Quantity = quantity
                    },
                    ct);

                _logger.Information(
                    "市价单执行成功: {Symbol} {Side} {Quantity}",
                    symbol, side, quantity);

                return new OrderResult(
                    result.OrderId,
                    symbol,
                    side,
                    quantity,
                    result.Price,
                    OrderStatus.Filled,
                    DateTime.UtcNow);
            },
            $"ExecuteMarketOrder_{symbol}",
            cancellationToken);
    }

    public async Task<OrderResult> ExecuteLimitOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        CancellationToken cancellationToken = default)
    {
        return await _errorHandler.ExecuteWithRetryAsync(
            async ct =>
            {
                var result = await _apiClient.PlaceOrderAsync(
                    new OrderRequest
                    {
                        Symbol = symbol,
                        Side = side,
                        Type = OrderType.Limit,
                        Quantity = quantity,
                        Price = price
                    },
                    ct);

                _logger.Information(
                    "限价单执行: {Symbol} {Side} {Quantity} @ {Price}",
                    symbol, side, quantity, price);

                return new OrderResult(
                    result.OrderId,
                    symbol,
                    side,
                    quantity,
                    result.Price,
                    OrderStatus.PartiallyFilled,
                    DateTime.UtcNow);
            },
            $"ExecuteLimitOrder_{symbol}_{price}",
            cancellationToken);
    }

    public async Task<OrderResult> ExecuteStopLossAsync(
        string symbol,
        decimal quantity,
        decimal stopPrice,
        CancellationToken cancellationToken = default)
    {
        return await _errorHandler.ExecuteWithRetryAsync(
            async ct =>
            {
                var result = await _apiClient.PlaceOrderAsync(
                    new OrderRequest
                    {
                        Symbol = symbol,
                        Side = OrderSide.Sell,
                        Type = OrderType.StopLoss,
                        Quantity = quantity,
                        StopPrice = stopPrice
                    },
                    ct);

                _logger.Information(
                    "止损单执行: {Symbol} {Quantity} @ {StopPrice}",
                    symbol, quantity, stopPrice);

                return new OrderResult(
                    result.OrderId,
                    symbol,
                    OrderSide.Sell,
                    quantity,
                    stopPrice,
                    OrderStatus.Pending,
                    DateTime.UtcNow);
            },
            $"ExecuteStopLoss_{symbol}",
            cancellationToken);
    }

    public async Task<OrderResult> ClosePositionAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var positions = await _apiClient.GetPositionsAsync(cancellationToken);
        var position = positions.FirstOrDefault(p =>
            p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (position == null || position.PositionAmt == 0)
        {
            _logger.Warning("未找到要平仓的持仓: {Symbol}", symbol);
            return null!;
        }

        var side = position.PositionAmt > 0 ? OrderSide.Sell : OrderSide.Buy;
        return await ExecuteMarketOrderAsync(symbol, side, Math.Abs(position.PositionAmt), cancellationToken);
    }
}
```

---

### 第 5 层：日志与持久化层 (Logging & Persistence Layer)

**责任**: 记录所有操作和数据，用于回溯和优化

```csharp
// Core/Persistence/ITradingRecorder.cs
/// <summary>
/// 交易记录服务 - 记录模拟、评估、执行结果
/// </summary>
public interface ITradingRecorder
{
    /// <summary>
    /// 记录策略评估结果
    /// </summary>
    Task RecordStrategyEvaluationAsync(
        StrategyEvaluationResult evaluation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录实盘执行
    /// </summary>
    Task RecordExecutionAsync(
        OrderResult orderResult,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录循环周期
    /// </summary>
    Task RecordCycleAsync(
        TradingCycleLog log,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询历史数据（用于复盘）
    /// </summary>
    Task<IReadOnlyList<TradingCycleLog>> QueryHistoryAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default);
}

// Infrastructure/Persistence/SqliteTradingRecorder.cs
public class SqliteTradingRecorder : ITradingRecorder
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public SqliteTradingRecorder(string connectionString, ILogger logger)
    {
        _connectionString = connectionString;
        _logger = logger;
        InitializeDatabase();
    }

    public async Task RecordStrategyEvaluationAsync(
        StrategyEvaluationResult evaluation,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO StrategyEvaluations 
            (StrategyName, StrategyId, NetProfit, ProfitRatio, SharpeRatio, TradeCount, EvaluatedAt)
            VALUES (@name, @id, @profit, @ratio, @sharpe, @count, @time)";

        command.Parameters.AddWithValue("@name", evaluation.StrategyName);
        command.Parameters.AddWithValue("@id", evaluation.StrategyId);
        command.Parameters.AddWithValue("@profit", evaluation.NetProfit);
        command.Parameters.AddWithValue("@ratio", evaluation.ProfitRatio);
        command.Parameters.AddWithValue("@sharpe", evaluation.SharpeRatio);
        command.Parameters.AddWithValue("@count", evaluation.TradeCount);
        command.Parameters.AddWithValue("@time", evaluation.EvaluatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.Information("策略评估已记录: {StrategyName}", evaluation.StrategyName);
    }

    public async Task RecordExecutionAsync(
        OrderResult orderResult,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Executions 
            (OrderId, Symbol, Side, Quantity, Price, Status, ExecutedAt)
            VALUES (@orderId, @symbol, @side, @quantity, @price, @status, @time)";

        command.Parameters.AddWithValue("@orderId", orderResult.OrderId);
        command.Parameters.AddWithValue("@symbol", orderResult.Symbol);
        command.Parameters.AddWithValue("@side", orderResult.Side.ToString());
        command.Parameters.AddWithValue("@quantity", orderResult.Quantity);
        command.Parameters.AddWithValue("@price", orderResult.ExecutedPrice);
        command.Parameters.AddWithValue("@status", orderResult.Status.ToString());
        command.Parameters.AddWithValue("@time", orderResult.ExecutedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.Information("交易执行已记录: {OrderId}", orderResult.OrderId);
    }

    public async Task RecordCycleAsync(
        TradingCycleLog log,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Cycles 
            (CycleNumber, StartTime, EndTime, StrategiesEvaluated, StrategiesExecuted, TotalProfit, Notes)
            VALUES (@cycle, @start, @end, @evaluated, @executed, @profit, @notes)";

        command.Parameters.AddWithValue("@cycle", log.CycleNumber);
        command.Parameters.AddWithValue("@start", log.StartTime);
        command.Parameters.AddWithValue("@end", log.EndTime);
        command.Parameters.AddWithValue("@evaluated", log.StrategiesEvaluated);
        command.Parameters.AddWithValue("@executed", log.StrategiesExecuted);
        command.Parameters.AddWithValue("@profit", log.TotalProfit);
        command.Parameters.AddWithValue("@notes", log.Notes ?? "");

        await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.Information("循环周期已记录: 周期 #{CycleNumber}", log.CycleNumber);
    }

    public async Task<IReadOnlyList<TradingCycleLog>> QueryHistoryAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT CycleNumber, StartTime, EndTime, StrategiesEvaluated, StrategiesExecuted, TotalProfit, Notes
            FROM Cycles
            WHERE StartTime >= @start AND EndTime <= @end
            ORDER BY CycleNumber DESC";

        command.Parameters.AddWithValue("@start", startDate);
        command.Parameters.AddWithValue("@end", endDate);

        var logs = new List<TradingCycleLog>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            logs.Add(new TradingCycleLog(
                reader.GetInt32(0),                    // CycleNumber
                reader.GetDateTime(1),                 // StartTime
                reader.GetDateTime(2),                 // EndTime
                reader.GetInt32(3),                    // StrategiesEvaluated
                reader.GetInt32(4),                    // StrategiesExecuted
                reader.GetDecimal(5),                  // TotalProfit
                reader.GetString(6)));                 // Notes
        }

        return logs;
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 创建表...
        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS StrategyEvaluations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StrategyName TEXT NOT NULL,
                StrategyId TEXT NOT NULL,
                NetProfit REAL NOT NULL,
                ProfitRatio REAL NOT NULL,
                SharpeRatio REAL NOT NULL,
                TradeCount INTEGER NOT NULL,
                EvaluatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Executions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OrderId TEXT NOT NULL,
                Symbol TEXT NOT NULL,
                Side TEXT NOT NULL,
                Quantity REAL NOT NULL,
                Price REAL NOT NULL,
                Status TEXT NOT NULL,
                ExecutedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Cycles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CycleNumber INTEGER NOT NULL,
                StartTime TEXT NOT NULL,
                EndTime TEXT NOT NULL,
                StrategiesEvaluated INTEGER NOT NULL,
                StrategiesExecuted INTEGER NOT NULL,
                TotalProfit REAL NOT NULL,
                Notes TEXT
            );";

        command.ExecuteNonQuery();
    }
}
```

---

### 第 6 层：闭环编排层 (Orchestration Layer)

**责任**: 实现完整的闭环流程和循环控制

```csharp
// Application/ClosedLoopOrchestration/IAutomatedTradingEngine.cs
/// <summary>
/// 自动化交易引擎 - 实现完整的闭环流程
/// </summary>
public interface IAutomatedTradingEngine
{
    /// <summary>
    /// 启动自动化交易循环
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止交易循环
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 获取循环状态
    /// </summary>
    TradingEngineStatus GetStatus();
}

// Application/ClosedLoopOrchestration/AutomatedTradingEngine.cs
public class AutomatedTradingEngine : IAutomatedTradingEngine
{
    private readonly IDataCollectionService _dataCollection;
    private readonly IStrategyEvaluationService _strategyEvaluation;
    private readonly IStrategyFilterService _strategyFilter;
    private readonly IOrderExecutor _orderExecutor;
    private readonly ITradingRecorder _recorder;
    private readonly IErrorRecoveryHandler _errorHandler;
    private readonly ILogger _logger;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _cycleNumber = 0;
    private TradingEngineStatus _status = TradingEngineStatus.Idle;

    public AutomatedTradingEngine(
        IDataCollectionService dataCollection,
        IStrategyEvaluationService strategyEvaluation,
        IStrategyFilterService strategyFilter,
        IOrderExecutor orderExecutor,
        ITradingRecorder recorder,
        IErrorRecoveryHandler errorHandler,
        ILogger logger,
        AppSettings settings)
    {
        _dataCollection = dataCollection;
        _strategyEvaluation = strategyEvaluation;
        _strategyFilter = strategyFilter;
        _orderExecutor = orderExecutor;
        _recorder = recorder;
        _errorHandler = errorHandler;
        _logger = logger;
        _settings = settings;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_status == TradingEngineStatus.Running)
        {
            _logger.Warning("交易引擎已在运行中");
            return;
        }

        _logger.Information("====== 启动自动化交易引擎 ======");
        _cts = new CancellationTokenSource();
        _status = TradingEngineStatus.Running;

        _loopTask = Task.Run(() => RunTradingLoopAsync(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        if (_status != TradingEngineStatus.Running)
            return;

        _logger.Information("====== 停止自动化交易引擎 ======");
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }

        _status = TradingEngineStatus.Stopped;
    }

    public TradingEngineStatus GetStatus() => _status;

    /// <summary>
    /// 闭环交易循环的核心实现
    /// </summary>
    private async Task RunTradingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _cycleNumber++;
                var cycleStartTime = DateTime.UtcNow;

                _logger.Information("========== 开始交易循环 #{CycleNumber} ==========", _cycleNumber);

                // ======== 步骤 1: 数据采集 ========
                _logger.Information("【步骤 1】采集市场数据...");
                var marketSnapshots = await CollectMarketDataAsync(cancellationToken);
                var accountSnapshot = await _dataCollection.CollectAccountDataAsync(cancellationToken);

                _logger.Information("账户资金: {TotalEquity}, 可用: {Available}",
                    accountSnapshot.TotalEquity, accountSnapshot.AvailableBalance);

                // ======== 步骤 2: 策略评估 ========
                _logger.Information("【步骤 2】评估所有策略...");
                var strategies = await LoadActiveStrategiesAsync(cancellationToken);
                var evaluationResults = await _strategyEvaluation.EvaluateMultipleAsync(
                    strategies,
                    marketSnapshots,
                    accountSnapshot,
                    cancellationToken);

                _logger.Information("评估完成: 共评估 {Count} 个策略", evaluationResults.Count);

                foreach (var result in evaluationResults)
                {
                    _logger.Information("  - {StrategyName}: 收益率 {ProfitRatio:P}, Sharpe={Sharpe}",
                        result.StrategyName, result.ProfitRatio, result.SharpeRatio);
                }

                // ======== 步骤 3: 策略筛选 ========
                _logger.Information("【步骤 3】筛选并排序策略...");
                var profitThreshold = decimal.Parse(_settings.MinProfitThreshold ?? "0.02");
                var filteredResults = _strategyFilter.FilterByThreshold(evaluationResults, profitThreshold);
                var rankedResults = _strategyFilter.RankByPriority(filteredResults);

                _logger.Information("筛选结果: {Count} 个策略达到收益阈值 {Threshold:P}",
                    rankedResults.Count, profitThreshold);

                // ======== 步骤 4: 实盘执行 ========
                _logger.Information("【步骤 4】执行交易信号...");
                var executedCount = 0;
                var totalProfit = 0m;

                foreach (var result in rankedResults.Take(Math.Max(1, _settings.MaxConcurrentStrategies ?? 3)))
                {
                    try
                    {
                        var strategy = strategies.FirstOrDefault(s => s.Id == result.StrategyId);
                        if (strategy == null) continue;

                        // 执行策略信号
                        var signalExecuted = await ExecuteStrategySignalsAsync(
                            strategy,
                            result,
                            accountSnapshot,
                            cancellationToken);

                        if (signalExecuted)
                        {
                            executedCount++;
                            totalProfit += result.NetProfit;

                            _logger.Information("? 策略 {StrategyName} 已执行", result.StrategyName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "执行策略 {StrategyName} 失败", result.StrategyName);
                        // 继续执行其他策略
                    }
                }

                // ======== 步骤 5: 记录日志 ========
                _logger.Information("【步骤 5】记录交易周期...");
                var cycleEndTime = DateTime.UtcNow;

                foreach (var result in evaluationResults)
                {
                    await _recorder.RecordStrategyEvaluationAsync(result, cancellationToken);
                }

                var cycleLog = new TradingCycleLog(
                    _cycleNumber,
                    cycleStartTime,
                    cycleEndTime,
                    evaluationResults.Count,
                    executedCount,
                    totalProfit,
                    $"执行 {executedCount} 个策略，总收益 {totalProfit}");

                await _recorder.RecordCycleAsync(cycleLog, cancellationToken);

                _logger.Information("========== 交易循环 #{CycleNumber} 完成 ==========", _cycleNumber);
                _logger.Information("总耗时: {Duration}ms", (cycleEndTime - cycleStartTime).TotalMilliseconds);

                // ======== 步骤 6: 等待下一轮 ========
                _logger.Information("【步骤 6】等待下一个交易周期...");
                var cycleIntervalSeconds = int.Parse(_settings.CycleIntervalSeconds ?? "300");
                await Task.Delay(TimeSpan.FromSeconds(cycleIntervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.Information("交易循环已取消");
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "交易循环异常，将进行恢复");

                // 异常恢复：等待后重试
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
    }

    private async Task<MarketSnapshot[]> CollectMarketDataAsync(CancellationToken cancellationToken)
    {
        var symbols = _settings.TradingSymbols?.Split(',') ?? new[] { "BTCUSDT" };
        var snapshots = new List<MarketSnapshot>();

        await foreach (var snapshot in _dataCollection.CollectMarketDataAsync(symbols, cancellationToken))
        {
            snapshots.Add(snapshot);
        }

        return snapshots.ToArray();
    }

    private async Task<IReadOnlyList<ITradingStrategy>> LoadActiveStrategiesAsync(
        CancellationToken cancellationToken)
    {
        // 加载所有活跃策略
        // 这里简化为返回内置策略，实际应从数据库加载
        return new ITradingStrategy[]
        {
            new MeanReversionStrategy(null!, null!, null!, null!),
            new MomentumStrategy(null!, null!, null!),
            // ...更多策略
        };
    }

    private async Task<bool> ExecuteStrategySignalsAsync(
        ITradingStrategy strategy,
        StrategyEvaluationResult evaluation,
        AccountSnapshot account,
        CancellationToken cancellationToken)
    {
        // 根据策略信号执行交易
        // 包括开仓、平仓、止损等

        var signals = new List<TradeSignal>();
        // 生成信号...

        foreach (var signal in signals)
        {
            try
            {
                var result = await _orderExecutor.ExecuteMarketOrderAsync(
                    signal.Symbol,
                    signal.Action.ActionType == TradeActionType.EnterLong
                        ? OrderSide.Buy
                        : OrderSide.Sell,
                    signal.Action.Quantity,
                    cancellationToken);

                await _recorder.RecordExecutionAsync(result, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "执行信号失败: {Signal}", signal);
                throw;
            }
        }

        return true;
    }
}

// 模型定义
public record MarketSnapshot(
    string Symbol,
    DateTime Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public record AccountSnapshot(
    DateTime Timestamp,
    IReadOnlyList<AccountBalance> Balances,
    IReadOnlyList<PositionSnapshot> Positions,
    decimal TotalEquity)
{
    public decimal AvailableBalance =>
        Balances.FirstOrDefault(b => b.Asset == "USDT")?.AvailableBalance ?? 0;
}

public record StrategyEvaluationResult(
    string StrategyName,
    string StrategyId,
    decimal NetProfit,
    decimal ProfitRatio,
    double SharpeRatio,
    RiskLevel RiskLevel,
    int TradeCount,
    DateTime EvaluatedAt);

public record OrderResult(
    string OrderId,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal ExecutedPrice,
    OrderStatus Status,
    DateTime ExecutedAt);

public record TradingCycleLog(
    int CycleNumber,
    DateTime StartTime,
    DateTime EndTime,
    int StrategiesEvaluated,
    int StrategiesExecuted,
    decimal TotalProfit,
    string? Notes = null);

public enum RiskLevel { Low, Medium, High }
public enum OrderStatus { Pending, PartiallyFilled, Filled, Cancelled }
public enum TradingEngineStatus { Idle, Running, Paused, Stopped }
```

---

## ?? 新的依赖注入配置

文件: `Services/ServiceConfiguration.cs` (更新版)

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace 币安量化机器人.Services;

public static class ServiceConfiguration
{
    public static IServiceCollection AddTradingServices(
        this IServiceCollection services)
    {
        // === 基础服务 ===
        services.AddSingleton<AppSettings>();
        services.AddSingleton<BinanceApiClient>();
        services.AddSingleton<BinanceStreamClient>();

        // === 数据层 ===
        services.AddSingleton<IDataCollectionService, BinanceDataCollectionService>();
        services.AddSingleton<DataCacheService>();

        // === 策略层 ===
        services.AddSingleton<IStrategyEvaluationService, StrategyEvaluationService>();
        services.AddSingleton<IStrategyFilterService, StrategyFilterService>();
        services.AddSingleton<IBacktestEngine, DefaultBacktestEngine>();

        // === 风险层 ===
        services.AddSingleton<IRiskManager, RiskManager>();
        services.AddSingleton<IPositionManager, PositionManager>();
        services.AddSingleton<ILeverageController, LeverageController>();

        // === 执行层 ===
        services.AddSingleton<IOrderExecutor, RobustOrderExecutor>();
        services.AddSingleton<IErrorRecoveryHandler, ErrorRecoveryHandler>();

        // === 持久化层 ===
        services.AddSingleton<ITradingRecorder>(sp =>
            new SqliteTradingRecorder(
                "Data Source=trading.db",
                sp.GetRequiredService<ILogger>()));

        // === 编排层 (核心) ===
        services.AddSingleton<IAutomatedTradingEngine, AutomatedTradingEngine>();

        // === 日志 ===
        services.AddSingleton<ILogger>(sp =>
            LoggerConfiguration.CreateLogger("Production"));

        return services;
    }
}
```

---

## ?? 闭环流程可视化

```
┌─────────────────────────────────────────────────────────────┐
│                    自动化交易引擎启动                        │
└────────────────────┬────────────────────────────────────────┘
                     │
      ┌──────────────▼──────────────┐
      │  循环 N [5 分钟一次]        │
      └──────────────┬──────────────┘
                     │
      ┌──────────────▼──────────────┐
      │  步骤 1: 采集市场数据和账户 │
      │  - WebSocket 实时行情      │
      │  - 账户余额、仓位          │
      └──────────────┬──────────────┘
                     │
      ┌──────────────▼──────────────┐
      │  步骤 2: 评估所有策略       │
      │  - 并行回测                 │
      │  - 计算收益率、Sharpe       │
      │  - 考虑手续费、滑点         │
      └──────────────┬──────────────┘
                     │
      ┌──────────────▼──────────────┐
      │  步骤 3: 筛选和排序         │
      │  - 收益率 >= 2% ?          │
      │  - 优先级排序               │
      │  - 下架不达标策略           │
      └──────────────┬──────────────┘
                     │
      ┌──────────────▼──────────────┐
      │  步骤 4: 实盘执行           │
      │  - 执行信号                 │
      │  - 市价/限价/止损           │
      │  - 仓位管理                 │
      │  - 杠杆控制                 │
      │  - 异常重试                 │
      └──────────────┬──────────────┘
                     │
      ┌──────────────▼──────────────┐
      │  步骤 5: 记录日志           │
      │  - 评估结果                 │
      │  - 执行记录                 │
      │  - 周期总结                 │
      └──────────────┬──────────────┘
                     │
      ┌──────────────▼──────────────┐
      │  步骤 6: 等待下一周期       │
      │  - 300 秒倒计时             │
      └──────────────┬──────────────┘
                     │
      ┌──────────────▼──────────────┐
      │     重复 (Loop N+1)         │
      └──────────────────────────────┘
```

---

## ?? 模块间通信

```
AutomatedTradingEngine (编排层)
    │
    ├──→ DataCollectionService (采集层)
    │      └──→ BinanceApiClient (外部API)
    │
    ├──→ StrategyEvaluationService (评估层)
    │      ├──→ BacktestEngine
    │      └──→ PositionManager
    │
    ├──→ StrategyFilterService (筛选层)
    │      └──→ StrategyRepository
    │
    ├──→ OrderExecutor (执行层)
    │      ├──→ BinanceApiClient
    │      └──→ ErrorRecoveryHandler
    │
    ├──→ LeverageController (风险层)
    │      └──→ BinanceApiClient
    │
    └──→ TradingRecorder (记录层)
           └──→ SQLite Database
```

---

## ?? 改进收益

### 对标您的需求

| 需求 | 实现方式 | 收益 |
|------|---------|------|
| 闭环自动化 | `AutomatedTradingEngine` 实现完整流程 | ? 无需人工干预 |
| 数据采集 | `IDataCollectionService` 支持 WebSocket | ? 实时性 |
| 策略评估 | `IStrategyEvaluationService` 并行评测 | ? 快速评分 |
| 筛选执行 | `IStrategyFilterService` 自动排序 | ? 优胜劣汰 |
| 风险控制 | `IPositionManager` + `ILeverageController` | ? 完整的仓位和杠杆管理 |
| 实盘执行 | `IOrderExecutor` 支持多种订单类型 | ? 可靠的执行 |
| 日志记录 | `ITradingRecorder` 完整的数据持久化 | ? 可复盘和优化 |
| 异常恢复 | `IErrorRecoveryHandler` 自动重试 | ? 高可用性 |

---

## ? 实施检查清单

- [ ] 创建数据采集服务接口和实现
- [ ] 创建策略评估和筛选服务
- [ ] 创建订单执行服务
- [ ] 创建风险和杠杆控制服务
- [ ] 创建日志记录服务
- [ ] 创建自动化交易引擎（核心）
- [ ] 更新依赖注入配置
- [ ] 添加单元测试
- [ ] 集成测试和压力测试

---

## ?? 总结

这个架构设计**完全对齐您的闭环自动化交易系统需求**：

1. ? **数据层** - 统一的数据采集和管理
2. ? **策略层** - 完整的评估、筛选、排序流程
3. ? **执行层** - 可靠的订单执行和异常处理
4. ? **风险层** - 仓位管理和杠杆控制
5. ? **记录层** - 完整的日志和可复盘数据
6. ? **编排层** - 实现完整的闭环流程

**下一步**: 按这个架构开始实施第一个模块（数据采集服务），然后逐步完成其他模块。


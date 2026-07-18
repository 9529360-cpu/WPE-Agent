# 闭环架构 - 快速参考卡

> 一页纸速查新架构的所有关键接口和实现

---

## ??? 六层架构快速导览

```
┌─────────────────────────────────────────────┐
│ 编排层 (Orchestration)                      │
│ IAutomatedTradingEngine - 闭环自动化        │
└────────┬────────────────────────────────────┘
         │
   ┌─────┼─────┬─────────┬──────────┬───────┐
   │     │     │         │          │       │
   ▼     ▼     ▼         ▼          ▼       ▼
┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐
│数│ │策│ │筛│ │风│ │执│ │记│ │错│
│据│ │略│ │选│ │控│ │行│ │录│ │误│
│  │ │  │ │  │ │  │ │  │ │  │ │  │
└──┘ └──┘ └──┘ └──┘ └──┘ └──┘ └──┘
```

---

## ?? 核心接口清单

### 1?? 数据层接口

```csharp
// IDataCollectionService
public interface IDataCollectionService
{
    // 实时市场数据流
    IAsyncEnumerable<MarketSnapshot> CollectMarketDataAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default);

    // 账户快照（余额、仓位）
    Task<AccountSnapshot> CollectAccountDataAsync(
        CancellationToken cancellationToken = default);

    // 历史K线（回测用）
    Task<IReadOnlyList<Kline>> GetHistoricalKlinesAsync(
        string symbol, string interval, DateTime start, DateTime end,
        CancellationToken cancellationToken = default);
}

// 关键模型
public record MarketSnapshot(
    string Symbol, DateTime Timestamp, decimal Open, decimal High,
    decimal Low, decimal Close, decimal Volume);

public record AccountSnapshot(
    DateTime Timestamp, decimal TotalEquity, decimal AvailableBalance,
    IReadOnlyList<PositionInfo> Positions);
```

**实现**: `BinanceDataCollectionService`

---

### 2?? 策略评估接口

```csharp
// IStrategyEvaluationService
public interface IStrategyEvaluationService
{
    // 评估单个策略
    Task<StrategyEvaluationResult> EvaluateAsync(
        ITradingStrategy strategy,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default);

    // 批量评估（并行）
    Task<IReadOnlyList<StrategyEvaluationResult>> EvaluateMultipleAsync(
        IEnumerable<ITradingStrategy> strategies,
        MarketSnapshot[] marketSnapshots,
        AccountSnapshot accountSnapshot,
        CancellationToken cancellationToken = default);
}

// 关键模型
public record StrategyEvaluationResult(
    string StrategyName, string StrategyId, decimal NetProfit,
    decimal ProfitRatio, double SharpeRatio, RiskLevel RiskLevel,
    int TradeCount, DateTime EvaluatedAt);
```

**实现**: `StrategyEvaluationService`

---

### 3?? 策略筛选接口

```csharp
// IStrategyFilterService
public interface IStrategyFilterService
{
    // 按收益阈值筛选
    IReadOnlyList<StrategyEvaluationResult> FilterByThreshold(
        IEnumerable<StrategyEvaluationResult> results,
        decimal profitThreshold);

    // 按优先级排序
    IReadOnlyList<StrategyEvaluationResult> RankByPriority(
        IEnumerable<StrategyEvaluationResult> results);

    // 下架不达标策略
    Task DeactivateAsync(
        ITradingStrategy strategy, string reason,
        CancellationToken cancellationToken = default);
}
```

**实现**: `StrategyFilterService`

---

### 4?? 风险管理接口

```csharp
// IPositionManager - 仓位管理
public interface IPositionManager
{
    // 计算推荐仓位大小
    decimal CalculatePositionSize(
        StrategyEvaluationResult evaluation,
        AccountSnapshot account,
        decimal maxRiskPercentage = 0.02m);

    // 检查能否开新仓
    bool CanOpenPosition(
        AccountSnapshot account, decimal requiredMargin);

    // 杠杆风险调整
    decimal GetLeverageRiskFactor(RiskLevel riskLevel);
}

// ILeverageController - 杠杆控制
public interface ILeverageController
{
    // 计算杠杆成本（利息）
    Task<decimal> CalculateLeverageCostAsync(
        string symbol, decimal borrowAmount, decimal daysHeld,
        CancellationToken cancellationToken = default);

    // 推荐杠杆倍数
    decimal GetRecommendedLeverage(
        StrategyEvaluationResult evaluation);

    // 检查是否在限额内
    bool IsLeverageWithinLimit(
        AccountSnapshot account, decimal proposedLeverage);
}

public enum RiskLevel { Low, Medium, High }
```

**实现**: `PositionManager`, `LeverageController`

---

### 5?? 执行层接口

```csharp
// IOrderExecutor - 订单执行
public interface IOrderExecutor
{
    // 市价单
    Task<OrderResult> ExecuteMarketOrderAsync(
        string symbol, OrderSide side, decimal quantity,
        CancellationToken cancellationToken = default);

    // 限价单
    Task<OrderResult> ExecuteLimitOrderAsync(
        string symbol, OrderSide side, decimal quantity, decimal price,
        CancellationToken cancellationToken = default);

    // 止损单
    Task<OrderResult> ExecuteStopLossAsync(
        string symbol, decimal quantity, decimal stopPrice,
        CancellationToken cancellationToken = default);

    // 平仓
    Task<OrderResult> ClosePositionAsync(
        string symbol, CancellationToken cancellationToken = default);
}

// 关键模型
public record OrderResult(
    string OrderId, string Symbol, OrderSide Side, decimal Quantity,
    decimal ExecutedPrice, OrderStatus Status, DateTime ExecutedAt);

public enum OrderSide { Buy, Sell }
public enum OrderStatus { Pending, PartiallyFilled, Filled, Cancelled }
```

**实现**: `RobustOrderExecutor`（支持重试）

---

### 6?? 日志记录接口

```csharp
// ITradingRecorder - 交易记录
public interface ITradingRecorder
{
    // 记录策略评估
    Task RecordStrategyEvaluationAsync(
        StrategyEvaluationResult evaluation,
        CancellationToken cancellationToken = default);

    // 记录执行结果
    Task RecordExecutionAsync(
        OrderResult orderResult,
        CancellationToken cancellationToken = default);

    // 记录周期总结
    Task RecordCycleAsync(
        TradingCycleLog log,
        CancellationToken cancellationToken = default);

    // 查询历史（复盘）
    Task<IReadOnlyList<TradingCycleLog>> QueryHistoryAsync(
        DateTime startDate, DateTime endDate,
        CancellationToken cancellationToken = default);
}

public record TradingCycleLog(
    int CycleNumber, DateTime StartTime, DateTime EndTime,
    int StrategiesEvaluated, int StrategiesExecuted,
    decimal TotalProfit, string? Notes = null);
```

**实现**: `SqliteTradingRecorder`

---

### 7?? 错误恢复接口

```csharp
// IErrorRecoveryHandler - 异常处理
public interface IErrorRecoveryHandler
{
    // 执行带重试的操作
    Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default);
}

// 配置
public record ErrorRecoveryConfig
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public double BackoffMultiplier { get; init; } = 2.0;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);
}
```

**实现**: `ErrorRecoveryHandler`

---

### 8?? 核心编排接口

```csharp
// IAutomatedTradingEngine - 自动化交易引擎
public interface IAutomatedTradingEngine
{
    // 启动交易循环
    Task StartAsync(CancellationToken cancellationToken = default);

    // 停止交易循环
    Task StopAsync();

    // 获取状态
    TradingEngineStatus GetStatus();
}

public enum TradingEngineStatus
{
    Idle,    // 空闲
    Running, // 运行中
    Paused,  // 暂停
    Stopped  // 已停止
}
```

**实现**: `AutomatedTradingEngine`

---

## ?? 闭环流程执行步骤

```
启动引擎
   │
   ▼
┌─────────────────────────────────────────┐
│ 循环 N [配置间隔时间]                    │
└──────────┬────────────────────────────────┘
           │
    ┌──────▼──────┐
    │ 步骤 1:     │  采集市场数据和账户余额
    │ 采集数据    │  → MarketSnapshot[]
    │             │  → AccountSnapshot
    └──────┬──────┘
           │
    ┌──────▼──────┐
    │ 步骤 2:     │  评估所有策略的收益
    │ 评估策略    │  → StrategyEvaluationResult[]
    │             │  包含: 净利、收益率、Sharpe、风险等级
    └──────┬──────┘
           │
    ┌──────▼──────┐
    │ 步骤 3:     │  筛选收益率 >= 阈值的策略
    │ 筛选排序    │  按 Sharpe 排序优先级
    │             │  下架不达标的策略
    └──────┬──────┘
           │
    ┌──────▼──────┐
    │ 步骤 4:     │  执行排名靠前的策略
    │ 实盘执行    │  计算仓位 → 生成订单 → 执行
    │             │  处理异常 → 重试机制
    └──────┬──────┘
           │
    ┌──────▼──────┐
    │ 步骤 5:     │  记录所有数据
    │ 记录日志    │  评估结果 → 数据库
    │             │  执行记录 → 数据库
    │             │  周期统计 → 数据库
    └──────┬──────┘
           │
    ┌──────▼──────┐
    │ 步骤 6:     │  等待下一个周期
    │ 等待循环    │  (通常 5-10 分钟)
    └──────┬──────┘
           │
    ┌──────▼──────┐
    │ 是否停止?   │
    │ Y → 结束    │
    │ N → 循环    │
    └─────────────┘
```

---

## ?? 依赖注入配置 (完整)

```csharp
// Program.cs or App.xaml.cs
var services = new ServiceCollection();

// 1. 基础
services.AddSingleton<AppSettings>();
services.AddSingleton<BinanceApiClient>();
services.AddSingleton<BinanceStreamClient>();

// 2. 数据层
services.AddSingleton<IDataCollectionService, 
    BinanceDataCollectionService>();

// 3. 策略层
services.AddSingleton<IStrategyEvaluationService, 
    StrategyEvaluationService>();
services.AddSingleton<IStrategyFilterService, 
    StrategyFilterService>();
services.AddSingleton<IBacktestEngine, 
    DefaultBacktestEngine>();

// 4. 风险层
services.AddSingleton<IPositionManager, PositionManager>();
services.AddSingleton<ILeverageController, LeverageController>();
services.AddSingleton<IRiskManager, ThreadSafeRiskManager>();

// 5. 执行层
services.AddSingleton<IOrderExecutor, RobustOrderExecutor>();
services.AddSingleton<IErrorRecoveryHandler, ErrorRecoveryHandler>();

// 6. 记录层
services.AddSingleton<ITradingRecorder, SqliteTradingRecorder>();

// 7. 核心编排
services.AddSingleton<IAutomatedTradingEngine, 
    AutomatedTradingEngine>();

// 8. 日志
services.AddSingleton<ILogger>(sp =>
    LoggerConfiguration.CreateLogger("Production"));

var serviceProvider = services.BuildServiceProvider();
```

---

## ?? 快速启动示例

```csharp
// 在 MainWindow.xaml.cs 或按钮点击事件中
private async void StartTradingButton_Click(object sender, RoutedEventArgs e)
{
    try
    {
        var engine = App.ServiceProvider
            .GetRequiredService<IAutomatedTradingEngine>();

        // 启动自动化交易
        await engine.StartAsync();

        MessageBox.Show("自动化交易已启动！");
    }
    catch (Exception ex)
    {
        MessageBox.Show($"启动失败: {ex.Message}");
    }
}

private async void StopTradingButton_Click(object sender, RoutedEventArgs e)
{
    try
    {
        var engine = App.ServiceProvider
            .GetRequiredService<IAutomatedTradingEngine>();

        await engine.StopAsync();

        MessageBox.Show("自动化交易已停止");
    }
    catch (Exception ex)
    {
        MessageBox.Show($"停止失败: {ex.Message}");
    }
}
```

---

## ?? 数据流向

```
Binance API
    │
    ├──→ [WebSocket] → MarketSnapshot[] → AutomatedTradingEngine
    │
    └──→ [REST API] → AccountSnapshot ──→ AutomatedTradingEngine
                                               │
                                               ├──→ StrategyEvaluationService
                                               │    └──→ BacktestEngine
                                               │
                                               ├──→ StrategyFilterService
                                               │
                                               ├──→ PositionManager
                                               │    └──→ LeverageController
                                               │
                                               ├──→ RobustOrderExecutor
                                               │    ├──→ [Place Orders] → Binance API
                                               │    └──→ ErrorRecoveryHandler
                                               │
                                               └──→ SqliteTradingRecorder
                                                    └──→ SQLite Database
```

---

## ?? 关键配置项

```csharp
// AppSettings.cs
public class AppSettings
{
    // 数据采集
    public string? TradingSymbols { get; set; } = "BTCUSDT,ETHUSDT";
    
    // 策略筛选
    public string? MinProfitThreshold { get; set; } = "0.02";     // 2%
    public string? MaxConcurrentStrategies { get; set; } = "3";    // 最多3个策略
    
    // 循环控制
    public string? CycleIntervalSeconds { get; set; } = "300";     // 5分钟
    
    // 环境
    public string? Environment { get; set; } = "Production";
    public bool UseFuturesTestnet { get; set; } = false;
}
```

---

## ?? 常见错误与解决

| 错误 | 原因 | 解决方案 |
|------|------|---------|
| `IDataCollectionService` 未注册 | DI 配置遗漏 | 检查 ServiceConfiguration |
| NullReferenceException | 组件未初始化 | 确保 DI 容器已构建 |
| 订单执行失败 | API 限制或网络 | 检查 ErrorRecoveryHandler 重试 |
| 策略永远不执行 | 收益率未达阈值 | 降低 `MinProfitThreshold` |
| 数据库锁定 | 并发写入 | 检查 SQLite 连接管理 |

---

## ?? 快速索引

- **启动交易**: `IAutomatedTradingEngine.StartAsync()`
- **停止交易**: `IAutomatedTradingEngine.StopAsync()`
- **查询历史**: `ITradingRecorder.QueryHistoryAsync()`
- **评估策略**: `IStrategyEvaluationService.EvaluateAsync()`
- **筛选策略**: `IStrategyFilterService.FilterByThreshold()`
- **执行订单**: `IOrderExecutor.ExecuteMarketOrderAsync()`
- **计算仓位**: `IPositionManager.CalculatePositionSize()`
- **异常重试**: `IErrorRecoveryHandler.ExecuteWithRetryAsync()`

---

## ? 检查清单

- [ ] 所有 7 个核心接口已实现
- [ ] DI 容器配置完整
- [ ] App.xaml.cs 已更新
- [ ] 闭环流程能正常执行
- [ ] 日志系统工作正常
- [ ] 异常恢复机制有效
- [ ] 单元测试覆盖 > 70%
- [ ] 集成测试通过

---

**下一步**: 参考 `Closed_Loop_Implementation_Roadmap.md` 开始实施！ ??


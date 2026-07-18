# ?? 快速使用指南 - Week 1 接口

## ?? 目录
1. [访问服务](#访问服务)
2. [数据采集](#数据采集)
3. [策略评估](#策略评估)
4. [策略筛选](#策略筛选)
5. [订单执行](#订单执行)
6. [风险管理](#风险管理)
7. [核心编排](#核心编排)

---

## 访问服务

### 方式 1: 从 App 获取服务提供者

```csharp
using 币安量化机器人;
using 币安量化机器人.Core.Data;

// 在任何地方获取服务
var serviceProvider = App.ServiceProvider;
var dataCollectionService = serviceProvider.GetRequiredService<IDataCollectionService>();
```

### 方式 2: 在构造函数中使用 DI

```csharp
public class MyTradingService
{
    private readonly IDataCollectionService _dataCollectionService;
    private readonly IOrderExecutor _orderExecutor;

    // 依赖注入
    public MyTradingService(
        IDataCollectionService dataCollectionService,
        IOrderExecutor orderExecutor)
    {
        _dataCollectionService = dataCollectionService;
        _orderExecutor = orderExecutor;
    }

    public async Task ExecuteTradeAsync()
    {
        // 使用注入的服务
        var snapshot = await _dataCollectionService.GetLatestMarketSnapshotAsync("BTCUSDT");
        // ...
    }
}
```

---

## 数据采集

### 启动数据采集

```csharp
var dataService = App.ServiceProvider.GetRequiredService<IDataCollectionService>();

// 启动采集
await dataService.StartAsync();

// 获取最新行情
var btcSnapshot = await dataService.GetLatestMarketSnapshotAsync("BTCUSDT");
Console.WriteLine($"BTC 价格: {btcSnapshot.Price}");
Console.WriteLine($"涨跌幅: {btcSnapshot.PriceChangePercent}%");
```

### 获取历史 K 线数据

```csharp
var klines = await dataService.GetHistoricalKlinesAsync(
    symbol: "BTCUSDT",
    interval: "1h",
    startTime: DateTime.Now.AddDays(-30),
    endTime: DateTime.Now);

foreach (var kline in klines)
{
    Console.WriteLine($"开: {kline.Open}, 高: {kline.High}, 低: {kline.Low}, 收: {kline.Close}");
}
```

### 订阅实时数据

```csharp
// 订阅 BTCUSDT 实时行情
var subscriptionId = dataService.SubscribeToRealTimeData(
    "BTCUSDT",
    async (snapshot) =>
    {
        Console.WriteLine($"[{snapshot.UpdateTime}] BTC: {snapshot.Price}");
        await Task.CompletedTask;
    });

// ... 后续操作 ...

// 取消订阅
dataService.UnsubscribeFromRealTimeData(subscriptionId);
```

### 获取订单簿

```csharp
var orderBook = await dataService.GetOrderBookAsync("BTCUSDT", depth: 20);

Console.WriteLine("买盘:");
foreach (var bid in orderBook.Bids.Take(5))
{
    Console.WriteLine($"  价格: {bid.Price}, 数量: {bid.Quantity}");
}

Console.WriteLine("卖盘:");
foreach (var ask in orderBook.Asks.Take(5))
{
    Console.WriteLine($"  价格: {ask.Price}, 数量: {ask.Quantity}");
}
```

---

## 策略评估

### 评估策略性能

```csharp
var evaluationService = App.ServiceProvider.GetRequiredService<IStrategyEvaluationService>();

// 假设你有一些历史交易
var trades = new List<TradeRecord>
{
    new()
    {
        Id = "1",
        Symbol = "BTCUSDT",
        EntryTime = DateTime.Now.AddDays(-1),
        ExitTime = DateTime.Now,
        EntryPrice = 40000m,
        ExitPrice = 41000m,
        Quantity = 1m,
        Commission = 10m,
        Direction = TradeDirection.Long
    },
    // ... 更多交易 ...
};

// 评估性能
var metrics = await evaluationService.EvaluatePerformanceAsync(trades, initialBalance: 10000m);

Console.WriteLine($"总利润: {metrics.TotalProfit}");
Console.WriteLine($"夏普比率: {metrics.SharpeRatio}");
Console.WriteLine($"最大回撤: {metrics.MaxDrawdown}%");
Console.WriteLine($"胜率: {metrics.WinRate}%");
Console.WriteLine($"利润因子: {metrics.ProfitFactor}");
```

### 计算具体指标

```csharp
// 计算夏普比率
var sharpeRatio = await evaluationService.CalculateSharpeRatioAsync(
    returns: new List<decimal> { 0.01m, -0.005m, 0.015m, 0.008m },
    riskFreeRate: 0.02m);

Console.WriteLine($"夏普比率: {sharpeRatio}");

// 计算最大回撤
var maxDrawdown = await evaluationService.CalculateMaxDrawdownAsync(
    equityCurve: new List<decimal> { 10000m, 10500m, 10200m, 11000m, 10800m });

Console.WriteLine($"最大回撤: {maxDrawdown}%");

// 计算胜率
var winRate = await evaluationService.CalculateWinRateAsync(trades);
Console.WriteLine($"胜率: {winRate}%");
```

---

## 策略筛选

### 按条件筛选策略

```csharp
var filterService = App.ServiceProvider.GetRequiredService<IStrategyFilterService>();

// 定义筛选条件
var criteria = new FilterCriteria
{
    MinAnnualizedReturn = 0.20m,           // 最小年化收益 20%
    MaxDrawdown = 0.20m,                   // 最大回撤 20%
    MinSharpeRatio = 0.5m,                 // 最小夏普比率 0.5
    MinWinRate = 0.4m,                     // 最小胜率 40%
    StrategyType = StrategyType.Momentum   // 只要动量策略
};

var strategies = new List<StrategyInfo>
{
    // ... 你的策略列表 ...
};

// 筛选
var filtered = await filterService.FilterStrategiesByCriteriaAsync(strategies, criteria);
Console.WriteLine($"通过筛选的策略数: {filtered.Count}");
```

### 排序策略

```csharp
// 按夏普比率排序（降序）
var sorted = await filterService.SortStrategiesAsync(
    strategies,
    StrategySortField.SharpeRatio,
    descending: true);

foreach (var strategy in sorted.Take(3))
{
    Console.WriteLine($"{strategy.Name}: 夏普比率 = {strategy.Performance.SharpeRatio}");
}
```

### 评估策略适用性

```csharp
// 定义当前市场条件
var marketConditions = new MarketConditions
{
    CurrentVolatility = 0.25m,              // 25% 波动率
    TrendStrength = 0.8m,                   // 趋势强度 80%
    CurrentTrend = MarketTrend.UpTrend,     // 上升趋势
    AverageVolume24h = 1_000_000m,
    MeasuredAt = DateTime.UtcNow
};

var strategy = strategies.First();
var fitnessScore = await filterService.EvaluateStrategyFitnessAsync(strategy, marketConditions);
Console.WriteLine($"策略适用性评分: {fitnessScore}/100");
```

### 获取推荐组合

```csharp
// 获取最佳的 3 个策略组合
var portfolio = await filterService.GetRecommendedPortfolioAsync(
    strategies,
    portfolioSize: 3);

Console.WriteLine("推荐策略组合:");
foreach (var strat in portfolio)
{
    Console.WriteLine($"  - {strat.Name}");
}
```

---

## 订单执行

### 执行市价单

```csharp
var executor = App.ServiceProvider.GetRequiredService<IOrderExecutor>();

var request = new MarketOrderRequest
{
    Symbol = "BTCUSDT",
    Side = OrderSide.Buy,
    Quantity = 0.1m,
    ClientOrderId = "order_001"
};

var result = await executor.ExecuteMarketOrderAsync(request);

if (result.Success)
{
    Console.WriteLine($"订单执行成功");
    Console.WriteLine($"订单 ID: {result.OrderId}");
    Console.WriteLine($"执行价格: {result.ExecutedPrice}");
    Console.WriteLine($"执行数量: {result.ExecutedQuantity}");
}
else
{
    Console.WriteLine($"订单执行失败: {result.ErrorMessage}");
}
```

### 执行限价单

```csharp
var limitRequest = new LimitOrderRequest
{
    Symbol = "BTCUSDT",
    Side = OrderSide.Buy,
    Quantity = 0.1m,
    Price = 39000m,
    TimeInForce = TimeInForce.GTC
};

var result = await executor.ExecuteLimitOrderAsync(limitRequest);
if (result.Success)
{
    Console.WriteLine($"限价单提交成功: {result.OrderId}");
}
```

### 获取订单状态

```csharp
var status = await executor.GetOrderStatusAsync("BTCUSDT", orderId: 123456789);

if (status != null)
{
    Console.WriteLine($"订单状态: {status.Status}");
    Console.WriteLine($"已执行数量: {status.ExecutedQty}/{status.OrigQty}");
    Console.WriteLine($"下单时间: {status.Time}");
}
```

### 执行止损单

```csharp
var stopLossRequest = new StopLossOrderRequest
{
    Symbol = "BTCUSDT",
    Side = OrderSide.Sell,
    Quantity = 0.1m,
    StopPrice = 35000m
};

var result = await executor.ExecuteStopLossOrderAsync(stopLossRequest);
Console.WriteLine(result.Success ? "止损单已设置" : "止损单设置失败");
```

---

## 风险管理

### 仓位管理

```csharp
var positionManager = App.ServiceProvider.GetRequiredService<IPositionManager>();

// 开仓
var position = new Position
{
    Symbol = "BTCUSDT",
    Direction = PositionDirection.Long,
    Quantity = 0.5m,
    EntryPrice = 40000m,
    CurrentPrice = 40000m,
    StopLossPrice = 38000m,
    TakeProfitPrice = 42000m,
    OpenTime = DateTime.UtcNow
};

await positionManager.OpenPositionAsync(position);

// 获取所有持仓
var positions = await positionManager.GetPositionsAsync();
foreach (var pos in positions)
{
    Console.WriteLine($"{pos.Symbol}: {pos.UnrealizedProfit} ({pos.UnrealizedProfitPercent}%)");
}

// 计算未实现利润
var unrealized = await positionManager.CalculateUnrealizedProfitAsync("BTCUSDT", 41000m);
Console.WriteLine($"未实现利润: {unrealized}");

// 平仓
await positionManager.ClosePositionAsync("BTCUSDT");
```

### 杠杆控制

```csharp
var leverageController = App.ServiceProvider.GetRequiredService<ILeverageController>();

// 设置杠杆倍数
await leverageController.SetLeverageAsync("BTCUSDT", leverage: 5);

// 获取当前杠杆
var currentLeverage = await leverageController.GetCurrentLeverageAsync("BTCUSDT");
Console.WriteLine($"当前杠杆: {currentLeverage}x");

// 获取杠杆信息
var leverageInfo = await leverageController.GetLeverageInfoAsync();
Console.WriteLine($"总保证金: {leverageInfo.TotalMargin}");
Console.WriteLine($"可用保证金: {leverageInfo.AvailableMargin}");
Console.WriteLine($"保证金率: {leverageInfo.MarginRatio}");

// 检查清算风险
var hasLiquidationRisk = await leverageController.IsLiquidationRiskAsync(warningThreshold: 0.2m);
if (hasLiquidationRisk)
{
    Console.WriteLine("?? 警告: 接近清算线!");
}

// 自动调整杠杆以维持目标保证金率
await leverageController.AutoAdjustLeverageToTargetRatioAsync(targetMarginRatio: 0.5m);
```

---

## 核心编排

### 启动和管理交易引擎

```csharp
var engine = App.ServiceProvider.GetRequiredService<IAutomatedTradingEngine>();

// 启动引擎
await engine.StartAsync();
Console.WriteLine("? 交易引擎已启动");

// 获取引擎状态
var status = await engine.GetStatusAsync();
Console.WriteLine($"引擎状态: {status.State}");
Console.WriteLine($"账户余额: {status.AccountBalance}");
Console.WriteLine($"未实现利润: {status.UnrealizedProfit}");
Console.WriteLine($"今日利润: {status.TodayProfit}");

// 暂停交易
await engine.PauseAsync();
Console.WriteLine("?? 交易已暂停");

// 恢复交易
await engine.ResumeAsync();
Console.WriteLine("?? 交易已恢复");

// 停止引擎
await engine.StopAsync();
Console.WriteLine("? 交易引擎已停止");
```

### 添加和管理策略

```csharp
// 创建策略
var strategy = new StrategyInfo
{
    Id = Guid.NewGuid().ToString(),
    Name = "均值回归策略",
    Description = "基于价格均值回归的交易策略",
    Type = StrategyType.MeanReversion,
    IsActive = true,
    SupportedSymbols = new List<string> { "BTCUSDT", "ETHUSDT" }
};

// 添加到引擎
await engine.AddStrategyAsync(strategy);
Console.WriteLine($"? 策略 '{strategy.Name}' 已添加");

// 获取活跃策略
var activeStrategies = await engine.GetActiveStrategiesAsync();
Console.WriteLine($"活跃策略数: {activeStrategies.Count}");

// 禁用策略
await engine.DisableStrategyAsync(strategy.Id);
Console.WriteLine($"策略已禁用");

// 移除策略
await engine.RemoveStrategyAsync(strategy.Id);
Console.WriteLine($"策略已移除");
```

### 订阅引擎事件

```csharp
// 订阅交易执行事件
engine.SubscribeToEvent(EngineEventType.TradeExecuted, async (engineEvent) =>
{
    Console.WriteLine($"?? 交易执行事件:");
    Console.WriteLine($"   时间: {engineEvent.Timestamp}");
    Console.WriteLine($"   消息: {engineEvent.Message}");
    await Task.CompletedTask;
});

// 订阅错误事件
engine.SubscribeToEvent(EngineEventType.Error, async (engineEvent) =>
{
    Console.WriteLine($"? 错误事件: {engineEvent.Message}");
    await Task.CompletedTask;
});

// 订阅循环完成事件
engine.SubscribeToEvent(EngineEventType.CycleCompleted, async (engineEvent) =>
{
    Console.WriteLine($"? 交易循环完成");
    await Task.CompletedTask;
});
```

### 手动触发交易信号

```csharp
// 创建交易信号
var signal = new TradeSignal
{
    Symbol = "BTCUSDT",
    Type = SignalType.Buy,
    TargetQuantity = 0.1m,
    TargetPrice = 40500m,
    StopLossPrice = 39000m,
    TakeProfitPrice = 42000m,
    StrategyId = strategy.Id
};

// 执行交易
var result = await engine.ManualTradeAsync(signal);
if (result.Success)
{
    Console.WriteLine($"? 交易成功: 订单 ID {result.OrderId}");
}
else
{
    Console.WriteLine($"? 交易失败: {result.ErrorMessage}");
}
```

### 获取性能统计

```csharp
// 获取引擎性能统计
var stats = await engine.GetPerformanceStatsAsync();

Console.WriteLine($"总循环数: {stats.TotalCycles}");
Console.WriteLine($"成功循环: {stats.SuccessfulCycles}");
Console.WriteLine($"失败循环: {stats.FailedCycles}");
Console.WriteLine($"平均循环时间: {stats.AverageCycleDurationMs}ms");
Console.WriteLine($"总交易数: {stats.TotalTradesExecuted}");
Console.WriteLine($"总利润: {stats.TotalProfit}");
Console.WriteLine($"胜率: {stats.WinRate}%");
Console.WriteLine($"运行时间: {stats.Uptime}");
```

---

## ?? 完整示例：端到端交易流程

```csharp
public class SimpleTradingExample
{
    public static async Task RunAsync()
    {
        var dataService = App.ServiceProvider.GetRequiredService<IDataCollectionService>();
        var evaluationService = App.ServiceProvider.GetRequiredService<IStrategyEvaluationService>();
        var filterService = App.ServiceProvider.GetRequiredService<IStrategyFilterService>();
        var executor = App.ServiceProvider.GetRequiredService<IOrderExecutor>();
        var positionManager = App.ServiceProvider.GetRequiredService<IPositionManager>();
        var engine = App.ServiceProvider.GetRequiredService<IAutomatedTradingEngine>();

        // 1. 启动数据采集
        await dataService.StartAsync();
        Console.WriteLine("? 数据采集已启动");

        // 2. 获取最新市场数据
        var snapshot = await dataService.GetLatestMarketSnapshotAsync("BTCUSDT");
        Console.WriteLine($"BTC 当前价格: {snapshot.Price}");

        // 3. 获取历史数据并评估
        var klines = await dataService.GetHistoricalKlinesAsync(
            "BTCUSDT", "1h", DateTime.Now.AddDays(-30), DateTime.Now);
        Console.WriteLine($"获取了 {klines.Count} 条 K 线数据");

        // 4. 启动自动交易引擎
        await engine.StartAsync();
        Console.WriteLine("? 交易引擎已启动");

        // 5. 订阅引擎事件
        engine.SubscribeToEvent(EngineEventType.TradeExecuted, async (evt) =>
        {
            Console.WriteLine($"?? 交易执行: {evt.Message}");
            await Task.CompletedTask;
        });

        // 6. 等待并监听
        await Task.Delay(TimeSpan.FromHours(1));

        // 7. 获取性能统计
        var stats = await engine.GetPerformanceStatsAsync();
        Console.WriteLine($"\n?? 交易统计:");
        Console.WriteLine($"   总交易: {stats.TotalTradesExecuted}");
        Console.WriteLine($"   总利润: {stats.TotalProfit}");
        Console.WriteLine($"   胜率: {stats.WinRate}%");

        // 8. 停止引擎
        await engine.StopAsync();
        Console.WriteLine("? 交易引擎已停止");

        // 9. 停止数据采集
        await dataService.StopAsync();
    }
}
```

---

## ?? 最佳实践

### 1. 错误处理

```csharp
try
{
    var result = await executor.ExecuteMarketOrderAsync(request);
    if (!result.Success)
    {
        Console.WriteLine($"订单失败: {result.ErrorMessage}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"执行异常: {ex.Message}");
}
```

### 2. 异步操作

```csharp
// ? 推荐: 使用 await
var positions = await positionManager.GetPositionsAsync();

// ? 避免: 阻塞等待
// var positions = positionManager.GetPositionsAsync().Result;
```

### 3. 资源清理

```csharp
// 在应用关闭时
protected override async void OnExit(ExitEventArgs e)
{
    await engine.StopAsync();
    await dataService.StopAsync();
    // ...
}
```

---

**更多信息**: 参考 `Docs/WEEK1_COMPLETION_REPORT.md` 和各接口的 XML 文档注释。

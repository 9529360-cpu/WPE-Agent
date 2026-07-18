# ?? Week 2-3 实现指南

## ?? Week 2-3 目标

**优先级**: P1 (高)  
**完成时间**: 2 周  
**目标**: 实现数据层和策略层的所有服务

---

## ?? Week 2 实现清单

### Day 1-2: BinanceDataCollectionService 实现

**文件**: `Infrastructure/Data/BinanceDataCollectionService.cs`

```csharp
public class BinanceDataCollectionService : IDataCollectionService
{
    private readonly BinanceApiClient _apiClient;
    private readonly BinanceStreamClient _streamClient;
    private readonly ILogger<BinanceDataCollectionService> _logger;
    
    // 需要实现的 9 个方法：
    // 1. StartAsync()
    // 2. StopAsync()
    // 3. GetLatestMarketSnapshotAsync()
    // 4. GetHistoricalKlinesAsync()
    // 5. SubscribeToRealTimeData()
    // 6. UnsubscribeFromRealTimeData()
    // 7. GetOrderBookAsync()
    // 8. GetRecentTradesAsync()
    // 9. 实例化相关代码
}
```

**关键实现点**:
- ? 使用 BinanceApiClient 获取数据
- ? 使用 BinanceStreamClient 订阅实时数据
- ? 缓存 MarketSnapshot 数据
- ? 处理异常和重试
- ? 支持多个实时订阅

**测试方法**:
```csharp
[Test]
public async Task GetLatestMarketSnapshotAsync_ReturnsBtcData()
{
    var service = new BinanceDataCollectionService(...);
    var snapshot = await service.GetLatestMarketSnapshotAsync("BTCUSDT");
    
    Assert.IsNotNull(snapshot);
    Assert.AreEqual("BTCUSDT", snapshot.Symbol);
    Assert.IsTrue(snapshot.Price > 0);
}
```

### Day 3: Core Models 完善

**文件**: `Core/Models/StrategyModels.cs`

```csharp
// 策略相关的模型
public class StrategyDefinition { }
public class StrategyBacktestResult { }
public class StrategyParameter { }
public class StrategySignalHistory { }

// 与 IStrategyEvaluationService 和 IStrategyFilterService 配合使用
```

### Day 4-5: StrategyEvaluationService 实现

**文件**: `Application/Services/StrategyEvaluationService.cs`

```csharp
public class StrategyEvaluationService : IStrategyEvaluationService
{
    // 需要实现的 8 个方法：
    // 1. EvaluatePerformanceAsync()
    // 2. CalculateSharpeRatioAsync()
    // 3. CalculateMaxDrawdownAsync()
    // 4. CalculateWinRateAsync()
    // 5. CalculateProfitFactorAsync()
    // 6. CalculateROIAsync()
    // 7. CalculateInformationRatioAsync()
    // 8. GetEvaluationReportAsync()
}
```

**数学公式实现**:

```csharp
// 夏普比率 = (年化收益 - 无风险利率) / 标准差
decimal SharpeRatio(decimal annualReturn, decimal riskFreeRate, decimal stdDev)
    => (annualReturn - riskFreeRate) / stdDev;

// 最大回撤 = 最大值到最小值的下降百分比
decimal MaxDrawdown(List<decimal> equity)
{
    decimal maxValue = 0;
    decimal maxDD = 0;
    foreach (var val in equity)
    {
        if (val > maxValue) maxValue = val;
        maxDD = Math.Max(maxDD, (maxValue - val) / maxValue);
    }
    return maxDD * 100;
}

// 胜率 = 盈利交易数 / 总交易数
decimal WinRate(List<TradeRecord> trades)
    => (decimal)trades.Count(t => t.Profit > 0) / trades.Count * 100;
```

**测试方法**:
```csharp
[Test]
public async Task CalculateSharpeRatioAsync_ReturnsValidValue()
{
    var service = new StrategyEvaluationService();
    var returns = new List<decimal> { 0.01m, -0.005m, 0.015m };
    var ratio = await service.CalculateSharpeRatioAsync(returns, 0.02m);
    
    Assert.IsTrue(ratio > -10 && ratio < 10); // 合理范围
}
```

---

## ?? Week 3 实现清单

### Day 1-2: StrategyFilterService 实现

**文件**: `Application/Services/StrategyFilterService.cs`

```csharp
public class StrategyFilterService : IStrategyFilterService
{
    private readonly IStrategyEvaluationService _evaluationService;
    
    // 需要实现的 8 个方法：
    // 1. FilterStrategiesByCriteriaAsync()
    // 2. SortStrategiesAsync()
    // 3. EvaluateStrategyFitnessAsync()
    // 4. FilterByRiskAsync()
    // 5. FilterByReturnAsync()
    // 6. GetRecommendedPortfolioAsync()
    // 7. CheckCompatibilityAsync()
}
```

**筛选逻辑**:

```csharp
public async Task<List<StrategyInfo>> FilterStrategiesByCriteriaAsync(
    List<StrategyInfo> strategies,
    FilterCriteria criteria)
{
    return strategies
        .Where(s => 
            (!criteria.MinAnnualizedReturn.HasValue || 
             s.Performance.AnnualizedReturn >= criteria.MinAnnualizedReturn) &&
            (!criteria.MaxDrawdown.HasValue || 
             s.Performance.MaxDrawdown <= criteria.MaxDrawdown) &&
            (!criteria.MinSharpeRatio.HasValue || 
             s.Performance.SharpeRatio >= criteria.MinSharpeRatio) &&
            (!criteria.StrategyType.HasValue || 
             s.Type == criteria.StrategyType))
        .ToList();
}
```

### Day 3: 创建测试框架

**文件**: `Tests/Unit/StrategyEvaluationTests.cs`

```csharp
[TestFixture]
public class StrategyEvaluationTests
{
    private IStrategyEvaluationService _service;

    [SetUp]
    public void Setup()
    {
        _service = new StrategyEvaluationService();
    }

    [Test]
    public async Task EvaluatePerformanceAsync_ValidTrades_ReturnsMetrics()
    {
        // Arrange
        var trades = new List<TradeRecord> { /* ... */ };
        
        // Act
        var metrics = await _service.EvaluatePerformanceAsync(trades, 10000m);
        
        // Assert
        Assert.IsNotNull(metrics);
        Assert.IsTrue(metrics.TotalTrades > 0);
    }
}
```

### Day 4: 创建集成测试

**文件**: `Tests/Unit/StrategyFilterTests.cs`

```csharp
[TestFixture]
public class StrategyFilterTests
{
    private IStrategyFilterService _filterService;
    private IStrategyEvaluationService _evalService;

    [Test]
    public async Task FilterStrategiesByCriteriaAsync_AppliesAllFilters()
    {
        // 测试筛选逻辑
    }
}
```

### Day 5: 文档和优化

- 编写单元测试和集成测试
- 性能优化
- 文档补充

---

## ?? 实现步骤详解

### 步骤 1: 创建类文件

```csharp
// 文件: Infrastructure/Data/BinanceDataCollectionService.cs
namespace 币安量化机器人.Infrastructure.Data;

using 币安量化机器人.Core.Data;
using 币安量化机器人.Services;

public class BinanceDataCollectionService : IDataCollectionService
{
    private readonly BinanceApiClient _apiClient;
    private readonly BinanceStreamClient _streamClient;
    
    // 在构造函数中注入依赖
    public BinanceDataCollectionService(
        BinanceApiClient apiClient,
        BinanceStreamClient streamClient)
    {
        _apiClient = apiClient;
        _streamClient = streamClient;
    }

    // 实现接口方法
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // TODO: 实现启动逻辑
    }

    // ... 其他方法 ...
}
```

### 步骤 2: 在 ServiceConfiguration 中注册

```csharp
// Services/ServiceConfiguration.cs
private static void RegisterDataServices(IServiceCollection services)
{
    services.AddSingleton<IDataCollectionService, BinanceDataCollectionService>();
}
```

### 步骤 3: 编写单元测试

```csharp
// Tests/Unit/DataCollectionTests.cs
[TestFixture]
public class BinanceDataCollectionServiceTests
{
    private Mock<BinanceApiClient> _mockApiClient;
    private IDataCollectionService _service;

    [SetUp]
    public void Setup()
    {
        _mockApiClient = new Mock<BinanceApiClient>();
        _service = new BinanceDataCollectionService(_mockApiClient.Object, ...);
    }

    [Test]
    public async Task GetLatestMarketSnapshotAsync_CallsApiClient()
    {
        // Arrange
        _mockApiClient.Setup(x => x.Get24hTickerAsync("BTCUSDT"))
            .ReturnsAsync(new Binance24hTicker { ... });

        // Act
        var snapshot = await _service.GetLatestMarketSnapshotAsync("BTCUSDT");

        // Assert
        Assert.IsNotNull(snapshot);
        _mockApiClient.Verify(x => x.Get24hTickerAsync("BTCUSDT"), Times.Once);
    }
}
```

---

## ?? 关键实现提示

### 1. 数据采集 (IDataCollectionService)

? **缓存策略**
```csharp
private readonly Dictionary<string, MarketSnapshot> _snapshotCache = new();
private readonly ConcurrentDictionary<string, Func<MarketSnapshot, Task>> _subscriptions = new();
```

? **实时更新处理**
```csharp
public string SubscribeToRealTimeData(string symbol, Func<MarketSnapshot, Task> onDataReceived)
{
    var id = Guid.NewGuid().ToString();
    _subscriptions[id] = onDataReceived;
    _streamClient.Subscribe(symbol, onDataReceived);
    return id;
}
```

### 2. 策略评估 (IStrategyEvaluationService)

? **性能指标计算**
```csharp
public async Task<StrategyPerformanceMetrics> EvaluatePerformanceAsync(
    List<TradeRecord> trades,
    decimal initialBalance)
{
    return new StrategyPerformanceMetrics
    {
        TotalProfit = trades.Sum(t => t.Profit),
        WinRate = CalculateWinRate(trades),
        SharpeRatio = CalculateSharpeRatio(trades),
        MaxDrawdown = CalculateMaxDrawdown(trades),
        // ... 其他指标
    };
}
```

### 3. 策略筛选 (IStrategyFilterService)

? **组合推荐**
```csharp
public async Task<List<StrategyInfo>> GetRecommendedPortfolioAsync(
    List<StrategyInfo> strategies,
    int portfolioSize = 3)
{
    // 使用现代投资组合理论 (MPT)
    // 选择夏普比率最高的组合
    return strategies
        .OrderByDescending(s => s.Performance.SharpeRatio)
        .Take(portfolioSize)
        .ToList();
}
```

---

## ?? 测试覆盖清单

### Unit Tests (单元测试)
- [ ] DataCollectionServiceTests (10+ 测试)
- [ ] StrategyEvaluationTests (12+ 测试)
- [ ] StrategyFilterTests (8+ 测试)
- [ ] OrderExecutorTests (15+ 测试)
- [ ] PositionManagerTests (12+ 测试)

### Integration Tests (集成测试)
- [ ] DataCollectionIntegrationTests
- [ ] StrategyEvaluationIntegrationTests
- [ ] End-to-EndTradingTests

### Coverage Target
```
整体代码覆盖率: >= 80%
关键逻辑覆盖率: >= 95%
```

---

## ?? 实现进度追踪

### Week 2 进度

| 任务 | Status | 完成日期 | 备注 |
|------|--------|---------|------|
| BinanceDataCollectionService | ? | - | Day 1-2 |
| StrategyModels.cs | ? | - | Day 3 |
| StrategyEvaluationService | ? | - | Day 4-5 |

### Week 3 进度

| 任务 | Status | 完成日期 | 备注 |
|------|--------|---------|------|
| StrategyFilterService | ? | - | Day 1-2 |
| 单元测试 | ? | - | Day 3-4 |
| 集成测试 | ? | - | Day 4-5 |

---

## ?? 设计建议

### 1. 错误处理

```csharp
try
{
    var snapshot = await _apiClient.GetMarketDataAsync(symbol);
    return MapToMarketSnapshot(snapshot);
}
catch (HttpRequestException ex)
{
    _logger.LogError(ex, "Failed to fetch market data for {Symbol}", symbol);
    throw new DataCollectionException($"Failed to fetch {symbol}", ex);
}
catch (OperationCanceledException ex)
{
    _logger.LogWarning("Market data request cancelled for {Symbol}", symbol);
    throw;
}
```

### 2. 异步并发

```csharp
// ? 推荐: 并发获取多个符号的数据
var tasks = symbols.Select(s => 
    GetLatestMarketSnapshotAsync(s, cancellationToken));
var snapshots = await Task.WhenAll(tasks);
```

### 3. 缓存策略

```csharp
public async Task<MarketSnapshot?> GetLatestMarketSnapshotAsync(string symbol)
{
    // 检查缓存（5 秒有效期）
    if (_cache.TryGetValue(symbol, out var cached) && 
        DateTime.UtcNow - cached.LastUpdateTime < TimeSpan.FromSeconds(5))
    {
        return cached;
    }

    // 从 API 获取新数据
    var snapshot = await _apiClient.GetMarketDataAsync(symbol);
    _cache[symbol] = snapshot;
    return snapshot;
}
```

---

## ?? 依赖关系

```
Week 2-3 的实现依赖于 Week 1 完成的接口：

BinanceDataCollectionService 
    ↓ 实现
IDataCollectionService (Week 1 ?)
    ↓ 依赖
StrategyEvaluationService
    ↓ 使用
IStrategyEvaluationService (Week 1 ?)
    ↓ 依赖
StrategyFilterService
    ↓ 使用
IStrategyFilterService (Week 1 ?)
```

---

## ?? 参考资源

### 代码示例
- `Docs/WEEK1_QUICK_START.md` - 接口使用示例
- `Docs/WEEK1_COMPLETION_REPORT.md` - 接口详细说明

### 技术参考
- [Binance API 文档](https://binance-docs.github.io/apidocs/)
- [C# 异步编程最佳实践](https://docs.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)
- [单位测试最佳实践](https://xunit.net/docs/getting-started/netfx)

---

## ?? 优先级排序

**Week 2 优先级（按重要性）**:
1. ?? BinanceDataCollectionService (最高)
2. ?? StrategyModels.cs (高)
3. ?? StrategyEvaluationService (高)

**Week 3 优先级**:
1. ?? StrategyFilterService (最高)
2. ?? 单元测试 (高)
3. ?? 集成测试 (高)

---

## ? 完成条件

每个实现都必须满足以下条件才视为完成：

- [ ] 实现所有接口方法
- [ ] 至少 80% 的代码覆盖率
- [ ] 无编译错误和警告
- [ ] 通过所有单元测试
- [ ] 包含完整的 XML 文档注释
- [ ] 遵循现有代码风格
- [ ] 异常处理完整

---

## ?? 常见问题

**Q: 如何集成 BinanceApiClient？**  
A: 它已在 `Services/BinanceApiClient.cs` 中定义。在 DI 中注册它，然后在构造函数中注入。

**Q: 如何处理实时数据更新？**  
A: 使用 `BinanceStreamClient` 订阅 WebSocket，在回调中更新缓存和触发事件。

**Q: 如何测试异步方法？**  
A: 使用 `async Task` 测试方法，并使用 `.Result` 或 `await`。

---

**下一步**: 
?? 根据本指南开始 Week 2 的实现  
?? 每日更新进度  
?? 遇到问题查看参考资源  

祝你编码愉快！??

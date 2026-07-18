# Week 4-5 实现指南

> 订单执行、错误恢复和风险管理层实现详解

---

## ?? Week 4-5 任务概览

### Week 4 目标
```
优先级: P0 (最高)

Application/Services/
  ├── RobustOrderExecutor.cs              ← 订单执行
  ├── ErrorRecoveryHandler.cs             ← 错误恢复
  ├── PositionManager.cs                  ← 仓位管理
  └── LeverageController.cs               ← 杠杆控制
```

### Week 5 目标
```
优先级: P0 (最高)

Application/ClosedLoopOrchestration/
  ├── AutomatedTradingEngine.cs           ← 核心编排
  └── TradingEngineStatus.cs              ← 状态管理

Infrastructure/Persistence/
  ├── SqliteTradingRecorder.cs            ← 交易记录
  ├── TradeRepository.cs                  ← 数据仓储
  └── StrategyRepository.cs               ← 策略仓储

Tests/
  ├── Unit/OrderExecutorTests.cs          ← 单元测试
  ├── Unit/PositionManagerTests.cs        ← 单元测试
  └── Integration/ClosedLoopIntegrationTests.cs
```

---

## ?? Week 4: 执行和风险管理层

### 1. RobustOrderExecutor (订单执行)

**文件**: `Application/Services/RobustOrderExecutor.cs`

#### 接口方法
```csharp
public interface IOrderExecutor
{
    // 订单执行
    Task<OrderResponse> PlaceOrderAsync(OrderRequest request);
    Task<OrderResponse> PlaceMarketOrderAsync(string symbol, OrderSide side, decimal quantity);
    Task<OrderResponse> PlaceLimitOrderAsync(string symbol, OrderSide side, decimal quantity, decimal price);
    
    // 订单管理
    Task<OrderResponse> CancelOrderAsync(string symbol, long orderId);
    Task<List<OrderResponse>> GetOpenOrdersAsync(string symbol = null);
    Task<OrderResponse> GetOrderStatusAsync(string symbol, long orderId);
    
    // 批量操作
    Task<List<OrderResponse>> PlaceBatchOrdersAsync(BatchOrderRequest batch);
    
    // 错误处理
    Task<OrderResponse> RetryOrderAsync(OrderRequest request, int maxAttempts = 3);
}
```

#### 实现要点

1. **订单验证**
   ```csharp
   - 验证交易对有效性
   - 验证数量和价格范围
   - 验证账户余额
   - 检查交易对状态
   ```

2. **重试逻辑**
   ```csharp
   // 指数退避重试
   - 第 1 次重试: 等待 250ms
   - 第 2 次重试: 等待 500ms
   - 第 3 次重试: 等待 1000ms
   ```

3. **错误处理**
   ```csharp
   - 捕获网络错误并重试
   - 捕获 API 限流（HTTP 429）
   - 捕获余额不足等业务错误
   - 记录所有订单操作日志
   ```

4. **状态跟踪**
   ```csharp
   - 维护订单状态机
   - 支持订单生命周期（新建→待执行→已执行→已取消）
   - 异步等待订单填充
   ```

#### 关键实现步骤
```csharp
public class RobustOrderExecutor : IOrderExecutor
{
    private readonly BinanceApiClient _apiClient;
    private readonly ILogger _logger;
    private readonly Dictionary<long, OrderState> _orderStates = new();

    public async Task<OrderResponse> PlaceOrderAsync(OrderRequest request)
    {
        // 1. 验证请求
        ValidateOrderRequest(request);
        
        // 2. 尝试下单
        OrderResponse response = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                response = await _apiClient.PlaceOrderAsync(request);
                _orderStates[response.OrderId] = new OrderState { Order = response };
                _logger.Information("订单已下达: {Symbol} {Side} {Quantity} @ {Price}", 
                    request.Symbol, request.Side, request.Quantity, request.Price);
                return response;
            }
            catch (HttpRequestException ex) when (ShouldRetry(ex))
            {
                if (attempt < 2)
                {
                    await Task.Delay(GetRetryDelay(attempt));
                    continue;
                }
                throw;
            }
        }
        
        return response;
    }
    
    public async Task<OrderResponse> CancelOrderAsync(string symbol, long orderId)
    {
        try
        {
            var cancelled = await _apiClient.CancelOrderAsync(symbol, orderId);
            _logger.Information("订单已取消: {OrderId}", orderId);
            return cancelled;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "取消订单失败: {OrderId}", orderId);
            throw;
        }
    }
}
```

---

### 2. ErrorRecoveryHandler (错误恢复)

**文件**: `Application/Services/ErrorRecoveryHandler.cs`

#### 接口方法
```csharp
public interface IErrorRecoveryHandler
{
    // 错误恢复
    Task<RecoveryResult> HandleOrderExecutionErrorAsync(OrderExecutionError error);
    Task<RecoveryResult> HandleNetworkErrorAsync(Exception exception);
    Task<RecoveryResult> HandleApiErrorAsync(ApiErrorResponse error);
    
    // 状态恢复
    Task<AccountState> ReconcileAccountStateAsync();
    Task<List<OrderResponse>> RecoverLostOrdersAsync();
    
    // 降级和降级
    Task<bool> EnableFallbackModeAsync();
    Task<bool> DisableFallbackModeAsync();
    
    // 报告生成
    Task<RecoveryReport> GetRecoveryReportAsync();
}
```

#### 实现要点

1. **错误分类**
   ```csharp
   - 网络错误: 可恢复，自动重试
   - API 限流: 需要等待，进行指数退避
   - 余额不足: 不可恢复，记录告警
   - 市场关闭: 需要等待市场重开
   - 系统维护: 需要等待维护完成
   ```

2. **恢复策略**
   ```csharp
   - 自动重试: 网络临时错误
   - 等待重试: API 限流、市场维护
   - 人工介入: 业务错误、账户异常
   - 降级模式: 核心功能失败时切换到只读模式
   ```

3. **账户对账**
   ```csharp
   - 定期查询账户余额
   - 对比本地订单记录
   - 处理丢失的订单
   - 更新持仓状态
   ```

#### 关键实现步骤
```csharp
public class ErrorRecoveryHandler : IErrorRecoveryHandler
{
    public async Task<RecoveryResult> HandleOrderExecutionErrorAsync(
        OrderExecutionError error)
    {
        _logger.Warning("订单执行错误: {Message}", error.Message);
        
        // 根据错误类型确定恢复策略
        switch (error.ErrorType)
        {
            case OrderErrorType.NetworkError:
                // 重试
                return await RetryOrderAsync(error.OrderRequest);
                
            case OrderErrorType.ApiRateLimit:
                // 等待后重试
                await Task.Delay(TimeSpan.FromSeconds(60));
                return await RetryOrderAsync(error.OrderRequest);
                
            case OrderErrorType.InsufficientBalance:
                // 记录告警，不重试
                _logger.Error("余额不足无法执行订单");
                return new RecoveryResult { Success = false, IsRecoverable = false };
                
            default:
                // 其他错误，等待手动介入
                _logger.Error("未知订单错误，需要手动处理");
                return new RecoveryResult { Success = false, IsRecoverable = true };
        }
    }
    
    public async Task<AccountState> ReconcileAccountStateAsync()
    {
        try
        {
            // 1. 获取账户最新状态
            var balances = await _apiClient.GetAccountBalancesAsync();
            var positions = await _apiClient.GetPositionsAsync();
            
            // 2. 对比本地记录
            var discrepancies = CompareWithLocalRecords(balances, positions);
            
            // 3. 记录差异并报警
            if (discrepancies.Count > 0)
            {
                _logger.Warning("发现账户对账差异: {Count} 项", discrepancies.Count);
                // 自动修复或需要手动处理
            }
            
            // 4. 返回对账结果
            return new AccountState 
            { 
                Balances = balances,
                Positions = positions,
                Discrepancies = discrepancies
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "账户对账失败");
            throw;
        }
    }
}
```

---

### 3. PositionManager (仓位管理)

**文件**: `Application/Services/PositionManager.cs`

#### 接口方法
```csharp
public interface IPositionManager
{
    // 仓位查询
    Task<PositionSnapshot> GetPositionAsync(string symbol);
    Task<List<PositionSnapshot>> GetAllPositionsAsync();
    Task<decimal> GetTotalNotionalValueAsync();
    
    // 仓位操作
    Task<bool> OpenPositionAsync(string symbol, decimal quantity, decimal entryPrice);
    Task<bool> ClosePositionAsync(string symbol, decimal exitPrice);
    Task<bool> ModifyPositionAsync(string symbol, decimal newQuantity);
    
    // 风险管理
    Task<bool> CheckPositionLimitsAsync(PositionAdjustment adjustment);
    Task<decimal> CalculatePositionRiskAsync(string symbol);
    Task<bool> ApplyStopLossAsync(string symbol, decimal stopPrice);
    
    // 仓位分析
    Task<PositionMetrics> GetPositionMetricsAsync();
}
```

#### 实现要点

1. **仓位状态跟踪**
   ```csharp
   - 维护当前持仓
   - 追踪开仓价格和时间
   - 计算未实现的 P&L
   - 监控杠杆使用率
   ```

2. **风险检查**
   ```csharp
   - 检查单个仓位限额
   - 检查总持仓限额
   - 检查杠杆使用
   - 检查保证金比率
   ```

3. **止损和止盈**
   ```csharp
   - 支持设置止损价格
   - 支持设置止盈价格
   - 自动清算亏损仓位
   - 追踪止损功能
   ```

#### 关键实现步骤
```csharp
public class PositionManager : IPositionManager
{
    private readonly BinanceApiClient _apiClient;
    private readonly Dictionary<string, PositionState> _positions = new();
    
    public async Task<bool> OpenPositionAsync(
        string symbol, 
        decimal quantity, 
        decimal entryPrice)
    {
        // 1. 检查是否超限
        if (!await CheckPositionLimitsAsync(
            new PositionAdjustment { Symbol = symbol, Quantity = quantity }))
        {
            _logger.Warning("仓位超限: {Symbol} {Quantity}", symbol, quantity);
            return false;
        }
        
        // 2. 更新本地仓位
        if (!_positions.ContainsKey(symbol))
            _positions[symbol] = new PositionState();
        
        _positions[symbol].Quantity += quantity;
        _positions[symbol].AveragePrice = CalculateAveragePrice(
            _positions[symbol], 
            quantity, 
            entryPrice);
        _positions[symbol].OpenTime = DateTime.UtcNow;
        
        _logger.Information("仓位已开启: {Symbol} {Qty} @ {Price}", 
            symbol, quantity, entryPrice);
        return true;
    }
    
    public async Task<bool> ClosePositionAsync(
        string symbol, 
        decimal exitPrice)
    {
        if (!_positions.ContainsKey(symbol) || _positions[symbol].Quantity == 0)
        {
            _logger.Warning("无仓位可平: {Symbol}", symbol);
            return false;
        }
        
        var position = _positions[symbol];
        var profit = (exitPrice - position.AveragePrice) * position.Quantity;
        
        position.Quantity = 0;
        position.CloseTime = DateTime.UtcNow;
        position.RealizedProfit = profit;
        
        _logger.Information("仓位已平: {Symbol}, 盈利={Profit}", symbol, profit);
        return true;
    }
}
```

---

### 4. LeverageController (杠杆控制)

**文件**: `Application/Services/LeverageController.cs`

#### 接口方法
```csharp
public interface ILeverageController
{
    // 杠杆查询
    Task<decimal> GetCurrentLeverageAsync(string symbol);
    Task<decimal> GetMaxLeverageAsync(string symbol);
    Task<decimal> GetAvailableLeverageAsync();
    
    // 杠杆调整
    Task<bool> SetLeverageAsync(string symbol, decimal leverage);
    Task<bool> AdjustLeverageAsync(string symbol, decimal deltaLeverage);
    
    // 风险监控
    Task<LeverageMetrics> GetLeverageMetricsAsync();
    Task<bool> CheckLeverageHealthAsync();
    
    // 自动管理
    Task<bool> AutoAdjustLeverageAsync(AccountHealth health);
    Task<bool> ReduceLeverageAsync(decimal targetRatio);
}
```

#### 实现要点

1. **杠杆管理**
   ```csharp
   - 记录每个交易对的杠杆倍数
   - 支持不同交易对的不同杠杆
   - 对杠杆变更进行验证
   - 防止杠杆过高
   ```

2. **风险监控**
   ```csharp
   - 监控总体杠杆使用率
   - 检查保证金充足率
   - 防止强制清算
   - 主动降低风险
   ```

3. **自动调整**
   ```csharp
   - 根据账户健康度自动调整
   - 大跌时自动降低杠杆
   - 盈利时逐步增加杠杆
   - 保险止损机制
   ```

---

## ?? Week 4-5 数据模型

### OrderRequest
```csharp
public class OrderRequest
{
    public string Symbol { get; set; }
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal StopPrice { get; set; }
    public string ClientOrderId { get; set; }
}
```

### OrderResponse
```csharp
public class OrderResponse
{
    public long OrderId { get; set; }
    public string Symbol { get; set; }
    public decimal Price { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal ExecutedQuantity { get; set; }
    public string Status { get; set; }
    public DateTime Time { get; set; }
}
```

### PositionSnapshot
```csharp
public class PositionSnapshot
{
    public string Symbol { get; set; }
    public decimal Quantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal UnrealizedProfit { get; set; }
    public DateTime OpenTime { get; set; }
}
```

---

## ?? Week 4-5 测试计划

### 单元测试
```csharp
// OrderExecutorTests.cs
- PlaceOrderAsync: 正常下单
- CancelOrderAsync: 正常取消
- GetOrderStatusAsync: 查询状态
- RetryOrderAsync: 重试逻辑
- ValidateOrderRequest: 验证逻辑

// PositionManagerTests.cs
- OpenPositionAsync: 开仓
- ClosePositionAsync: 平仓
- CheckPositionLimitsAsync: 限额检查
- GetTotalNotionalValueAsync: 总资产计算

// ErrorRecoveryTests.cs
- HandleOrderExecutionErrorAsync: 订单错误恢复
- ReconcileAccountStateAsync: 账户对账
- RecoverLostOrdersAsync: 订单恢复
```

### 集成测试
```csharp
// ClosedLoopIntegrationTests.cs
- 完整的订单生命周期测试
- 数据采集 → 策略评估 → 订单执行 → 仓位管理
- 错误场景测试：网络中断、API 限流、余额不足
- 性能测试：并发订单、高频交易
```

---

## ?? 最佳实践

### 1. 订单执行最佳实践
```csharp
? 总是验证订单请求
? 使用重试机制处理临时错误
? 记录所有订单操作日志
? 异步非阻塞执行
? 实现超时控制
```

### 2. 错误恢复最佳实践
```csharp
? 区分可恢复和不可恢复的错误
? 实现指数退避重试
? 定期账户对账
? 保留完整的错误日志
? 支持手动干预
```

### 3. 仓位管理最佳实践
```csharp
? 强制执行仓位限额
? 实时 P&L 计算
? 自动止损和止盈
? 防止过度杠杆
? 记录所有交易历史
```

---

## ?? 实现时间表

### Week 4
- Day 1-2: RobustOrderExecutor 实现
- Day 3: ErrorRecoveryHandler 实现
- Day 4: PositionManager + LeverageController 实现
- Day 5: 单元测试编写和调试

### Week 5
- Day 1-2: AutomatedTradingEngine 实现
- Day 3: Persistence 层实现 (SqliteTradingRecorder)
- Day 4: 集成测试编写
- Day 5: 文档和代码评审

---

## ? Week 4-5 验收标准

- [ ] 4 个核心服务实现完成
- [ ] 单元测试覆盖率 > 80%
- [ ] 集成测试通过
- [ ] 零编译错误和警告
- [ ] 完整的 XML 文档注释
- [ ] 代码审查通过
- [ ] 性能基准测试完成

---

**文档版本**: 1.0  
**创建日期**: Week 2-3  
**目标实现**: Week 4-5  
**状态**: 准备就绪

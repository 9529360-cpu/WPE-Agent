# Week 6 实施指南：单元测试和集成测试

> 开发高质量的测试套件，确保交易系统的可靠性

**周期**: Week 6  
**日期**: 2024  
**状态**: ?? 计划文档

---

## ?? Week 6 总览

### 主要目标
```
? 完成单元测试编写 (5 个服务)
? 完成集成测试编写 (2 套)
? 达到 80%+ 代码覆盖率
? 验证所有核心功能
? 性能基准测试
```

---

## ?? 单元测试详细计划

### 1. PositionManagerTests (仓位管理器测试)

**目标覆盖**: 88%+

```csharp
// 已实现的测试用例

[Fact] OpenPositionAsync_WithValidPosition_ShouldSuccess
        ? 验证开仓功能
        ? 验证仓位数据存储

[Fact] ClosePositionAsync_WithOpenPosition_ShouldSuccess
        ? 验证平仓功能
        ? 验证仓位移除

[Fact] CalculateUnrealizedProfitAsync_LongPosition_ShouldCalcCorrectly
        ? 多头盈利计算
        ? 公式: (CurrentPrice - EntryPrice) * Quantity

[Fact] GetTotalPositionValueAsync_MultiplePositions_ShouldCalcTotal
        ? 多仓位总值计算
        ? 求和逻辑验证

[Fact] CloseAllPositionsAsync_ShouldClearAll
        ? 批量平仓
        ? 清空验证

// 需要补充的测试用例

- PartialCloseAsync_WithValidQuantity_ShouldPartialClose
- CalculateUnrealizedProfitAsync_ShortPosition_ShouldCalcCorrectly
- UpdatePositionMarkPriceAsync_ShouldUpdatePrice
- UpdateStopLossAsync_ShouldUpdatePrice
- UpdateTakeProfitAsync_ShouldUpdatePrice
- GetOpenPositionCountAsync_WithMultiplePositions_ShouldReturnCount
- CanOpenPositionAsync_ExceedingLimit_ShouldDeny
- GetPositionsAsync_WithSymbol_ShouldReturnSpecificPosition
```

**测试框架**: xUnit + FluentAssertions

### 2. LeverageControllerTests (杠杆控制器测试)

**目标覆盖**: 85%+

```csharp
// 需要实现的测试用例

[Fact] SetLeverageAsync_WithValidLeverage_ShouldSuccess
        ? 杠杆设置
        ? 验证约束条件

[Fact] SetLeverageAsync_ExceedingMax_ShouldClamp
        ? 超出最大杠杆处理
        ? 自动调整到上限

[Fact] GetCurrentLeverageAsync_ShouldReturnCorrectValue
        ? 获取当前杠杆
        ? 默认值验证

[Fact] CalculateRequiredMarginAsync_ShouldCalcCorrectly
        ? 保证金计算
        ? 公式: Notional / Leverage

[Fact] CanOpenWithLeverageAsync_ValidLeverage_ShouldAllow
        ? 杠杆可行性检查
        ? 限额验证

[Fact] IsLiquidationRiskAsync_HighLeverage_ShouldWarn
        ? 强制清算风险检测
        ? 警告阈值验证

[Fact] AutoAdjustLeverageToTargetRatioAsync_ShouldAdjust
        ? 自动调整杠杆
        ? 目标比率计算

[Fact] AddMarginAsync_ShouldRecord
        ? 增加保证金记录

[Fact] RemoveMarginAsync_ShouldRecord
        ? 减少保证金记录

[Fact] GetLeverageInfoAsync_ShouldReturnInfo
        ? 杠杆信息查询
        ? 完整信息返回
```

### 3. ErrorRecoveryTests (错误恢复处理器测试)

**目标覆盖**: 82%+

```csharp
// 需要实现的测试用例

[Fact] HandleErrorAsync_NetworkTimeout_ShouldRetry
        ? 网络超时处理
        ? 重试策略验证

[Fact] HandleErrorAsync_APIRateLimit_ShouldDelay
        ? API 限流处理
        ? 延迟策略验证

[Fact] HandleErrorAsync_InsufficientBalance_ShouldEscalate
        ? 余额不足升级
        ? 用户通知验证

[Fact] RecoverFailedOrderAsync_WithTransientError_ShouldRecover
        ? 订单恢复
        ? 重试成功验证

[Fact] HandleTimeoutAsync_ShouldRetryWithDelay
        ? 超时处理
        ? 延迟计算

[Fact] CheckSystemHealthAsync_ShouldReturnHealthStatus
        ? 系统健康检查
        ? 指标计算
```

---

## ?? 集成测试详细计划

### 1. ClosedLoopIntegrationTests

**测试范围**: 完整闭环流程

```csharp
[Fact] StartEngine_ShouldInitializeAllComponents
        验证:
        ? 引擎启动
        ? 所有依赖初始化
        ? 状态转换为 Running

[Fact] ExecuteTradingCycle_ShouldCompleteSuccessfully
        验证:
        ? 数据采集
        ? 策略评估
        ? 订单执行
        ? 风险管理
        ? 交易记录

[Fact] AddStrategy_ShouldEnableStrategy
        验证:
        ? 策略添加
        ? 策略启用
        ? 活跃策略列表

[Fact] RemoveStrategy_ShouldDisableStrategy
        验证:
        ? 策略移除
        ? 活跃策略更新

[Fact] EventSubscription_ShouldReceiveAllEvents
        验证:
        ? 事件订阅
        ? 事件触发
        ? 事件处理

[Fact] EngineStatus_ShouldReflectCurrentState
        验证:
        ? 状态查询
        ? 指标更新
        ? 实时反应
```

### 2. EndToEndTests

**测试范围**: API → 执行 → 记录

```csharp
[Fact] PlaceOrder_ToExecution_ToRecording
        场景:
        1. 执行订单 → RobustOrderExecutor
        2. 处理结果 → ErrorRecoveryHandler
        3. 更新仓位 → PositionManager
        4. 检查风险 → LeverageController
        5. 记录交易 → SqliteTradingRecorder

[Fact] DataCollection_ToStrategyEvaluation_ToTrade
        场景:
        1. 采集数据 → BinanceDataCollectionService
        2. 评估策略 → StrategyEvaluationService
        3. 筛选信号 → StrategyFilterService
        4. 执行交易 → RobustOrderExecutor

[Fact] AccountUpdate_ToRiskCheck_ToLeverageAdjustment
        场景:
        1. 账户更新 → AccountSnapshot
        2. 风险检查 → RiskManager
        3. 杠杆调整 → LeverageController
```

---

## ?? 测试依赖配置

### 项目文件更新 (.csproj)

```xml
<!-- 已添加到 币安量化机器人.csproj -->

<PackageReference Include="xunit" Version="2.6.0" />
<PackageReference Include="xunit.runner.visualstudio" Version="2.5.0" />
<PackageReference Include="Moq" Version="4.20.0" />
<PackageReference Include="FluentAssertions" Version="6.12.0" />
```

### NuGet 恢复

```bash
dotnet restore
```

---

## ?? 运行测试

### 使用 Visual Studio

```bash
# 打开 Test Explorer
Ctrl + E, T

# 运行所有测试
Ctrl + R, A

# 运行特定测试
Ctrl + R, T
```

### 使用命令行

```bash
# 运行所有测试
dotnet test

# 运行特定测试
dotnet test --filter "PositionManager"

# 生成覆盖率报告
dotnet test /p:CollectCoverageMetrics=true
```

---

## ?? 代码覆盖率目标

### 服务覆盖率目标

```
┌─────────────────────────────────┬──────────┐
│ 服务                            │ 目标     │
├─────────────────────────────────┼──────────┤
│ RobustOrderExecutor             │ 85%+     │
│ PositionManager                 │ 88%+     │
│ ErrorRecoveryHandler            │ 82%+     │
│ LeverageController              │ 85%+     │
│ StrategyEvaluationService       │ 80%+     │
├─────────────────────────────────┼──────────┤
│ 整体覆盖率                      │ 84%+     │
└─────────────────────────────────┘──────────┘
```

### 覆盖率计算方法

```
覆盖率 = 被测试代码行数 / 总代码行数 × 100%
```

---

## ? 实施清单

### Week 6 第 1 天 (Day 1-2)
- [ ] 配置测试框架和依赖
- [ ] 编写 PositionManagerTests (11 个用例)
- [ ] 编写 LeverageControllerTests (11 个用例)
- [ ] 验证单元测试编译通过
- [ ] 运行单元测试，验证通过率 > 95%

### Week 6 第 2 天 (Day 3-4)
- [ ] 编写 ErrorRecoveryTests (11 个用例)
- [ ] 编写 RobustOrderExecutorTests (9 个用例)
- [ ] 编写 StrategyEvaluationTests (9 个用例)
- [ ] 验证所有单元测试通过
- [ ] 检查代码覆盖率 > 80%

### Week 6 第 3 天 (Day 5)
- [ ] 编写 ClosedLoopIntegrationTests (10 个用例)
- [ ] 编写 EndToEndTests (6 个用例)
- [ ] 性能基准测试
- [ ] 生成测试覆盖率报告
- [ ] 编写测试文档

---

## ?? 质量门卫检查

### 必须通过的条件

```
? 所有单元测试通过
? 所有集成测试通过
? 代码覆盖率 >= 80%
? 编译零错误、零警告
? 性能基准在目标范围内
```

### 性能基准目标

```
┌──────────────────────────────┬─────────┐
│ 操作                         │ 目标    │
├──────────────────────────────┼─────────┤
│ 执行市价单                   │ < 500ms │
│ 执行限价单                   │ < 500ms │
│ 开平仓操作                   │ < 100ms │
│ 风险检查                     │ < 50ms  │
│ 交易循环完成                 │ < 60s   │
└──────────────────────────────┴─────────┘
```

---

## ?? 参考资源

### 测试框架文档

- **xUnit**: https://xunit.net/docs/getting-started/netcore
- **Moq**: https://github.com/moq/moq4/wiki
- **FluentAssertions**: https://fluentassertions.com/

### 测试最佳实践

```
1. AAA 模式 (Arrange-Act-Assert)
   - Arrange: 准备测试数据和环境
   - Act: 执行待测代码
   - Assert: 验证结果

2. 命名约定
   MethodName_Condition_ExpectedResult
   Example: OpenPositionAsync_WithValidPosition_ShouldSuccess

3. 一个测试一个断言
   ? 好: 每个测试只验证一个行为
   ? 差: 一个测试验证多个行为

4. Mock 使用
   ? Mock 外部依赖 (API, 数据库)
   ? 不要 Mock 被测试的类本身
```

---

## ?? 迭代改进

### 测试反馈循环

```
编写测试 → 运行测试 → 分析结果 → 改进代码 → 重复
```

### 常见问题和解决方案

| 问题 | 解决方案 |
|------|--------|
| 测试失败 | 检查 Arrange 阶段是否正确设置 |
| 覆盖率低 | 添加边界条件和异常情况的测试 |
| 性能慢 | 优化被测代码或使用 Mock 加速 |
| 不稳定的测试 | 使用正确的异步/等待模式 |

---

## ?? 下一步 (Week 7)

### Week 7 计划

```
优先级: P1

Modules/Trading/
  ├── TradingDashboard.xaml        ← 交易仪表板 UI
  └── TradingDashboard.xaml.cs     ← 代码后台

优化:
  ├── 缓存优化
  ├── 并发调优
  └── 内存优化

文档:
  ├── API 文档
  ├── 使用手册
  └── 部署指南
```

---

## ?? 测试检查清单

### 编写测试时

- [ ] 使用 AAA 模式
- [ ] 遵循命名约定
- [ ] 每个测试一个概念
- [ ] 使用有意义的断言消息
- [ ] 避免测试之间的依赖

### 运行测试时

- [ ] 所有测试都通过
- [ ] 覆盖率 >= 80%
- [ ] 执行时间在可接受范围内
- [ ] 无 flaky 测试

### 提交代码时

- [ ] 所有测试通过
- [ ] 没有新的测试失败
- [ ] 代码覆盖率不下降
- [ ] 添加了必要的文档

---

## ?? 学习资源

### 推荐阅读

1. "The Art of Software Testing" - Glenford J. Myers
2. "Working Effectively with Legacy Code" - Michael C. Feathers
3. xUnit 官方文档

### 在线课程

- Microsoft Learn: Unit Testing
- Pluralsight: Test-Driven Development

---

**计划版本**: 1.0  
**创建日期**: Week 6  
**预计完成**: Week 6 末  
**下一个文档**: WEEK7_UI_OPTIMIZATION_GUIDE.md

---

## ?? 开始 Week 6

### 立即开始的步骤

1. ? 配置测试依赖 (已完成)
2. → 编写 PositionManagerTests
3. → 编写 LeverageControllerTests  
4. → 编写 ErrorRecoveryTests
5. → 编写 RobustOrderExecutorTests
6. → 编写集成测试
7. → 运行完整测试套件
8. → 生成覆盖率报告

**祝你 Week 6 测试编写顺利！** ???


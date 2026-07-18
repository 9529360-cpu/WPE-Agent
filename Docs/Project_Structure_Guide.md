# 项目目录结构规划

> 新架构的完整目录布局（可直接使用）

---

## ?? 完整项目结构

```
币安量化机器人/
├── ?? App.xaml                         ← 更新
├── ?? App.xaml.cs                      ← 更新 (DI 初始化)
├── ?? MainWindow.xaml
├── ?? MainWindow.xaml.cs
│
├── ?? Core/                            ← 核心业务逻辑
│   │
│   ├── ?? Data/
│   │   └── IDataCollectionService.cs   ← 【新建】 数据采集接口
│   │
│   ├── ?? Strategy/
│   │   ├── IStrategyEvaluationService.cs   ← 【新建】 策略评估接口
│   │   ├── IStrategyFilterService.cs       ← 【新建】 策略筛选接口
│   │   └── ITradingStrategy.cs             ← 已有 (改进)
│   │
│   ├── ?? Execution/
│   │   ├── IOrderExecutor.cs               ← 【新建】 订单执行接口
│   │   └── IErrorRecoveryHandler.cs        ← 【新建】 错误恢复接口
│   │
│   ├── ?? Risk/
│   │   ├── IPositionManager.cs             ← 【新建】 仓位管理接口
│   │   ├── ILeverageController.cs          ← 【新建】 杠杆控制接口
│   │   ├── IRiskManager.cs                 ← 已有 (改进)
│   │   └── RiskManager.cs                  ← 已有 (改进)
│   │
│   ├── ?? Persistence/
│   │   └── ITradingRecorder.cs             ← 【新建】 交易记录接口
│   │
│   ├── ?? Logging/
│   │   └── LoggerConfiguration.cs          ← 【新建】 日志配置
│   │
│   └── ?? Models/
│       ├── TradingModels.cs                ← 已有 (扩展)
│       ├── StrategyModels.cs               ← 【新建】
│       ├── RiskModels.cs                   ← 【新建】
│       └── ExecutionModels.cs              ← 【新建】
│
├── ?? Application/                      ← 应用层
│   │
│   ├── ?? Services/
│   │   ├── StrategyEvaluationService.cs    ← 【新建】
│   │   ├── StrategyFilterService.cs        ← 【新建】
│   │   ├── PositionManager.cs              ← 【新建】
│   │   ├── LeverageController.cs           ← 【新建】
│   │   ├── RobustOrderExecutor.cs          ← 【新建】
│   │   ├── ErrorRecoveryHandler.cs         ← 【新建】
│   │   ├── DataCacheService.cs             ← 已有
│   │   ├── NotificationService.cs          ← 已有
│   │   └── AppSettingsService.cs           ← 已有
│   │
│   ├── ?? ClosedLoopOrchestration/       ← 【新建目录】 闭环编排
│   │   ├── IAutomatedTradingEngine.cs      ← 【新建】 核心接口
│   │   ├── AutomatedTradingEngine.cs       ← 【新建】 实现
│   │   └── TradingEngineStatus.cs          ← 【新建】 状态枚举
│   │
│   ├── ?? Backtesting/
│   │   ├── RobustBacktestEngine.cs         ← 已有
│   │   ├── GridSearchStrategyOptimizer.cs  ← 已有
│   │   └── WalkForwardOptimizer.cs         ← 已有
│   │
│   └── ?? Strategies/                    ← 策略实现
│       ├── MeanReversionStrategy.cs        ← 已有
│       ├── MomentumStrategy.cs             ← 已有
│       └── CustomStrategy.cs               ← (根据需要添加)
│
├── ?? Infrastructure/                   ← 基础设施层
│   │
│   ├── ?? Data/
│   │   ├── BinanceDataCollectionService.cs ← 【新建】
│   │   ├── RealTimeDataPipeline.cs         ← 已有
│   │   ├── DatabaseDataSource.cs           ← 已有
│   │   ├── ApiDataSource.cs                ← 已有
│   │   └── FileDataSource.cs               ← 已有
│   │
│   ├── ?? Persistence/
│   │   ├── SqliteTradingRecorder.cs        ← 【新建】
│   │   ├── TradeRepository.cs              ← 【新建】
│   │   └── StrategyRepository.cs           ← 【新建】
│   │
│   ├── ?? External/
│   │   ├── BinanceApiClient.cs             ← 已有 (改进)
│   │   └── BinanceStreamClient.cs          ← 已有 (改进)
│   │
│   └── ?? Monitoring/
│       ├── InMemoryTradeMonitoringHub.cs   ← 已有
│       └── PerformanceMonitor.cs           ← 已有
│
├── ?? Services/                         ← 配置和工厂
│   ├── ServiceConfiguration.cs              ← 【新建】 DI 配置
│   ├── AppSettings.cs                      ← 已有 (改进)
│   └── Settings/                           ← 已有
│
├── ?? Modules/                          ← WPF UI 模块
│   ├── ?? Settings/
│   │   ├── SystemSettingsView.xaml
│   │   └── SystemSettingsView.xaml.cs
│   ├── ?? Diagnostics/
│   │   ├── DiagnosticsView.xaml
│   │   └── DiagnosticsView.xaml.cs
│   ├── ?? AI/
│   │   └── ModelHub.xaml.cs
│   ├── ?? Research/
│   │   └── BacktestView.xaml.cs
│   └── ?? Trading/                      ← 【新建】
│       ├── TradingDashboard.xaml         ← 【新建】
│       └── TradingDashboard.xaml.cs      ← 【新建】
│
├── ?? Tests/                            ← 单元测试
│   ├── ?? Unit/
│   │   ├── StrategyEvaluationTests.cs     ← 【新建】
│   │   ├── StrategyFilterTests.cs         ← 【新建】
│   │   ├── OrderExecutorTests.cs          ← 【新建】
│   │   ├── PositionManagerTests.cs        ← 【新建】
│   │   └── ErrorRecoveryTests.cs          ← 【新建】
│   │
│   ├── ?? Integration/
│   │   ├── ClosedLoopIntegrationTests.cs  ← 【新建】
│   │   └── EndToEndTests.cs               ← 【新建】
│   │
│   └── ?? Fixtures/
│       ├── ServiceFixture.cs              ← 【新建】
│       ├── MockDataGenerator.cs           ← 【新建】
│       └── TestHelpers.cs                 ← 【新建】
│
├── ?? Docs/                             ← 文档
│   ├── README.md
│   ├── COMPLETE_SOLUTION_SUMMARY.md      ← 【新建】 总体方案
│   ├── Closed_Loop_Architecture_Design.md ← 【新建】 架构设计
│   ├── Closed_Loop_Implementation_Roadmap.md ← 【新建】 实施路线图
│   ├── Quick_Reference_Architecture.md   ← 【新建】 快速参考
│   ├── Requirements_Alignment_Checklist.md ← 【新建】 需求对齐
│   ├── QUICK_START_WEEK1.md              ← 【新建】 快速启动
│   ├── Project_Structure.md              ← 【新建】 本文件
│   ├── Architecture_Improvement_Guide.md
│   ├── Quick_Implementation_Guide.md
│   ├── Thread_Safety_Improvement_Guide.md
│   ├── Summary_Report.md
│   └── DELIVERY_CHECKLIST.md
│
├── ?? Data/                             ← 数据目录
│   ├── trading.db                       ← SQLite 数据库
│   ├── import/                          ← 导入文件目录
│   └── logs/                            ← 日志目录
│
├── ?? 币安量化机器人.csproj             ← 项目文件
└── ?? appsettings.json                  ← 【新建】 配置文件

```

---

## ?? 按优先级创建文件

### 【第 1 周】必须完成

```
优先级: P0 (最高)

Core/Data/
  └── IDataCollectionService.cs           ← ? Week 1 必须
Core/Strategy/
  ├── IStrategyEvaluationService.cs       ← ? Week 1 必须
  └── IStrategyFilterService.cs           ← ? Week 1 必须
Core/Execution/
  ├── IOrderExecutor.cs                   ← ? Week 1 必须
  └── IErrorRecoveryHandler.cs            ← ? Week 1 必须
Core/Risk/
  ├── IPositionManager.cs                 ← ? Week 1 必须
  └── ILeverageController.cs              ← ? Week 1 必须
Core/Persistence/
  └── ITradingRecorder.cs                 ← ? Week 1 必须
Application/ClosedLoopOrchestration/
  └── IAutomatedTradingEngine.cs          ← ? Week 1 必须
Services/
  └── ServiceConfiguration.cs             ← ? Week 1 必须
App.xaml.cs                              ← ? Week 1 必须 (更新)
```

### 【第 2-3 周】数据层实现

```
优先级: P1 (高)

Infrastructure/Data/
  └── BinanceDataCollectionService.cs     ← Week 2 完成

Core/Models/
  └── StrategyModels.cs                   ← Week 2 完成
```

### 【第 4-5 周】策略和执行层实现

```
优先级: P1 (高)

Application/Services/
  ├── StrategyEvaluationService.cs        ← Week 3 完成
  ├── StrategyFilterService.cs            ← Week 3 完成
  ├── RobustOrderExecutor.cs              ← Week 4 完成
  ├── ErrorRecoveryHandler.cs             ← Week 4 完成
  ├── PositionManager.cs                  ← Week 4 完成
  └── LeverageController.cs               ← Week 4 完成
```

### 【第 6-7 周】核心编排实现

```
优先级: P0 (最高)

Application/ClosedLoopOrchestration/
  ├── AutomatedTradingEngine.cs           ← Week 5 完成
  └── TradingEngineStatus.cs              ← Week 5 完成

Infrastructure/Persistence/
  ├── SqliteTradingRecorder.cs            ← Week 5 完成
  └── TradeRepository.cs                  ← Week 5 完成
```

### 【第 8 周】测试和优化

```
优先级: P2 (中等)

Tests/
  └── (所有测试文件)                      ← Week 6 完成

Modules/Trading/
  ├── TradingDashboard.xaml               ← Week 7 完成
  └── TradingDashboard.xaml.cs            ← Week 7 完成

appsettings.json                          ← Week 1 完成
```

---

## ?? 文件创建检查清单

### ? 第 1 周 (Week 1)

- [ ] `Core/Data/IDataCollectionService.cs`
- [ ] `Core/Strategy/IStrategyEvaluationService.cs`
- [ ] `Core/Strategy/IStrategyFilterService.cs`
- [ ] `Core/Execution/IOrderExecutor.cs`
- [ ] `Core/Execution/IErrorRecoveryHandler.cs`
- [ ] `Core/Risk/IPositionManager.cs`
- [ ] `Core/Risk/ILeverageController.cs`
- [ ] `Core/Persistence/ITradingRecorder.cs`
- [ ] `Application/ClosedLoopOrchestration/IAutomatedTradingEngine.cs`
- [ ] `Services/ServiceConfiguration.cs`
- [ ] `App.xaml.cs` (更新)
- [ ] `appsettings.json`

### ? 第 2-3 周 (Week 2-3)

- [ ] `Infrastructure/Data/BinanceDataCollectionService.cs`
- [ ] `Application/Services/StrategyEvaluationService.cs`
- [ ] `Application/Services/StrategyFilterService.cs`
- [ ] `Core/Models/StrategyModels.cs`
- [ ] `Tests/Unit/StrategyEvaluationTests.cs`

### ? 第 4-5 周 (Week 4-5)

- [ ] `Application/Services/RobustOrderExecutor.cs`
- [ ] `Application/Services/ErrorRecoveryHandler.cs`
- [ ] `Application/Services/PositionManager.cs`
- [ ] `Application/Services/LeverageController.cs`
- [ ] `Tests/Unit/OrderExecutorTests.cs`
- [ ] `Tests/Unit/PositionManagerTests.cs`

### ? 第 6-7 周 (Week 6-7)

- [ ] `Application/ClosedLoopOrchestration/AutomatedTradingEngine.cs`
- [ ] `Infrastructure/Persistence/SqliteTradingRecorder.cs`
- [ ] `Infrastructure/Persistence/TradeRepository.cs`
- [ ] `Tests/Integration/ClosedLoopIntegrationTests.cs`
- [ ] `Modules/Trading/TradingDashboard.xaml`
- [ ] `Modules/Trading/TradingDashboard.xaml.cs`

### ? 第 8 周 (Week 8)

- [ ] 所有测试编写完成
- [ ] 性能优化完成
- [ ] 文档完善
- [ ] 部署就绪

---

## ?? 目录创建命令

如果需要一次性创建所有目录结构：

```bash
# Windows PowerShell
New-Item -ItemType Directory -Force -Path "Core/Data", "Core/Strategy", "Core/Execution", "Core/Risk", "Core/Persistence", "Core/Logging", "Core/Models"
New-Item -ItemType Directory -Force -Path "Application/Services", "Application/ClosedLoopOrchestration", "Application/Backtesting", "Application/Strategies"
New-Item -ItemType Directory -Force -Path "Infrastructure/Data", "Infrastructure/Persistence", "Infrastructure/External", "Infrastructure/Monitoring"
New-Item -ItemType Directory -Force -Path "Services/Settings"
New-Item -ItemType Directory -Force -Path "Modules/Settings", "Modules/Diagnostics", "Modules/AI", "Modules/Research", "Modules/Trading"
New-Item -ItemType Directory -Force -Path "Tests/Unit", "Tests/Integration", "Tests/Fixtures"
New-Item -ItemType Directory -Force -Path "Data/import", "Data/logs"

# Linux/Mac
mkdir -p Core/{Data,Strategy,Execution,Risk,Persistence,Logging,Models}
mkdir -p Application/{Services,ClosedLoopOrchestration,Backtesting,Strategies}
mkdir -p Infrastructure/{Data,Persistence,External,Monitoring}
mkdir -p Services/Settings
mkdir -p Modules/{Settings,Diagnostics,AI,Research,Trading}
mkdir -p Tests/{Unit,Integration,Fixtures}
mkdir -p Data/{import,logs}
```

---

## ?? 命名规范

### 接口命名

```csharp
// ? 正确
public interface IDataCollectionService { }
public interface IStrategyEvaluationService { }
public interface IOrderExecutor { }

// ? 避免
public interface DataCollectionService { }      // 缺少 I 前缀
public interface DataCollection { }             // 名字太短
```

### 实现类命名

```csharp
// ? 正确
public class BinanceDataCollectionService : IDataCollectionService { }
public class StrategyEvaluationService : IStrategyEvaluationService { }
public class RobustOrderExecutor : IOrderExecutor { }

// ? 避免
public class BinanceService { }                 // 不清晰
public class Service1 { }                       // 太通用
```

### 模型命名

```csharp
// ? 正确
public record MarketSnapshot { }
public record StrategyEvaluationResult { }
public record TradingCycleLog { }

// ? 避免
public record MarketData { }                    // 太通用
public class Result { }                         // 不清晰
```

---

## ?? 依赖关系

```
┌─────────────────────────────────────────┐
│      IAutomatedTradingEngine            │
│      (编排层 - 最顶层)                  │
└────┬────────┬─────────┬────┬─────┬──────┘
     │        │         │    │     │
     ▼        ▼         ▼    ▼     ▼
IDataCollection  IStrategyEvaluation  IPositionManager
IStrategyFilter  IOrderExecutor       ILeverageController
ITradingRecorder IErrorRecoveryHandler

(所有依赖通过 ServiceConfiguration 注入)
```

---

## ?? 关键提示

1. **接口优先**: 先定义接口 (Week 1)，再实现 (Week 2+)
2. **分层清晰**: 严格遵循 6 层架构，避免循环依赖
3. **DI 驱动**: 所有依赖通过 `ServiceConfiguration` 管理
4. **文件位置**: 接口放在 `Core/`，实现放在 `Application/` 或 `Infrastructure/`
5. **测试配套**: 每个模块都应有对应的单元测试

---

## ?? 立即开始

从第 1 周的文件开始创建：

```bash
# Step 1: 创建核心接口目录
mkdir -p Core/{Data,Strategy,Execution,Risk,Persistence,Logging}

# Step 2: 创建应用层目录
mkdir -p Application/{Services,ClosedLoopOrchestration}

# Step 3: 创建基础设施目录
mkdir -p Infrastructure/{Data,Persistence}

# Step 4: 创建服务配置目录
mkdir -p Services

# 现在开始创建文件，参考 QUICK_START_WEEK1.md
```

---

**下一步**: 
打开 [QUICK_START_WEEK1.md](./QUICK_START_WEEK1.md) 开始第 1 周的任务！


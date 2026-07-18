# 币安量化机器人 (Binance Quantitative Trading Bot)

基于 .NET 8.0 和 WPF 的币安合约量化交易机器人。

## 快速开始

### 系统要求

- **操作系统：** Windows 10/11
- **.NET SDK：** 8.0 或更高版本
- **IDE：** Visual Studio 2022 或 VS Code
- **NuGet 包管理器：** 已集成在 Visual Studio 中

### 编译项目

```bash
# 1. 克隆仓库
git clone https://github.com/9529360-cpu/WPE-.git
cd WPE-

# 2. 还原 NuGet 包
dotnet restore

# 3. 构建项目
dotnet build -c Debug
```

### 在 Visual Studio 中打开

1. 打开 Visual Studio 2022
2. 选择 "打开项目或解决方案"
3. 选择 `币安量化机器人.csproj` 文件
4. 按 `Ctrl+Shift+B` 构建项目

## 最近更新 (Recent Updates)

### ✅ 编译问题修复 (2025-11-18)

修复了以下编译错误，确保项目可在 Visual Studio 2022+ 中成功编译：

1. **重复程序集属性错误** - 禁用自动生成 AssemblyInfo
2. **异步迭代器警告 (CS8425)** - 添加 `[EnumeratorCancellation]` 特性
3. **ScottPlot 5.x API 兼容性** - 更新为新版 API

详细信息请查看：
- [中文文档](Docs/CompilationFixes.md)
- [English Documentation](Docs/CompilationFixes.en.md)

## 功能特性

### 市场与行情
- 📊 实时行情监控
- 💰 资金费率分析
- 📈 技术指标计算

### 策略与 AI
- 🤖 AI 预测模型 (LSTM)
- 📋 策略配置管理
- 📚 策略模板库
- 🔍 策略优化 (Walk-Forward)

### 交易与风控
- 💼 实盘交易
- 📝 纸上交易模拟
- 🛡️ 风险管理中心
- ⚠️ 实时预警系统

### 账户管理
- 👤 多账户支持
- 🔑 API 密钥管理
- 💵 资金与持仓查看

### 系统功能
- ⚙️ 系统设置
- 🔧 诊断工具
- 📊 性能监控

## 项目结构

```
├── Application/          # 应用层（回测、优化等）
├── Core/                 # 核心业务逻辑
├── Data/                 # 数据文件
├── Docs/                 # 文档
├── Infrastructure/       # 基础设施（数据管道）
├── Models/               # 数据模型
├── Modules/              # UI 模块
├── Monitoring/           # 监控服务
├── Services/             # 服务层
└── Tests/                # 测试

```

## 技术栈

- **UI 框架：** WPF (Windows Presentation Foundation)
- **图表库：** ScottPlot 5.0.56
- **交易所 API：** Binance.Net 8.3.0
- **数据库：** SQLite (Microsoft.Data.Sqlite 8.0.4)
- **AI 框架：** 自定义 LSTM 实现
- **目标框架：** .NET 8.0

## 安全提示

⚠️ **重要：** 本项目涉及金融交易，请注意：

1. **不要提交 API 密钥**到代码仓库
2. 使用前请先在**纸上交易模式**测试
3. 设置合理的**风险控制参数**
4. 了解量化交易的**风险**
5. **投资有风险，入市需谨慎**

## 开发指南

### 添加新模块

1. 在 `Modules/` 下创建新文件夹
2. 创建 `.xaml` 和 `.xaml.cs` 文件
3. 在 `MainWindow.xaml.cs` 的 `_viewMap` 中注册

### 添加新策略

1. 实现 `ITradingStrategy` 接口
2. 在 `Core/Strategies/` 中添加策略类
3. 在策略模板库中注册

## 贡献指南

欢迎提交 Pull Request 和 Issue！

在提交代码前，请确保：
- [ ] 代码编译无错误
- [ ] 遵循现有代码风格
- [ ] 添加必要的注释
- [ ] 更新相关文档

## 许可证

本项目仅供学习和研究使用。

## 联系方式

- GitHub Issues: [提交问题](https://github.com/9529360-cpu/WPE-/issues)
- 文档中心: [查看文档](Docs/)

---

**最后更新：** 2025-11-18  
**版本：** 1.0.0 (Alpha)

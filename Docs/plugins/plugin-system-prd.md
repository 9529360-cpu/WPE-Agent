# WPE 商业化插件体系 PRD

## 现状审计
- `WebUi/app/(dashboard)/plugins/page.tsx` 目前只是本地演示列表，不是可交易的插件市场。
- `Services/Exchange/ExchangeProviderCatalog.cs` 已有“可发现、可安装、可扩展”的适配器骨架。
- `Services/Agent/SkillExecutionGuard.cs` 已有技能权限拦截雏形，但仅覆盖少量硬编码规则。
- `Services/Agent/BrainProviders.cs` 已有 Brain provider 抽象，但尚未平台化。
- `Core/Risk/RiskManager.cs` 和 `Core/Abstractions/ITradingStrategy.cs` 说明策略/风控已是核心运行面。

## 目标
把 WPE 变成可商业化插件平台，支持六类插件：
交易所适配器、数据源、策略、风险规则、通知、Brain provider。

## 产品原则
- 默认拒绝高危权限。
- 所有插件可签名、可审计、可回滚。
- 安装与升级必须是可控发布。
- 运行时隔离优先于开发便利。
- 市场页展示能力、权限、价格、兼容性和风险等级。

## 核心能力
- 插件发现、安装、启用、禁用、升级、回滚、卸载。
- 版本约束与运行时兼容检查。
- 交易、数据、通知、LLM/Brain 的权限分层。
- 审核流：自动扫描 + 人工复核 + 分级上架。
- 计费：免费、订阅、按调用、按节点数、企业授权。

## 非目标
- 不在首期允许任意本地代码无审计热加载。
- 不把插件市场和交易执行解绑到公共匿名执行环境。

## 成功指标
- 插件安装成功率。
- 插件崩溃率与回滚率。
- 审核通过时长。
- 付费转化率与留存。
- 交易相关插件的权限违规率为 0。

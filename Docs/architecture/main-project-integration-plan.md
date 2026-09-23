# 主项目集成实施计划

主线项目：`C:\Users\bz977\Documents\Codex\2026-07-18\new-chat\work\wpe-agent-clean\WPE-`

## 1. 集成目标

- 把 `Master Backlog`、`RuntimeSnapshotV1`、真实六角色本地 Agent、LocalOnly direct-decision 架构、动态市场选择器 handoff、插件 `Phase0 manifest`、品牌方案，统一纳入主项目主线。
- 保证本地 Agent 在无 LLM 时可完整运行。
- 保证 Brain 只是可选增强，不是单点依赖。

## 2. 核心原则

- `Local Only Trading`: 自动交易只依赖本地市场证据、direct decision、风控、持久化和执行/恢复链。
- `Remote Brain Retired`: 旧远程 Brain 配置只为兼容读取；不能进入自动交易决策、授权或执行。
- `Fail Closed`: 本地证据不完整、上下文不一致、风控/对账失败时不增加风险；不以远程模型或旧评分兜底。
- `Snapshot Driven`: UI 和编排只消费 `RuntimeSnapshotV1`，不直连服务内部状态。

## 3. 无 LLM 完整运行方案

### 本地最小闭环

- 数据采集: 本地行情/账户/订单缓存。
- 研究: 本地历史特征、回测与旁路审计；不生成交易评分权威。
- 决策: `DeterministicBrainProvider` 或同类本地 provider。
- 风控: 本地 `Risk Gate` 强制门禁。
- 执行: 仅在本地风控通过后下单。
- 记录: SQLite + 运行时快照 + 审计事件。

### 运行时降级顺序

1. 自动交易决策永远使用本地 deterministic provider，不接入远程大模型。
2. 本地决策不可用或证据不完整 -> fail closed，只允许 Hold/风险降低/恢复动作。
3. Research/Backtest 不可用 -> 不阻塞已有持仓的保护与恢复，但不得用旧评分或旧策略状态替代当前行情事实。
4. Exchange 不可用 -> 只读、回放、研究、等待，不增加风险。

### 必须保留的本地能力

- 直接 K 线/市场结构决策
- 风险门禁
- 仓位/杠杆计算
- 市场选择器
- 交易记录与恢复
- UI 状态展示

## 4. 本地决策单权威设计

- `AssistantProviderFactory` 的交易运行时只创建 `local-deterministic` provider。
- 旧 `Hybrid / AI Research / Remote Brain` 配置可以被读取用于兼容展示，但不能改变交易决策 provider。
- 决策链只有一个执行权威：fresh canonical market evidence -> direct local decision -> deterministic plan -> independent risk -> execution/recovery。
- 外部研究、回测和 Model-off Research/Strategy 输出只能作为旁路审计证据，不能覆盖、提升或批准订单。

## 5. 文件级迁移表

| 领域 | 新增目录/文件 | 扩展接口 | 修改 UI | 说明 |
|---|---|---|---|---|
| RuntimeSnapshotV1 | `Core/Runtime/RuntimeSnapshotV1.cs`，`Infrastructure/Runtime/RuntimeSnapshotMapper.cs` | `IAgentEventBus`、`AgentRuntimeEvent`、`WorkflowCheckpoint` | `components/runtime-bridge.tsx`、全站 runtime consumer | 统一状态模型，UI 只读快照 |
| Master Backlog | `Docs/Backlog/`，`Services/Agent/MasterBacklogStore.cs` | `IAgentMemoryStore` | `app/(dashboard)/agents/page.tsx` | 任务、优先级、状态、完成度；不再依赖已退役的策略研究 Agent/策略注册表页面 |
| 本地 Agent 角色/技能 | `Services/AgentRoleRuntimeRegistry.cs`、`Services/Agent/` | `IAssistantProvider`、`SkillExecutionGuard` | `app/(dashboard)/agents/page.tsx`、`plugins/page.tsx` | 仅暴露真实运行角色：market / decision / risk / execution / recovery / audit |
| Local-only Decision | `Services/Agent/RuntimeModePolicy.cs`、`Services/Agent/Agents/TechnicalDecisionAgent.cs`、`Services/Agent/Skills/TechnicalAnalysis/DirectMarketStructureDecisionSkill.cs` | `AssistantProviderFactory`、`DecisionIntelligence` | `app/(dashboard)/settings/page.tsx`、`monitoring/page.tsx` | 自动交易固定 LocalOnly；旧远程配置无执行权 |
| 动态市场选择器 handoff | `Services/Exchange/MarketSelector.cs`，`Services/Exchange/MarketHandoff.cs` | `IExchangeProvider`、`IMarketDataProvider` | `app/(dashboard)/backtest/page.tsx`、`orders/page.tsx` | 市场选择、切换、handoff |
| 插件 Phase0 manifest | `.codex-plugin/plugin.json`，`Plugins/`，`Marketplace/` | `IExchangeProviderPlugin` | `app/(dashboard)/plugins/page.tsx` | 插件注册、可见性、权限 |
| 品牌方案 | `WebUi/public/brand/`，`WebUi/app/globals.css`，`WebUi/components/brand/*` | 无 | 全站 shell、dashboard、settings | Logo、色板、命名、文案 |

## 6. 接口扩展建议

- `RuntimeSnapshotV1`: 保留 `mode`、`localOnly`、`fallbackReason`、`marketHandoffState` 等可观测状态；不再暴露远程 Brain provider chain 作为交易能力。
- `IAssistantProvider`: 自动交易只要求本地 deterministic 实现；不再设计远程 provider 优先级/回退链。
- `IExchangeProvider`: 增加 `SupportsHandoff`、`PreferredSymbols`、`CanServeAsPrimary`.
- `IAgentRuntimeSupervisor`: 增加 `RecoverFromBrainLossAsync`、`RecoverFromExchangeLossAsync`.

## 7. UI 页面修改范围

- `/` 仪表盘：显示 LocalOnly、direct decision、风险与运行时事实。
- `/agents`：展示真实六角色（market / decision / risk / execution / recovery / audit）、技能与当前工作流节点。
- `/backtest`：展示本地研究产物、市场选择结果、离线回放。
- `/orders`：展示 direct decision 上下文、handoff 来源与执行门禁。
- `/risk`：展示风控门禁、降级原因、拒单原因。
- `/plugins`：展示 manifest、插件状态、权限、可用性。
- `/settings`：展示模式切换、本地 only 开关、Brain 接入开关。

## 8. 测试策略

- 单元测试：本地 provider、快照映射、市场选择器、风控门禁、插件 manifest 解析。
- 集成测试：无 Brain、Brain 失败、Exchange 失败、Handoff 切换、快照回放。
- 端到端测试：本地 only 跑完整闭环；Hybrid 跑本地+远端；AI Research 只增强不阻断。
- 回归测试：现有安全门禁、SQLite、配置、权限验证。

## 9. 发布顺序

1. `RuntimeSnapshotV1` + 本地降级链
2. 多 Agent 角色/技能
3. 动态市场选择器 handoff
4. 插件 Phase0 manifest
5. UI 品牌重构
6. Hybrid/Brain 接入
7. AI Research 增强

## 10. 回滚点

- `R0`: 仅保留现有 runtime bridge 与静态 UI。
- `R1`: 回滚 `RuntimeSnapshotV1`，继续用旧 runtime 字段。
- `R2`: 回滚 Brain 接入，强制 `allowRemote=false`。
- `R3`: 回滚 market handoff，锁定单一主市场。
- `R4`: 回滚插件 manifest，保留内置注册表。

## 11. 每阶段验收命令

- 基线检查：`dotnet build "C:\Users\bz977\Documents\Codex\2026-07-18\new-chat\work\wpe-agent-clean\WPE-\币安量化机器人.csproj"`
- .NET 测试：`dotnet test "C:\Users\bz977\Documents\Codex\2026-07-18\new-chat\work\wpe-agent-clean\WPE-\WPE.Tests\WPE.Tests.csproj"`
- Web UI 类型检查：`pnpm --dir "C:\Users\bz977\Documents\Codex\2026-07-18\new-chat\work\wpe-agent-clean\WPE-\WebUi" typecheck`
- Web UI 构建：`pnpm --dir "C:\Users\bz977\Documents\Codex\2026-07-18\new-chat\work\wpe-agent-clean\WPE-\WebUi" build`
- Web UI lint：`pnpm --dir "C:\Users\bz977\Documents\Codex\2026-07-18\new-chat\work\wpe-agent-clean\WPE-\WebUi" lint`
- 本地 only 验收：禁用远端 Brain 后启动并检查 `/`、`/agents`、`/risk`、`/orders`、`/backtest`
- Hybrid 验收：开启 Brain 后检查 fallback 仍可执行

## 12. 建议落地顺序

- 先做 `RuntimeSnapshotV1` 和本地降级链。
- 再做市场 handoff 和插件 manifest。
- 最后做品牌和 Brain 增强。

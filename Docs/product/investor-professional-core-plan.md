# WPE 投资者与专业交易核心计划

> [!IMPORTANT]
> ## MO-01 当前权威覆盖
>
> 当前产品权威是私人使用、全自动、fail-closed 的 Testnet 闭环，且不设置逐周期人工 `approve`、`reject`、`revoke` 或 L2/L3 审批。Mainnet 保持 `disabled`。本文后续关于默认 `Review`、审批队列、人工审核、商业销售或公开分发的内容均为历史规划，仅为兼容追踪而保留，不得用于当前实现或验收。
>
> [`model-off-capability-maturity.json`](model-off-capability-maturity.json) 是当前 `model_off` 成熟度机器权威：13 个保留能力逐项核算并映射到 7 个运行时聚合。聚合不构成成熟度升级；核心能力为 `no` 或 `partial` 时必须同时为未实现、未验收。Teacher 是冻结的未来非核心能力，不得进入当前实现或验收。

## MO-01 权威模型与历史边界

- **当前目标：** 自动化 Testnet，在安全、能力、数据、配置或执行状态无法证明时自动拒绝、隔离或暂停，并记录事实审计；不把人工审批作为继续执行条件。
- **核算层级：** Orchestrator、Market Data、News、Macro、Technical、Fundamental、Strategy、Backtest、Risk、Execution、Position、Review/post-trade、Teacher 共 13 项；Market、Research、Strategy、Risk、Execution、Recovery、Audit 共 7 个聚合仅用于组织运行时拓扑。
- **有限接受：** Backtest、Risk、Execution 的 `yes` 仅分别代表基础回测、覆盖的加密 Risk Gate、执行合同边界；不接受完整 Agent 或端到端闭环。
- **冻结项：** Teacher 保留在清单中以防范围丢失，但状态固定为未来、非核心、未实现、未验收。Mainnet 固定禁用。
- **历史解释：** 下文维持原始商业计划和缺口记录。凡与本节冲突，以本节及机器权威为准；历史文本不得反向覆盖当前 authority。

## 文档目的

本文把产品愿景映射到当前源码事实，并冻结后续商业优先级。它服务于产品、工程、风控、发布和商业团队，不是功能完成声明，也不授权 Mainnet、实盘资金、对外投顾或客户消息自动发送。

事实口径分为四类：

- **已实现且验证**：当前源码存在确定性契约，并有本地自动化测试或已记录的发布门禁证据。
- **部分实现**：已有可复用能力，但产品闭环、UI、数据覆盖、真实环境认证或运营门禁尚未完成。
- **未实现 / Unknown**：源码没有足够证据，或当前状态无法证明。不得用演示数据、静态页面或计划文档替代事实。
- **外部依赖**：需要交易所、券商、数据商、平台审批、法律合规、签名基础设施或目标环境验证。

## 冻结产品原则

1. **交易闭环是核心产品。** 市场数据、研究、信号、策略、回测、风险、审批、执行、持仓、复盘和审计必须形成可追踪闭环。
2. **目标市场是加密资产与全球股票。** 当前工程事实以加密资产 Testnet 为主；全球股票仍是路线图，不是已交付能力。
3. **交易授权模式固定为 `Research / Signal / Review / Auto`，默认 `Review`。** 这与现有 `Local Only / Hybrid / AI Research` 智能能力模式是两个独立维度，禁止混用。
4. **Mainnet 继续禁用。** `Auto` 在实现后也只能先用于通过认证的 Testnet；任何 Mainnet 开放必须经过独立安全、法律、运营和发布授权。
5. **股票与券商先做 provider-neutral contract，再接具体券商。** 不允许复制加密交易所直连代码形成第二条执行链。
6. **风险与执行必须确定性、本地化、fail closed。** LLM 只可辅助研究、解释和草拟，不能批准风险、修改事实或直接执行。
7. **老师、课程、CRM、Telegram 和 WhatsApp 只能消费可追踪事实。** 所有输出必须引用研究、策略、风险、订单、成交或复盘 artifact，不得凭空生成市场结论。
8. **高风险通知必须分级并人工审核。** 交易信号、加仓、止损调整、自动执行结果和个性化客户内容不得与普通市场课程使用同一自动发布策略。
9. **本地秘密边界必须如实描述。** API 与通知凭据已有 Windows DPAPI CurrentUser 保护；SQLite 全库加密尚未实现，不能宣称“本地数据库已加密”或“数据库文件不可直接读取”。

## 两个独立模式轴

### 交易授权模式

| 模式 | 允许行为 | 禁止行为 | 当前状态 |
| --- | --- | --- | --- |
| Research | 读取事实、研究、回测、生成候选，不生成可提交订单 | 任何交易写操作 | **未形成统一产品契约**；已有研究与回测能力可复用 |
| Signal | 输出完整、可验证信号，由用户在系统外或手动下单 | 系统提交订单 | **未形成统一产品契约** |
| Review | 生成订单意图，经风险门禁后等待用户确认，再交给可靠执行器 | 未确认执行；绕过 Risk Gate | **目标默认模式，尚未端到端实现** |
| Auto | 经策略、数据质量、风险、能力和执行门禁后自动提交 | Mainnet；未知/过期/不支持能力下执行 | **现有自动 Testnet 引擎是基础，但尚未按四模式重构和商业认证** |

每次模式变化必须记录用户、设备、旧值、新值、时间、原因和授权证据。启动时缺失或损坏的模式配置必须回落到 `Review`，不能回落到 `Auto`。

### 智能能力模式

| 模式 | 作用 | 当前状态 |
| --- | --- | --- |
| Local Only | 规则、指标、本地 provider 和确定性模板完成核心工作 | **已实现且验证**；默认模式 |
| Hybrid | 本地为主，受预算、隐私和治理门禁约束地使用远端 Brain | **已实现且验证其治理/降级边界** |
| AI Research | 远端能力可增强探索与综合，本地风险和执行仍为权威 | **已实现基础模式；不拥有执行权限** |

任意智能能力模式都不能扩大交易授权。例如 `AI Research + Review` 仍需用户确认，`Local Only + Auto` 仍需全部确定性门禁。

## 当前事实地图

### 已实现且验证

| 能力 | 当前事实 | 证据边界 |
| --- | --- | --- |
| 本地优先与秘密主控 | WPF 拥有配置写入；Web UI 是宿主快照的只读投影；凭据使用 DPAPI CurrentUser | `Services/SecretVaultService.cs`、`AgentSettingsStore`、Setup bridge 测试 |
| RuntimeSnapshotV1 | 账户、持仓、订单、风险、市场、策略、回测、审计、记忆、通知等使用版本化 collection/value 状态；未知、过期和错误不伪造数据 | `Core/Contracts/RuntimeSnapshotV1.cs`、`Services/RuntimeSnapshotFactory.cs`、对应 Runtime 测试 |
| 加密市场 provider-neutral 基础 | Binance Futures、OKX、Bybit、Gate.io、Bitget 插件具有统一 provider、market catalog、环境和权限契约，并有离线 conformance 测试 | 这证明实现和 fail-closed 契约，不等于五家都完成真实 Testnet 交易认证 |
| 风险与可靠执行 | 风险增加必须经过数据质量、研究、组合风险、能力新鲜度和执行门禁；`ReliableOrderExecutor` 处理幂等、保护单和不确定订单恢复 | `EvidenceAndExecution.cs`、Risk/Execution/Safety 测试 |
| Binance Testnet 发布边界 | Binance Futures Testnet 已接入权威闭环 | 商业 Beta 仍要求每个候选版本在目标机进行有凭据 smoke test |
| 策略生命周期与本地研究 | Draft/Shadow/Active/Degraded/Retired 生命周期、本地策略研究、历史指标、Walk-Forward/Monte Carlo 基础和调度已存在 | 不代表每个策略已通过样本外、Paper 和小资金实盘认证 |
| 回测持久化与投影 | 回测结果写入 SQLite，并通过 RuntimeSnapshot 提供 available/stale/error/unsupported 真相 | 当前覆盖为本地策略研究结果，不是全球股票级回测数据平台 |
| 审计与运行恢复 | 决策、技能、执行和运行事件有 SQLite 记录及安全投影，工作流有检查点/恢复基础 | 审计不可篡改签名和长期合规归档仍未完成 |
| 本地记忆 | 工作、情景和长期记忆的确定性过滤、去重、隔离、过期和安全投影已有测试 | SQLite 文件本身未全库加密 |
| 通知后端 | Telegram/WhatsApp 配置加密、事实事件、outbox、重试、dead letter、限速、传输脱敏和运行时安全投影已有实现与测试 | 外部真实送达、模板审批、完整 UI 和高风险人工审核仍不是已验证交付 |
| LLM 治理 | Local Only 零远端调用、预算/隐私门禁、脱敏审计和本地降级已有测试 | LLM 输出始终只是建议 |

### 部分实现

| 能力 | 已有基础 | 主要缺口 |
| --- | --- | --- |
| 多交易所 Testnet | 五个加密 provider 的适配和 conformance 路径已存在 | 除 Binance 外未完成逐 provider 的凭据化订单全生命周期认证；Gate/Bitget 权限信息不足时保持只读 |
| 多品种与组合 | 加密符号、持仓、订单、组合 VaR/CVaR、集中度和相关性能力存在 | 统一多账户运营、跨交易所组合净额、币种和资产类别风险模型不完整 |
| 市场数据 Agent | 实时市场 hub、provider market catalog、历史 K 线、衍生品证据和 freshness/quality gate 可复用 | 数据血缘、完整性 SLA、全市场覆盖及授权数据源目录不完整 |
| 新闻 Agent | 新闻证据、来源、时间、可靠度、去重和 affected assets 模型存在 | 稳定外部源、交叉验证、授权、宏观事件日历和全球覆盖未认证 |
| 技术分析 Agent | 市场 regime、信号聚合、本地策略和部分技术/质量因子存在 | 尚未形成独立角色契约、完整多周期指标矩阵与可解释 artifact |
| 策略与持仓 Agent | 策略生成、研究门禁、持仓保护调整、reduce-only 和策略生命周期存在 | 四种交易授权模式、用户审批队列、跨资产持仓逻辑和完整复盘闭环未完成 |
| Review / 复盘 | 决策 reviewer、风险 review、执行与审计记录可复用 | 决策前人工审批与交易后归因应分为两个明确契约；情绪化操作等结论需要事实证据 |
| Web 专业工作台 | RuntimeSnapshot 驱动的仪表盘、订单、风险、回测、Agent、监控等页面存在 | 部分页面/集合仍需真实数据验收；配置写入只能走 allowlisted WPF bridge |
| 通知产品 | outbox 四态安全投影和后端传输基础存在 | 配置/审核 UI、渠道实测、客户/群组路由、内容风险分级、retention 和运营流程仍需完成 |
| 本地数据保护 | 秘密 DPAPI、敏感文本脱敏、安装目录只读、用户数据位于 LocalAppData | SQLite 全库加密、不同业务用户的强隔离、加密备份恢复、紧急擦除证据未实现 |

### 未实现 / Unknown

- 全球股票实时行情、历史行情、公司行动、交易日历和市场状态的生产数据接入。
- 美股、港股、欧洲和亚洲股票的 broker-neutral instrument/order/account contract 完整实现。
- 任何真实券商连接、股票 Paper/Review/Auto 执行或券商认证。
- 股票基本面、财报、估值、机构持仓、内部人交易和期权数据的授权数据管道。
- 加密链上、鲸鱼、稳定币供应、ETF 流、爆仓和交易所资金流的完整可信数据管道。
- 独立的宏观分析 Agent 和基本面研究 Agent 产品契约。
- 四种交易授权模式及默认 `Review` 的统一状态机、审批 UI 和审计闭环。
- 金融老师、早/中/晚课程生成、课程审核和课程事实引用界面。
- CRM、客户资料、客户分类、客户适当性、同意记录和个性化发送。
- SQLite/数据库文件全库加密、密钥轮换、加密备份恢复和多用户强隔离。
- Mainnet 交易、真实资金表现、股票或加密收益承诺。

### 外部依赖

- 交易所逐家 Testnet 账号、API 权限、官方 endpoint 和订单生命周期 smoke test。
- 股票行情/基本面/新闻/宏观/链上数据商的许可、质量 SLA、地域覆盖和成本合同。
- 券商 sandbox、认证、市场权限、订单类型、公司行动、税务和地域合规。
- Telegram Bot 权限和目标会话；WhatsApp Cloud API app、phone number、recipient 资格及已批准 template/language。
- 对外投资研究、信号、自动交易、客户沟通和数据保存的司法辖区法律审查。
- 商业代码签名证书、可信发布身份、安装器和目标 Windows 环境认证。

## 专业 Agent roster

所有角色输出统一事件头、`input_refs`、事实时间、来源、freshness、reason code 和 artifact ID。角色没有证据时必须输出 `unsupported / stale / error`，不能补写结论。

| 目标角色 | 职责 | 当前映射 | 状态 |
| --- | --- | --- | --- |
| Market Data | 行情、历史、账户、订单、衍生品与数据质量 | Provider catalog、RealTimeMarketHub、EvidenceCollector、HistoricalData | **部分实现**，加密市场为主 |
| News | 来源抓取、去重、可信度、时间和影响资产 | NewsResearch、NewsEvidence | **部分实现** |
| Macro | 宏观事件到资产影响的可验证逻辑链 | 可使用通用研究/新闻 artifact | **未形成独立角色** |
| Technical | 多周期结构、趋势、波动、量价与技术因子 | MarketStructureIntelligence、DirectMarketStructureDecisionSkill | **部分实现** |
| Fundamental | 股票财报/估值与加密项目/链上基本面 | 无足够生产证据 | **未实现 / Unknown** |
| Decision | 把合格行情事实直接组合成完整、可验证交易计划 | DirectMarketStructureDecisionSkill、DeterministicPlan、DecisionGovernance | **部分实现；旧 StrategyResearchAgent/评分晋升链已退役** |
| Backtest | 历史验证、成本、样本外、稳健性和复现 | RuntimeBacktest、历史行情持久化 | **已实现基础；仅验证/研究用途，不具订单授权权** |
| Risk | 独立阻断、限额、组合和异常门禁 | IndependentRisk、PortfolioRisk、Risk Gate | **已实现且验证** |
| Execution | 仅执行已批准且能力可用的订单 | ReliableOrderExecutor、provider adapters | **Testnet 基础已验证；商业认证部分实现** |
| Position | 跟踪持仓、保护、减仓和重新风险评估 | PositionManagement、保护单审计 | **有限验收**：本地确定性数量、保护与外部仓位隔离门禁已通过；尚无实盘 Testnet provider 认证 |
| Review | 交易前审批；交易后事实归因和改进 | DecisionReviewer、audit、memory | **部分实现，需拆分 pre-trade / post-trade** |
| Teacher | 只把可追踪交易事实解释成课程和客户内容 | 无独立实现 | **未实现** |

Teacher 不能成为新的研究真源。任何课程或客户答复必须引用 Market Data、News/Macro、Decision、Risk、Execution、Position 或 Review artifact；来源过期、冲突或缺失时只允许说明未知。

## 通知和内容风险分级

| 等级 | 示例 | 默认策略 |
| --- | --- | --- |
| L0 信息 | 系统健康、课程已生成、非个性化教育内容 | 可按用户预设自动发送，保留事实引用和发送记录 |
| L1 市场观察 | 新闻摘要、市场变化、非执行信号 | 可自动或批量审核；必须标注时间、来源和不是订单 |
| L2 交易建议 | 入场区、仓位、止盈止损、策略变更 | 必须人工审核；禁止默认自动发送 |
| L3 高风险动作 | 加仓、止损调整、自动交易、紧急退出、个性化客户指令 | 强制二次确认、适当性/权限检查、不可批量静默发送 |

通知只消费确认事实。发送失败不能改变交易结果；重试、dead letter、人工重放和 retention 必须可审计。

## 路线图与依赖顺序

### P0：加密 Testnet 的默认 Review 专业闭环

依赖顺序：

1. 冻结交易授权模式契约，默认 `Review`，与智能能力模式解耦。
2. 冻结 provider-neutral 事实、信号、订单意图、审批、风险、执行和复盘 artifact。
3. 确认所有风险增加路径只能经过 Risk Gate 和 ReliableOrderExecutor。
4. 完成 Review 审批队列、WPF 二次确认、快照投影和审计回执。
5. 完成真实订单/持仓/回测/审计/通知 UI 的 stale/error/unsupported 验收。
6. 完成 Binance Testnet 发布 smoke gate；其他 provider 逐家认证，不整体打包宣称。
7. 完成高风险通知分级、人工审核和安全发送记录。

P0 验收标准：

- 新安装、缺失配置和损坏配置均进入 `Review`，Mainnet 始终不可选。
- Review 模式下未经用户确认不产生 provider mutation；确认记录与最终交易结果可关联。
- 未知、过期、不支持、错误的市场/能力/权限均在交易所调用前阻断。
- 从市场事实到研究、策略、风险、审批、执行、持仓和复盘具有同一 trace/correlation 链。
- Local Only 可完成核心闭环；远端 Brain 不可用不会绕过或阻断确定性安全路径。
- 发布报告只声明已通过的 provider 和环境；真实 Testnet smoke 由目标机外部门禁完成。

### P1：全球股票基础与专业研究扩展

依赖顺序：

1. 定义 provider-neutral `AssetClass / Venue / Instrument / Session / CorporateAction / Account / Position / Order / Fill` 合约。
2. 扩展风险模型处理交易时段、币种、行业、公司行动、做空和跨资产集中度。
3. 先接授权的股票只读/延迟数据与 broker sandbox，再做 Paper，最后才评估 Review 执行。
4. 落地 Macro、Technical、Fundamental 独立 artifact 和数据血缘。
5. 扩展回测到交易日历、复权、公司行动、手续费、滑点和样本外验证。
6. 实现 SQLite 全库加密、密钥生命周期和加密备份恢复后，才扩大敏感历史数据范围。

P1 验收标准：

- 同一核心闭环可处理 crypto 与 equity，但 provider 细节不进入领域/风险/审批契约。
- 股票数据具备许可、来源、时区、交易日历、freshness 和公司行动语义。
- 第一个券商只通过 sandbox/Paper/Review 认证；未认证订单类型显示 unsupported。
- 回测结果可在相同数据版本和成本模型上复现，不因缺失数据生成指标。
- 数据库静态文件的加密声明必须有独立测试证据；在此之前只声明“秘密已 DPAPI 保护”。

### P2：老师、课程、CRM 和受控分发

依赖顺序：

1. 建立事实引用图和内容 artifact schema。
2. 实现 Teacher 的只读生成与人工审核，不授予研究、风险或执行写权限。
3. 实现早/中/晚课程模板、时区/市场日历、多语言和版本记录。
4. 在法律与数据保护评审后实现 CRM、客户同意/分类/适当性和保留策略。
5. 将 Telegram/WhatsApp 路由到已批准内容，按 L0-L3 风险分级发送。

P2 验收标准：

- 每段市场判断和每个交易数字都能回到未过期 artifact；无引用内容不得发布。
- 高风险内容必须人工审核和二次确认，发送人、收件范围、版本和结果可追踪。
- CRM 数据在强隔离、全库加密、备份恢复、删除/保留政策完成前不得进入生产。
- 渠道失败只影响分发，不改变策略、风险、订单或持仓事实。

## 商业化门禁

### Crypto Testnet Beta

- P0 验收全部通过。
- Windows 目标环境、发布包、签名/完整性和回滚流程通过。
- Binance Futures Testnet 在目标机完成有凭据 smoke test。
- 其他交易所按实际认证结果逐家列出，不使用“全面支持多交易所交易”。
- 明确声明 Testnet、非收益承诺、Mainnet disabled。

### Professional / Pro

- Review 默认与审批审计达到发布 SLA。
- 历史数据、回测复现、策略生命周期、持仓和复盘覆盖达到产品指标。
- SQLite 全库加密若未完成，销售与 UI 必须继续披露其未实现。
- 支持和事故响应、备份恢复、数据保留、遥测隐私有明确责任人。

### Global Equities Pilot

- provider-neutral contract 与股票风险/回测语义冻结。
- 数据授权和第一个 broker sandbox/Paper 认证完成。
- 不得以 crypto Testnet 成功替代股票执行认证。
- 每个市场的交易时段、公司行动、币种、税务和监管边界通过评审。

### Teacher / CRM / Messaging

- P2 事实引用、人工审核、内容留痕、客户同意和数据保护门禁通过。
- WhatsApp 模板、Telegram 权限及真实送达只能作为外部验证结果声明。
- 不把教育内容包装成未经审查的个性化投资建议。

### Mainnet

Mainnet 不属于当前 P0/P1/P2 的默认授权结果。未来只有在独立威胁模型、资金限额、双人审批、凭据最小权限、真实故障演练、法律审查和明确产品授权全部通过后，才可提出单独启用计划。

## 不做 / 不虚报

- 不做无 Risk Gate、无审计或默认 Auto 的交易机器人。
- 不让 Web UI、LLM、Teacher、CRM 或通知渠道持有执行权威。
- 不把静态 UI、preview、fixture、mock 或 catalog entry 宣称为实时数据或交易支持。
- 不把五个 provider 的离线 conformance 测试宣称为五家交易所都已完成真实 Testnet 或 Mainnet 认证。
- 不宣称股票实时、券商、公司基本面、链上、ETF 流、客户管理或课程系统已经完成。
- 不把 `Local Only / Hybrid / AI Research` 当成 `Research / Signal / Review / Auto`。
- 不把 DPAPI 凭据保护宣称为 SQLite 全库加密、数据库不可读或多用户强隔离。
- 不生成不存在的行情、持仓、订单、回测、绩效、客户或渠道送达记录。
- 不承诺收益、胜率、回撤或策略表现；指标只能来自可复现的特定数据和版本。
- 不在未授权时启用 Mainnet，不以“用户自行确认”替代产品和合规门禁。

## 事实证据入口

- 商业 Beta 边界：`Docs/product/beta-release-readiness.md`
- 商业评估：`Docs/product/wpe-commercial-assessment.md`
- 本地优先架构：`Docs/architecture/wpe_multi_agent_local_first_design.md`
- LLM 可选 PRD：`Docs/architecture/prd-local-first-llm-optional.md`
- 运行时契约：`Core/Contracts/RuntimeSnapshotV1.cs`
- 权威闭环：`Services/AutoTradingAgent.cs`
- 风险与执行：`Services/Agent/EvidenceAndExecution.cs`
- provider 目录与契约：`Services/Exchange/ExchangeProviderCatalog.cs`、`ExchangeContracts.cs`
- 决策、回测、审计与记忆：`Services/Agent/DirectMarketStructureDecisionSkill.cs`、`Services/Agent/AgentSqliteStore.cs`、`Services/Runtime*StateStore.cs`
- 通知：`Services/Notifications/`
- 自动化证据：`WPE.Tests/`

源码和测试发生变化时，先更新事实状态，再调整商业声明。路线图目标不能反向覆盖运行时真相。

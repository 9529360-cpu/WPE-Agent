# WPE Agent 品牌命名与 Logo 决策简报

> 状态：P2 Epic 8 决策研究稿  
> 初筛日期：2026-07-21（Asia/Tokyo）  
> 边界：公开网络、DNS、HTTP、RDAP 和公开代码仓库信号；未购买域名、未提交商标、未联系外部人员、未生成 Logo 图形。

## 执行结论

- **条件性推荐主名：`WPE Agent`**。在三个候选中，它最能表达“本地 Agent 为主体、LLM 可选”的产品事实，也比 `WPE Trading` 和 `WPE AI` 更有区分度。
- **不推荐 `WPE Trading`**。`Trading` 描述性太强，而且公开存在同样使用 `WPE` 的 World Property Exchange，其定位直接涉及数字交易、投资和 AI，商业混淆风险最高。
- **不推荐 `WPE AI`**。`AI` 描述性、拥挤且会弱化“无需 LLM 也可运行”的核心差异，容易造成产品能力误解。
- **`WPE Agent` 仍只能作为工作名，不能视为已完成商标清查。** `WPE` 在金融交易领域已有明显近邻使用，进入付费发布、签约、应用商店或域名采购前，应由目标法域专业人士检索文字商标、图形商标、公司名和未注册在先使用。
- **推荐 Logo 方向：`Guarded W Circuit`**，以 `W Monogram Circuit` 为主、吸收 `Guard Loop` 的单一闭环边界；必须在 16px 单色版本中删去所有非必要节点。这样兼顾品牌记忆和企业风控表达，避免使用通用盾牌模板。

## 1. 名称公开冲突初筛

### 1.1 可核查发现

| 候选 | 同名/近似公开信号 | 风险判断 | 产品判断 |
|---|---|---:|---|
| `WPE Agent` | 本次精确短语公开搜索和 GitHub 仓库搜索未发现明显同类产品；但基础词 `WPE` 已被金融交易近邻使用 | **中高** | 三者中最合适，但必须附带完整副标题并做法律清查 |
| `WPE Trading` | [World Property Exchange](https://www.wpe.com/) 使用 `WPE`，公开描述为数字房地产代币交易所；[World Property Journal](https://www.worldpropertyjournal.com/real-estate-news/united-states/miami-real-estate-news/real-estate-news-world-property-exchange-wpe-world-property-bank-world-property-ventures-real-estate-token-exchange-transactioncoin-real-estate-stable-14634.php) 也将其描述为 AI 驱动、拟受监管的数字交易平台 | **高** | `Trading` 过于描述性，且把双方拉到同一交易语境；淘汰 |
| `WPE AI` | 精确短语未发现明显同类交易产品，但 `AI` 是高度描述性后缀；GitHub 有 `WPeGPT` 等近似技术命名，说明 WPE/WPe 在 AI 软件语境并不独占 | **中高** | 与 Local Only/LLM 可选事实冲突；淘汰 |

补充噪声：`WPE` 还用于 [WPE WebKit](https://wpewebkit.org/) 等软件项目；`WP Engine` 也在软件服务领域形成稳定近似读写印象。这些并非本次发现的直接金融同类冲突，但说明三个字母本身较弱、难以独占，不能只依赖 `WPE` 字标建立识别。

公开代码仓库的精确短语搜索在检查时返回：[`WPE Agent` 0 个仓库](https://github.com/search?q=%22WPE+Agent%22&type=repositories)、[`WPE Trading` 0 个仓库](https://github.com/search?q=%22WPE+Trading%22&type=repositories)。这只能说明该数据源当时未命中，**不能**证明名称可用或不存在未公开/未被索引的在先使用。

### 1.2 商标初筛边界

本报告不是法律意见，也不是完整商标检索。公开网页、搜索引擎和 GitHub 不能覆盖各法域的文字/图形商标、申请中标志、驳回记录、公司名、域名争议和未注册在先使用。

正式决定前至少应在以下官方入口，由专业人士按精确词、近似拼写、读音、图形要素和相关类别复核：

- [USPTO Trademark Search](https://www.uspto.gov/trademarks/search)
- [WIPO Global Brand Database](https://branddb.wipo.int/)
- 目标市场对应的国家/地区商标数据库
- 重点评估软件/SaaS、金融与交易服务、教育/研究工具等可能涉及的类别；类别号和商品服务描述应由商标专业人士确认，本报告不代填。

## 2. 域名信号

| 域名 | DNS/HTTP（检查时） | RDAP（检查时） | 当前解释 |
|---|---|---|---|
| `wpeagent.com` | 无 A/AAAA 解析，HTTPS 无法解析 | [Verisign RDAP](https://rdap.verisign.com/com/v1/domain/wpeagent.com) 返回 404 | 未发现公开登记信号；需在购买时由注册商实时确认 |
| `wpeagent.ai` | 无 A/AAAA 解析，HTTPS 无法解析 | [Identity Digital RDAP](https://rdap.identitydigital.services/rdap/domain/wpeagent.ai) 返回 404 | 未发现公开登记信号；`.ai` 价格、续费和溢价需另查 |
| `wpe-trading.com` | 无 A/AAAA 解析，HTTPS 无法解析 | [Verisign RDAP](https://rdap.verisign.com/com/v1/domain/wpe-trading.com) 返回 404 | 技术上未发现登记信号，但名称本身不推荐 |
| `wpe.ai` | 有 A 记录，HTTPS 返回 200 | [Identity Digital RDAP](https://rdap.identitydigital.services/rdap/domain/wpe.ai) 返回已注册，注册于 2022-11-01；名称服务器指向 Afternic | 已注册/可能停放，不视为可用资产 |

注意：DNS 无记录、HTTP 不通或 RDAP 404 都不等于“保证可注册”。注册局同步延迟、保留名、premium 名、经纪挂牌和注册商策略都会影响最终结果。本批次没有尝试购买或锁定任何域名。

如果保留工作名，域名优先顺序为：`wpeagent.com` → `wpeagent.ai`。`wpe-trading.com` 即使可注册也不建议购买作为主域名，避免固化高风险命名。

## 3. Logo 概念评分

评分：1（弱）到 5（强）。总分只用于比较，最终仍需用真实 16px 栅格、黑白打印和相似图形检索验证。

| 概念 | 16px | 桌面应用 | 企业/合规 | 品牌记忆 | 避免币圈刻板印象 | 总分 | 主要问题 |
|---|---:|---:|---:|---:|---:|---:|---|
| `Guard Loop` | 5 | 4 | 5 | 3 | 5 | **22/25** | 稳健但偏通用，容易像安全、监控或身份产品 |
| `Node Shield` | 2 | 4 | 4 | 2 | 3 | **15/25** | 16px 节点易糊；盾牌+节点已是常见网络安全/区块链模板 |
| `W Monogram Circuit` | 5 | 5 | 3 | 5 | 4 | **22/25** | 记忆强，但若电路线过多会变成泛科技/芯片 Logo |

### 推荐组合：`Guarded W Circuit`

- 以一个独特、可注册性更有希望的 `W` 几何骨架作为主体。
- 只保留一条闭合或近闭合边界表达 Risk Gate/审计闭环；不要叠加完整盾牌。
- 16px 版本：纯单色、最多 2 个负空间、无小节点、无细于 1px 的独立线。
- 桌面/官网版本可以增加一个“受控路径终点”，但不得出现行情 K 线、币、火箭、机器人脸或神经网络脑形。
- 在图形商标检索完成前，这只是设计方向，不是最终 Logo。

## 4. 推荐品牌系统

### 名称与文案

- **英文主名：** `WPE Agent`（条件性工作名）
- **英文副标题：** `Local-first trading agents with enforced risk and audit.`
- **中文主名：** `WPE 本地交易智能体`
- **中文定位句：** `本地优先、风控强制、全程可审计；LLM 可选。`
- **避免使用：** “AI 自动赚钱”“无人值守稳赚”“自主绕过风控”“全交易所已支持”等无法验证或合规风险高的表述。

### 颜色

- **主背景：** `Graphite #111418`
- **主文字：** `Mist #E8ECEF`
- **品牌强调：** `Signal Teal #18A999`，用于品牌和正常运行状态，不用于盈亏语义。
- **警示：** `Amber #D99A2B`；**危险/拒绝：** `Red #D04A4A`。
- 盈亏红绿与品牌色分离，且必须同时使用符号/文字，不能只靠颜色传达。
- 所有正文和关键控件以 WCAG 对比度实测为准；本简报中的色值不是免测批准。

### 字标与字体

- 英文字标使用中性、开放字形的无衬线体，`WPE` 与 `Agent` 同一基线，不把 `AI` 高亮。
- 中文使用中性无衬线字体，与产品现有命令中心 UI 保持一致。
- 不使用斜体速度线、切角电竞字体、过窄字体或仿交易所字标。
- 图形标与字标必须可分离；16px/favicon 只用图形标，窗口标题与安装器使用完整字标。

### 禁用清单

- 禁止比特币符号、通用硬币、火箭、牛熊、K 线、机器人头像、脑形神经网络。
- 禁止霓虹紫蓝渐变、发光描边、3D 金属币、复杂粒子和赛博博彩视觉。
- 禁止盾牌内塞 3 个以上节点；禁止在 16px 图标中保留装饰性电路线。
- 禁止 Logo 使用盈亏红绿作为唯一识别色。
- 禁止未完成法律清查前使用 `®`；`™` 的使用也应由目标市场法律顾问确认。

## 5. 发布前门禁

1. 明确首发法域和真实商品/服务范围。
2. 对 `WPE Agent`、`WPE`、近似读音/拼写及 `Guarded W Circuit` 图形执行专业清查。
3. 在同一天通过注册商复核域名价格和可注册状态；先完成商标风险判断，再购买高价域名。
4. 设计三套最小验证稿：16px 单色、桌面 256px、企业文档黑白；做同类 Logo 相似性检索。
5. 用户书面确认名称和 Logo 方向后，才进入图形生成、UI/安装器资产替换和品牌发布。

## 6. 需要用户最终决定（最多 2 项）

1. **命名路线：** 接受 `WPE Agent` 作为条件性工作名并先做专业商标清查，还是因为 `WPE.com / World Property Exchange` 的金融近邻风险，立即启动一轮“非 WPE”的独特主品牌命名？**推荐后者**，`WPE Agent` 可保留为内部项目代号。
2. **Logo 路线：** 采用推荐的 `Guarded W Circuit`（品牌记忆优先、含轻量风控边界），还是采用纯 `Guard Loop`（企业合规优先、但区分度较弱）？**推荐 `Guarded W Circuit`**。

## 7. 证据与不确定性摘要

- 直接产品证据：[WPE.com / World Property Exchange](https://www.wpe.com/)、[World Property Journal 对 WPE 的公开报道](https://www.worldpropertyjournal.com/real-estate-news/united-states/miami-real-estate-news/real-estate-news-world-property-exchange-wpe-world-property-bank-world-property-ventures-real-estate-token-exchange-transactioncoin-real-estate-stable-14634.php)。
- 软件语境噪声：[WPE WebKit](https://wpewebkit.org/)、[WP Engine](https://wpengine.com/)。
- 代码仓库信号：[GitHub `WPE Agent` 搜索](https://github.com/search?q=%22WPE+Agent%22&type=repositories)、[GitHub `WPE Trading` 搜索](https://github.com/search?q=%22WPE+Trading%22&type=repositories)、[GitHub `WPE AI` 搜索](https://github.com/search?q=%22WPE+AI%22&type=repositories)。
- 官方商标入口：[USPTO](https://www.uspto.gov/trademarks/search)、[WIPO](https://branddb.wipo.int/)。本批次没有把网页初筛冒充官方法律检索结果。
- 域名证据来自上述 RDAP 链接，以及检查时的本机 DNS/HTTPS 探测。状态随时可能变化。
- 最大不确定性：目标国家/地区、商品服务描述、未注册在先使用、图形近似、域名实时价格均未确定，因此不能给出“可注册/无冲突”的结论。

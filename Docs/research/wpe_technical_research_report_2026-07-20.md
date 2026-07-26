# WPE 外部技术调研报告

截至 2026-07-20，基于 GitHub 官方仓库 / GitHub Docs / 官方文档整理。

## 1. 本地优先且 LLM 可选的多 Agent 框架

### 1) LangGraph
- 仓库：<https://github.com/langchain-ai/langgraph>
- 许可证：MIT
- 活跃度/星数：37.7k stars，2026-07-20 有提交，最新 release `1.2.9`
- 核心机制：有状态图编排，支持 `durable execution`、human-in-the-loop、长任务恢复
- WPE 借鉴点：把交易/风控/补单/复核拆成状态节点，天然适合“中断后续跑”
- 风险：抽象偏通用，落地交易域需自定义状态与审计模型
- 优先级：P1

### 2) smolagents
- 仓库：<https://github.com/huggingface/smolagents>
- 许可证：Apache-2.0
- 活跃度/星数：28.5k stars，2026-07-14 有提交，最新 release `v1.26.0`
- 核心机制：轻量代码型 Agent，支持本地 `transformers` / `ollama` / LiteLLM
- WPE 借鉴点：适合本地优先、低依赖、可插拔模型与工具执行
- 风险：框架轻，生产级编排/恢复能力需要外层补齐
- 优先级：P1

### 3) CrewAI
- 仓库：<https://github.com/crewaiinc/crewai>
- 许可证：MIT
- 活跃度/星数：55.8k stars，2026-07-20 有提交，最新 release `1.15.5`
- 核心机制：角色分工、任务流、工具/MCP、memory、checkpointing
- WPE 借鉴点：适合“研究员/交易员/风控/审计”角色化协作
- 风险：角色编排灵活但复杂，需强约束输出格式
- 优先级：P2

### 4) AutoGen
- 仓库：<https://github.com/microsoft/autogen>
- 许可证：CC-BY-4.0
- 活跃度/星数：59.8k stars，2026-04-15 有提交，最新 release `python-v0.7.5`
- 核心机制：多 Agent 协作、handoff、工具调用生态成熟
- WPE 借鉴点：适合复杂协作链路与异构 LLM 组合
- 风险：仓库已进入 maintenance mode，长期建议看其后继框架
- 优先级：P2

### 5) OpenAI Agents SDK（参考项）
- 仓库：<https://github.com/openai/openai-agents-python>
- 许可证：MIT
- 活跃度/星数：28.0k stars，2026-07-20 有提交，最新 release `v0.18.3`
- 核心机制：Agents / handoffs / tools / guardrails / sessions / tracing / sandbox agents
- WPE 借鉴点：权限、会话、追踪、沙箱这套工程化边界做得很完整
- 风险：对 OpenAI 生态依赖更强，不是最“本地优先”
- 优先级：P2

## 2. Agent skill / 插件注册 / 权限沙箱

### 6) GitHub Copilot Agent Skills / Sandboxes（官方文档）
- 文档：<https://docs.github.com/copilot/concepts/agents/about-agent-skills>  
  <https://docs.github.com/copilot/how-tos/cloud-and-local-sandboxes>  
  <https://docs.github.com/en/copilot/concepts/agents/about-cloud-agent>
- 许可证：GitHub Docs 开源
- 活跃度/星数：不适用
- 核心机制：skills 作为可发现的 instructions/scripts/resources；MCP 扩展工具；云/本地沙箱与企业级 policy
- WPE 借鉴点：用 manifest + policy 控制“能做什么、能访问什么、如何安装”
- 风险：这是平台能力，不是可直接嵌入的库；要抽象出自有注册中心
- 优先级：P1

## 3. 多交易所 / 多品种统一适配

### 7) CCXT
- 仓库：<https://github.com/ccxt/ccxt>
- 许可证：MIT
- 活跃度/星数：43.4k stars，2026-07-20 有提交，最新 release `v4.5.67`
- 核心机制：统一交易 API，覆盖 100+ 交易所/预测市场，REST + WebSocket，支持跨所标准化
- WPE 借鉴点：最适合做“交易所适配层”的标准答案
- 风险：统一接口会丢失部分交易所特色能力，需保留 escape hatch
- 优先级：P1

### 8) NautilusTrader
- 仓库：<https://github.com/nautechsystems/nautilus_trader>
- 许可证：LGPL-3.0
- 活跃度/星数：24.8k stars，2026-07-20 有提交，最新 release `v1.230.0`
- 核心机制：Rust 核心 + Python 控制面，多资产/多 venue，research-to-live 同语义
- WPE 借鉴点：统一研究、仿真、实盘语义；适合做交易主干
- 风险：Rust/Python 双栈复杂，LGPL 对闭源集成要先审法务
- 优先级：P1

### 9) Lumibot
- 仓库：<https://github.com/Lumiwealth/lumibot>
- 许可证：GPL-3.0
- 活跃度/星数：1.8k stars，2026-07-16 有提交，最新 release `v4.5.78`
- 核心机制：同一策略在 backtest / paper / live broker 跑通
- WPE 借鉴点：适合参考“策略一份代码多环境执行”的开发体验
- 风险：GPL 约束强，商业化复用要谨慎
- 优先级：P3

### 10) hftbacktest
- 仓库：<https://github.com/nkaz001/hftbacktest>
- 许可证：MIT
- 活跃度/星数：4.3k stars，2025-12-23 有提交，最新 release `rust-v0.9.4`
- 核心机制：高频订单簿回测、撮合与盘口重放
- WPE 借鉴点：适合做高频策略验证与成交路径压力测试
- 风险：更偏研究/回测，不是完整交易执行框架
- 优先级：P2

### 11) Quod Financial / QuantReplay
- 仓库：<https://github.com/Quod-Financial/quantreplay>
- 许可证：Apache-2.0
- 活跃度/星数：38 stars，2026-07-08 有提交，最新 release `v11`
- 核心机制：面向交易回放与模拟的轻量框架
- WPE 借鉴点：可参考“交易事件回放 + 审计可追溯”
- 风险：社区规模小，生态和长期维护不如前几项
- 优先级：P3

## 4. token / prompt cache / memory / RAG 成本治理

### 12) LiteLLM
- 仓库：<https://github.com/BerriAI/litellm>
- 许可证：NOASSERTION
- 活跃度/星数：54.1k stars，2026-07-20 有提交，最新 release `v1.93.0`
- 核心机制：统一 LLM gateway，cost tracking、spend management、caching、fallback、load balancing
- WPE 借鉴点：适合作为模型路由与成本治理中枢
- 风险：许可证需单独核实；功能面很宽，边界容易膨胀
- 优先级：P1

### 13) Mem0
- 仓库：<https://github.com/mem0ai/mem0>
- 许可证：Apache-2.0
- 活跃度/星数：61.3k stars，2026-07-20 有提交，最新 release `cli-node-v0.2.11`
- 核心机制：通用 memory layer，支持 token-efficient memory、实体链接、多信号检索
- WPE 借鉴点：把“长期记忆”与“查询成本”分层治理
- 风险：记忆抽取质量直接影响召回与成本，需要评测闭环
- 优先级：P1

### 14) GPTCache
- 仓库：<https://github.com/zilliztech/gptcache>
- 许可证：MIT
- 活跃度/星数：8.1k stars，2025-07-11 有提交，最新 release `0.1.44`
- 核心机制：语义缓存，量化 hit ratio / latency / recall
- WPE 借鉴点：适合做 prompt cache 和重复问答降本
- 风险：项目偏早期，活跃度较前几项弱
- 优先级：P2

### 15) LightRAG
- 仓库：<https://github.com/hkuds/lightrag>
- 许可证：MIT
- 活跃度/星数：37.9k stars，2026-07-20 有提交，最新 release `v1.5.5rc1`
- 核心机制：图 + 向量双层 RAG，支持增量更新，强调低成本高质量
- WPE 借鉴点：适合知识库、策略文档、规则手册的低成本检索层
- 风险：图索引与数据治理复杂度高
- 优先级：P1

## 5. 交易恢复 / 订单审计

### 优先结论
- 首选主干：NautilusTrader
- 统一接入：CCXT
- 回放/审计：QuantReplay + hftbacktest
- 恢复与追踪：参考 OpenAI Agents SDK 的 sessions/tracing、GitHub 的 sandbox/policy 模式

## 建议落地顺序
1. CCXT + NautilusTrader
2. LiteLLM + Mem0 + GPTCache
3. GitHub skills/sandbox 体系化抽象
4. LangGraph 或 CrewAI 做上层编排
5. hftbacktest / QuantReplay 补回放与审计

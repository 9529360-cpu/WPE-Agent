# WPE 商业化许可证 / 依赖风险矩阵

基于已调研仓库，面向“可商业化引入”的判断。结论默认按闭源 WPE 主项目评估。

## 分类规则
- `可直接引入`：MIT / Apache-2.0，且无明显强传染依赖。
- `仅设计借鉴`：更适合参考架构、流程、接口，不建议复制代码。
- `需要法务审查`：LGPL、CC-BY、NOASSERTION，或依赖链复杂、授权边界不清。
- `禁止引入`：GPL-3.0 及其对闭源产品的直接代码复用。

## 矩阵

| 仓库 | 许可证 | 分类 | 主要原因 | 传递性风险 |
|---|---|---:|---|---|
| LangGraph | MIT | 可直接引入 | 宽松许可，适合框架级集成 | 需检查下游 LLM / tracing / graph 相关依赖 |
| smolagents | Apache-2.0 | 可直接引入 | 宽松许可，便于本地优先 Agent 集成 | 关注模型适配器与工具执行依赖 |
| CrewAI | MIT | 可直接引入 | 宽松许可，适合角色化编排 | MCP / memory / checkpoint 组件需逐项审计 |
| AutoGen | CC-BY-4.0 | 需要法务审查 | 许可证不适合直接按常见代码许可理解 | 需核实代码、文档、示例的授权边界 |
| OpenAI Agents SDK | MIT | 可直接引入 | 宽松许可，工程化能力强 | OpenAI 生态与云能力依赖要单独评估 |
| CCXT | MIT | 可直接引入 | 统一交易所适配层非常成熟 | 各交易所 API 条款、地域限制、WebSocket 依赖 |
| NautilusTrader | LGPL-3.0 | 需要法务审查 | LGPL 对分发/链接方式有约束 | Rust/Python 组合及静态/动态链接方式要确认 |
| Lumibot | GPL-3.0 | 禁止引入 | 闭源商业化场景下传染风险高 | 任何派生/链接都可能把约束扩散到主项目 |
| hftbacktest | MIT | 可直接引入 | 许可宽松，适合回测组件 | 注意 Rust 依赖与性能库授权 |
| QuantReplay | Apache-2.0 | 可直接引入 | 宽松许可，适合回放/审计参考 | 关注实验性依赖与数据格式库 |
| Mem0 | Apache-2.0 | 可直接引入 | 宽松许可，适合 memory 层 | 检索/向量库/数据库后端需逐项审计 |
| LiteLLM | NOASSERTION | 需要法务审查 | 仓库许可证声明不清晰 | 依赖树很深，需 SBOM + 逐级 license 扫描 |
| GPTCache | MIT | 可直接引入 | 宽松许可，可做语义缓存 | 关注向量库、ANN、序列化依赖 |
| LightRAG | MIT | 可直接引入 | 宽松许可，适合 RAG 层 | 图数据库/向量库/嵌入模型依赖需审计 |
| OpenHands | NOASSERTION | 需要法务审查 | 仓库级许可不明确 | 依赖链长，且通常包含大量 agent/tooling 组件 |

## WPE 依赖治理建议
- 先做 `SBOM` 和 `license scan`，再允许依赖进入主干。
- 把 `GPL / LGPL / CC-BY / NOASSERTION` 单独放入“审查白名单”，默认不进生产。
- 对 `MIT / Apache` 也要做传递依赖扫描，避免被上游二次带入强传染许可。
- 对交易执行、审计、恢复链路，优先选择 `MIT / Apache` 组件并保留替换层。

## 推荐决策
- 优先可引入：`LangGraph`、`smolagents`、`CrewAI`、`CCXT`、`Mem0`、`LightRAG`、`GPTCache`、`hftbacktest`、`QuantReplay`、`OpenAI Agents SDK`
- 先法务审查：`NautilusTrader`、`LiteLLM`、`AutoGen`、`OpenHands`
- 禁止直接引入：`Lumibot`

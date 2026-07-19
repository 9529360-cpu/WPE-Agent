# LLM 调用链分析

## 当前调用链

远程模型不是交易控制器。交易运行时固定使用 `DeterministicBrainProvider`，其后仍需经过本地 Planner、Critic、Reviewer、Risk Manager 和 Execution Manager。

可选远程调用链为：

`UI/辅助任务 -> HttpBrainProvider -> LlmRequestGovernor -> Provider HTTP API`

任何远程请求必须经过 `LlmRequestGovernor`，不能直接从 Workflow、Memory、Scheduler、Event 或交易所适配器发出。

## 审计结论

- 当前 `AutoTradingAgent` 创建的是 `DeterministicBrainProvider`，交易周期不调用远程模型。
- `HttpBrainProvider.DecideAsync` 过去可构造包含完整市场、新闻和上下文的大 Prompt；若被旧交易周期调用，会每周期消耗一次大请求。
- `HttpBrainProvider.HealthCheckAsync` 过去会发送真实请求，而不是无成本的配置检查。
- 旧实现没有 Prompt 缓存、每日调用上限、Token 上限、费用上限或逐次审计。
- `DeepSeekDecisionSkill` 仍含旧的直接 HTTP 实现，但当前无引用，不参与运行；后续应删除或迁移为 Adapter，禁止重新接入工作流。
- UI 刷新、Memory 保存、Event 路由和策略研究当前没有发现远程模型调用。

## 已实施保护

- 相同 Provider、Model、Purpose 和 Prompt 在 15 分钟内命中内存缓存。
- 默认每日最多 100 次远程调用。
- 默认每日最多估算 250,000 Token。
- 默认每日估算费用上限 10 USD。
- 达到任一上限后拒绝远程调用，交易系统继续使用本地确定性模式。
- 每次请求、缓存命中和预算拒绝均写入 `llm-calls.jsonl`。
- 审计只保存 Prompt SHA-256，不保存 API Key，也不保存完整 Prompt。

## 后续迁移

下一阶段将把 Provider 协议解析拆分为独立 Adapter，并把 `HttpBrainProvider` 更名为 Assistant Provider，彻底移除“远程模型是 Brain”的命名歧义。

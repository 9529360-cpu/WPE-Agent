# Agent kernel, tools, and MCP architecture

## Intent

WPE uses a local-first autonomous trading runtime. The live trading path must remain deterministic, replay-safe, and protected by hard risk gates.

The maintainable architecture is:

`Runtime/Workflow -> Agent -> Skill/Tool -> Port -> Adapter`

MCP is an integration boundary for tools that must be discovered or invoked outside the in-process runtime. It is not the internal transaction bus for exchange mutations.

## Live roles

The six runtime roles remain:

- `market`: collect and normalize fresh market/account evidence.
- `decision`: read price structure and decide OpenLong/OpenShort/Hold.
- `risk`: enforce hard portfolio, liquidity, freshness, and sizing rules.
- `execution`: persist and submit approved order intents.
- `recovery`: reconcile orders, positions, and protection after faults.
- `audit`: record health, evidence, decisions, and runtime facts.

A role is a responsibility boundary, not necessarily an operating-system process or LLM.

## Module shape

Production code should converge on this layout without a big-bang rewrite:

```text
Services/Agent/
  Agents/
    TechnicalDecisionAgent.cs
  Skills/
    TechnicalAnalysis/
      MarketStructureIntelligence.cs
      DirectMarketStructureDecisionSkill.cs
    Risk/
    Execution/
    Recovery/
  Runtime/
    TradingCycleWorkflow.cs
    AgentRuntimeSupervisor.cs
  Ports/
    exchange, persistence, clock, market-feed contracts
  Adapters/
    Binance/
    Sqlite/
  Mcp/
    read-only and explicitly authorized tool adapters
```

Existing code moves into these owners incrementally. Namespace compatibility may be retained while physical file ownership is cleaned up.

## Technical decision agent

`TechnicalDecisionAgent` owns the decision role. Its technical-analysis skills consume canonical evidence and never call the exchange directly.

Current decision evidence uses:

- 4h and 1h structure for higher-timeframe bias.
- 15m structure for location, scenario, liquidity sweep, rejection, break, retest, and displacement.
- closed 1m realtime candles for fast follow-through confirmation after a 15m candidate trigger.
- structural support/resistance and ATR for invalidation and stop placement.

Risk-increasing decisions require both a valid trigger and confirmation. A candidate without confirmation remains HOLD.

## MCP boundary

MCP is appropriate for safe tool exposure such as:

- runtime health and diagnostics;
- market/evidence inspection;
- technical-structure analysis;
- decision preview;
- positions/orders read models;
- audit/replay queries.

Raw exchange mutation must not be exposed as an unrestricted MCP tool.

If a future MCP tool can request a trade, it must enter through the same durable authorization pipeline as the native runtime:

`decision context -> hard risk -> durable intent -> idempotent execution -> protection -> reconciliation`

No MCP caller may bypass Risk Gate, idempotency, ownership, circuit breaker, or recovery.

## Migration rules

1. Do not create fake live agents for research or strategy.
2. Do not reintroduce score/rank/vote authority.
3. Move one vertical slice at a time and keep tests green.
4. Prefer local function calls for latency-sensitive trading logic.
5. Use MCP when crossing an application/process/tool-discovery boundary.
6. Keep exchange and SQLite details behind adapters.
7. Keep `AutoTradingAgent` shrinking toward composition/workflow ownership instead of accumulating domain logic.

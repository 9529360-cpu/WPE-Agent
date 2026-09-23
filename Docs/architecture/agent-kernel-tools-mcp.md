# Agent kernel, tools, and MCP architecture

## Intent

WPE uses a local-first autonomous trading runtime. The live trading path must remain deterministic, replay-safe, and protected by hard risk gates.

The maintainable architecture is:

`Runtime/Workflow -> Agent -> Skill/Tool -> Port -> Adapter`

MCP is the preferred transport boundary for exchange/tool capabilities that are discovered or invoked outside the in-process runtime. The local workflow remains the authority for decision, risk, durable intent, idempotency, ownership, and recovery; MCP may carry an approved exchange mutation but does not replace those controls.

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

Exchange mutation tools may be exposed over MCP, but never as unrestricted authority.

An MCP tool that requests a trade must enter through the same durable authorization pipeline as the native runtime:

`decision context -> hard risk -> durable intent -> idempotent execution -> protection -> reconciliation`

No MCP caller may bypass Risk Gate, idempotency, ownership, circuit breaker, or recovery.

## Exchange MCP transports

Exchange protocol ownership follows a reuse-first rule:

- OKX uses the official `@okx_ai/okx-trade-mcp` server. WPE maps its canonical provider contract onto official OKX MCP tools and does not duplicate OKX REST signing, fee, instrument-rule, order, or recovery endpoints.
- Binance Agent OS is supported as an official remote MCP transport at `https://agent.binance.com/mcp/agentic`. WPE uses OAuth Client ID Metadata Documents (CIMD) plus a current-user Windows-protected token cache. The metadata URL must be an approved HTTPS document supplied through `WPE_BINANCE_MCP_CLIENT_METADATA_URI`; WPE does not use Dynamic Client Registration and does not fall back to Binance API keys for that transport.
- Binance Futures Testnet keeps the already-validated native provider as the protocol implementation, but `WPE.BinanceMcp` exposes that existing provider as a local stdio MCP server. This is a wrapper, not a second Binance implementation.
- The local Binance MCP server is read-only by default. Write tools are registered only when both `--allow-write` and `WPE_BINANCE_MCP_ALLOW_WRITE=1` are present, and it refuses Mainnet writes.
- `WPE.ExchangeMcp` is the generic MCP client/bridge. The Agent/runtime can therefore address OKX official MCP, Binance official MCP when authorized, or the local Binance Testnet MCP through one tool-client boundary.

No online language model is required by any exchange MCP transport.

## Migration rules

1. Do not create fake live agents for research or strategy.
2. Do not reintroduce score/rank/vote authority.
3. Move one vertical slice at a time and keep tests green.
4. Prefer local function calls for latency-sensitive trading logic.
5. Use MCP when crossing an application/process/tool-discovery boundary.
6. Keep exchange and SQLite details behind adapters.
7. Keep `AutoTradingAgent` shrinking toward composition/workflow ownership instead of accumulating domain logic.

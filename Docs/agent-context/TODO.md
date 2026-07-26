# TODO

## P0
- [x] Add focused Setup Bridge message tests.
- Keep the active safety suites in the release gate; add new collection-contract tests only together with the corresponding orders, equity, backtest, skill-call, or audit backend protocol.
- [x] Re-enable Agent settings resilience and Brain endpoint validation suites with the stage 2A configuration migration.
- [x] Verify order placement cannot bypass Risk Gate across all provider paths: current production order-path audit found no bypass evidence, and `TradingExecutionGatewayTests` adds a production-composition root guard around `ReliableOrderExecutor`; `43/43` passed, but this remains a source-wiring guard rather than a live exchange proof.
- [x] Audit logs and exception messages for secret redaction: audited and fixed redaction before `RealTimeDataPipeline` error-frame persistence and before both runners write `report.Cases`; `SensitiveDataRedactorTests` `6/6` and `AccessRunnerRedactionTests` `4/4` passed.
- [x] Connect the read-only trading-authorization projection to the Reference UI runtime snapshot: `BuildRuntimeJson()` refreshes and supplies `RuntimeAuthorization`; `MainWindowRuntimeBridgeTests` `2/2` passed. This is visibility only, not approval mutation or approved-order processing.

## P1
- Expose strategy registry and lifecycle events to the Web UI.
- [x] Add historical orders, equity, backtest, skill-call, and audit collection protocols with bounded signed cursors, stale/error withholding, SQLite read-only projection, runtime bridge integration, and focused tests.
- [x] Add the first deterministic Macro research component: a versioned observed-fact contract requiring canonical Macro source metadata, UTC observation/release times, geography, frequency, unit, and numeric value. Live official-source ingestion and revision history remain open, so the full Macro capability is not promoted.
- [x] Add the first keyless official Macro adapter for allowlisted BLS public series. It pins the official HTTPS endpoint, performs read-only POST, bounds responses, hashes raw evidence, labels fetch time as first-observed rather than formal release time, and fails closed on malformed/unavailable data. Persistent revision history and release-calendar correlation remain open.
- Consolidate or quarantine legacy Binance-direct services.

## P2
- Add compact context/token/latency/cost metrics for Brain calls.
- Add tiered working, episodic, and long-term memory retrieval using existing SQLite.
- [x] Token milestone order completed for the July 21, 2026 batch: landed `two-level planner prompt slimming`, `structured short memory` planner-history compression, and the `runtime/UI` governance baseline.
- [x] Replace `RecentOutcomesAsync` free-text planner history with bounded `structured short memory` that reuses existing SQLite memory tiers without weakening fail-closed trading context.
- [x] First `structured short memory` batch is contract-only: added the read-side `StructuredOutcomeMemory[]` projection plus narrow store/persistence/degraded-path tests before changing `AutoTradingAgent` or deleting the old text aggregation path.
- [x] Prompt-compression follow-up from the July 21, 2026 audit: `Markets` brief now uses a bounded priority subset, slim-mode `marketAssessments.Signals` now keeps only directional essentials, and slim-mode news no longer carries `BodySummary`.
- [x] Expose the runtime/UI governance baseline clearly enough to track token/cost/remote-usage regression without re-reading prompt internals.
- Next token follow-up only if additional savings are still needed: consider switching `AutoTradingAgent` from legacy `PreviousOutcomes` string consumption to the structured contract directly, using the current runtime/UI baseline as the regression gate.

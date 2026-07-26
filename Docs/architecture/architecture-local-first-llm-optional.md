# WPE Agent Local-First / LLM-Optional Architecture

## 1. Layers
- UI layer
- Policy and mode manager
- Local rules engine
- Metrics and confidence engine
- Task router
- Brain provider adapter
- Memory, summary, and cache services
- Telemetry and cost ledger

## 2. Routing
1. Classify task by type: deterministic, analytical, generative, research.
2. Score local confidence using rule coverage, data freshness, and risk.
3. Check mode policy, privacy policy, budget, and connectivity.
4. Route to local execution or Brain provider.
5. On failure, fall back to cached summary, local heuristic, or user prompt.

## 3. Brain Provider
- Single interface for all LLM vendors.
- Supports model selection, token limits, timeout, retries, and circuit breaking.
- Provider is optional and fully disabled in Local Only mode.

## 4. Local Engines
- Rules engine: deterministic task handling.
- Metrics engine: confidence, coverage, freshness, risk, and cost estimate.
- Memory engine: short-term state, long-term summaries, and retrieval.
- Cache engine: prompt/result cache with TTL and hash keys.

## 5. Reliability
- Hard timeout per call.
- Per-provider circuit breaker.
- Budget gate before request dispatch.
- Offline-first execution path.

## 6. Mode Matrix
- Local Only: no provider, local engines only.
- Hybrid: local first, provider on gated tasks.
- AI Research: provider preferred, local guardrails always on.

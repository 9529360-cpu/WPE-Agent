# WPE Agent Local-First / LLM-Optional PRD

## 1. Objective
Make WPE Agent fully usable without any LLM dependency. LLM becomes an optional enhancement for planning, research, summarization, and natural-language assistance.

## 2. Modes
### Local Only
- No network LLM calls.
- All core workflows must run via local rules, metrics, and deterministic templates.
- Best for privacy, offline use, and zero variable cost.

### Hybrid
- Local engine is primary.
- LLM is used only for eligible tasks with budget, policy, and confidence gates.
- Default mode for most users.

### AI Research
- LLM-first for exploration and synthesis.
- Local engine still enforces safety, budget, and fallback behavior.
- Best for high-iteration research and drafting.

## 3. Core Features
- Local rule engine for task classification, routing, and execution.
- Metrics engine for confidence, freshness, risk, and cost scoring.
- Optional Brain provider abstraction for any LLM backend.
- Prompt cache, memory, summaries, and circuit breakers.
- Privacy mode and offline mode.

## 4. User Experience
- Clear runtime badge: `Local Only`, `Hybrid`, `AI Research`.
- Show `LLM available`, `LLM blocked`, `Offline`, `Budget used`, and `Fallback active`.
- When LLM fails, user sees deterministic degraded output, not a dead end.

## 5. Degradation Rules
- If provider unavailable, continue with local-only path.
- If budget exceeded, freeze new LLM calls for the cycle.
- If confidence is low, route to local clarification or ask user.
- If privacy mode is on, disable external calls and remote memory sync.

## 6. Success Metrics
- 100% of core workflows complete in Local Only mode.
- < 1% hard failures due to LLM unavailability.
- Cost per task visible and bounded.
- Offline mode works for all deterministic flows.

# Acceptance Tests

## Mode Coverage
- Local Only completes key workflows with zero external calls.
- Hybrid routes eligible tasks to Brain provider and non-eligible tasks locally.
- AI Research uses Brain provider by default and still enforces limits.

## Degradation
- Provider outage triggers fallback without blocking task completion.
- Budget exhaustion blocks new LLM calls and preserves local execution.
- Offline mode disables remote calls and still permits deterministic paths.

## Privacy
- Privacy mode prevents external LLM requests.
- No prompt payload leaks into telemetry.
- Memory persistence respects scope and retention.

## UX
- UI shows current mode, budget, and fallback state.
- Users can see why a task was routed locally or to Brain.
- Failures expose actionable next steps.

## Performance
- Prompt cache returns identical results for stable inputs.
- Circuit breaker trips after repeated provider failures.
- Token budgets cap spend per session and per period.

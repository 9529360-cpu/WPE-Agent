# Cost Targets

## Targets
- Local Only: $0 variable LLM cost.
- Hybrid: target average cost <= $0.03 per task.
- AI Research: target average cost <= $0.15 per task.

## Guardrails
- Per-task hard cap on tokens and USD.
- Per-session soft warning before 80% budget usage.
- Per-period hard stop at 100% budget.
- Cache hit rate target >= 35% in Hybrid, >= 20% in AI Research.

## Operational Goals
- LLM call rate reduced by routing and caching.
- Fallback rate under 5% in normal operation.
- Provider failure should not increase core completion failure rate above 1%.

## Metrics to Track
- tokens/task
- USD/task
- cache hit rate
- fallback rate
- offline completion rate
- privacy-block rate

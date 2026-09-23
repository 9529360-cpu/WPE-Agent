# Retired: LLM-Optional Acceptance Tests

The Hybrid / AI Research acceptance matrix is retired.

Current acceptance requirements are:

- Trading decisions complete with the local deterministic brain only.
- No browser network egress is introduced by the Web UI.
- No remote Brain/provider selection appears in runtime state, settings, or product UI.
- Market, risk, execution, recovery, position-management, and protection-order tests remain authoritative.
- Old persisted configuration and historical skill-call records may still be read safely, but they cannot activate remote trading behavior.
- Missing, stale, or inconsistent evidence must fail closed or reduce risk; no remote model is used as a fallback.

# Seven-Agent Formal Acceptance

Machine gate: `wpe.model-off-aggregate-acceptance/1.0` in `Services/Agent/ModelOffAggregateAcceptanceV1.cs`.

The seven aggregate Agents remain `partial` until their own four required evidence classes pass together. Component acceptance, a passing unit test, a live process, or a public price response cannot promote an aggregate.

Every evidence record must bind one requirement and Agent to the required `Local`, `Target`, or `Testnet` environment, a UTC observation and bounded expiry, a passing result, canonical artifact bytes whose recomputed lowercase SHA-256 matches the declared identity, and a bounded provider identity for live evidence. Missing, duplicate, cross-Agent, unexpected, stale, overlong, failed, malformed, hash-mismatched, or wrong-environment evidence fails closed. Mainnet is not an accepted evidence environment.

The current gates cover:

- Market: canonical contract, adversarial failure behavior, live provider read, sustained freshness.
- Research: canonical contract, point-in-time replay, live source availability, model-off target cycle.
- Strategy: lifecycle contract, anti-overfit validation, Testnet shadow observation, no Risk Gate bypass.
- Risk: deterministic gate, adversarial bypass rejection, live preauthorization, time-of-use revalidation.
- Execution: mutation routing, idempotency, live Testnet order lifecycle, exchange correlation.
- Recovery: restart reconciliation, unknown quarantine, live timeout reconciliation, no-resubmit proof.
- Audit: append-only enforcement, seven-role correlation, live-cycle completeness, restart durability.

The gate produces a deterministic evidence-set hash but does not edit the maturity authority. Promotion requires an explicit reviewed update to `model-off-capability-maturity.json` after the gate accepts the corresponding aggregate.

Market live evidence is produced only by `--market-aggregate-acceptance`. The runner uses the official Binance Futures Testnet endpoint with execution disabled and empty credentials. It emits one immediate read artifact and four five-minute samples spanning at least 15 minutes. Every artifact binds the executing assembly version and SHA-256, is atomically persisted, then independently reread, strictly parsed, canonicalized and hash-verified. Every sample must contain canonical Market provenance, at least 31 confirmed candles, quality score 65 or higher, a source no older than 20 minutes, and the sustained artifact must contain at least two distinct canonical market states. The runner exposes no account, order, placement, cancellation, or mutation method.

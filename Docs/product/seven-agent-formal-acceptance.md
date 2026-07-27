# Seven-Agent Formal Acceptance

Machine gate: `wpe.model-off-aggregate-acceptance/1.0` in `Services/Agent/ModelOffAggregateAcceptanceV1.cs`.

Each aggregate Agent remains `partial` until its own four required evidence classes pass together. Component acceptance, a passing unit test, a live process, or a public price response cannot promote an aggregate. Market and Research are formally accepted only within their exact candidate-bound scopes recorded in the maturity authority; the other five aggregates remain `partial`.

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

Market local evidence is produced by two independent focused TRX runs and packaged with `--market-local-acceptance`. The structural TRX parser requires the exact allowlisted test identities, matching definitions and results, all-pass counters, and no missing, duplicate, failed, or extra tests. Each canonical artifact binds its requirement ID, candidate version and DLL SHA-256, test DLL SHA-256, raw TRX SHA-256, UTC completion time, and exact sorted test set. The command is read-only and cannot promote maturity by itself.

Research local evidence uses the same structural verifier through `--research-local-acceptance`, but has separate exact allowlists and independent TRX files. `research.canonical-contract` covers canonical News, Macro, Technical, Fundamental and Backtest admission. `research.point-in-time-replay` covers symbol/range isolation, publication-time ordering, preserved validation timestamps and source-specific expiry. These local artifacts do not satisfy either Target requirement and cannot promote Research without live-source availability and a complete model-off target cycle from the same candidate.

Research Target evidence is produced by `--research-aggregate-acceptance`. It reads no credentials and exposes no mutation path. The runner requires canonical Binance Futures Testnet BTCUSDT market and venue-instrument facts, both allowlisted BLS series, at least one valid allowlisted HTTPS news source, and a newly computed five-capability model-off cycle. Technical provenance uses only fully closed one-minute candles for current source time while retaining deterministic confirmed multi-timeframe signals. Official BLS `value:"-"` rows are skipped only when a bounded official footnote explicitly states `Data unavailable`; unfootnoted or arbitrary nonnumeric values remain invalid. `--research-acceptance-assess` requires both local and both Target artifacts to bind the executing candidate before the common aggregate gate can accept Research.

Strategy local evidence is produced by three independent focused TRX runs and packaged with `--strategy-local-acceptance`. Exact allowlists bind lifecycle isolation and lineage, chronological anti-overfit gates, and the mandatory pre-authorization no-bypass boundary to one candidate DLL and test assembly. These three Local artifacts do not satisfy `strategy.shadow-observation`; Strategy remains `partial` until independently verified Testnet shadow evidence from the same candidate completes the common aggregate gate.

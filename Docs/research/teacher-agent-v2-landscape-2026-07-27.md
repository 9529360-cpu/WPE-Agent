# Teacher Agent V2 External Landscape

Observed: 2026-07-27. This is a read-only design survey, not a dependency approval or a claim that any external project is production-safe.

## Compared Projects

| Project | Public evidence observed | Useful pattern for WPE | License observed | Decision |
|---|---|---|---|---|
| [FinRobot](https://github.com/AI4Finance-Foundation/FinRobot) | Lead, data, analysis, modeling, synthesis and report Agents; bull/bear/judge debate; numeric provenance; deterministic Python valuation; traceable multi-chapter equity reports | Separate numeric computation from language, add evidence-linked report chapters and structured counterarguments | Apache-2.0 | Adopt concepts; do not import code without dependency review |
| [TradingAgents](https://github.com/TauricResearch/TradingAgents) | Fundamental, sentiment, news, technical, research debate, trader and risk roles; checkpoint resume; persistent decision log; realised-return reflection; multiple providers | Add recommendation-outcome reflection, checkpointed lesson work and explicit disagreement, but keep all authority deterministic | Apache-2.0 | Concepts only; its LLM-driven non-determinism cannot become WPE authority |
| [OpenBB](https://github.com/OpenBB-finance/OpenBB) | Provider-neutral financial data platform and “connect once, consume everywhere” architecture | Reinforces WPE's provider-neutral evidence envelope and source registry | README states AGPLv3; repository metadata was nonstandard/`Other` in the query | Do not embed or copy without explicit license review; adapters remain independently implemented |
| [agents-for-openbb](https://github.com/OpenBB-finance/agents-for-openbb) | Agent examples for raw widget data, citations, charts, tables, PDFs, dashboard context and MCP | Add typed report blocks and machine-resolvable citations, not prose-only reports | MIT | Reference concepts; any code reuse still requires file-level review |
| [Qlib](https://github.com/microsoft/qlib) | Point-in-time datasets, data health checks, full research/backtest pipeline, automatic data updates and reporting | Add source-health gates, point-in-time lesson replay, look-ahead prevention and reproducible historical case snapshots | MIT | Candidate future research adapter, not a current Teacher dependency |
| [RD-Agent](https://github.com/microsoft/RD-Agent) | Iterative factor/model research, benchmarked experiment loops and trace inspection | Use offline proposal/evaluation loops to improve lesson rules without allowing production self-modification | MIT | Future offline evaluation only |
| [FinMem](https://github.com/pipiku915/FinMem-LLM-StockTrading) | Layered memory, profiling and decision-making with training/test separation | Add layered Teacher memory and explicit outcome horizons, but store facts and lesson outcomes rather than model beliefs | MIT | Concepts only; no trading-decision authority |
| [Freqtrade](https://github.com/freqtrade/freqtrade) | Crypto data, backtests, performance reports, look-ahead analysis and recursive analysis | Add historical-case leakage checks and recommendation-outcome diagnostics | GPL-3.0 | Do not embed into WPE without an explicit GPL architecture decision |
| [ai-hedge-fund](https://github.com/virattt/ai-hedge-fund) | Multiple investor personas plus valuation, sentiment, fundamentals, technical and risk roles | Confirms demand for explainable viewpoints, but named-investor imitation is unnecessary and can blur evidence ownership | MIT | Do not copy personas; retain one original WPE professor |

Repository popularity and update timestamps were inspected only as discovery signals and are intentionally not treated as quality or safety evidence. Stars can change and do not enter the product design.

## Official-Source Availability Probe

The current machine performed bounded unauthenticated read probes on 2026-07-27:

- SEC EDGAR returned an automated-access policy rejection directing clients to developer and Fair Access guidance.
- BLS returned an automated-access denial.
- FRED documentation probe timed out.
- EIA documentation probe was inconclusive because the local HTTP client failed while processing the response.
- The attempted ECB service root returned 404 and therefore did not validate an endpoint contract.

These results do not prove that the official services are generally unavailable. They prove that source existence, endpoint correctness, current-machine reachability, policy compliance and sustained availability must be accepted separately. WPE must use documented identification, rate limits, caching and provider-specific health checks and must preserve `unavailable` rather than silently falling back to unverified sources.

## Design Additions

The comparison produced the following additions to Teacher V2:

1. **Evidence argument map:** every recommendation stores supporting, contradicting and missing evidence. Optional bull/bear drafts are allowed, but a deterministic evidence judge owns the final state.
2. **Numeric provenance barrier:** language generation cannot calculate or alter prices, returns, valuation, score, confidence, risk/reward or performance numbers.
3. **Typed report blocks:** reports consist of citations, tables, charts, facts, hypotheses, counterarguments and correction blocks, each with a stable identity.
4. **Point-in-time historical lessons:** a historical lesson sees only information available at the simulated lesson time. Later outcomes are revealed in a separate review step.
5. **Recommendation outcome ledger:** recommendations are evaluated at declared horizons against a declared benchmark, with corporate actions and missing data handled explicitly. It evaluates calibration, not just raw return.
6. **Layered teaching memory:** working, episodic and long-term memories have different retention, eligibility and correction rules. Unsupported model summaries cannot become long-term facts.
7. **Network source budget:** every scheduled lesson has bounded requests, bytes, retries, domains and wall-clock time. Exhaustion degrades sections rather than delaying the seven-Agent loop.
8. **Provider and license registry:** availability, terms, attribution, redistribution, authentication, rate limit, data delay and commercial-use status are first-class metadata.
9. **Offline improvement loop:** proposed scoring, lesson or causal-rule changes run against frozen historical fixtures and cannot self-promote into production.
10. **Anti-herding explanation:** Teacher shows material disagreement and counterevidence instead of compressing every Agent/source into one confident narrative.

## Remaining Uncertainty

- No surveyed project proves reliable financial causality; WPE must continue to label explanations as observed, supported, plausible or unknown.
- Public repository claims and README descriptions were not treated as independent performance validation.
- Provider data rights and redistribution terms must be reviewed per adapter and deployment jurisdiction.
- Real-time equity, filing, commodity and macro availability on the target Windows machine remains unaccepted.

# Teacher Agent V2 Product And System Design

Status: the bounded local-first crypto Teacher scope was formally accepted on 2026-07-27; cross-asset/news expansion remains pending. Evidence: [`../validation/teacher-v2-acceptance-2026-07-27.md`](../validation/teacher-v2-acceptance-2026-07-27.md).

This document defines the full Teacher Agent that extends the accepted bounded `MarketTeacherBriefV1`. V1 remains compatible and continues to publish one deterministic opt-in market brief. V2 adds scheduled lessons, evidence-based recommendations, historical teaching, read-only network research, and seven-Agent explanations without entering the trading authority chain.

## 1. Product Role

Teacher is the user-facing cognitive layer above the seven core Agents:

`Market -> Research -> Strategy -> Risk -> Execution -> Recovery -> Audit`

Teacher may:

- explain canonical facts and decisions from all seven Agents;
- teach crypto, equities, commodities, rates, foreign exchange, macroeconomics, and market history;
- publish morning, afternoon, evening, event, weekly, and post-trade lessons;
- create evidence-based `research`, `watch`, `avoid`, or `expired` recommendations;
- identify what additional Research evidence is required;
- use bounded public read-only network sources;
- explain uncertainty, disagreement, invalidation, and historical analogues.

Teacher may never:

- place, amend, cancel, retry, or approve an order;
- change a strategy, risk limit, position, provider configuration, or seven-Agent artifact;
- turn a recommendation into execution authority;
- use private account credentials or trading API credentials for research;
- invent unavailable prices, news, fundamentals, scores, causes, or historical facts;
- present a model-generated explanation as verified evidence.

A Teacher recommendation can enter Research as a candidate. Any later trade still requires the ordinary Strategy, Risk, Execution, Recovery, and Audit chain.

## 2. Persona

Contract: `wpe.market-teacher-persona/2.0`

- male financial professor, approximately 68 years old;
- approximately 45 years of cross-market observation, research, and risk-management experience;
- experienced across inflation and rate cycles, commodity shocks, equity crashes, the internet bubble, the global financial crisis, and the development of crypto markets;
- calm, evidence-first, plain-spoken, patient, candid, historically informed, and non-performative;
- teaches beginners without patronizing them and gives professionals sufficient methodological depth;
- acknowledges uncertainty and corrects earlier lessons when later evidence changes the conclusion.

The persona affects wording only. It cannot alter facts, scores, recommendation state, source eligibility, notification consent, or trading authority.

## 3. Operating Schedule

Default schedule uses `Asia/Shanghai` and is configurable by the user:

| Time | Lesson | Primary purpose |
|---|---|---|
| 08:00 | Morning lesson | Overnight crypto, Asia context, macro calendar, risk map, current research list |
| 14:00 | Afternoon lesson | Asia review, Europe pre-open context, BTC/ETH structure, sector rotation, changes since morning |
| 20:00 | Evening lesson | Europe session, US pre-market context, daily Agent-chain review, history lesson, next observations |

The market-calendar service, not a hard-coded sentence, determines whether a venue is closed, pre-open, open, in daylight-saving time, or on holiday. At 14:00 Beijing time the lesson is normally a Europe preview unless the authoritative calendar proves that a selected venue is already open.

Crypto monitoring is continuous. Equity and commodity commentary is session-aware. Event lessons may interrupt the schedule for a verified material event, but duplicate facts are suppressed.

## 4. Lesson Types

### Morning

- one-sentence market conclusion;
- verified overnight changes;
- BTC, ETH, major crypto structure;
- available global index, rates, FX, gold, and oil context;
- scheduled macro and market events;
- Research candidates and invalidation conditions;
- current Risk constraints;
- one bounded teaching topic.

### Afternoon

- what changed since morning;
- which morning conclusions were confirmed, weakened, invalidated, or remain unknown;
- Asia close and Europe pre-open/open state;
- breadth, leadership, sector rotation, volume, volatility, and liquidity when available;
- one current-market case lesson.

### Evening

- Europe and US pre-market/current-session context;
- seven-Agent chain explanation;
- execution, recovery, and audit facts when a real cycle exists;
- what the system avoided as well as what it captured;
- next-session observation plan;
- historical analogue with explicit similarities and differences.

### Event Lesson

Required sections:

1. what is confirmed;
2. what moved and when;
3. primary and alternative explanations;
4. transmission mechanism across assets;
5. current seven-Agent view;
6. affected recommendations;
7. invalidation and next evidence;
8. uncertainty and risk warning.

## 5. Evidence And Network Model

Teacher network access is `public-read-only` and is provided by a dedicated evidence gateway. Teacher does not receive a general-purpose browser mutation surface.

Allowed operations:

- HTTPS `GET` and bounded official read-only APIs;
- RSS/Atom and bounded public documents;
- cached reads with source and retrieval timestamps;
- deterministic parsing, hashing, deduplication, and provenance storage.

Forbidden operations:

- login, cookies that identify the user, form submission, posting, messaging, file upload, or external mutation;
- executable download;
- use of exchange trading credentials;
- bypass of robots, paywalls, access controls, rate limits, or provider terms;
- sending source code, logs, personal data, positions, credentials, or private project data to a source.

Source tiers:

1. official: governments, central banks, regulators, exchanges, statistical agencies, issuer/company filings;
2. primary reporting: established news wires and directly attributable reporting;
3. secondary analysis: established financial publications;
4. leads only: social media, community claims, unverified on-chain commentary.

Tier 4 cannot establish a fact or causal conclusion by itself. Material geopolitical or security claims require either one primary official source or corroboration by at least two independent eligible sources.

Every evidence item carries:

- source ID and tier;
- canonical URL or provider endpoint;
- asset, geography, and event identity;
- event time, source publication time, retrieval time, and expiry;
- raw-evidence hash and parsed-fact hash;
- status: `available`, `stale`, `unknown`, `unsupported`, `conflicting`, `invalid`, or `error`;
- correction/retraction link when applicable.

## 6. Cross-Asset Data Contract

Teacher consumes a provider-neutral evidence envelope:

```text
TeacherEvidenceEnvelope
  identity: asset / market / geography / event
  category: crypto / equity / index / commodity / rates / fx / macro / news / geopolitical
  facts: typed observed values only
  provenance: source, time, hashes, status
  agentReferences: canonical seven-Agent artifact hashes
  availability: available / stale / unknown / unsupported / conflicting / error
```

Interfaces may be designed before providers are connected. Unsupported fields remain explicit and cannot be rendered as zero, neutral, or inferred values.

Initial crypto evidence may use existing WPE Market, Research, Strategy, Risk, Execution, Recovery, and Audit artifacts. Future adapters may add:

- global equity indices, sectors, securities, filings, earnings, and corporate actions;
- oil, gold, rates, yield curves, currencies, and volatility indices;
- ETF flows, liquidations, funding, open interest, and verified on-chain facts;
- economic calendars and market-session calendars.

## 7. Causal Explanation Contract

Teacher never converts temporal coincidence directly into causality. Every explanation separates:

- `confirmedFacts`;
- `observedMarketReaction`;
- `primaryHypotheses` with supporting and contradicting evidence;
- `alternativeHypotheses`;
- `transmissionMechanisms`;
- `uncertainties`;
- `nextVerificationEvents`.

Example chain:

`geopolitical escalation -> possible supply disruption -> oil repricing -> inflation expectations -> rate expectations -> equity valuation pressure`

Each arrow is classified as `observed`, `supported`, `plausible`, or `unknown`. The lesson must not state “war caused oil to rise” when inventory, production policy, currency, demand, positioning, or timing evidence remains unresolved.

Every material conclusion also has an evidence argument map: supporting evidence, contradicting evidence, missing evidence, and the strongest alternative explanation. Optional model-generated bull and bear arguments are drafts only; a deterministic evidence judge checks source eligibility, time ordering, contradiction and coverage before the conclusion enters a lesson.

## 8. Recommendation Contract

Schema: `wpe.teacher-recommendation/2.0`

Allowed states:

- `research_candidate`;
- `priority_watch`;
- `wait_for_confirmation`;
- `avoid_high_risk`;
- `expired`;
- `unavailable`.

Every recommendation includes:

- instrument and asset class;
- recommendation state, horizon, issue time, expiry, and version;
- concise thesis;
- canonical Market/Research/Strategy/Risk evidence references;
- external evidence references where used;
- confirmed facts and unresolved assumptions;
- catalyst conditions;
- confirmation conditions;
- invalidation conditions;
- material risks and liquidity constraints;
- evidence coverage and freshness;
- change from the preceding recommendation;
- explicit `executionAuthority=false`.

Teacher may recommend an equity only when the minimum equity evidence profile is available and fresh. Until equity providers are connected, Teacher may teach historical equity cases and describe what would be required for a current recommendation, but must return `unavailable` for a live equity recommendation.

Recommendation language can be direct, but must remain conditional and reversible. “Priority research candidate because X; invalid if Y” is allowed. “Buy now” and guaranteed-return language are not.

## 9. Deterministic Scoring

Scores are optional explanatory projections, not execution authority. The model never chooses or changes a score.

Each asset-class profile defines versioned components, weights, source requirements, freshness, and rejection rules. A score is emitted only when mandatory components are complete and minimum evidence coverage is met. Otherwise the result is `unavailable`, not a partial score disguised as certainty.

Crypto profile may include market regime, technical structure, liquidity, derivatives, catalyst, fundamental/on-chain evidence, risk/reward, and source confidence. Equity profile may include macro regime, industry, filing/earnings facts, valuation inputs, technical structure, liquidity, catalyst, risk/reward, and source confidence.

The report displays component scores, missing components, profile version, evidence coverage, and the fact that the score cannot authorize a trade.

All numeric fields pass through a numeric-provenance barrier. Prices, changes, returns, valuation inputs, scores, confidence, risk/reward, benchmarks and performance metrics must originate from typed deterministic operators. A language model cannot calculate, round, replace or “repair” them.

## 10. Teaching Levels And Memory

User-selectable levels:

- beginner: plain language, definitions, fewer assumptions;
- intermediate: structure, flows, strategy fit, invalidation, risk/reward;
- professional: volatility, correlations, liquidity, positioning, microstructure, evidence quality, and strategy diagnostics.

Teacher memory stores only local, bounded learning state:

- lessons delivered and acknowledged;
- concepts already explained;
- selected level, language, timezone, markets, and notification channels;
- recommendation revisions and corrections;
- user-authored notes when explicitly saved.

Teacher does not diagnose a user's psychology. Deterministic behaviors may be described as possible discipline risks, with the exact supporting events. Personalization and message delivery are opt-in and locally auditable.

Memory is layered:

- working memory: evidence and lesson deltas for the current schedule window;
- episodic memory: lessons, recommendations, corrections and outcomes for bounded recent periods;
- long-term memory: only accepted definitions, durable user preferences, validated historical cases and repeated calibrated findings.

Free-text model summaries never become long-term facts without deterministic reconstruction from canonical evidence.

## 10A. Point-In-Time Lessons And Recommendation Outcomes

Historical teaching and recommendation evaluation must prevent look-ahead. A lesson replay receives only evidence published or observed by the simulated lesson time. Later price action, filings, corrections and outcomes appear only in a separately timestamped review.

Every recommendation declares evaluation horizons and a benchmark. The outcome ledger records absolute return, benchmark-relative return, maximum adverse/favorable excursion, invalidation timing, evidence availability and whether the original conditions were followed. Corporate actions, stale prices, delistings and missing benchmark data fail the affected metric closed. Teacher learns calibration and explanation quality from this ledger; it does not reward a lucky outcome produced by an invalid process.

## 11. Local-Only And Optional Model Behavior

Local Only must produce complete factual lessons using:

- deterministic templates;
- versioned financial definitions;
- evidence status and causal-language rules;
- local historical case records;
- canonical seven-Agent facts;
- locally cached public evidence.

An optional model may improve wording, translate, or offer alternative explanations. Its output is parsed as an untrusted draft. A deterministic verifier must reject any changed number, source, timestamp, recommendation state, score, risk condition, or Agent conclusion. Model failure falls back to the local lesson without blocking the seven-Agent runtime.

## 12. Architecture

```text
Public read-only adapters        Seven-Agent canonical store
             |                              |
             +------ Teacher Evidence Reader+
                              |
                    Evidence/Causality Gate
                              |
          +-------------------+-------------------+
          |                   |                   |
    Lesson Planner     Recommendation Engine   History Mapper
          |                   |                   |
          +-------------------+-------------------+
                              |
                  Deterministic Report Composer
                              |
                   Optional wording enhancement
                              |
                     Post-generation verifier
                              |
              Local archive / opt-in notification
```

Teacher has no dependency edge into `TradingExecutionGateway`, `ReliableOrderExecutor`, provider secret stores, or configuration mutation.

Report output is a collection of typed blocks rather than one opaque paragraph: cited fact, table, chart specification, hypothesis, counterargument, recommendation, risk, unknown, correction and provenance footer. Each block has its own hash so clients can render, diff and cite it without reparsing prose.

## 13. Persistence And Audit

Persist append-only:

- lesson identity, type, schedule window, language, and teaching level;
- exact evidence and Agent artifact hashes;
- recommendation version and supersession chain;
- causal hypotheses and evidence strength;
- generated content hash and generator version;
- optional model usage and verification result;
- delivery consent, destination kind, delivery state, and retry state without destination secrets;
- corrections, retractions, and reason codes.

The current lesson may be projected read-only, but historical versions are never overwritten or deleted by ordinary runtime operations.

The provider registry also records source terms, attribution, redistribution permission, commercial-use status, authentication class, rate limit, expected delay and target-machine health. Open-source software licenses and financial-data rights are evaluated separately; repository availability never implies permission to redistribute its data or embed its code.

## 13A. Network Budgets And Isolation

Each lesson run has configured maximum domains, requests, response bytes, retries and elapsed time. Redirects stay within the source policy, content types are allowlisted and responses are parsed out of process or through bounded parsers where practical. Budget exhaustion marks only the affected section unavailable and cannot delay or degrade the seven-Agent trading/recovery loop.

Official source existence is not enough for acceptance. Endpoint correctness, policy-compliant identification, current-machine access, freshness and sustained availability are separately tested. A denied or timed-out official source remains unavailable; Teacher cannot silently replace it with a lower-tier source while preserving the original confidence.

## 14. Delivery Policy

Telegram, WhatsApp, and future channels remain explicitly opt-in. Morning, afternoon, evening, event, recommendation, and correction notifications have separate consent switches. Delivery failure cannot change trading, recommendation, or evidence state.

Quiet hours, timezone, deduplication, maximum messages per window, and event severity are enforced locally. A correction may bypass normal deduplication but must reference the superseded lesson.

## 15. Delivery Phases

### Phase A: contracts and local generation

- persona 2.0;
- lesson, evidence, causality, recommendation, and correction contracts;
- 08:00/14:00/20:00 scheduler with timezone and session-calendar states;
- seven-Agent read projection;
- deterministic Chinese templates and append-only persistence;
- no new network provider and no UI dependency.

### Phase B: bounded public network

- source registry and public-read-only gateway;
- official macro, geopolitical, commodity, market-calendar, and correction evidence;
- source health, cache, rate limit, deduplication, expiry, and conflict handling;
- event lessons.

### Phase C: recommendations and history

- deterministic crypto recommendation profile;
- recommendation revisions and invalidation;
- historical analogue store and differences-first teaching;
- weekly, post-trade, and growth reports.
- point-in-time replay, benchmark-relative recommendation outcomes and leakage diagnostics;
- deterministic support/counterevidence maps and recommendation calibration.

### Phase D: equities expansion

- provider-neutral equity, filing, earnings, corporate-action, and session contracts;
- licensed/authorized adapters and source-specific acceptance;
- equity recommendation profile remains disabled until live evidence passes formal acceptance.

### Phase E: product surfaces

- course center, report center, recommendation radar, history, corrections, and notification controls;
- runtime truth only; unavailable states remain visible;
- UI cannot write secrets or gain execution authority.

## 16. Formal Acceptance

Teacher V2 remains unaccepted until all of the following pass together:

- deterministic Local Only lessons at all three schedule windows;
- exact evidence citations and rejection of stale, missing, conflicting, or tampered inputs;
- recommendation lifecycle, expiry, invalidation, and no-execution proof;
- read-only network enforcement and source-tier/corroboration rules;
- restart-safe append-only lessons, recommendations, corrections, and delivery state;
- model-off output equivalence for facts, scores, risks, and recommendation states;
- opt-in delivery, quiet hours, deduplication, and bounded retries;
- source-current Target evidence for every asset class claimed as available.
- point-in-time replay and look-ahead leakage rejection;
- numeric-provenance rejection when generated prose changes a computed value;
- bounded network budgets, provider-policy metadata and lower-tier fallback visibility;
- benchmarked recommendation calibration without promoting lucky but invalid processes.

Passing Teacher acceptance does not change Mainnet status or the accepted boundaries of the seven core Agents.

The external comparison and license notes supporting these additions are recorded in [`../research/teacher-agent-v2-landscape-2026-07-27.md`](../research/teacher-agent-v2-landscape-2026-07-27.md).

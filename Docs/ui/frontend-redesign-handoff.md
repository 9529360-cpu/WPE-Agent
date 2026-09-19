# WPE Frontend Redesign Handoff

Status: implementation guidance for the private Windows build. This document is not an immutable blueprint and must be updated when current runtime contracts, accepted product behavior, accessibility requirements, or validated implementation evidence supersede it.

## Authority and decision order

When guidance conflicts, use this order:

1. Security, trading, execution, recovery and runtime invariants enforced by the host and backend.
2. Canonical runtime contracts and active production code.
3. Passing contract/integration tests bound to the current implementation.
4. Current product and accessibility requirements.
5. This document and older design notes.

Do not preserve a historical layout, page map, naming scheme or component split merely because it appears in Markdown. Prefer mature, conventional interaction patterns and a single clear owner for each product capability.

## Responsibility split

The Web UI owns information architecture, responsive layout, component composition, interaction presentation, typography, color, density, motion and accessible wayfinding. It may replace earlier UI structures when the new implementation preserves accepted capabilities and safety boundaries.

The WPE backend owns runtime truth, permissions, data freshness, trading authorization, execution, recovery, audit, persistence, notification delivery and host-side command enforcement. The Web UI must not duplicate or reinterpret those responsibilities.

## Runtime topology

- Framework: Next.js static export embedded in the Windows WPF application through WebView2.
- Production origin: `https://wpe-reference.local/index.html`, mapped by WebView2 to packaged `WebUi/out` files.
- Production HTTP API port: none.
- Production browser network access: forbidden. The Web UI must not call `fetch`, XHR, WebSocket, EventSource, exchange endpoints, localhost APIs or remote analytics.
- Development preview: the normal Next.js development port may be used for visual work. Development preview data must never ship in `WebUi/out`.
- Host-to-Web update: WPF dispatches the complete runtime snapshot through the `wpe-runtime` window event.
- Web-to-host commands are emitted only through the shared typed bridge in `WebUi/lib/host-command.ts`.

The production UI is a projection of one complete host snapshot. It is not a separately authenticated web service and must not create a second source of truth.

## Allowed host commands

| Command | Purpose | Host enforcement |
| --- | --- | --- |
| `open-settings` | Open the secure WPF settings window | WPF owns configuration and secret writes |
| `open-notification-settings` | Open the WPF notification settings surface | WPF owns notification destinations, credentials and subscriber mutations |
| `agent-start` | Request Agent start | Host revalidates readiness, Testnet environment and current authority |
| `agent-stop` | Request Agent stop | Host owns lifecycle transition and resulting runtime state |

No other Web-to-host command is supported. In particular, the Web UI cannot save credentials, place orders directly, bypass Risk Gate, modify strategies, approve risk, enable Mainnet or edit SQLite.

## Runtime contract

- Contract version: `1.0`.
- Canonical TypeScript contract and validation: `WebUi/components/runtime-bridge.tsx`.
- Canonical C# contract: `Core/Contracts/RuntimeSnapshotV1.cs`.
- Host composition: `Services/RuntimeSnapshotFactory.cs` and `Services/DesktopRuntimeHost.cs`.
- A snapshot is accepted only when the runtime bridge's trust and freshness checks succeed.
- The runtime bridge replaces the previous snapshot atomically; it must never merge fresh fields into stale or preview state.

Every collection has one of four states:

| State | UI behavior |
| --- | --- |
| `available` | Render validated values and their source time |
| `stale` | Withhold actionable values; display stale status and last trusted timestamp where supplied |
| `unsupported` | Explain that the capability is not connected; do not create placeholders |
| `error` | Withhold values and show only the sanitized diagnostic supplied by the host |

Empty and unavailable are different. `available + []` means a real empty result. `unsupported`, `stale` or `error` must never be presented as zero.

## Required real-data surfaces

### Home command view

- Runtime freshness, Testnet environment and provider identity.
- Agent running/stopped/degraded state.
- Account, open positions, active orders and risk readiness.
- Current seven-Agent workflow and recent canonical handoffs.
- Current market facts and data-source health.
- No hero marketing layout, fabricated portfolio chart or invented reasoning stream.

The home page should be a scan-first operating surface. Put high-value status and anomalies first, then link to deeper pages instead of duplicating every subsystem in card form.

### Seven core Agents

Display exactly the canonical chain:

`Market -> Research -> Strategy -> Risk -> Execution -> Recovery -> Audit`

Use `agentOperations` and `agentHandoffs` as runtime truth. Do not use early demo names or role fixtures. Each role needs state, current activity, last activity time, operating mode and latest relevant handoff. The Teacher is a separate cognitive layer, not an eighth trading-chain member.

### Financial Teacher

The host exposes:

- `teacherLessons`: morning, afternoon, evening and material-event lessons with typed blocks.
- `teacherRecommendations`: conditional research candidates only.
- `teacherCorrections`: append-only corrections linked to superseded lessons.
- `teacherOutcomes`: point-in-time outcome evaluation relative to a benchmark.

The UI must visibly preserve `executionAuthority=false`. Recommended structure: current lesson, lesson archive, research candidates, corrections, outcome review and notification state. Never label a recommendation as an order, guaranteed opportunity or approved trade.

### Trading and risk

- Positions, orders, pending approval history, automatic execution status and authorization mode.
- Risk readiness, circuit-breaker state where exposed, current risk load, PnL and drawdown.
- Strategy registry, lifecycle events, backtests and research evidence.
- Start/stop controls use only the shared typed host-command bridge.
- Mainnet controls must not be introduced without a separately accepted product and safety contract.

### Monitoring and settings

- Provider connection readiness, data freshness, runtime heartbeat, diagnostics and security-storage status.
- Notification readiness, sanitized Telegram subscriber state and notification outbox state.
- Local-only / optional AI usage and cost telemetry without implying the LLM has execution authority.
- Settings and notification configuration buttons hand off to the secure WPF owner; secrets never enter React state.

### Future capabilities

Equities, broad cross-asset research, commercial distribution and unsupported providers may remain outside primary navigation until their operator workflow is accepted. If exposed, they must render honest unavailable/future states and never sample values.

## Visual and UX requirements

- Audience: one serious private investor operating a local trading/research system.
- Style: restrained, information-dense, calm and professional; this is an operating console, not a crypto marketing page.
- Navigation should be brief, task-oriented and easy to scan; search is a supplement, not a substitute for coherent information architecture.
- Use progressive disclosure for detail-heavy data rather than nesting decorative cards.
- Prefer semantic status tokens and plain language. Use color for state and risk, not decoration, and never rely on color alone.
- Desktop is primary, but narrow desktop and mobile must preserve information and controls without overlap. Unsafe actions may be deliberately omitted or gated on constrained layouts.
- Tables need stable numeric alignment, explicit units, source time access, loading/empty/stale/error states, overflow behavior and a clear path to details or filtering when data volume grows.
- Important actions require visible consequence text, disabled reasons, keyboard focus and recovery behavior.
- Respect reduced-motion preferences for ticker, pulse and sweep animations.
- Keep focus order and heading hierarchy logical; temporary UI must restore focus to its trigger.
- No oversized marketing headlines, decorative gradient orbs, fake candlestick charts, placeholder assets, fake notifications or invented Agent thoughts.

## Execution command center

The home command view is execution-first. Its primary trading surface is a read-only command center that composes existing host-authoritative projections rather than introducing another trading state:

- current provider/environment and authorization mode;
- deterministic Risk Gate and execution-gate state;
- current provider positions and open orders;
- recent persisted execution events from the historical order projection;
- links to deeper position, order, risk, and history views.

The command center must not add an order-entry form, direct exchange calls, approval mutation, cancellation mutation, or a second ledger. Settlement detail is exposed through the canonical read-only `historicalPostTradeReviews` projection from persisted `trade_outcomes`; reconciliation status is exposed through `historicalReconciliations` from the append-only Position, protection and external-position-isolation audit tables. React must not recompute those facts. Strategy identity is attribution metadata only, not a causal performance claim. Reconciliation projections expose bounded metadata and canonical hashes only; canonical payload bytes remain backend-owned.

Mature exchange terminals, including OpenDAX-style layouts, may be studied for information hierarchy, density and interaction patterns. Do not copy or vendor third-party frontend source unless its exact license permits WPE's intended use and the dependency/license review is recorded. The current command-center implementation is original WPE code and does not reuse BaseApp components.

## Acceptance gates

1. `pnpm lint`, `pnpm typecheck`, Web contract tests and `pnpm build` pass in `WebUi`.
2. The production export contains no preview, fixture, fake or cached-fallback values.
3. All four collection states are handled for every redesigned data surface.
4. Malformed or stale runtime evidence fails closed rather than partially rendering actionable values.
5. The exact seven-Agent chain is shown, with Teacher outside the trading chain.
6. No browser network API exists in production code.
7. Only the four allowlisted host commands are emitted, through the shared typed bridge.
8. Start/stop controls remain disabled unless the host runtime reports the corresponding action as allowed.
9. Representative desktop and narrow/mobile layouts show no overlap, clipped controls or unreadable long localized text.
10. Security, execution, risk and persistence authority remain with their existing backend/WPF owners unless a separate accepted change explicitly moves that authority.
11. Product CI passes on the exact candidate head, including .NET tests and publish-boundary verification.

## Delivery model

Design and implementation proceed as one evidence-driven product loop rather than a fixed handoff sequence:

`operator goal -> information hierarchy -> component/state contract -> implementation -> type/contract validation -> rendered verification -> adversarial review -> cleanup`

Wireframes, page maps and design notes are intermediate tools, not approval gates or permanent architecture. When repository evidence shows that an earlier design choice is wrong, update the implementation and then update or supersede the documentation so the repository has one current story.

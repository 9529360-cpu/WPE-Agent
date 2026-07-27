# WPE Frontend Redesign Handoff

Status: authoritative redesign contract for the private Windows build.

## Responsibility split

The external design AI owns visual direction, information architecture, responsive layout, component composition, interaction presentation, typography, color, density, and motion. It may replace the existing UI completely.

The WPE backend owns runtime truth, permissions, data freshness, trading authorization, execution, recovery, audit, persistence, notification delivery, and host commands. The design must not duplicate or reinterpret these responsibilities.

## Runtime topology

- Framework: Next.js static export embedded in the Windows WPF application through WebView2.
- Production origin: `https://wpe-reference.local/index.html`, mapped by WebView2 to packaged `WebUi/out` files.
- Production HTTP API port: none.
- Production browser network access: forbidden. The Web UI must not call `fetch`, XHR, WebSocket, EventSource, exchange endpoints, localhost APIs, or remote analytics.
- Development preview: the normal Next.js development port may be used for visual work, conventionally `3000`. Development preview data must never ship in `WebUi/out`.
- Host-to-Web update: every two seconds WPF dispatches `window` event `wpe-runtime`; `event.detail` contains the complete runtime snapshot.
- Web-to-host command: `window.chrome.webview.postMessage({type})`.

The production UI is a projection of one complete host snapshot. It is not a separately authenticated web service.

## Allowed host commands

| Command | Purpose | Host enforcement |
| --- | --- | --- |
| `open-settings` | Open the secure WPF settings window | WPF owns configuration and secret writes |
| `agent-start` | Request Agent start | Accepted only for a fresh trusted Testnet snapshot with current provider authority |
| `agent-stop` | Request Agent stop | Accepted only for a fresh trusted Testnet snapshot with current provider authority |

No other command is supported. In particular, the Web UI cannot save credentials, place orders directly, bypass Risk Gate, modify strategies, approve risk, enable Mainnet, or edit SQLite.

## Runtime contract

- Contract version: `1.0`.
- Canonical TypeScript contract and validation: `WebUi/components/runtime-bridge.tsx`.
- Canonical C# contract: `Core/Contracts/RuntimeSnapshotV1.cs`.
- Host composition: `Services/RuntimeSnapshotFactory.cs` and `Services/DesktopRuntimeHost.cs`.
- A snapshot is accepted only for `environment=Testnet`, valid UTC timestamps, valid freshness, and the exact contract version.
- The runtime bridge replaces the previous snapshot atomically; it must never merge fresh fields into stale or preview state.

Every collection has one of four states:

| State | UI behavior |
| --- | --- |
| `available` | Render validated values and their source time |
| `stale` | Withhold actionable values; display stale status and last trusted timestamp where supplied |
| `unsupported` | Explain that the capability is not connected; do not create placeholders |
| `error` | Withhold values and show only the sanitized diagnostic supplied by the host |

Empty and unavailable are different. `available + []` means a real empty result. `unsupported`, `stale`, or `error` must never be presented as zero.

## Required real-data surfaces

### Home command view

- Runtime freshness, Testnet environment and provider identity.
- Agent running/stopped/degraded state.
- Account, open positions, active orders and risk readiness.
- Current seven-Agent workflow and most recent handoff.
- Current market facts and data-source health.
- No hero marketing layout and no fabricated portfolio chart.

### Seven core Agents

Display exactly the canonical chain:

`Market -> Research -> Strategy -> Risk -> Execution -> Recovery -> Audit`

Use `agentOperations` and `agentHandoffs` as runtime truth. Do not use early demo names or role fixtures. Each role needs state, current activity, last activity time, operating mode and latest incoming/outgoing handoff. The Teacher is a separate cognitive layer, not an eighth trading-chain member.

### Financial Teacher

The host now exposes:

- `teacherLessons`: morning, afternoon, evening and material-event lessons with typed blocks.
- `teacherRecommendations`: conditional research candidates only.
- `teacherCorrections`: append-only corrections linked to superseded lessons.
- `teacherOutcomes`: point-in-time outcome evaluation relative to a benchmark.

The UI must visibly preserve `executionAuthority=false`. Recommended structure: current lesson, lesson archive, research candidates, corrections, outcome review and notification preferences. Never label a recommendation as an order, signal to buy, guaranteed opportunity or approved trade.

### Trading and risk

- Positions, orders, pending approvals, automatic execution status and authorization mode.
- Risk readiness, circuit breaker, current risk load, PnL and drawdown.
- Strategy registry, lifecycle events, backtests and research evidence.
- Start/stop controls use only the allowlisted host commands.
- Mainnet controls must not exist.

### Monitoring and settings

- Provider connection readiness, data freshness, runtime heartbeat, diagnostics and security-storage status.
- Notification readiness and outbox states for Telegram and WhatsApp.
- Local Only / optional AI usage and cost telemetry without implying the LLM has execution authority.
- The settings button sends `open-settings`; secrets never enter React state.

### Future capabilities

Equities, broad cross-asset research, commercial distribution and unsupported providers may remain visible only as honest unavailable/future capability states. They should not dominate the private-use navigation and must never display sample values.

## Visual and UX requirements

- Default language: Simplified Chinese. English may be optional.
- Audience: one serious private investor operating a local trading/research system.
- Style: restrained, information-dense, calm and professional; this is an operating console, not a crypto marketing page.
- Desktop is primary. Narrow desktop and mobile layouts must remain readable, but mobile must not expose unsafe controls merely to preserve feature parity.
- Use color for state and risk, not decoration. Never rely on color alone.
- Tables require fixed numeric alignment, explicit units, UTC/source time access, loading/empty/stale/error states and overflow behavior.
- Important actions require clear consequence text and disabled reasons.
- No nested decorative cards, oversized headlines, gradient-orb backgrounds, fake candlestick charts, placeholder assets, fake notifications or invented Agent thoughts.

## Acceptance gates

1. `pnpm lint`, `pnpm typecheck`, and `pnpm build` pass in `WebUi`.
2. The production export contains no preview, fixture, fake or cached-fallback values.
3. All four collection states are rendered for every redesigned data surface.
4. A malformed Teacher item causes its collection to fail closed rather than partially render.
5. The exact seven-Agent chain is shown, with Teacher outside the trading chain.
6. No browser network API exists in production code.
7. Only the three allowlisted host commands are emitted.
8. Start/stop controls remain disabled without fresh trusted Testnet authority.
9. Desktop and narrow/mobile screenshots show no overlap, clipped controls or unreadable long Chinese text.
10. Existing backend, risk, execution, recovery, database and WPF secret/configuration code remains unchanged.

## Delivery boundary

The design AI should first return a page map, navigation proposal, desktop/mobile wireframes, component inventory, state matrix and visual system. Implementation begins only after that design is selected. The implementation owner may edit `WebUi/**`; backend and WPF changes require a separate WPE review.

# External AI Prompt - WPE UI Redesign

You are improving the frontend of WPE Agent, a local-first Windows financial research and Testnet trading Agent system. Treat existing visual choices and older Markdown as evidence, not immutable requirements. Preserve current accepted capabilities and safety boundaries, but replace outdated information architecture, wording, layout or interaction patterns when a clearer mature pattern is available.

Before proposing or implementing a material UI change, inspect the current versions of:

1. `WebUi/components/runtime-bridge.tsx`
2. `Core/Contracts/RuntimeSnapshotV1.cs`
3. `ReferenceUiWindow.xaml.cs`
4. `WebUi/lib/host-command.ts`
5. `WebUi/lib/nav.ts`
6. `Docs/ui/frontend-redesign-handoff.md`
7. Relevant Product CI contract tests

When these disagree with older design notes, runtime contracts, active code and passing tests take precedence.

Important facts:

- Production is a static Next.js export inside WPF WebView2; there is no production REST API port.
- Runtime truth arrives through the `wpe-runtime` window event as a complete host snapshot.
- The only control commands are `open-settings`, `open-notification-settings`, `agent-start`, and `agent-stop`. History additionally has one bounded read-only `history-page` query carrying only a request id, collection key, and host-signed cursor; it cannot supply a limit, offset, SQL, mutation, or authority.
- All Web-to-host messages must go through the shared typed bridge in `WebUi/lib/host-command.ts`; do not add direct `postMessage` call sites.
- The canonical trading chain is exactly Market, Research, Strategy, Risk, Execution, Recovery, Audit.
- The Financial Teacher is outside that chain and has no trading authority.
- Unsupported, stale and error data must be withheld. Never invent preview values, sample Agents, charts, positions, orders, lessons or recommendations.
- Mainnet authority is not part of the accepted Web UI contract.
- The product supports Simplified Chinese, Traditional Chinese, English, Japanese, Korean and Italian. Locale may change copy and formatting, but must not swap the component tree.

Design a quiet, professional, information-dense operating console for a serious private investor. Prefer mature application patterns over novelty: brief task-oriented navigation, scan-first dashboards, progressive disclosure for detail, clear data provenance, stable numeric alignment, accessible focus behavior, reduced-motion support and explicit recovery/error states. Do not create a marketing landing page. Avoid oversized hero sections, decorative gradients, excessive nested cards and game-like crypto styling.

For substantial redesign work, keep these decisions explicit in the implementation or review notes:

1. Product information architecture and page ownership.
2. Desktop navigation and narrow/mobile adaptation.
3. Primary information hierarchy and action placement for changed pages.
4. Reused or changed component/token contracts.
5. State behavior for available, empty, stale, unsupported, error, disconnected and unauthorized data.
6. Exact mapping from visible runtime data to `WpeRuntimeState` fields.
7. Action mapping to the four allowlisted control commands plus the bounded read-only history-page query.
8. Localization and long-text behavior across all supported locales.
9. Accessibility, dense-table, keyboard/focus, reduced-motion and consequential-action treatment.
10. Any missing backend fields or unsafe ambiguities. Do not invent missing fields.

Do not modify backend security, persistence, Risk Gate, execution, recovery or audit authority merely to satisfy a visual concept. Ordinary reversible Web UI decisions do not require a separate design-approval ceremony; implement the smallest complete slice, run the repository gates, inspect the resulting product behavior, and iterate. If a desired experience truly requires moving a backend/WPF authority boundary, treat that as a separate engineering and safety change rather than hiding it inside the redesign.

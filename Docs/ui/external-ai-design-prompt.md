# External AI Prompt - WPE UI Redesign

You are redesigning the frontend of WPE Agent, a local-first Windows financial research and Testnet trading Agent system. The current UI is not a visual reference and may be replaced completely. Your task in this phase is product/UI design, not backend development.

Read these files before proposing the design:

1. `Docs/ui/frontend-redesign-handoff.md`
2. `WebUi/components/runtime-bridge.tsx`
3. `Core/Contracts/RuntimeSnapshotV1.cs`
4. `ReferenceUiWindow.xaml.cs`
5. `Docs/product/seven-agent-formal-acceptance.md`
6. `Docs/product/teacher-agent-v2-design.md`

Important facts:

- Production is a static Next.js export inside WPF WebView2; there is no production REST API port.
- Runtime truth arrives through the `wpe-runtime` window event every two seconds.
- The only Web-to-host commands are `open-settings`, `agent-start`, and `agent-stop`.
- The canonical trading chain is exactly Market, Research, Strategy, Risk, Execution, Recovery, Audit.
- The Financial Teacher is outside that chain and has no trading authority.
- Unsupported, stale and error data must be withheld. Never invent preview values, sample Agents, charts, positions, orders, lessons or recommendations.
- Mainnet is disabled.
- The default product language is Simplified Chinese.

Design a quiet, professional, information-dense operating console for a serious private investor. Do not create a marketing landing page. Avoid oversized hero sections, decorative gradients, excessive cards and game-like crypto styling.

Your first deliverable must contain:

1. Product information architecture and page map.
2. Desktop navigation and narrow/mobile adaptation.
3. Detailed wireframes for home, seven Agents, Financial Teacher, positions/orders, strategies/research, risk, monitoring and settings.
4. Component inventory and a design-token proposal.
5. State matrix for available, empty, stale, unsupported, error, disconnected and unauthorized states.
6. Exact mapping from every visible section to fields in `WpeRuntimeState`.
7. Action matrix showing which controls emit which allowlisted host command.
8. Chinese content hierarchy and terminology.
9. Accessibility, long-text, dense-table and destructive-action treatment.
10. A list of assumptions and any backend fields that appear missing. Do not implement or invent missing fields.

Do not modify backend contracts, WPF, persistence, Risk Gate, execution, recovery or audit logic. Do not begin coding until the design proposal is reviewed.

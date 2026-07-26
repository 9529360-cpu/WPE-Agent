# Project State

- Current source: `WPE-` worktree.
- Authoritative product/assembly version: the application project's `<Version>` (`3.6.0` at this milestone). Runtime, plugin compatibility, release reports and packaging read the generated assembly/project version.
- `WPE-Agent-3.8.2-P0-Truth` is a historical validation label, not an assembly version or a distributable release version.
- Mutable runtime data lives under `%LOCALAPPDATA%\WPE Agent\`; the application installation directory is treated as read-only.
- Desktop entry: `币安量化机器人.exe --reference-ui`.
- Setup Bridge opens the existing secure WPF `SetupWindow` from `/settings`.
- Runtime environment now comes from `SystemState.Mode`; stale runtime data is exposed to Web UI.
- Access, architecture, strategy lifecycle, frontend, .NET build, publish, and launch checks pass.
- The isolated `WPE.Tests` project is discovery-guarded by `eng/test.ps1`. Current safety coverage includes active configuration, Risk Gate, execution-mutation, trading-gateway, auto-trading authorization, unknown-order recovery, secret-redaction, and provider read-only contracts. Historical collection protocols remain a product gap rather than a reason to report these active suites as disabled.
- Migration stage 2A hardened configuration only: corrupt Agent settings fail closed with safe diagnostics, saves are atomic, DPAPI failures remain explicit, environment credential slots do not fall back, Setup synchronizes its Testnet slot and active profile through the store, Binance Testnet REST/WS origins are constrained to official endpoints, and Brain endpoints enforce provider-specific host rules. The two configuration suites are enabled; 33 tests pass.

Known gaps: historical collection APIs for orders, equity, backtests, skill calls, and audit events; legacy Binance-direct services remain alongside the provider architecture.

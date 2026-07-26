# Project State

- Current source: `WPE-` worktree.
- Authoritative product/assembly version: the application project's `<Version>` (`3.6.0` at this milestone). Runtime, plugin compatibility, release reports and packaging read the generated assembly/project version.
- `WPE-Agent-3.8.2-P0-Truth` is a historical validation label, not an assembly version or a distributable release version.
- Mutable runtime data lives under `%LOCALAPPDATA%\WPE Agent\`; the application installation directory is treated as read-only.
- Desktop entry: `币安量化机器人.exe --reference-ui`.
- Setup Bridge opens the existing secure WPF `SetupWindow` from `/settings`.
- Runtime environment now comes from `SystemState.Mode`; stale runtime data is exposed to Web UI.
- Access, architecture, strategy lifecycle, frontend, .NET build, publish, and launch checks pass.
- The isolated `WPE.Tests` project is discovery-guarded by `eng/test.ps1`. Current safety coverage includes active configuration, Risk Gate, execution mutation, trading gateway, auto-trading authorization, unknown-order recovery, secret redaction, provider read-only contracts, and bounded historical collection protocols.
- The bounded `review-post-trade` capability is accepted for deterministic confirmed-close accounting: durable close identity, decimal moving-weighted entry cost, explicit fee provenance, signed funding when unambiguous, explanatory slippage without double charging, strategy identity, and append-only long-term review memory. This does not accept the full Audit Agent or claim broad causal strategy performance.
- Migration stage 2A hardened configuration only: corrupt Agent settings fail closed with safe diagnostics, saves are atomic, DPAPI failures remain explicit, environment credential slots do not fall back, Setup synchronizes its Testnet slot and active profile through the store, Binance Testnet REST/WS origins are constrained to official endpoints, and Brain endpoints enforce provider-specific host rules. The two configuration suites are enabled; 33 tests pass.

Known gaps: target-machine live availability evidence for the official BLS release calendar, protocol/tokenomics/on-chain Fundamental evidence beyond verified Binance Testnet instrument metadata, broader causal post-trade strategy attribution, and fee/funding/fundamental certification for providers other than Binance Futures Testnet. Binance Futures Testnet funding attribution is accepted only for a complete, unambiguous single-side position epoch covered by a successful signed read-only income window; partial reductions, any opposite-side overlap, incomplete/error evidence, legacy fills without exchange timestamps, and non-USDT income remain explicitly unavailable. Provider-neutral intent-versus-fill slippage attribution is persisted, but venue-specific latency and market-impact decomposition remain open. Legacy Binance-direct execution services are excluded from production compilation and guarded by regression tests.

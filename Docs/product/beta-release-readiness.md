# WPE Agent Commercial Beta Support Matrix

This document freezes the first commercial Beta boundary. It does not authorize deployment, Mainnet trading, or distribution.

## Supported Beta Surface

| Area | Beta status | Boundary |
| --- | --- | --- |
| Host OS | Supported | Windows 11 x64 with .NET 8 Desktop Runtime and WebView2 Runtime. `win-x64` is the only release target. |
| Windows 10, ARM64, macOS, Linux | Unsupported | Not certified by this Beta gate. |
| Binance Futures Testnet | Conditional | Catalog, capability probe, Risk Gate and `ReliableOrderExecutor` path are integrated. A credentialed Testnet smoke test is required for each release candidate. |
| OKX, Bybit, Gate.io, Bitget | Catalog only | Official read-only instrument discovery is supported. Trading is not Beta-certified and must remain unavailable until provider-specific Testnet lifecycle certification passes. |
| Mainnet | Disabled | No Mainnet execution, plugin trading, or production credential rollout is permitted. |
| Local deterministic brain | Supported | Trading decisions use the built-in deterministic technical brain. No online/remote LLM is part of the trading product. |
| Hybrid / AI Research / Remote Brain | Retired | Historical designs/configuration may exist only as migration history. They are not runtime capabilities and must not be advertised or reconnected to trading. |
| Plugin Phase 0 | Metadata registry only | Local manifest discovery, compatibility and permission validation are supported. Arbitrary code loading, marketplace installation, hot loading, and Mainnet plugin execution are unsupported. |
| Web UI | Read-only projection | Runtime truth comes from the WPF host. Browser preview exists only in development and is excluded from production artifacts. |
| Team workflows / multi-account operations | Unsupported | No commercial Beta certification yet. |

## Release Candidate Gate

Run from the repository root in a PowerShell process that permits local scripts:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\release-readiness.ps1
```

The gate runs serially: Web lint, typecheck and production build; .NET tests; Release build; local publish; and artifact validation. Results are written to `artifacts/release-readiness/report/`. The gate does not deploy or upload anything.

A candidate is releasable only when the JSON report status is `passed`, the credentialed Binance Testnet smoke test passes in the target environment, and product ownership records an explicit go/no-go decision.

## Known Beta Blockers

- Code signing and installer packaging are not part of this milestone.
- Windows 10 and ARM64 compatibility are not certified.
- Provider-specific Testnet execution certification is incomplete outside Binance Futures Testnet.
- A credentialed Testnet smoke test cannot be automated in source control and remains a per-candidate operational gate.
- Brand name and Logo legal clearance remain separate commercial decisions.

## Rollback Procedure

1. Stop the Agent and confirm no Testnet order operation is in progress.
2. Export or copy `%LOCALAPPDATA%\WPE Agent` to a timestamped backup. Never place it inside the release artifact.
3. Keep the previous verified application directory intact; releases are installed side by side and must not overwrite the previous binary set.
4. Start the previous verified build with Mainnet still disabled and validate local login, runtime snapshot freshness, Risk Gate readiness, and Testnet endpoint allowlists.
5. Reuse the existing `%LOCALAPPDATA%\WPE Agent` data only after backing it up. Do not downgrade or rewrite SQLite files if schema compatibility is unknown.
6. Run the previous build's Testnet access check before starting the Agent. If it fails, remain stopped and restore the backed-up data.
7. Record the failed release report, application version, rollback time, and reason without API keys, secrets, prompts, database contents, or raw request headers.

Rollback never authorizes Mainnet, external upload, database deletion, or credential migration.

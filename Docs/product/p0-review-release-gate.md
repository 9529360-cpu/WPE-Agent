# P0 Review Release Gate

This gate is required before a P0 release candidate can advance. It validates local safety contracts only. It does not publish, deploy, access provider endpoints, or place live or Testnet orders.

## Automated Gate

Run from the repository root after dependencies have already been restored locally:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\test-p0-review.ps1 -Configuration Release
```

The script first verifies that every named test class is discoverable, then runs every class as a separate serial `dotnet test` invocation and stops with a non-zero exit code on the first failure:

- authorization UI and authorization policy/settings boundaries;
- approval service, approval store, and review queue store;
- runtime authorization projection and WPF runtime bridge;
- review execution worker and processor;
- execution gateway, mutation boundary, and Risk Gate;
- unknown-order recovery, fault injection, and idempotency;
- sensitive-data and access-runner redaction.

All test invocations use `--no-restore`; the execution phase also uses `--no-build`. To verify discovery and filter names without executing the filtered suites, add `-ValidateFiltersOnly`.

A passing script result is necessary but not sufficient for release. It proves the checked code contracts on the local machine; it is not evidence of provider availability or an exchange lifecycle test.

## Target-Machine External Gates

The release owner must record these checks separately on the intended Windows 11 x64 target machine:

1. Verify the signed package and installer according to the packaging/signing gate.
2. Confirm .NET 8 Desktop Runtime and WebView2 Runtime prerequisites.
3. Confirm runtime state, authorization mode, and stale/error indicators are sourced from the WPF host.
4. Run the credentialed Binance Futures Testnet access and lifecycle smoke test against official Testnet endpoints only.
5. Confirm Risk Gate readiness, approval audit records, recovery behavior, and redacted diagnostics without recording credentials or raw secret-bearing payloads.
6. Record an explicit release-owner go/no-go decision and retain the local gate result plus target-machine evidence.

These external checks must fail closed when credentials, capabilities, provider state, endpoint validation, or evidence are missing, stale, unknown, unsupported, or erroneous. They must not be run from source-control CI with shared credentials.

## Execution And Provider Boundary

**Mainnet is disabled.** This gate does not authorize Mainnet credentials, Mainnet execution, deployment, upload, or distribution. Optional LLM providers have no execution authority and cannot bypass deterministic authorization, Risk Gate, execution, recovery, or audit services.

The only provider execution claim permitted by this release gate is **conditional Binance Futures Testnet support**, subject to a successful credentialed target-machine smoke test for the exact release candidate. OKX, Bybit, Gate.io, and Bitget may be described only as catalog/read-only discovery surfaces where the corresponding checks pass; their trading paths are not P0 release-certified. Unknown providers or capabilities must be reported as unsupported, never inferred from adapters, catalog metadata, or unit-test success.

Release notes and approval records must state only provider capabilities demonstrated by the automated gate and the recorded target-machine evidence. A failed or missing gate cannot be waived by changing the release claim after the fact.

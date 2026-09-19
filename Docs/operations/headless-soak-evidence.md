# Headless runtime soak evidence

This runbook captures read-only operational evidence from a running WPE Headless service. It does not start, stop, restart, reconfigure, or connect to the trading runtime, and it does not contact an exchange.

## Preconditions

- Use the same absolute `WPE_AGENT_DATA_ROOT` configured for the Windows Service.
- The Headless service is already running in the approved Testnet/Paper configuration.
- The operator output directory is outside `<data-root>\Data`.
- Do not treat the CI harness test as a runtime soak. CI only validates evidence parsing and tamper rejection.

## Capture

For a 24-hour qualification:

    pwsh -NoProfile -File .\eng\ops\Watch-HeadlessSoak.ps1 `
      -DataRoot "D:\WPE-State" `
      -OutputDirectory "D:\WPE-Evidence\soak-24h" `
      -DurationSeconds 86400 `
      -PollSeconds 5 `
      -StartupGraceSeconds 60 `
      -MaximumHealthAgeSeconds 15

For a 48-hour qualification use `-DurationSeconds 172800`.

The watcher reads only `Runtime/headless-health-v1.json`. It persists a sanitized JSONL sample stream and one summary. Samples exclude process id, runtime run id, event sequence, user identity, positions, orders, provider identity, configuration, credentials, and payload data.

A sample is ready only when the process reports `ready`, runtime readiness is true, the Agent is running, access and heartbeat are fresh, and the runtime lease is not lost. Startup grace may contain non-ready samples; after grace, any unavailable, malformed, stale, blocked, failed, lease-lost, non-running, or non-fresh sample makes the evidence fail.

## Verify

    pwsh -NoProfile -File .\eng\ops\Test-HeadlessSoakEvidence.ps1 `
      -EvidencePath "D:\WPE-Evidence\soak-24h\headless-soak-evidence.json" `
      -MinimumDurationMinutes 1440

The verifier checks:

- evidence schema and UTC time ordering;
- requested observation duration;
- minimum sample coverage;
- zero invalid samples;
- the configured unhealthy-sample budget;
- maximum observed health age;
- the SHA-256 of the complete JSONL sample stream;
- exact per-sample field allowlist;
- no lease-loss sample.

The default unhealthy-sample budget is zero. Do not raise it to make a failing candidate pass; if a specific operational policy later permits a bounded maintenance window, that policy must be explicit and separately reviewed.

## Release use

Treat a passing soak artifact as one release input, not as proof of profitability or Mainnet safety. Bind the evidence file hash and source candidate identity in the release record. If the service binary, runtime configuration schema, migration behavior, or candidate package changes, rerun the soak for the new artifact identity.

A 24-hour pass is the minimum operational qualification target for the first long-running Testnet/Paper candidate. A 48-hour pass provides stronger evidence before any later small-capital Mainnet discussion, but does not by itself authorize Mainnet.

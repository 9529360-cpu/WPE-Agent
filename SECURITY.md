# Security Policy

WPE Agent is a safety-sensitive trading application. Security reports involving order authorization, risk controls, exchange adapters, secret storage, update/signing boundaries, or Mainnet isolation should be treated as high priority.

## Reporting

Use GitHub's private vulnerability reporting / Security Advisory flow for this repository when available. Do not place API keys, access tokens, account identifiers, private keys, certificates, or exploit details containing sensitive user data in a public issue.

If a public issue is necessary to establish that a problem exists, keep it minimal and omit secrets, credentials, and operationally sensitive reproduction data.

## Supported Surface

Security fixes target the current `main` branch and the latest supported release candidate where applicable. Historical or quarantined modules that are excluded from production composition are not treated as current runtime authority.

## Safety Invariants

A security fix must not silently weaken:

- Testnet/Mainnet isolation;
- Risk Gate or execution authorization;
- reduce-only recovery;
- encrypted secret storage;
- audit/reconciliation evidence;
- local deterministic trading authority.

Repository governance and required CI remain in force for security fixes. Emergency repository-recovery changes should follow the break-glass procedure in `GOVERNANCE.md`.

# Gate.io and Bitget Testnet/Sandbox Certification

## Current Decision

Gate.io Testnet and Bitget Demo Trading are **read-only discovery surfaces** for the current release. Neither provider is certified for order execution. Mainnet remains disabled.

The adapters, catalog parsers, environment guards, order-state mappings, and offline conformance tests are implementation evidence only. They do not prove that a credential has trading permission, that an order lifecycle succeeds against the exchange, or that a release candidate is fit to execute.

## Fail-Closed Contract

- Gate.io must use the exact HTTPS origin `https://api-testnet.gateapi.io`.
- Bitget Demo Trading must use the exact HTTPS origin `https://api.bitget.com` and the provider must attach Bitget's demo-trading header to private and catalog requests.
- Mainnet profiles, non-HTTPS origins, lookalike hosts, redirects to unapproved origins, missing credentials, and endpoint validation failures cannot enable execution.
- Gate APIv4 and Bitget do not currently provide enough verified key-permission information through this integration to prove trade authority. A successful private account read therefore yields `CanRead=true` and `CanTrade=false`.
- Catalog metadata and adapter methods named `place-order`, `cancel-order`, or `protection-orders` describe implemented surfaces; they are not certification evidence and must not override the permission snapshot.
- `Unknown`, `Stale`, `Unsupported`, or `Error` capability, a missing capability, an expired `CheckedAt`, a symbol/provider mismatch, or `CanTrade=false` blocks execution before any provider mutation.

All future mutations must still pass the trading authorization boundary, deterministic Risk Gate, a fresh provider capability precondition, and `ReliableOrderExecutor`. LLMs, Web UI state, catalog entries, and release notes have no authority to bypass these controls.

## Order And Reconciliation Contract

Provider states are normalized to the executor vocabulary: `NEW`, `PARTIALLY_FILLED`, `FILLED`, `CANCELED`, `EXPIRED`, and `REJECTED`. Every unrecognized order, fill, or protection terminal is normalized to explicit, non-terminal `UNKNOWN`; raw provider status text must never become an executable state or map to `FILLED` by default.

After a timeout, transport error, restart, or ambiguous submission result, recovery queries the provider by the original client order ID. A missing or unknown result remains `UNKNOWN`, sets `SafeToIncreaseRisk=false`, and does not submit a replacement order. Partial fills remain partial, and any protective or emergency action continues through `ReliableOrderExecutor` with the same correlation and audit chain.

## Certification Evidence Required

Execution can be reconsidered separately for each provider only when all of the following evidence exists for the exact release candidate and target Windows machine:

1. Dedicated least-privilege Testnet/Demo credentials, with withdrawal disabled and trade permission proved rather than inferred.
2. Official endpoint and server-time checks, a fresh market capability probe, and an explicit supported-symbol result.
3. Credentialed lifecycle evidence for submit, query by client order ID, partial fill, fill, cancel, reject/expire, timeout, reconnect, and restart reconciliation.
4. Risk Gate decision, approval when required, `ReliableOrderExecutor` trace, provider request correlation, local persistence, and redacted audit records linked end to end.
5. Fault-injection proof that ambiguous or unknown states do not duplicate orders or increase risk.
6. A release-owner sign-off naming the provider, environment, release version, artifact hash, test time, account class, limitations, and retained evidence location.

These checks are external gates. They must use locally injected credentials, must not run in shared source-control CI, and must never persist API secrets or raw secret-bearing payloads.

## Offline Fault-Injection Boundary

The provider contract is tested with an in-memory HTTP handler and no external connection. HTTP `429`, timeouts, malformed JSON, partial account payloads, and credential failures must produce an unavailable health result. Diagnostics must redact authorization values, API keys, secrets, and passphrases. Fault handling may issue read-only retries, but it must not issue `POST`, `PUT`, `PATCH`, or `DELETE` requests.

Duplicate and out-of-order order/fill pages are merged by stable provider order identity, with the newest observed event winning. An older `FILLED` event cannot overwrite a newer `UNKNOWN` observation. Anonymous events without an order ID or client order ID are retained separately rather than being incorrectly collapsed. Any unresolved terminal remains `UNKNOWN`, keeps risk increase disabled, and cannot trigger cancel, protection, replacement, or another provider mutation.

## Canonical Signing Contract

Gate.io and Bitget signing inputs use provider-specific fixed canonical forms. Query parameters are sorted by ordinal key, names and values are URL encoded, null values are omitted, and duplicate or empty parameter names are rejected. HTTP methods are normalized only from the allowlisted `GET`, `POST`, and `DELETE` methods; invalid methods, absolute targets, whitespace-bearing paths, duplicate query delimiters, and malformed timestamps fail before a request is sent. Decimal and timestamp formatting use invariant culture.

Gate.io signs `METHOD + path + canonical query + SHA-512(body) + seconds timestamp` with HMAC-SHA512. Bitget signs `milliseconds timestamp + METHOD + path/query + body` with HMAC-SHA256 and Base64 output. The exact body bytes participate in each signature. Timestamp checks use a five-second receive window and reject malformed, stale, or future values. Each authentication header is added once; attempts to add duplicate key, timestamp, signature, or passphrase headers fail closed.

Offline fixed vectors and fake-handler request captures verify method, path, query, body, timestamp, URL encoding, header uniqueness, and culture independence without contacting an exchange. Diagnostics and fault payloads must redact API keys, secrets, passphrases, authorization values, and signatures. No fixed vector contains a real credential.

## Release Claims

Until the provider-specific evidence above passes, permitted wording is:

> Gate.io Testnet and Bitget Demo Trading catalog/read-only discovery are implemented with fail-closed environment, permission, capability, and recovery contracts. Trading is not certified or enabled. Mainnet is disabled.

Do not claim Gate.io or Bitget Testnet trading support, multi-exchange execution coverage, production readiness, Mainnet support, or successful live order certification from offline unit tests, adapter presence, catalog output, screenshots, or documentation alone. A failed, stale, missing, or unknown certification result is a failed release gate, not a conditional execution approval.

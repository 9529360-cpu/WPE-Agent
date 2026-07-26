# OKX and Bybit Testnet Certification Contract

## Scope and claim boundary

This document freezes the current OKX Demo Trading and Bybit Testnet certification matrix. It is a source-contract and offline-conformance record, not evidence that either provider has completed a live sandbox order lifecycle.

- Mainnet remains disabled.
- Unknown, stale, unsupported, permission-error, catalog-error, or environment-error states block execution.
- A catalog entry, adapter implementation, or offline test is not a real provider certification.
- Provider mutation remains behind deterministic Risk Gate approval and `ReliableOrderExecutor`; recovery queries by client order id and never resubmits an ambiguous order merely because local state is incomplete.
- A real certification claim requires external, credentialed, auditable sandbox evidence from the target environment.

## Frozen matrix

| Area | OKX Demo Trading | Bybit Testnet | Current certification claim |
| --- | --- | --- | --- |
| Environment | Testnet profile; exact HTTPS origin `https://www.okx.com`; simulated-trading header used by the adapter | Testnet profile; exact HTTPS origin `https://api-testnet.bybit.com` | Offline contract only; Mainnet and untrusted origins fail closed |
| Credentials | API key, secret, passphrase | API key, secret | Shape implemented; credential validity is `Unknown` until an external check succeeds |
| Permissions | Account config is parsed for read, trade, and withdraw; withdraw produces a warning | API metadata is parsed; missing/unknown/read-only values do not grant trade; withdraw is false | `Unknown`; must be captured from a real sandbox account, with withdrawal disabled |
| Instrument/catalog | Perpetual/SWAP catalog parser and capability projection implemented | Linear perpetual catalog parser, pagination guard, and capability projection implemented | Offline conformance only; live catalog availability/freshness is `Unknown` |
| Place/cancel | Market, limit, protection, and cancel methods implemented | Market, limit, position TP/SL, and cancel methods implemented | `Unknown`; implementation is not a successful live order proof |
| Query | Open orders and lookup by client order id implemented | Open orders and lookup by client order id implemented | Offline contract only; real response mapping remains `Unknown` |
| Recent order history | No `IRecentOrderProvider` implementation | No `IRecentOrderProvider` implementation | `Unsupported` |
| Reconcile | Shared `ReliableOrderExecutor` queries `FindOrderAsync` and returns succeeded/not-submitted/unknown/failed without mutation in the reconcile path | Same shared contract | Offline conformance only; full live lifecycle is `Unknown` |
| Real sandbox certification | No evidence recorded by this contract | No evidence recorded by this contract | `Unknown`, not certified |

## Execution gate

An OKX or Bybit order is eligible for submission only when all of the following are current and affirmative:

1. The selected profile is Testnet and its exact official origin passes the environment guard.
2. The discovered instrument maps to the requested canonical symbol and its catalog/capability state is available and fresh.
3. Read and trade permissions are positively observed; missing or ambiguous permission data does not grant trade.
4. The intent has passed deterministic evidence, authorization, Risk Gate, and capability checks.
5. The mutation is performed by `ReliableOrderExecutor`, with an auditable correlation/client order id.

Failure at any step stops before provider mutation. Reconciliation uses the existing client order id. Query failure or conflicting state becomes `Unknown`/manual-required and must not cause duplicate submission.

## Evidence required to change `Unknown`

Each provider must be certified separately on a target release candidate. The evidence bundle must contain redacted timestamps and correlation identifiers for:

1. Official endpoint and server-time check.
2. Credential validation and least-privilege permission snapshot, including withdrawal disabled.
3. Catalog discovery plus canonical/native instrument mapping for the tested perpetual.
4. Risk-approved minimal order submission through `ReliableOrderExecutor` only.
5. Query by the same client order id, observed status transitions, and cancel or reduce-only cleanup.
6. Ambiguous-response recovery showing reconcile without duplicate mutation.
7. Audit records linking environment, permission, capability, risk receipt, approval, execution, query, reconcile, and cleanup.

Until that bundle exists, release notes and UI must say `Unsupported` or `Unknown` as listed above and must not say OKX or Bybit is certified for real Testnet trading.

## Automated contract evidence

`WPE.Tests/OkxBybitCertificationContractTests.cs` freezes the source-level matrix without network calls. It verifies descriptors, credential shape, official-origin guards, catalog/order/query interfaces, shared reconcile ownership, the missing recent-order interface, and the explicit non-certification state.

## Offline failure-injection contract

Phase 3 exercises provider HTTP and payload handling through an in-memory handler; it never opens a network connection or submits an order.

- HTTP 429, timeout, and transport failures become `UNAVAILABLE`.
- Malformed JSON, missing required fields, partial payloads, and unknown response shapes become `UNKNOWN` or an error-state catalog with an `UNKNOWN` diagnostic.
- Mixed item-level success is a failure when any OKX `sCode` or Bybit item code is non-zero.
- Duplicate or out-of-order order observations are reduced monotonically: terminal states win, then `UNKNOWN`, then partial, then new. An older or repeated event cannot reopen a terminal order.
- Provider and item error messages are redacted before they enter diagnostics; API keys, bearer tokens, signatures, and credential-bearing URLs must not survive.
- Bybit synthetic `position-tpsl:*` cancellation remains `Unsupported`; it must never be reported as a successful cancel.

These checks remain offline conformance evidence only. They do not change either provider's real sandbox certification from `Unknown`.

## Signing canonicalization contract

Phase 4 freezes signing behavior with fixed public test vectors and fake HTTP handlers. No vector contains a real credential and no request leaves the process.

- OKX signs `UTC timestamp + uppercase method + canonical path/query + canonical body` with HMAC-SHA256/Base64.
- Bybit V5 signs `Unix milliseconds + API key + receive window + canonical query-or-body` with HMAC-SHA256/lowercase hex. Method and path are validated but, per the V5 signing format, are not included in the HMAC payload.
- Query keys and object body keys are ordinally sorted and percent encoded where applicable. Duplicate keys are rejected case-insensitively.
- JSON numbers, receive windows, timestamps, and provider-generated numeric parameters use `InvariantCulture`.
- OKX timestamps must be millisecond UTC values ending in `Z`; Bybit timestamps must be positive Unix milliseconds.
- Absolute paths, embedded query strings, fragments, control characters, unsupported methods, invalid receive windows, empty signing credentials, and duplicate authentication headers fail closed.
- Header and validation exceptions never include credential values. Fake handlers assert exactly one value for every authentication header.
- SHA-256 body hashes are recorded as deterministic vector evidence; neither provider substitutes that audit hash for the exchange-specific HMAC input.

Passing these signing vectors proves deterministic offline canonicalization only. It is not proof that exchange credentials or sandbox order execution work.

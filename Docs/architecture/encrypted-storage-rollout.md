# Encrypted Storage Rollout

## Status and boundary

This document defines a contract-first rollout. It does not claim that any current SQLite database is encrypted, does not migrate an existing database, and does not handle a real secret. Current production truth remains: local credentials use Windows DPAPI CurrentUser protection, while SQLite full-database encryption is not implemented.

The rollout preserves local-first operation. It does not change provider capability, trading authorization, Mainnet status, Risk Gate, `ReliableOrderExecutor`, or the audit correlation chain. Mainnet remains disabled and Testnet/sandbox execution remains fail closed.

## Key lifecycle

Key material is generated locally and is never written to configuration, logs, backup manifests, UI snapshots, or audit payloads. `IPlatformStorageKeyStore` is the boundary for Windows DPAPI CurrentUser and future platform keystores. A production implementation must bind protected material to the current OS user and application purpose.

The lifecycle is `Pending -> Active -> RotationPending -> Retired`. `RecoveryOnly` permits an explicitly authorized offline restore but never opens the active store. `Revoked` is terminal and cannot decrypt an active store or restore a backup. Rotation creates a new version before rekeying, verifies every database, retires the prior version only after verification, and retains it solely according to the recovery retention policy. Missing, ambiguous, revoked, or unreadable keys block startup; no empty key or plaintext fallback is allowed.

## Backup manifest

Every encrypted backup has an authenticated manifest containing format version, backup ID, UTC creation time, key ID and version, cipher suite, logical filenames, ciphertext lengths, and SHA-256 hashes. It contains no key material, connection strings, API credentials, provider payloads, or plaintext database metadata.

Restore is allowed only into an empty staging target when the manifest is authenticated, the format and cipher suite are supported, every ciphertext hash matches, and the exact recovery key is available in an allowed state. Restore verification opens the staged encrypted databases, runs integrity checks and schema checks, and records a redacted audit result before an atomic cutover. Any unknown or failed check leaves the current store untouched.

## Migration phases

1. **Inventory:** enumerate all SQLite database, WAL, and SHM files under the application data root; stop writers and verify ownership. No file is modified.
2. **Provider qualification:** select and license a SQLite encryption provider, pin its version, verify platform support, and prove that static database files do not expose seeded plaintext. No provider is considered supported before this gate passes.
3. **Key provisioning:** create the first platform-protected key, verify round-trip access under the intended OS account, and record only key metadata.
4. **Pre-migration backup:** create and independently restore an encrypted backup in an isolated staging directory. Migration cannot start without this evidence.
5. **Staged migration:** use SQLite-native export/rekey facilities into new files. Never encrypt a live file in place. Preserve the source files until database integrity, schema, row-count, audit-chain, WAL, and restart checks pass.
6. **Cutover:** atomically replace the database set while writers are stopped. A failed cutover restores the prior set; it never starts against plaintext or partially migrated files.
7. **Rotation rehearsal:** rotate to a new key version, verify active-store access and backup restore, then retire the prior version according to policy.

## Fail-closed startup and recovery

Encrypted-store startup requires a qualified cipher provider, a valid active key, migration state `Complete`, and no detected plaintext database. A process encountering plaintext after activation exits the storage initialization path with a stable reason code and must not rename, delete, or import the file automatically.

Recovery is an explicit operator workflow. It must preserve the failed files for forensic review, use an empty staging directory, produce correlated redacted audit events, and require verification before cutover. Emergency recovery does not enable Mainnet, bypass Risk Gate, alter orders, or call a provider.

## Migration dry-run contract

`CreateDryRunPlan` is a pure, read-only policy evaluation. It does not open a database, load protected key material, stop a writer, create a backup, rename a file, or update migration state. Callers supply synthetic or separately collected evidence and receive an ordered list of checks with stable reason codes.

The evaluation order is keystore availability, stable rotation state, migration prerequisites, authenticated backup integrity, stopped writers, preserved source set, verified source set, and rollback-key availability. All checks are returned for auditability, while the first failed check is the plan result. A plan is executable only when every check passes.

The following conditions fail closed:

- The platform keystore is unavailable or cannot supply metadata for the active key.
- Rotation stopped after new-key creation, during rekey, during verification, or in an explicitly interrupted state. Operators must reconcile the key journal and database generation before creating another plan.
- The backup manifest authentication fails, its canonical SHA-256 changes, any ciphertext hash changes, or its recovery key does not match.
- Writers are active, the pre-migration source set is missing or unverified, or the rollback key is unavailable.

Rollback is a separate decision, never an automatic side effect of dry-run or startup. It is rejected before migration starts, after a completed migration or completed rotation, when the preserved source set lacks verification evidence, when the rollback key is missing/revoked/in transition, or when the backup cannot independently pass restore verification. Rejection leaves all source, staging, and backup files untouched for forensic review.

## Acceptance evidence for later implementation

- Static-file inspection cannot recover seeded plaintext from the database, WAL, SHM, or backup payload.
- Missing, corrupt, wrong-version, retired-for-active-use, and revoked keys all fail closed with no plaintext fallback.
- Crash tests at every migration and rotation checkpoint preserve either the verified old set or the verified new set.
- Backup tampering, manifest tampering, unsupported format/cipher, and non-empty restore targets are rejected.
- Restore drills reproduce schema, row counts, integrity results, and audit-chain continuity without exposing key material.
- Logs, diagnostics, manifests, runtime snapshots, and tests contain synthetic identifiers only and pass secret-redaction checks.

Until all evidence is present, product and UI wording must continue to state that SQLite full-database encryption and encrypted backup recovery are not implemented.

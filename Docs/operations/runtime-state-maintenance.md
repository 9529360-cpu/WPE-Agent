# Runtime-state maintenance

WPE.Maintenance is an offline Windows-only maintenance executable for WPE runtime-state backup, verification, and restore. It does not start the trading runtime, connect to an exchange, change authorization mode, or expose a network listener.

## Preconditions

- Run under the same Windows user that owns the WPE state and DPAPI material.
- Use an explicit absolute WPE data root.
- Stop every WPE desktop/headless process before backup or restore. When WPE runs as a Windows Service, stop `WPE Agent Headless` and wait for the process to exit before maintenance.
- Keep backup output outside the active Data directory.
- Treat backup files as sensitive encrypted state even though the payload is encrypted.

The backup and restore commands acquire the exclusive data-root maintenance lease. If any normal WPE process still owns the shared lease, the operation fails closed. The Windows Service and the maintenance CLI must point at the same absolute root: configure the service with `WPE_AGENT_DATA_ROOT`, then pass that exact path as `--data-root` to maintenance.

Because backup encryption and restore journals use Windows current-user DPAPI, run the service and maintenance CLI under the same Windows account. Moving only the data directory to another account is not a supported recovery path.

## Backup

    WPE.Maintenance.exe backup --data-root "$env:LOCALAPPDATA\WPE Agent" --destination "D:\WPE-Backups"

A successful command prints one JSON object containing the authenticated backup id, directory, item count, inventory SHA-256, and creation time. It never prints credentials or decrypted record contents.

## Verify

    WPE.Maintenance.exe verify --data-root "$env:LOCALAPPDATA\WPE Agent" --backup "D:\WPE-Backups\wpe-state-..."

Verification checks product/device identity, encrypted manifest attestation, exact encrypted-file inventory, ciphertext hashes, plaintext item hashes, and SQLite integrity. Plaintext is reconstructed only into a temporary staging directory and removed when the command ends.

Use the authenticated backupId returned by this command for restore confirmation.

## Restore

    WPE.Maintenance.exe restore --data-root "$env:LOCALAPPDATA\WPE Agent" --backup "D:\WPE-Backups\wpe-state-..." --confirm-backup-id "wpe-state-..."

The confirmation value is compared only after cryptographic backup verification and before any safety backup or data-generation swap. A mismatch leaves the active generation untouched.

When authoritative state already exists, restore creates an encrypted safety checkpoint first. It then performs the authenticated generation swap under the exclusive lease, re-verifies the activated generation, commits security-storage restore evidence when encrypted records are present, and only then marks the restore committed.

An interrupted uncommitted swap is recovered before normal process bootstrap. A committed restore is kept; an uncommitted restore returns to the original generation.

## Exit codes

- 0: success.
- 2: invalid command or arguments.
- 3: unsupported platform.
- 4: maintenance operation rejected or failed.

Output is JSON and contains no stack trace. Failure does not imply the active data generation was changed; restore mutation begins only after backup verification, explicit id confirmation, and exclusive maintenance ownership.

## Windows Service maintenance sequence

1. Confirm the service account and the absolute `WPE_AGENT_DATA_ROOT` value.
2. Request graceful shutdown with `WPE-Headless.exe control shutdown --confirm shutdown-wpe-headless`, then confirm the Windows Service/process has exited. Windows Service Control Manager remains the start/restart authority.
3. Run `verify` on the intended backup. Record the authenticated `backupId` from the JSON result.
4. Run `backup` if a fresh pre-maintenance checkpoint is required.
5. Run `restore` only with the exact verified `--confirm-backup-id`.
6. Start `WPE Agent Headless` again. Startup recovery runs before the shared process lease is acquired; if an interrupted restore cannot be proven recoverable, startup fails closed instead of starting trading.
7. Run `WPE-Headless.exe control health` under the same service account and require a successful fresh `ready` response with runtime ready, Agent running, access/heartbeat fresh, and no lease loss before treating the runtime as operational.

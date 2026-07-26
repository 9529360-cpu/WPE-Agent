# P0 Package Readiness

This gate validates an already-built Release candidate without restoring dependencies, rebuilding, rerunning tests, launching WPE Agent, contacting an exchange, or using the network.

## Candidate inputs

- Authoritative version: the single `<Version>` in the application `.csproj`.
- Release build: optional supporting evidence, checked only when an explicit `-BuildPath` is supplied.
- Release publish: a fresh explicit staging directory passed to both `publish.ps1 -Output` and `verify-p0-package.ps1 -PublishPath`.
- Web export: `WebUi/out/index.html`, copied into both candidate artifacts at `WebUi/out/index.html`.
- Safety invariants: writable data rooted at `%LOCALAPPDATA%\WPE Agent`, startup and runtime Mainnet rejection, and Setup forced back to Testnet.

Run from the repository root:

```powershell
powershell.exe -NoProfile -File .\eng\verify-p0-package.ps1 -DryRun
powershell.exe -NoProfile -File .\publish.ps1 -Configuration Release -Runtime win-x64 -Output artifacts/p0-rc/<unique-rc-name> -InternalUnsignedRc
powershell.exe -NoProfile -File .\eng\verify-p0-package.ps1 -PublishPath artifacts/p0-rc/<unique-rc-name>
```

The staging path must be new and unique; never overwrite a historical package. `-InternalUnsignedRc` generates `INTERNAL-RC-METADATA.json` and `FILE-MANIFEST.json`, and explicitly records `unsigned`, `distributable: false`, and `commercialReleaseApproved: false`. Paths outside the repository are rejected. The verifier is read-only and emits its result as JSON on stdout.

An internal unsigned RC is suitable only for controlled evaluation. It is not a commercially distributable release and must not be relabeled when no signing certificate is available.

## Required RC evidence

- The exact commit SHA and a clean or fully explained working-tree status.
- The application project version and SHA-256 hashes for the final distributable and published executable.
- A passing `eng/test-p0-review.ps1` result from the same commit and Release build inputs. The package verifier deliberately does not rerun this test gate.
- A passing `eng/verify-p0-package.ps1` JSON result showing `offline: true`, version `3.6.0`, embedded Web export, LocalAppData storage, Mainnet disabled, zero forbidden files, verified manifest, and the true signature/distribution state.
- The release-readiness, license, SBOM, signing, and package verification reports required by the selected distribution channel.
- A reviewer record confirming that no API keys, `.env` files, private keys, databases, SQLite sidecars, logs, source files, test payloads, or local settings entered the package.

## Target-machine manual checks

Use a clean supported Windows x64 machine or VM with networking disabled for the first launch. Do not enter real credentials or enable live trading.

1. Verify the downloaded package hash and Authenticode signature against the approved RC evidence.
2. Install or extract to a non-admin location, launch the reference UI, and confirm the packaged Web UI renders without a development server.
3. Confirm the displayed version matches the project, package metadata, and executable version.
4. Confirm first launch creates mutable state only under `%LOCALAPPDATA%\WPE Agent`; the installation directory remains unchanged and writable operation does not require elevation.
5. Confirm Setup remains on Testnet and selecting Mainnet is refused. Leave execution disabled and do not perform an exchange access check while offline.
6. Restart the application and confirm local settings, logs, database, runtime, backup, and export directories remain under LocalAppData.
7. Uninstall or remove the candidate and confirm the installation payload is removed. Treat LocalAppData retention as user data and record whether the release channel preserves or explicitly removes it.

Networked Testnet smoke testing is a separate, explicitly authorized release activity. It is not part of this offline package gate.

## Failure and rollback

- Any version mismatch, missing Web export, unexpected executable count, installation-directory write, Mainnet-enabled path, forbidden file, invalid signature, or missing evidence blocks the RC. Do not waive the gate by editing the artifact in place.
- Quarantine the candidate and its hashes, preserve the failing verifier output, and identify the producing commit and build job.
- Roll back to the last signed candidate that passed the same P0 review, package, license, and target-machine gates. Never reuse a version or overwrite an already published artifact.
- Fix the source or packaging pipeline, produce a clean Release build and publish output, rerun the existing P0 test gate once, then run this package verifier against the new artifacts.
- If an artifact may contain a credential or private key, stop distribution, revoke and rotate the credential, remove public downloads, and record the incident before producing a replacement candidate.

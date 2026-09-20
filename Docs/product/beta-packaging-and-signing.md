# WPE Agent Beta Packaging and Signing

This process creates an offline, side-by-side portable Beta package. It does not deploy, upload, purchase a certificate, enable Mainnet, or claim installer certification.

## Current Package Boundary

- Format: versioned `win-x64` portable ZIP containing the exact source-bound `app/`, `headless/`, and `maintenance/` runtime artifacts accepted by `eng/release-readiness.ps1`. Runtime roots are source/debug/state/secret free before packaging.
- Certified host: Windows 11 x64 with .NET 8 Desktop Runtime and Microsoft Edge WebView2 Runtime.
- Windows 10 x64: technically plausible for `net8.0-windows`, but untested and unsupported until a clean Windows 10 compatibility matrix passes.
- Windows ARM64: unsupported. The application, publish runtime, WebView2 loader, SQLite and graphics native assets are currently `win-x64`; renaming the package is not an ARM64 build.
- MSIX/WiX: deferred. A portable package has the smallest tool and rollback surface and does not require untrusted downloads. It also makes no Start Menu, upgrade, repair or uninstall claims.
- Installer filesystem status: production settings, DPAPI ciphertext containers, SQLite/WAL, logs, backups, exports and runtime state now resolve under `%LOCALAPPDATA%\WPE Agent`; the installation directory is treated as read-only.
- First-run portable migration copies only allowlisted user state from the old `Data` directory. It is atomic and idempotent, never overwrites conflicts, never deletes the source, and excludes `API.txt`, plaintext keys, logs and unknown files. Its redacted report is stored in `%LOCALAPPDATA%\WPE Agent\Runtime`.
- Trading: Testnet-only. Packaging does not certify additional exchange execution paths or enable Mainnet.

The audited build host has .NET SDK 8.0.423 and PowerShell `Compress-Archive`, but no Windows SDK, SignTool, MakeAppx, WiX or Visual Studio installation. A local self-signed certificate is not a commercial publisher identity and is deliberately ignored. These facts make portable ZIP the only currently available package format; signing and installer production remain blocked until trusted tooling and credentials are provisioned.

## Build and Package

Run the release gate first, then package its verified output:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\release-readiness.ps1 -Runtime win-x64
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\package-beta.ps1 -Runtime win-x64
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\verify-beta-package.ps1
```

The output directory contains the portable ZIP, `SHA256SUMS`, and `package-result.json`. Inside the ZIP, `app/`, `headless/`, and `maintenance/` are the three runtime roots; `RELEASE-METADATA.json` records version/channel/architecture, source identity, runtime tree hashes and signing provenance; `RELEASE-READINESS.json` preserves the exact readiness report; a signed bundle also contains `SIGNING-RESULT.json` plus the same-certificate detached `SIGNING-RESULT.p7s`; `FILE-MANIFEST.json` and `PAYLOAD-SHA256SUMS` cover every file under all three runtime roots using package-root-relative paths; `sbom.cdx.json` is a CycloneDX 1.5 inventory generated from the Desktop, Headless, Maintenance NuGet graphs plus the pnpm production dependency graph. Unknown licenses remain `NOASSERTION`; the script does not guess.

An unsigned package is permitted only when all three executables are NotSigned and all three runtime trees still exactly match release-readiness bytes; it is internal-evaluation-only and non-distributable. A signed package requires all three executables to be Valid, one approved signer subject/thumbprint, a detached CMS signature authenticating the readiness-to-signing transition record and runtime-tree hashes, clean source, and no mixed signing state.

## Production Code-Signing Requirements

Use an organization-validated or extended-validation Authenticode code-signing certificate issued to the final legal publisher. The certificate must:

- include the Code Signing EKU;
- chain to a root trusted by supported Windows hosts;
- match the final product publisher/legal entity;
- be valid at signing time and support an RFC 3161 SHA-256 timestamp;
- be stored in controlled signing infrastructure, ideally a hardware token, HSM, or managed signing service.

Do not create a self-signed certificate and present it as a production signature. Do not commit a PFX or password. Provision the approved certificate through the Windows Current User certificate store backed by controlled key storage or an HSM. The helper rejects self-signed, expired, wrong-subject, missing-private-key and missing-Code-Signing-EKU certificates, then selects it by thumbprint without putting a PFX password in process arguments:

```powershell
$ProductVersion = ([xml](Get-Content -Raw .\币安量化机器人.csproj)).Project.PropertyGroup.Version
$SigningRoot = ".\artifacts\signing-staging\$ProductVersion"
Copy-Item .\artifacts\release-readiness\publish "$SigningRoot\desktop" -Recurse
Copy-Item .\artifacts\release-readiness\headless "$SigningRoot\headless" -Recurse
Copy-Item .\artifacts\release-readiness\maintenance "$SigningRoot\maintenance" -Recurse
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\sign-beta.ps1 `
  -PublishPath "$SigningRoot\desktop" `
  -AdditionalPublishPaths @("$SigningRoot\headless", "$SigningRoot\maintenance") `
  -ReadinessReportPath ".\artifacts\release-readiness\report\release-readiness.json" `
  -SigningResultPath "$SigningRoot\signing-result.json" `
  -CertificateThumbprint '<40 hex characters>' `
  -ExpectedSubject 'CN=<approved legal publisher>'

powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\package-beta.ps1 `
  -PublishPath "$SigningRoot\desktop" `
  -HeadlessPublishPath "$SigningRoot\headless" `
  -MaintenancePublishPath "$SigningRoot\maintenance" `
  -SigningResultPath "$SigningRoot\signing-result.json" `
  -ExpectedSignerSubject 'CN=<approved legal publisher>' `
  -ExpectedSignerThumbprint '<40 hex characters>'

powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\verify-beta-package.ps1 `
  -ExpectedSignerSubject 'CN=<approved legal publisher>' `
  -ExpectedSignerThumbprint '<40 hex characters>'
```

Before distribution, independently verify all three executable signatures and timestamps with `signtool verify /pa /all /v`, run `verify-beta-package.ps1` with the approved signer identity so it verifies `SIGNING-RESULT.p7s` before trusting runtime-tree hashes, verify the ZIP against `SHA256SUMS`, and record a release go/no-go decision. The signing and packaging scripts do not upload the artifact.

The signing helper preflights every staging root before the first signature mutation. If signing or verification fails after mutation has begun, discard the entire signing staging tree and recreate it from the source-bound release-readiness artifacts; do not promote or package a partially signed tree. In bundle mode, `signing-result.json` binds the readiness-report SHA-256 and exact source commit to each pre-sign tree hash and verified post-sign tree/executable hash. `sign-beta.ps1` also writes `signing-result.p7s`, a detached SHA-256 CMS signature over the exact JSON bytes using the same approved publisher certificate. Consumers must verify that detached signature before trusting the recorded runtime-tree hashes; the executable Authenticode signatures alone do not authenticate neighboring DLL/resource bytes.

## SmartScreen

Authenticode establishes publisher identity and file integrity; it does not guarantee immediate Microsoft Defender SmartScreen reputation. New certificates and low-download binaries can still show warnings. Do not bypass SmartScreen, weaken endpoint protection, or tell customers to ignore warnings. Build reputation through stable publisher identity, timestamped releases, consistent versioning, HTTPS distribution from an owned domain, malware scanning, and low false-positive rates. EV certificates can improve identity assurance but do not guarantee warning-free launch.

## Install, Uninstall, and Rollback

Portable install:

1. Verify the ZIP SHA-256, `FILE-MANIFEST.json`, and all three Authenticode signatures for a signed bundle.
2. Extract to a versioned directory named from `RELEASE-METADATA.json.packageVersion` without overwriting an existing version.
3. Keep `app/`, `headless/`, and `maintenance/` under that immutable version directory; the package does not automatically register or change a Windows Service.
4. Confirm .NET 8 Desktop Runtime and WebView2 Runtime are installed for Desktop use.
5. Start the approved Desktop or Headless entry point with Mainnet disabled and the intended absolute data root; run access/readiness checks before starting the Agent.

Portable uninstall removes only that versioned application directory after the Agent is stopped. User data, encrypted credentials, databases and logs must be identified and backed up separately; the release package intentionally contains none of them.

Rollback:

1. Stop the Agent and confirm no Testnet order operation is active.
2. Back up local data outside all package directories.
3. Start the previous verified side-by-side version with Mainnet still disabled.
4. Re-run runtime freshness, Risk Gate, endpoint allowlist and Testnet access checks.
5. Never downgrade or rewrite SQLite data when schema compatibility is unknown.

## Remaining Distribution Blockers

- Final legal product name, publisher identity and trademark clearance.
- Trusted commercial Authenticode certificate and controlled signing infrastructure.
- Clean-source reproducible release candidate and explicit product go/no-go.
- Credentialed Binance Futures Testnet smoke test on the target machine.
- Windows 10 x64 certification if it will be marketed; ARM64-native publish and dependency certification if ARM64 will be supported.
- Installer choice, upgrade/repair/uninstall tests and publisher identity continuity if MSIX or WiX is introduced later.
- Legal review of every `NOASSERTION` SBOM component and generation of final third-party notices.

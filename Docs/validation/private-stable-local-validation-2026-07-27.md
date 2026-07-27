# Private Stable Local Validation - 2026-07-27

This validation records the private-use Windows x64 stability boundary. It does not authorize commercial distribution or Mainnet trading.

## Result

- Source: clean `main` at `b0dbb74a9107`.
- Product version: `3.6.0`.
- Release-readiness: passed all eight serial stages in 238.293 seconds.
- Web: lint, typecheck, and production build passed; 17 static routes generated.
- .NET: `1862/1862` Release tests passed; Release build completed with zero warnings and zero errors.
- Publish: 212 files; secret scan passed; no production preview data; Mainnet disabled.
- Portable ZIP: `WPE-Agent-3.6.0-beta.1-b0dbb74a9107-win-x64-portable.zip`.
- Portable ZIP SHA-256: `125153fddf9084d650c74501db91e38331cd5908649fb71e1d615f6df3b31dab`.
- Independent package verification: passed with zero manifest failures and zero forbidden files.
- Release-slot tests: install, startup, restart, rollback preflight, atomic switch, Mainnet rejection, mutation-default denial, and user-data separation passed.
- Real WPF process smoke: the extracted portable executable remained alive for eight seconds on both first start and restart, then only the validation-owned process was stopped.

## Boundary

- Suitable for continued private local evaluation on this validated Windows x64 machine.
- The package is unsigned and is not approved for commercial distribution.
- No credentialed Testnet mutation was performed in this validation.
- Remote CI, repository branch protection, commercial licensing, signing, installer certification, equities, broad cross-asset data, and commercial data redistribution remain optional future work and do not block this private-use result.
- Mainnet remains disabled.

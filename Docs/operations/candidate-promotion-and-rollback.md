# Candidate promotion and rollback

This runbook promotes one already-verified WPE Testnet candidate into the existing side-by-side dogfood slot model. It does not build a second release system and it does not authorize Mainnet.

## Release topology

`verified package -> immutable candidate directory -> candidate-bound target-machine soak -> trusted runtime proof -> install/start/restart/rollback probes -> atomic current.json switch`

The candidate and last-known-good directories remain immutable. Promotion changes only the operator pointer after every gate passes.

## 1. Create the immutable candidate

Use the verified package directory that already contains `RELEASE-METADATA.json`, `FILE-MANIFEST.json`, and the passed package-verification result.

    pwsh -NoProfile -File .\eng\dogfood\New-Candidate.ps1 `
      -SourceRoot "<verified-package-root>" `
      -SlotsRoot "<slots-root>" `
      -Version "<candidate-version>" `
      -SourceIdentity "commit/<exact-source-sha>" `
      -ConfigurationSchema "<configuration-schema>" `
      -MigrationVersion "<migration-version>" `
      -Gates @('build','tests','package') `
      -ExpectedSourceManifestHash "<file-manifest-sha256>" `
      -PackageVerificationPath "<verification-result.json>" `
      -ExpectedPackageVerificationHash "<verification-result-sha256>"

Record the returned candidate `release-manifest.json` SHA-256. That hash identifies the exact staged bytes.

## 2. Run target-machine soak

Run `eng/ops/Watch-HeadlessSoak.ps1` against the exact candidate source identity and candidate release-manifest SHA-256. The first operational qualification target is 24 hours.

    pwsh -NoProfile -File .\eng\ops\Watch-HeadlessSoak.ps1 `
      -DataRoot "<absolute-service-data-root>" `
      -OutputDirectory "<external-evidence-root>" `
      -SourceIdentity "commit/<exact-source-sha>" `
      -CandidateManifestSha256 "<candidate-release-manifest-sha256>" `
      -DurationSeconds 86400

Verify the produced evidence independently with `eng/ops/Test-HeadlessSoakEvidence.ps1`. Preserve both the evidence-file SHA-256 and the JSONL sample-stream SHA-256.

The release validator does not execute a package-local probe script and does not control Windows SCM. Install/start/restart/rollback actions remain target-machine/operator responsibilities. Their results must be captured by the trusted runtime-proof producer and bound to the candidate and last-known-good manifest hashes before promotion.

## 3. Validate and switch the slot

`Switch-DogfoodSlot.ps1` delegates to `Test-DogfoodRelease.ps1`. Promotion requires all of the following together:

- exact candidate and last-known-good manifest hashes;
- fresh trusted runtime proof bound to both manifests;
- trusted target-machine probe evidence inside that proof for `install`, `startup`, `restartRecovery`, and `rollback`, each marked `passed` with a 64-hex evidence SHA-256;
- passed candidate-bound soak evidence, 24 hours by default;
- read-only provider mode;
- install, startup, restart-recovery, and rollback probes;
- Mainnet rejection.

    pwsh -NoProfile -File .\eng\dogfood\Switch-DogfoodSlot.ps1 `
      -CandidateRoot "<candidate-root>" `
      -LastKnownGoodRoot "<lkg-root>" `
      -OperatorRoot "<operator-root>" `
      -ExpectedCandidateManifestHash "<candidate-manifest-sha256>" `
      -ExpectedLastKnownGoodManifestHash "<lkg-manifest-sha256>" `
      -TrustedRuntimeProofPath "<trusted-runtime-proof.json>" `
      -ExpectedTrustedRuntimeProofHash "<trusted-runtime-proof-sha256>" `
      -ExpectedRuntimeProofId "<proof-id>" `
      -ExpectedRuntimeSourceIdentity "<trusted-proof-source>" `
      -ExpectedEvidenceGateRefs @('gate/build','gate/tests') `
      -SoakEvidencePath "<headless-soak-evidence.json>" `
      -ExpectedSoakEvidenceHash "<soak-evidence-sha256>"

On success, `current.json` records the promoted candidate root, candidate manifest hash, soak evidence hash, switch time, and previous root. A failed validation must not write or replace the pointer.

## Rollback

Rollback uses the already-verified last-known-good directory; it does not rebuild old bytes and it does not downgrade or rewrite user data. Before any rollback, stop the current runtime cleanly and follow the runtime-state maintenance policy if data recovery is required.

Do not treat a passing soak, slot switch, or Testnet mutation smoke as Mainnet approval. Mainnet remains separately prohibited by the current product safety boundary.

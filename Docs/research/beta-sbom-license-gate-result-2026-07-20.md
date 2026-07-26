# Beta SBOM License Gate Result

## Outcome

This milestone adds an engineering license release control. It is not legal advice and does not grant, accept, or reinterpret any license.

- CycloneDX components: 469 (unchanged; no component was deleted to reduce unknowns)
- Original `NOASSERTION`: 42
- Remaining `NOASSERTION`: 0
- Allow: 451
- Review: 16
- Deny: 0
- Unknown/custom metadata: 2
- Commercial blockers without an explicit review approval: 18
- Scope: 419 build graph / not in the win-x64 payload; 50 runtime / bundled or native payload
- Evaluation package gate: passed with `review_required`
- Commercial package gate: failed closed (exit code 1)

The package remains unsigned, built from a dirty source tree, non-distributable, and was not uploaded or deployed.

## Evidence Resolution

The 42 original unknown records are preserved in `sbom-noassertion-license-evidence-draft.json`, keyed by exact purl.

- 31 npm records use the official exact-version npm registry metadata and the locked package version.
- Nine OpenTK 4.3.0 split packages use the exact-version aggregate package plus the upstream `4.3.0` MIT license. Confidence is `medium-high` because the split nuspecs omit a license field.
- OpenTK.GLWpfControl 4.2.3 uses the NuGet-linked upstream MIT license.
- System.Memory 4.5.3 uses the fixed corefx commit license referenced by the package metadata.

Resolved expressions: MIT 19, Apache-2.0 10, LGPL-3.0-or-later 10, and three Apache/LGPL/MIT compound expressions. These are recorded evidence classifications, not legal conclusions.

## Legal Review Queue

Commercial distribution remains blocked until component/version-specific review is recorded outside the automatic evidence process:

- 10 non-Windows `@img/sharp-libvips-*` build packages: `LGPL-3.0-or-later`
- `@img/sharp-wasm32`: `Apache-2.0 AND LGPL-3.0-or-later AND MIT`
- `@img/sharp-win32-arm64`, `@img/sharp-win32-ia32`, and `@img/sharp-win32-x64`: `Apache-2.0 AND LGPL-3.0-or-later`
- `@vercel/analytics@1.6.1`: `MPL-2.0`
- `caniuse-lite@1.0.30001769`: `CC-BY-4.0`
- `Microsoft.Web.WebView2@1.0.2903.40`: package metadata says `LICENSE.txt`, not an SPDX expression
- `OpenTK.redist.glfw@3.3.0-pre20200830200122`: package metadata says `COPYING.md`, not an SPDX expression

The first 14 Sharp/libvips records are build-graph components and are not in the current win-x64 payload, but they remain visible for supply-chain review. WebView2 and GLFW are runtime payload components and require priority review of their actual license files and notice obligations.

## Reproducible Controls

- `eng/license-policy.json`: allow/review/deny policy
- `eng/license-review-overrides.json`: empty, component/version-specific approval ledger
- `eng/license-gate.ps1`: machine JSON, human Markdown, policy decision, NOTICE, and third-party notices
- `eng/test-license-gate.ps1`: allow, copyleft, network copyleft, Commons Clause, NOASSERTION, evaluation, and unknown-custom fixtures
- `eng/classify-nuget-assets.mjs`: resolves the current RID runtime/native payload from `project.assets.json`
- `eng/package-beta.ps1`: enriches the SBOM and embeds license artifacts
- `eng/verify-beta-package.ps1`: validates SBOM/report consistency after independent ZIP extraction

## Verified Artifact

- Package SHA256: `c986ae07744fffd8ed1495d190163fd6ec32644d911d5c70d8c7a80cca6e2204`
- ZIP bytes: 10,615,566
- Payload files: 152
- Manifest failures: 0
- Forbidden files: 0
- License gate tests: 7/7
- Package verification: passed

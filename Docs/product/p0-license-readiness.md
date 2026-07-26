# P0 License And Distributability Readiness

Audit date: 2026-07-21  
Target: WPE Agent 3.6.0, Windows x64 portable commercial distribution  
Decision type: engineering release control, not legal advice

## Decision

**The current release candidate is not commercially distributable.** Do not upload, publish, sell, or otherwise distribute it.

The checked-in policy denies commercial distribution while any review, missing, or unknown/custom component lacks a component-and-version-specific approval. `eng/license-review-overrides.json` contains no approvals. Phase 2 corrected the authoritative SBOM and regenerated the reports offline: 468 components, 453 allow, 15 review, zero deny, zero NOASSERTION, zero unknown/custom, and **15 commercial blockers**. The commercial gate still fails closed.

Distribution may proceed on license grounds only after all of the following are true:

1. Legal/release review records a component-and-version-specific disposition for the 15 review items, or a formally approved payload-scoped policy excludes build-only packages.
2. Final notices include the applicable license text, copyright, attribution, and modification notices.
3. The commercial license gate passes for the exact final package. Signing, clean-source, Testnet smoke, and other release gates remain separate blockers.

## Audited Evidence

The following repository artifacts were cross-checked offline:

- Direct .NET dependencies and version: `币安量化机器人.csproj`.
- Resolved .NET graph: `obj/project.assets.json`.
- Direct Web dependencies: `WebUi/package.json`.
- Locked Web graph: `WebUi/pnpm-lock.yaml`.
- Corrected 468-component SBOM: `artifacts/beta-packages/WPE-Agent-3.6.0-beta.1-91ba0bde4bb3-win-x64-portable/sbom.cdx.json`.
- Regenerated gate result: `artifacts/license-gate-commercial/license-report.json` and `policy-decision.json`.
- Packaged payload: `artifacts/beta-packages/WPE-Agent-3.6.0-beta.1-91ba0bde4bb3-win-x64-portable/app/`.
- Policy and approval ledger: `eng/license-policy.json` and `eng/license-review-overrides.json`.
- Exact NuGet package metadata and license files under the package folder recorded by `obj/project.assets.json`: `%USERPROFILE%/.nuget/packages/microsoft.web.webview2/1.0.2903.40/` and `%USERPROFILE%/.nuget/packages/opentk.redist.glfw/3.3.0-pre20200830200122/`.

`eng/check-p0-licenses.ps1 -Apply` used only the current lockfile, restored local package metadata/license files, and the verified payload. It removed the stale Vercel purl, mapped the two exact NuGet file licenses, preserved all 14 Sharp records as build-only/non-payload with their license expressions intact, and regenerated the commercial and embedded evaluation reports. Running the script without `-Apply` is the offline consistency check.

## Current 15 Review Items

All records below are confirmed by exact-version entries in `WebUi/pnpm-lock.yaml`. The existing SBOM marks each `distributionScope=build` and `includedInPayload=false`; the portable `app/` payload contains none of these package names or native Sharp files. They are still blockers under the current whole-SBOM commercial policy because no review approval exists.

| # | Component / version | Confirmed license | Evidence | Required disposition |
| ---: | --- | --- | --- | --- |
| 1 | `@img/sharp-libvips-darwin-arm64@1.2.4` | `LGPL-3.0-or-later` | `WebUi/pnpm-lock.yaml`; exact purl in existing SBOM | Legal approval for build-only presence, or formally exclude non-payload optional packages from the commercial payload gate. |
| 2 | `@img/sharp-libvips-darwin-x64@1.2.4` | `LGPL-3.0-or-later` | Same sources as #1 | Same as #1. |
| 3 | `@img/sharp-libvips-linux-arm@1.2.4` | `LGPL-3.0-or-later` | Same sources as #1 | Same as #1. |
| 4 | `@img/sharp-libvips-linux-arm64@1.2.4` | `LGPL-3.0-or-later` | Same sources as #1 | Same as #1. |
| 5 | `@img/sharp-libvips-linuxmusl-arm64@1.2.4` | `LGPL-3.0-or-later` | Same sources as #1 | Same as #1. |
| 6 | `@img/sharp-libvips-linuxmusl-x64@1.2.4` | `LGPL-3.0-or-later` | Same sources as #1 | Same as #1. |
| 7 | `@img/sharp-libvips-linux-ppc64@1.2.4` | `LGPL-3.0-or-later` | Same sources as #1 | Same as #1. |
| 8 | `@img/sharp-libvips-linux-riscv64@1.2.4` | `LGPL-3.0-or-later` | Same sources as #1 | Same as #1. |
| 9 | `@img/sharp-libvips-linux-s390x@1.2.4` | `LGPL-3.0-or-later` | Same sources as #1 | Same as #1. |
| 10 | `@img/sharp-libvips-linux-x64@1.2.4` | `LGPL-3.0-or-later` | Same sources as #1 | Same as #1. |
| 11 | `@img/sharp-wasm32@0.34.5` | `Apache-2.0 AND LGPL-3.0-or-later AND MIT` | `WebUi/pnpm-lock.yaml`; exact purl in existing SBOM | Review LGPL obligations and confirm it remains absent from the Windows payload. |
| 12 | `@img/sharp-win32-arm64@0.34.5` | `Apache-2.0 AND LGPL-3.0-or-later` | Same sources as #11 | Same as #11. |
| 13 | `@img/sharp-win32-ia32@0.34.5` | `Apache-2.0 AND LGPL-3.0-or-later` | Same sources as #11 | Same as #11. |
| 14 | `@img/sharp-win32-x64@0.34.5` | `Apache-2.0 AND LGPL-3.0-or-later` | Lockfile plus `WebUi/node_modules/.pnpm/@img+sharp-win32-x64@0.34.5/node_modules/@img/sharp-win32-x64/package.json` and `LICENSE` | Confirm build-only use. If any native binary is later shipped, legal must review LGPL relinking/source/notice obligations before distribution. |
| 15 | `caniuse-lite@1.0.30001769` | `CC-BY-4.0` | Lockfile plus `WebUi/node_modules/.pnpm/caniuse-lite@1.0.30001769/node_modules/caniuse-lite/package.json` and `LICENSE` | Legal/release owner must decide whether compiled browser data is shared in `WebUi/out`; if yes, supply creator/source/license attribution and modification indication required by CC-BY-4.0. |

No repository fact supports silently treating LGPL or CC-BY as allowlisted. Until the required decision is recorded, all 15 remain `review_required`.

## Resolved Custom Records

These records were previously unknown because the SBOM stored a filename rather than an SPDX expression. Phase 2 used their exact restored package files as evidence. They are now allow-classified and the regenerated report has zero unknown/custom records.

| Component / version | Previous value | Repository/local package evidence | Corrected identifier | Payload and obligations |
| --- | --- | --- | --- | --- |
| `Microsoft.Web.WebView2@1.0.2903.40` | `LICENSE.txt` | Direct reference in `.csproj`; resolved in `obj/project.assets.json`; exact package `microsoft.web.webview2.nuspec`, `LICENSE.txt`, and `NOTICE.txt` in the NuGet package folder | `BSD-3-Clause` | `WebView2Loader.dll` is in the win-x64 package. Reproduce copyright, conditions, disclaimer, and applicable `NOTICE.txt` third-party notices in package documentation/materials. |
| `OpenTK.redist.glfw@3.3.0-pre20200830200122` | `COPYING.md` | Transitive OpenTK dependency in `obj/project.assets.json`; exact package `opentk.redist.glfw.nuspec` points to `COPYING.md`; its text is the zlib license | `Zlib` | `glfw3.dll` is in the win-x64 package. Preserve the license notice; altered source must be marked if applicable. Acknowledgment is appreciated by the text but not required. |

The WebView2 identification is based on the exact three-clause BSD text shipped in version `1.0.2903.40`, not on the filename or package publisher. The GLFW identification is based on the exact zlib license text shipped in the specified prerelease package. `Zlib` was added to the engineering policy's permissive allowlist; no legal approval override was fabricated for either component.

## Stale SBOM Item

`@vercel/analytics@1.6.1` (`MPL-2.0`) was absent from current `WebUi/package.json`, `WebUi/pnpm-lock.yaml`, and restored packages. Phase 2 removed the stale purl from both authoritative SBOM copies; the consistency check asserts that it cannot remain. If a future locked graph contains it again, it must return as `review_required` and receive an MPL-2.0 file-level copyleft/source-availability review.

## Replacement And Reduction Options

These are recommendations, not authorized dependency changes:

- Prefer correcting SBOM scope first: generate a complete build SBOM for supply-chain visibility and a separately attested win-x64 payload SBOM for the commercial distribution gate. Do not delete records from an SBOM by hand.
- Determine why Next's build toolchain installs Sharp optional packages. If image optimization is unused in the static export, evaluate a supported configuration that avoids Sharp in a clean install; validate output equivalence before changing dependencies.
- If `caniuse-lite` attribution cannot be operationalized, evaluate a build chain that does not embed its data. A version bump alone is unlikely to change its CC-BY-4.0 license.
- Do not replace WebView2 solely for licensing: its exact package license is permissive. Fix evidence ingestion and notices.
- Do not replace GLFW solely for licensing: `Zlib` is permissive. Fix evidence ingestion and notices; separately consider whether the ScottPlot/OpenTK runtime path is actually used before a future dependency-removal decision.

## Minimum Human Actions

1. Legal reviews one Sharp/libvips obligation pattern covering the 14 exact purls and records component/version-specific approvals if the current policy requires each purl.
2. Legal/release owner decides the `caniuse-lite` compiled-data attribution treatment and records the exact-version approval or requires replacement.
3. Release engineering carries the WebView2 BSD, GLFW zlib, and any applicable CC-BY attribution into the final distribution notices.
4. Release owner reruns the offline consistency check and commercial gate, then signs a go/no-go only when the exact packaged SBOM has zero unapproved review, missing, unknown/custom, or denied records.

Until those actions are evidenced, the answer remains **not distributable**.

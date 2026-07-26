# Sharp LGPL Build-Graph Legal Review Packet

Prepared: 2026-07-21  
Release target: WPE Agent 3.6.0, Windows x64 portable  
Status: **14 component/version records remain `review_required`; no approval is recorded.**  
Purpose: concise offline evidence and an approval checklist. This packet is an engineering record, not legal advice.

## Decision Requested

Legal and the release owner must decide whether the 14 exact build-graph records may be excluded from the win-x64 commercial payload license gate because no corresponding Sharp/libvips artifact is conveyed. An approval must be component/version/license-specific and must not be inferred from this packet.

Choose and record one outcome for every row:

- `Approve build-only scope`: accept the evidence that the component is not distributed in this payload and record the exact purl/license approval in the controlled approval ledger.
- `Require obligations`: specify license text, source/relinking offer, notice, or other LGPL compliance material required despite the current non-payload classification.
- `Reject / replace`: keep the component blocked and require an approved dependency/build change in a separate engineering task.
- `More evidence`: identify the missing build, bundle, provenance, or legal fact. The item remains blocked.

## Frozen Evidence

- Lockfile: `WebUi/pnpm-lock.yaml`, SHA-256 `3999E0D39C85050611A06ED4B01AA188DFA09A1A3FFC18E1552A3957AC1C596F`.
- Corrected SBOM: `artifacts/beta-packages/WPE-Agent-3.6.0-beta.1-91ba0bde4bb3-win-x64-portable/sbom.cdx.json`, SHA-256 `BDEB5A437157B93466F9A9ABF95799227A74CEF233D73B9AD48B463E9CA3E7B6`.
- Payload manifest: `artifacts/beta-packages/WPE-Agent-3.6.0-beta.1-91ba0bde4bb3-win-x64-portable/FILE-MANIFEST.json`, SHA-256 `B59F8B123A160345FEA0B5255A7A61C8E45D0E4C97E2D466F775C98A33DF1812`.
- Payload contains 152 files. Exact filename/path checks found no `sharp.node`, libvips library, `@img/sharp-*` package path, or Sharp npm native package artifact.
- `libSkiaSharp.dll` and `libHarfBuzzSharp.dll` are separate .NET components. Their names are not evidence that any of the npm purls below entered the payload.
- The authoritative SBOM and `artifacts/license-gate-commercial/license-report.json` mark all 14 records `distributionScope=build` and `includedInPayload=false`.
- Supporting exact-version evidence is in `Docs/research/sbom-noassertion-license-evidence-draft.json`.

This proves the recorded package does not contain an identifiable target Sharp/libvips package or native binary. It does not decide whether internal build use, remote build services, source conveyance, or a future package triggers additional obligations.

## Component Checklist

| # | Exact purl | License expression | Platform / scope | Payload evidence | Human outcome | Reviewer / date / record |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | `pkg:npm/%40img/sharp-libvips-darwin-arm64@1.2.4` | `LGPL-3.0-or-later` | Darwin arm64; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 2 | `pkg:npm/%40img/sharp-libvips-darwin-x64@1.2.4` | `LGPL-3.0-or-later` | Darwin x64; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 3 | `pkg:npm/%40img/sharp-libvips-linux-arm@1.2.4` | `LGPL-3.0-or-later` | Linux arm glibc; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 4 | `pkg:npm/%40img/sharp-libvips-linux-arm64@1.2.4` | `LGPL-3.0-or-later` | Linux arm64 glibc; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 5 | `pkg:npm/%40img/sharp-libvips-linuxmusl-arm64@1.2.4` | `LGPL-3.0-or-later` | Linux arm64 musl; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 6 | `pkg:npm/%40img/sharp-libvips-linuxmusl-x64@1.2.4` | `LGPL-3.0-or-later` | Linux x64 musl; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 7 | `pkg:npm/%40img/sharp-libvips-linux-ppc64@1.2.4` | `LGPL-3.0-or-later` | Linux ppc64 glibc; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 8 | `pkg:npm/%40img/sharp-libvips-linux-riscv64@1.2.4` | `LGPL-3.0-or-later` | Linux riscv64 glibc; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 9 | `pkg:npm/%40img/sharp-libvips-linux-s390x@1.2.4` | `LGPL-3.0-or-later` | Linux s390x glibc; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 10 | `pkg:npm/%40img/sharp-libvips-linux-x64@1.2.4` | `LGPL-3.0-or-later` | Linux x64 glibc; build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 11 | `pkg:npm/%40img/sharp-wasm32@0.34.5` | `Apache-2.0 AND LGPL-3.0-or-later AND MIT` | wasm32; optional build graph | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 12 | `pkg:npm/%40img/sharp-win32-arm64@0.34.5` | `Apache-2.0 AND LGPL-3.0-or-later` | Windows arm64; wrong architecture | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 13 | `pkg:npm/%40img/sharp-win32-ia32@0.34.5` | `Apache-2.0 AND LGPL-3.0-or-later` | Windows x86; wrong architecture | SBOM `build/false`; no target artifact in manifest | **Pending** | ___ |
| 14 | `pkg:npm/%40img/sharp-win32-x64@0.34.5` | `Apache-2.0 AND LGPL-3.0-or-later` | Windows x64; optional build graph | SBOM `build/false`; no `sharp.node` or libvips artifact in manifest | **Pending** | ___ |

## Obligations To Evaluate

The reviewer must evaluate the exact LGPL-licensed material and actual conveyance model. At minimum:

- whether any LGPL-covered libvips binary, modified library, source, or derivative is conveyed outside the organization;
- whether dynamic/static linking or another combination occurs in any distributable not represented by this manifest;
- whether recipients require the LGPL license text, copyright notices, corresponding source or written offer, installation information, or a relinking mechanism;
- whether Apache-2.0 and MIT texts/notices for the compound-expression packages must also be carried;
- whether CI caches, build services, installer inputs, symbols, or separately delivered assets change the non-payload conclusion;
- whether one legal decision may cover a defined family while the approval ledger still records every exact purl and expression.

## Approval Record

Do not mark this section complete until all 14 rows have a signed disposition.

- Release/package identifier and hash: ___
- Approved distribution scope: ___
- Required notices/source/relinking actions: ___
- Exceptions or expiration conditions: ___
- Legal reviewer and authority: ___
- Release owner: ___
- Decision date: ___
- External ticket/matter/approval record: ___

Until the completed decision is transferred to the controlled approval ledger and the commercial gate is rerun, all 14 records remain blockers.

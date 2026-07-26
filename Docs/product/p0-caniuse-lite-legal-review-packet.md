# caniuse-lite CC-BY-4.0 Legal Review Packet

Prepared: 2026-07-21  
Release target: WPE Agent 3.6.0, Windows x64 portable  
Component: `pkg:npm/caniuse-lite@1.0.30001769`  
Status: **`review_required`; no approval is recorded.**  
Purpose: concise offline evidence and an approval checklist. This packet is an engineering record, not legal advice.

## Decision Requested

Legal and the release owner must decide whether caniuse-lite data is reproduced or shared in the exported `WebUi/out` JavaScript and, if so, approve an attribution method satisfying CC-BY-4.0 for this exact version. Absence of a package directory or package-name string from the payload is not sufficient proof that compiled browser-support data is absent.

Allowed outcomes:

- `Approve with attribution`: specify the creator/source/license/modification notice that must ship and where it will appear.
- `Approve as non-conveyed`: document bundle-level evidence that no licensed database material is reproduced in the distributed static output.
- `Reject / replace`: require an approved build/dependency change in a separate engineering task.
- `More evidence`: identify the required bundle trace, source map, generated-file provenance, or legal fact. The item remains blocked.

## Frozen Evidence

- Exact purl: `pkg:npm/caniuse-lite@1.0.30001769`.
- License: `CC-BY-4.0`, confirmed by the restored exact-version `package.json` and full `LICENSE` text.
- Lockfile: `WebUi/pnpm-lock.yaml`, SHA-256 `3999E0D39C85050611A06ED4B01AA188DFA09A1A3FFC18E1552A3957AC1C596F`.
- Corrected SBOM: `artifacts/beta-packages/WPE-Agent-3.6.0-beta.1-91ba0bde4bb3-win-x64-portable/sbom.cdx.json`, SHA-256 `BDEB5A437157B93466F9A9ABF95799227A74CEF233D73B9AD48B463E9CA3E7B6`.
- Payload manifest: `artifacts/beta-packages/WPE-Agent-3.6.0-beta.1-91ba0bde4bb3-win-x64-portable/FILE-MANIFEST.json`, SHA-256 `B59F8B123A160345FEA0B5255A7A61C8E45D0E4C97E2D466F775C98A33DF1812`.
- Restored evidence: `WebUi/node_modules/.pnpm/caniuse-lite@1.0.30001769/node_modules/caniuse-lite/package.json` and `LICENSE`.
- Evidence ledger: `Docs/research/sbom-noassertion-license-evidence-draft.json`.
- The authoritative SBOM/report mark the component `distributionScope=build`, `includedInPayload=false`, and `review`.
- The 152-file payload contains no `node_modules` or `caniuse-lite` path. A byte-string search of `app/WebUi/out` found no `caniuse-lite`, `browserslist/caniuse-lite`, or `CC-BY-4.0` marker.

The last two facts show no directly identifiable package copy. Minification, compilation, or data transformation may remove names while retaining licensed data, so they do not close the attribution question.

## CC-BY-4.0 Conditions To Evaluate

If licensed material is shared, the reviewer must determine a reasonable medium and placement for:

- identification of the creator and other designated attribution parties supplied with the material;
- copyright notice, if supplied;
- notice of and link or text for CC-BY-4.0;
- reference to the disclaimer of warranties;
- URI or hyperlink to the source material where reasonably practicable;
- indication of modifications or transformations, including compilation into generated browser data where legally applicable;
- confirmation that no additional terms or effective technological measures restrict recipients' licensed rights.

The reviewer must also decide whether the exported subset is a reproduction, adaptation, database extraction/reuse, or outside the license because no protected material is conveyed. Engineering must not decide that legal characterization from a filename scan.

## Approval Checklist

| Item | Required record | Status |
| --- | --- | --- |
| Exact component | `pkg:npm/caniuse-lite@1.0.30001769`, `CC-BY-4.0` | Confirmed |
| Distribution scope | Static Windows x64 application containing `WebUi/out`; npm package itself is not copied | Confirmed |
| Bundle provenance | Identify generated files, fields, or tables derived from caniuse-lite, or prove none are included | **Pending** |
| Legal characterization | Reproduction, adaptation, database extraction/reuse, or non-conveyed | **Pending** |
| Attribution text | Creator/source/copyright/license/disclaimer/link | **Pending** |
| Modification notice | State transformations or record why none is required | **Pending** |
| Notice placement | Package notice, documentation, in-app notice, or other approved reasonable medium | **Pending** |
| Approval scope | Exact package version, release artifact, channel, and expiration/re-review conditions | **Pending** |
| Sign-off | Legal reviewer, release owner, date, external approval record | **Pending** |

## Approval Record

- Outcome: ___
- Bundle/provenance evidence: ___
- Required attribution text and placement: ___
- Modification statement: ___
- Release/package identifier and hash: ___
- Legal reviewer and authority: ___
- Release owner: ___
- Decision date: ___
- External ticket/matter/approval record: ___

Until this record is complete, transferred to the controlled approval ledger, and verified by a rerun of the commercial gate, this component remains a blocker.

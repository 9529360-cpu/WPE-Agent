# FinancialEvidenceRecordV1

Status: Proposed for review (ARC-0002 red-contract only)  
Date: 2026-07-22 (Asia/Tokyo)  
Scope: immutable financial-evidence envelope, retrieval eligibility, and retrieval audit  
Out of scope: persistence implementation, UI, providers, Teacher, risk authorization, and execution behavior

## Context

WPE has evidence, news, memory, backtest, runtime-audit, and distribution records, but no common envelope or retrieval gate that proves provenance, entitlement, freshness, conflict handling, schema compatibility, and exact downstream inputs. Existing memory retrieval records filters and result counts, not the exact returned record IDs and hashes.

The current product authority is private autonomous Testnet with deterministic fail-closed controls. This contract does not introduce a human approval workflow. `Approved` below means a machine-verifiable evidence lifecycle state, not permission to trade, publish, or bypass Risk Gate and `ReliableOrderExecutor`.

## Decision

### Ten collections

`FinancialEvidenceCollectionTypeV1` is a closed V1 set:

1. `RawIntake`: source payloads and collection observations before validation.
2. `QuarantinedMaterial`: isolated records awaiting or failing provenance, safety, entitlement, or quality validation.
3. `ApprovedFact`: reconciled factual records approved for their declared use and scope.
4. `SpecialistAnalysis`: traceable market, news, macro, technical, fundamental, portfolio, or risk analyses.
5. `StrategyBacktestArtifact`: strategy hypotheses, datasets, methods, backtests, robustness results, and failure cases.
6. `LessonOrInstrumentIdea`: educational or instrument-idea artifacts that only cite eligible inputs.
7. `Decision`: deterministic or advisory decisions and reason codes; never execution authority by record presence alone.
8. `Outcome`: observed order, fill, recovery, attribution, or other realized outcome evidence, with environment kept explicit.
9. `Correction`: corrections, withdrawals, conflict resolutions, and supersession explanations.
10. `CapabilityEvaluation`: versioned evaluation cases/results, including false acceptance, false rejection, unresolved cases, and regressions.

No implementation may silently map an unknown collection value into one of these ten. Unknown or future values are schema-incompatible until a supported migration exists.

### State dimensions

Eligibility is derived from independent, explicit dimensions; it must not be inferred from collection name, file location, fluent text, or record existence.

- `LifecycleStateV1`: `Unapproved | Approved | Withdrawn`. Only `Approved` is eligible.
- `CustodyStateV1`: `Quarantined | Released`. Only `Released` is eligible.
- `ConflictStateV1`: `Clear | Conflicted | Resolved`. `Conflicted` is ineligible; `Resolved` requires links to the correction or resolution records.
- `EntitlementStateV1`: `Unknown | Unentitled | Entitled`. Only `Entitled` is eligible for the requested consumer, purpose, jurisdiction, and time.
- `SupersessionStateV1`: `Current | Superseded`. Only the current leaf is eligible by default. Historical retrieval must be an explicit audit/reconstruction purpose and must not make a superseded record current.

Freshness and schema compatibility are computed states. A record is `Stale` when `ExpiresAt` is present and `asOf >= ExpiresAt`, or when the consumer's declared maximum age is exceeded. Missing time needed by the requested artifact type fails closed. A schema is compatible only when its identifier/version is on the consumer's explicit allowlist; unknown versions are not best-effort parsed.

### Mandatory common fields

Every `FinancialEvidenceRecordV1` contains these fields. Conditional values remain present as explicit empty/unknown values; consumers must not invent them.

| Group | Required fields |
| --- | --- |
| Identity | `RecordId` (stable, globally unique), `RecordVersion`, `CollectionType`, `SchemaId`, `SchemaVersion`, `ContentHash`, `PayloadMediaType` |
| Time | `CreatedAt`, `ObservedAt`, `EffectiveAt`, `ExpiresAt`, `Timezone` |
| Provenance | `SourceProvider`, `SourceUriOrDatasetId`, `Publisher`, `EntitlementReference`, `LicenseReference`, `ExtractionMethod`, `ExtractionVersion` |
| Market scope | `InstrumentId`, `Symbol`, `InstrumentFullName`, `AssetClass`, `Venue`, `Currency` |
| Method | `AuthoringAgent`, `MethodVersion`, `ModelVersion`, `RuleVersion`, `InputRecordRefs` (exact ID/hash/version tuples) |
| Quality | `Confidence`, `Limitations`, `ContradictionRefs`, all five state dimensions |
| Trace | `TraceId`, `CorrelationId` |
| History | `SupersedesRecordRefs`, `SupersededByRecordRefs`, `WithdrawalRecordRef`, `ConflictResolutionRecordRefs` |
| Integrity | `RecordedAt`, `RecordedBy`, `RecordHash` |

`ContentHash` hashes the canonical payload bytes. `RecordHash` hashes the canonical envelope excluding `RecordHash` itself. Hash algorithm and canonicalization version are part of `SchemaId`/`SchemaVersion`; V1 implementations must not compare display strings as hashes. IDs, hashes, versions, timestamps, and trace values are opaque exact values and must round-trip without normalization.

### Append-only and supersession

- Accepted records are immutable and never updated or overwritten. Storage operations append a new record or fail.
- A correction, withdrawal, conflict resolution, or revised fact is a new record with its own ID, hashes, times, provenance, and trace.
- `SupersedesRecordRefs` must identify exact predecessor ID/hash/version tuples. The predecessor remains queryable for explicit audit/reconstruction, but normal retrieval selects only the non-withdrawn current leaf.
- Supersession is acyclic. Missing targets, hash/version mismatches, multiple unresolved current leaves, or a broken reverse link make the set conflicted and ineligible.
- Deduplication may link identical content hashes but may not discard provenance, entitlement, observation time, or historical records.
- Retention/deletion policy may restrict access or cryptographically erase data where required, but must leave a non-sensitive append-only tombstone and audit evidence; it must not silently rewrite history.

### Retrieval eligibility

`FinancialEvidenceRetrievalRequestV1` declares `ConsumerId`, `ConsumerCorrelationId`, purpose, jurisdiction, requested collection types, instrument, venue, effective-time window, retrieval `AsOf`, maximum age, compatible schema versions, and entitlement scope. Empty or unknown safety-critical criteria fail closed.

A record is eligible only when all of the following are true:

1. It matches the requested collection, instrument, venue, and effective-time scope.
2. Its lifecycle is `Approved`, custody is `Released`, conflict state is not `Conflicted`, entitlement is `Entitled` for this consumer/purpose/jurisdiction, and supersession state is `Current`.
3. It is fresh at the request's `AsOf` under both record expiry and consumer maximum-age policy.
4. Its schema is explicitly compatible, its hashes and referenced input hashes verify, and all mandatory fields for its collection are present.
5. Every required supersession, correction, conflict-resolution, provenance, and entitlement reference resolves exactly.

The gate returns deterministic reason codes and no payload for rejection. At minimum: `evidence.quarantined`, `evidence.stale`, `evidence.withdrawn`, `evidence.conflicted`, `evidence.unapproved`, `evidence.unentitled`, `evidence.schema-incompatible`, `evidence.superseded`, `evidence.integrity-failed`, and `evidence.scope-mismatch`. Multiple failures may be recorded, but eligibility remains false. Missing/unknown/error conditions never degrade to eligible.

An eligible result returns the exact stored `RecordId`, `ContentHash`, `RecordHash`, `RecordVersion`, time fields, `TraceId`, and `CorrelationId`. Retrieval must not rewrite these values or substitute a newer record without reporting its exact identity.

### Retrieval audit contract

Every retrieval attempt, including zero-result and rejected attempts, appends one `FinancialEvidenceRetrievalAuditV1` record containing:

- audit ID, request time, completion time, gate/schema/rule versions;
- `ConsumerId`, required `ConsumerCorrelationId`, purpose, jurisdiction, and caller trace;
- canonical request/filter hash and the declared `AsOf`/freshness policy;
- candidate count and per-reason rejection counts;
- ordered exact returned tuples of `RecordId`, `ContentHash`, `RecordHash`, and `RecordVersion`;
- outcome (`Returned | Empty | Rejected | Error`) and deterministic reason codes.

Filter text and result count alone are insufficient. The audit append must succeed atomically with releasing the result to the consumer; if exact-ID/hash audit persistence fails, retrieval fails closed and returns no evidence. Consequential downstream artifacts must cite the same exact tuples and consumer correlation.

## Public CLR contract expected by red tests

The fixture reserves namespace `WpeAgent.FinancialEvidence` and the types `FinancialEvidenceRecordV1`, `FinancialEvidenceRetrievalRequestV1`, `FinancialEvidenceEligibilityV1`, `FinancialEvidenceRetrievalResultV1`, and `FinancialEvidenceRetrievalAuditV1`, plus `FinancialEvidenceRetrievalGateV1`. The eventual implementation may refine construction APIs, but review must update this ADR and its red fixture together rather than weakening a rejection case.

## Alternatives rejected

- Reusing memory tiers: they do not encode the ten financial collections, full provenance/entitlement, supersession, schema compatibility, or exact returned hashes.
- One aggregate `Status` enum: it permits impossible or hidden combinations and cannot explain independent quarantine, conflict, entitlement, freshness, and supersession failures.
- Mutable latest-row storage: it destroys correction history and cannot prove what a prior consumer saw.
- Auditing only filter/count: it cannot reproduce or attribute a consequential decision to exact immutable inputs.
- Treating approval as execution authorization: it conflicts with the deterministic Risk Gate and reliable-executor boundary.

## Consequences and verification boundary

The contract adds storage, migration, integrity, access-control, backup/restore, and retrieval-gate work that is intentionally not implemented in ARC-0002. The accompanying tests are expected red evidence only. No claim is made that current memory/news/backtest/audit stores conform, that any external entitlement is valid, or that Testnet/provider behavior was exercised.


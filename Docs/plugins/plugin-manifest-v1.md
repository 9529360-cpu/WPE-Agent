# WPE Plugin Manifest v1 (Frozen)

Status: Phase 0 contract, frozen for WPE 3.6.x.

## Safety boundary

- The registry reads local `*.plugin.json` metadata only.
- Phase 0 does not load entry code, scan plugin DLLs, install remote packages, or expose an install endpoint.
- `data-source` and `notification` are the Phase 0 extension types.
- `exchange-adapter` is catalogued only when `testnetOnly=true` and `defaultEnabled=false`.
- `strategy`, `risk-rule`, and `brain-provider` remain reserved.
- Registry enablement is metadata state; it does not connect a plugin to order execution.
- Existing execution remains behind SkillExecutionGuard, Risk Gate, and ReliableOrderExecutor.

## Required JSON shape

```json
{
  "schemaVersion": "1.0",
  "id": "publisher.plugin-id",
  "name": "Plugin name",
  "type": "data-source",
  "version": "1.2.3",
  "publisher": {
    "id": "publisher",
    "name": "Publisher",
    "signatureKeyId": "release-key"
  },
  "compatibility": {
    "wpeMinVersion": "3.6.0",
    "wpeMaxVersion": "3.9.9",
    "os": ["windows"],
    "runtimes": ["net8.0-windows"]
  },
  "entry": {
    "kind": "wpe-contract",
    "contract": "data-source.v1"
  },
  "permissions": ["data.read"],
  "lifecycle": {
    "defaultEnabled": false,
    "testnetOnly": false
  },
  "signature": {
    "algorithm": "ed25519",
    "value": "signature-metadata",
    "signedAtUtc": "2026-01-01T00:00:00Z"
  }
}
```

## Closed dictionaries

Entry contracts:

- `data-source` -> `data-source.v1`
- `notification` -> `notification.v1`
- `exchange-adapter` -> `exchange-provider-catalog.v1`

Permissions:

- data source: `market.read`, `account.read`, `data.read`
- notification: `notify.emit`, `notify.webhook`
- exchange adapter: `exchange.testnet.read`, `exchange.testnet.trade`

Compatibility metadata is closed to `windows` and `net8.0-windows` in Phase 0. Schema and plugin versions use strict three-component versions. Unknown fields, types, entries, permissions, OS values, runtime values, or signature algorithms fail validation. Signature metadata presence is recorded honestly as `metadataPresent`; cryptographic trust verification is not claimed by Phase 0.

## Runtime projection

`RuntimeSnapshotV1.plugins` uses the standard `available | unsupported | stale | error` collection envelope. Each item exposes identity, type, version, publisher, entry kind, permissions, enable/default state, Testnet restriction, compatibility, signature metadata status, risk level, and a status message. Non-available collections contain no items.

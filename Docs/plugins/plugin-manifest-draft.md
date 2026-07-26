# WPE Plugin Manifest v1

## Scope
- Phase 0 only.
- Allow `data-source` and `notification` plugins.
- `exchange-adapter` plugins are Testnet-only and disabled by default.
- `strategy`, `risk-rule`, and `brain-provider` are reserved for later phases.

## Required fields
- `schemaVersion`
- `id`
- `name`
- `type`
- `version`
- `publisher`
- `compatibility`
- `entry`
- `permissions`
- `lifecycle`

## Type enum
- `exchange-adapter`
- `data-source`
- `strategy`
- `risk-rule`
- `notification`
- `brain-provider`

## Permission dictionary
- `market.read`
- `account.read`
- `data.read`
- `notify.emit`
- `notify.webhook`
- `exchange.testnet.read`
- `exchange.testnet.trade`
- `plugin.install`
- `plugin.upgrade`
- `plugin.rollback`
- `plugin.uninstall`

## Signature fields
- `publisher.id`
- `publisher.name`
- `publisher.signatureKeyId`
- `signature.algorithm`
- `signature.value`
- `signature.signedAtUtc`

## Compatibility rules
- `wpeMinVersion` and `wpeMaxVersion` are required.
- Plugin must match host OS/runtime list.
- `exchange-adapter` requires `testnetOnly: true` in Phase 0.
- `defaultEnabled` must be `false` for all trading-capable plugins.
- Unknown permissions or unsupported entry kinds fail validation.

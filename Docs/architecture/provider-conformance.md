# Provider conformance matrix

This matrix describes product capabilities, not merely API code present in an adapter.
Mainnet execution is not a supported product capability.

| Provider | Market catalog | Account | Positions | Orders | Mutation implementation | Product Testnet status |
| --- | --- | --- | --- | --- | --- | --- |
| Binance Futures | Implemented | Implemented; explicit canTrade required | Implemented | Regular and algo orders; only confirmed not-found maps to null | Market, limit, leverage, margin, hedge mode, protection, cancel | Supported only on testnet.binancefuture.com; credentialed lifecycle still required before release |
| OKX | Implemented | Implemented; key permissions introspected | Implemented | Regular and conditional/oco queries fail as one observation | Market, limit, leverage, margin, hedge mode, protection, cancel | Demo Trading only on www.okx.com with simulated-trading header; credentialed lifecycle still required |
| Bybit | Implemented | Implemented; missing/unknown readOnly is non-trading | Implemented | Implemented | Market, limit, leverage, margin, hedge mode, protection, cancel | Supported only on api-testnet.bybit.com; credentialed lifecycle still required |
| Gate.io | Implemented | Read verified; trade permission cannot be introspected | Implemented | Regular and protection queries fail as one observation | Implemented but capability remains read-only | Supported only on api-testnet.gateapi.io; trade remains unknown until credentialed lifecycle evidence exists |
| Bitget | Implemented | Read verified; trade permission cannot be introspected | Implemented | Regular and protection queries fail as one observation | Implemented but capability remains read-only | Demo Trading only on api.bitget.com with demo header; trade remains unknown until credentialed lifecycle evidence exists |

## Fail-closed rules

- An uninstalled provider declares no Testnet, Mainnet, read, or mutation capability.
- An unofficial endpoint is rejected during profile validation and again before REST request construction.
- Catalog failure stops the probe before private permission calls.
- Missing symbol, catalog error, permission error, environment mismatch, disabled execution, or unknown trade permission cannot produce a tradeable capability.
- Descriptor mutation claims require place, cancel, and protection support together.
- Gate.io and Bitget remain read-only until a credentialed Testnet lifecycle provides explicit evidence. A successful account read is not trade permission evidence.

## Credentialed Testnet unknowns

The offline suite does not prove live exchange behavior. Release validation still needs separately authorized Testnet credentials for:

- account, positions, regular orders, and protection-order observation fixtures for all five providers;
- one idempotent create, query, cancel, protection, and cleanup lifecycle per provider;
- exchange-specific hedge mode, margin mode, leverage, reduce-only direction, and client-order-id behavior;
- Gate.io and Bitget trade-permission evidence strong enough to replace their current read-only status.

These checks must never run as normal unit tests and must never target Mainnet.

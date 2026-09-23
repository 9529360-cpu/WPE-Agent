# Retired: Local-First / LLM-Optional Architecture

This architecture is no longer active.

The current trading architecture has one decision brain: local deterministic technical analysis. There is no provider router, remote Brain adapter, prompt cache, LLM budget gate, token-cost ledger, or Hybrid / AI Research runtime mode in the trading product.

Current authority chain:

1. Read fresh market/account/order evidence.
2. Analyze multi-timeframe market structure locally.
3. Produce BUY / SELL / HOLD locally.
4. Calculate stop, take-profit, and position size.
5. Pass deterministic risk gates.
6. Execute through the exchange gateway.
7. Manage position and protection orders.
8. Persist trade/execution/audit facts.

Remote/online LLM capability must not be reattached to this chain.

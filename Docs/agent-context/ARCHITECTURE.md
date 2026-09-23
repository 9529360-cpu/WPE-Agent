# Architecture

WPF host -> secure configuration/services -> domain state -> Risk Gate -> execution -> SQLite audit -> WebView2 Runtime Bridge -> Web UI.

The Web UI must not store secrets or call exchanges directly. Exchange providers implement the common adapter contracts. Live risk-increasing authority is `fresh canonical market evidence -> TechnicalDecisionAgent -> DirectMarketStructureDecisionSkill -> deterministic plan -> independent Risk Gate -> execution/recovery`; research, backtests, legacy strategy state, and model-off Research/Strategy stages are offline/read-only audit evidence and cannot approve, rank, promote, or replace a live decision. Module ownership and the MCP boundary are defined in `Docs/architecture/agent-kernel-tools-mcp.md`.

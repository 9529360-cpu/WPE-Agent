# WPE Agent - Start Here

This repository snapshot was prepared for a clean pause and portable resume on 2026-07-26.

## Read in this order

1. `../AGENTS.md`
2. `agent-context/PROJECT_STATE.md`
3. `agent-context/ARCHITECTURE.md`
4. `agent-context/DECISIONS.md`
5. `agent-context/TODO.md`
6. `product/master-backlog.md`
7. The exact contract and focused tests for the next task

Do not use chat history as project authority. Do not load every document or historical report by default.

## Current product authority

- Private-use, commercial-grade software.
- Public market data does not require trading credentials.
- Private account and trading actions use explicit Testnet/Sandbox credentials.
- Market, Research, Strategy, Risk, Execution, Recovery, and Audit form the target runtime aggregates.
- Unknown, stale, incomplete, or unsafe state fails closed.
- Mainnet remains disabled.
- Teacher/mentor functionality is retired from the current product and trading runtime.
- Local Only remains useful without a local or online language model. Model output is optional and cannot bypass deterministic risk, execution, recovery, or audit.

Machine-readable model-off authority is `product/model-off-capability-maturity.json`. The detailed private autonomous contract is `product/private-autonomous-deployment.md`.

## Pause state

- The snapshot intentionally includes a large set of existing source changes, deletions, and newly added files.
- Do not run broad reset, checkout, clean, restore, or deletion commands against this worktree.
- Historical worker leases and chat assignments are not valid resume authority. Establish a fresh milestone and ownership boundary.
- Generated dependencies, binaries, browser captures, local databases, logs, and publish outputs are not versioned. Restore them from source and lockfiles.
- Build Web UI before .NET because MSBuild packages `WebUi/out`.

## Known gaps

- Historical collection APIs for orders, equity, backtests, skill calls, and audit events.
- Legacy Binance-direct services coexist with the provider architecture and need consolidation or quarantine.
- `product/investor-professional-core-plan.md` has visible encoding corruption in this snapshot. Do not treat it as default authority until verified.

## Tooling preference

N-Forge is not required for this repository and must not be enabled unless the user explicitly requests it again.


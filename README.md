# WPE Agent

WPE Agent is a local-first, auditable trading Agent platform for Windows. The current production boundary targets private autonomous Testnet operation with deterministic risk, execution, recovery, and audit controls. Local Only remains useful without a local or online language model.

## Current Status

- Version: `3.6.0`
- Platform: Windows 10/11, .NET 8, WPF with a read-only WebView runtime UI
- Default environment: Testnet/Paper
- Mainnet: disabled
- Remote or local LLM: optional advisory layer only
- User state: `%LOCALAPPDATA%\WPE Agent\`

Accepted capabilities are deliberately bounded. Current accepted components include the seven-role orchestrator, Market Data, Technical, Backtest, Risk Gate, execution contract boundary, Position safety gates, post-trade review, and the opt-in Teacher market brief. The seven aggregate Agents remain partially accepted until their full production and live Testnet evidence gates are complete. The machine-readable authority is [`Docs/product/model-off-capability-maturity.json`](Docs/product/model-off-capability-maturity.json).

## Safety Boundary

- Unknown, stale, unsupported, inconsistent, or tampered evidence fails closed.
- LLM output cannot place orders or bypass deterministic validation.
- Every mutation must pass the Risk Gate and `ReliableOrderExecutor` path.
- Mainnet is not an authorized runtime result.
- Testnet provider support is not claimed without provider-specific evidence.
- Secrets and configuration writes belong to the WPF host, not the Web UI.
- Application files are separate from local databases, logs, credentials, and user settings.

This software does not promise returns and is not ready for real-money production trading.

## Repository

```powershell
git clone https://github.com/9529360-cpu/WPE-Agent.git
cd WPE-Agent
```

The current solution and main project retain historical Chinese filenames. Do not rename them casually; a coordinated project-boundary migration is planned after the seven-Agent backend closes.

## Build And Test

```powershell
dotnet restore "币安量化机器人.sln"
dotnet build "币安量化机器人.sln" -c Release --no-restore
dotnet test "WPE.Tests\WPE.Tests.csproj" -c Release --no-build -- xUnit.ParallelizeTestCollections=false
```

Run the desktop reference UI after a successful build:

```powershell
dotnet run --project "币安量化机器人.csproj"
```

## Source Of Truth

- Product priorities: [`Docs/product/master-backlog.md`](Docs/product/master-backlog.md)
- Current state: [`Docs/agent-context/PROJECT_STATE.md`](Docs/agent-context/PROJECT_STATE.md)
- Architecture: [`Docs/agent-context/ARCHITECTURE.md`](Docs/agent-context/ARCHITECTURE.md)
- Decisions: [`Docs/agent-context/DECISIONS.md`](Docs/agent-context/DECISIONS.md)
- Remaining work: [`Docs/agent-context/TODO.md`](Docs/agent-context/TODO.md)
- Local-first design: [`Docs/architecture/wpe_multi_agent_local_first_design.md`](Docs/architecture/wpe_multi_agent_local_first_design.md)

## Repository Layout

- `Core/`: contracts and domain primitives
- `Services/Agent/`: deterministic Agent, risk, execution, recovery, and audit services
- `Services/Exchange/`: provider-neutral exchange adapters
- `Infrastructure/`: local infrastructure
- `Modules/`: desktop modules and quarantined migration references
- `WebUi/`: reference runtime UI
- `WPE.Tests/`: xUnit safety and regression suite
- `Docs/`: product, architecture, evidence, and handoff records

Files excluded from the production project are not production authority. Do not reuse legacy Binance-bound modules for new execution paths.

## Local Data And Secrets

Never commit API keys, credentials, certificates, databases, WAL/SHM files, logs, runtime settings, or order-state files. Templates must contain placeholders only. Runtime state belongs under `%LOCALAPPDATA%\WPE Agent\`, not beside the executable.

## Release Direction

The bounded private Windows x64 portable package is implemented and locally validated: immutable application files stay separate from local user state, payload hashes/manifests are independently verified, and side-by-side candidate/last-known-good rollback preserves user data. This is not a commercial release claim: trusted signing, commercial license clearance, installer certification, target release go/no-go, upload/deployment, and Mainnet authorization remain separate gates.

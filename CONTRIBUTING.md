# Contributing to WPE Agent

WPE Agent is a safety-sensitive trading system. Keep changes small, auditable, and aligned with the repository's local-first, Testnet-first operating boundary.

## Development Flow

1. Start from the latest `main`.
2. Create a short-lived topic branch.
3. Make the smallest complete change in the existing authoritative owner.
4. Run the checks relevant to the changed surface.
5. Open a pull request using the repository template.
6. Resolve review conversations and update the branch if `main` moved.
7. Merge only after the required `Product CI / build-and-test` check succeeds.

Do not push directly to `main`.

## Required Local Checks

At minimum, run the repository governance gate:

```powershell
./eng/repository-governance.ps1
```

For .NET changes:

```powershell
dotnet restore "币安量化机器人.sln"
dotnet build "币安量化机器人.sln" -c Release --no-restore
dotnet test "WPE.Tests\WPE.Tests.csproj" -c Release --no-build -- xUnit.ParallelizeTestCollections=false
```

For Web UI changes, run the Web UI lint, typecheck, contract tests, surface tests, and build used by Product CI.

Local success is not a substitute for the required remote CI result.

## Safety Invariants

Preserve all of the following unless the product owner explicitly authorizes a boundary change:

- Mainnet stays disabled.
- Risk-increasing orders pass deterministic Risk Gate and execution controls.
- Reduce-only recovery remains available when risk increase is blocked.
- The trading brain remains local deterministic technical analysis.
- Remote/Hybrid/online-LLM trading authority is not reintroduced.
- WPF remains the authority for secrets and configuration writes.
- Runtime state and credentials stay outside the repository.

Do not create parallel execution, configuration, persistence, or trading-authority paths when an existing owner already exists.

## External Code And Dependencies

Before copying source from another project, verify its license and record any required attribution or notices. Ideas may be independently reimplemented when license compatibility does not permit source reuse.

Do not commit API keys, access tokens, private keys, certificates, local databases, WAL/SHM files, logs, generated binaries, or user runtime state.

## Pull Request Expectations

A pull request should state:

- what changed and why;
- whether trading/risk/execution authority changed;
- failure and recovery behavior for safety-sensitive changes;
- exact tests/builds run;
- external dependency or licensing impact;
- any remaining limitation or intentionally deferred work.

See `GOVERNANCE.md` for the enforced repository policy.

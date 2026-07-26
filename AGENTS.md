# WPE Agent Project Context

Read `Docs/agent-context/PROJECT_STATE.md`, `ARCHITECTURE.md`, `DECISIONS.md`, and `TODO.md` before changing the project.

Rules:
- Preserve Testnet isolation, Risk Gate enforcement, auditability, and encrypted secret storage.
- The WPF host owns configuration writes and secrets. Web UI mutations must use the host bridge.
- Reuse existing services and contracts; do not create parallel trading or configuration paths.
- Build Web UI before .NET because MSBuild packages `WebUi/out`.
- Do not commit API keys, generated binaries, databases, logs, or local runtime state.

Product and platform specs:
- `Docs/product/master-backlog.md` is the approved commercial priority order.
- `Docs/product/wpe-commercial-assessment.md` contains positioning, users, gaps, and differentiation.
- `Docs/product/wpe-agent-product-design-review.html` contains the independent UI/product design review.
- `Docs/architecture/main-project-integration-plan.md` contains file-level integration and rollback steps.
- `Docs/architecture/wpe_multi_agent_local_first_design.md` defines local-first Agent roles and boundaries.
- `Docs/architecture/prd-local-first-llm-optional.md` defines Local Only, Hybrid, and AI Research modes.
- `Docs/plugins/` contains plugin manifests, permissions, rollout, and skill contract drafts.
- `Docs/research/` contains GitHub research, licensing, SBOM, and dependency gate plans.

Product rule: Local Only must remain useful without an LLM. LLM providers are optional enhancement layers and never replace deterministic risk, execution, recovery, or audit services.

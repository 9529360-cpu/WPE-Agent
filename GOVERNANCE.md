# Repository Governance

WPE Agent treats repository controls as part of the product safety boundary. The repository configuration must prevent unreviewed or unvalidated changes from silently becoming the authoritative trading runtime.

## Main Branch Contract

`main` is the authoritative integration branch.

- Changes to `main` must arrive through a pull request.
- The required GitHub Actions check is `build-and-test` from the `Product CI` workflow.
- Required checks must run against a branch that is current with `main`.
- Review conversations must be resolved before merge.
- Force pushes and branch deletion are prohibited.
- Linear history is required.
- Repository merges use squash merge only.
- Merged topic branches are deleted automatically.

The repository currently has one primary maintainer, so the branch rule does not require an approving review count that would deadlock solo maintenance. CODEOWNERS still records ownership, and review requirements can be raised when another active maintainer exists.

## Safety-Critical Change Rules

Changes touching trading authority, risk, execution, recovery, exchange adapters, persistence, or release tooling must preserve these invariants:

- Mainnet remains disabled unless a separately authorized product decision changes that boundary.
- Risk-increasing mutations cannot bypass the Risk Gate or the authoritative execution path.
- Reduce-only recovery remains available when new risk is blocked.
- The trading brain remains local deterministic technical analysis; remote or hybrid LLM trading authority must not be reintroduced.
- Secrets, credentials, private keys, runtime databases, logs, and local state must never enter the repository or published artifacts.
- External code must be license-reviewed before source is copied or adapted.

A safety-critical pull request must describe the changed authority boundary, failure behavior, and the validation that can falsify the change.

## CI Authority

A green local build is useful evidence, but it does not replace the required remote CI result for a pull request.

The `Product CI / build-and-test` job is the merge gate. It validates repository governance, the Web UI, source encoding, .NET build/test boundaries, headless/MCP/maintenance artifacts, release scripts, and publish boundaries.

If the required check is missing, cancelled, skipped unexpectedly, stale, or failing, the change is not merge-ready.

## Merge And History Policy

Use short-lived topic branches. Rebase or update the branch when GitHub reports that it is behind `main`, then let CI run again on the new head.

Squash merge is the canonical merge method so one pull request becomes one mainline change. Do not bypass the pull-request path with direct pushes, merge commits, or force pushes.

## Release And Recovery

A release candidate must bind to an exact `main` commit with a successful required CI result. Release tooling must not publish from an uncommitted or unpushed working tree.

Repository governance is fail-closed: if branch protection, required checks, or release evidence cannot be verified, do not treat the candidate as governed or release-ready.

## Break-Glass Changes

Changing repository protection itself is an administrative operation, not an ordinary development shortcut. If protection must be changed to recover the repository, keep the change narrowly scoped, restore the protection immediately after recovery, and document the reason and resulting configuration in repository history.

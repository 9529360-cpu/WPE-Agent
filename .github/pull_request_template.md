## Summary

Describe the user/product outcome and the smallest authoritative owner changed.

## Safety boundary

- [ ] Mainnet remains disabled, or this PR contains an explicitly authorized boundary change.
- [ ] Risk-increasing mutations still pass deterministic risk and execution gates.
- [ ] Reduce-only recovery remains available when new risk is blocked.
- [ ] No remote/Hybrid/online-LLM trading authority was introduced.
- [ ] No secrets, credentials, runtime databases, logs, or local state are included.

If this PR does not touch trading/risk/execution/recovery authority, state that explicitly.

## Validation

List the exact commands, focused tests, integration checks, and runtime evidence used.

- [ ] `./eng/repository-governance.ps1` passes.
- [ ] Relevant focused tests pass.
- [ ] Full Release build/test was run when the change surface requires it.
- [ ] Required `Product CI / build-and-test` is green on the current PR head.

## Failure and recovery

For safety-sensitive changes, describe fail-closed behavior, replay/idempotency behavior, restart recovery, and rollback/forward-repair implications.

## External code / dependencies

- [ ] New or copied third-party source was license-reviewed.
- [ ] Attribution/notices were added where required.
- [ ] No unrelated dependency churn is included.

## Notes

Call out deliberate deferrals, compatibility constraints, or evidence that is not yet available.

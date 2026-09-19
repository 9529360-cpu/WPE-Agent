# WPE Headless Windows Service deployment contract

`WPE-Headless.exe` can run under Windows Service Control Manager. Service registration/start/restart remains an operator/SCM responsibility; WPE does not silently create, replace, or reconfigure a Windows service.

## Required identity and state root

Run the service under the Windows account that owns the WPE DPAPI-protected runtime state. The Maintenance CLI used for backup/restore must run under that same account.

For service mode, provide one explicit absolute path on a fixed local drive. The preferred deployment form is to include it in the service ImagePath:

    "C:\Program Files\WPE\<version>\headless\WPE-Headless.exe" --data-root "D:\WPE-State"

`WPE_AGENT_DATA_ROOT` remains supported. If both the command-line argument and environment variable are present, both must resolve to the same full path. A conflict fails closed before Host construction. Relative paths, UNC paths, mapped/network or non-fixed drives, duplicate `--data-root`, missing argument values, and invalid environment roots are rejected. A drive root such as `D:\` remains a fully qualified root and is not normalized to the drive-relative form `D:`.

Do not put account passwords, API keys, certificate secrets, or exchange credentials in the service command line or environment.

Before registering or updating the Windows Service, run the read-only deployment preflight against the exact immutable candidate root:

    .\eng\ops\Test-HeadlessServiceDeployment.ps1 \
      -CandidateRoot "D:\\WPE\\versions\\<version>" \
      -DataRoot "D:\\WPE-State" \
      -ServiceAccount "MACHINE\\wpe-service" \
      -MaintenanceAccount "MACHINE\\wpe-service"

The preflight verifies that the candidate contains both `headless\WPE-Headless.exe` and `maintenance\WPE.Maintenance.exe`, that candidate/data paths are absolute fixed-local non-reparse paths with no overlap, and that the declared service and maintenance identities match for DPAPI CurrentUser compatibility. It returns the exact service ImagePath string but never creates, starts, stops, or reconfigures a service.

## Startup exit codes relevant to service deployment

- `45`: service mode did not receive an explicit data root.
- `46`: data-root configuration is invalid, duplicate, non-fixed/network-backed, UNC, relative, or conflicts between argument and environment.
- Other Headless exit codes continue to represent platform/license/setup/access/runtime failures.

The process sets the accepted root only in its own process environment before `AppDataPaths` is initialized. It does not modify machine/user environment variables.

## Operational control

Use the same packaged executable for same-user local health and graceful shutdown:

    WPE-Headless.exe control health
    WPE-Headless.exe control shutdown --confirm shutdown-wpe-headless

These commands use the fixed CurrentUserOnly named pipe. They do not start/restart the service. Use SCM for start/restart and confirm the process exits before runtime-state maintenance.

## Release/rollback

Keep each application version in an immutable side-by-side directory. The data root is outside package directories and is not removed on application rollback. Do not downgrade or rewrite runtime databases merely because application binaries roll back; use the runtime-state backup/restore policy when data recovery is actually required.

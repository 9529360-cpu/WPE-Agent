# Headless local control

`WPE-Headless.exe` exposes a same-user local control client for operational health checks and graceful shutdown. It uses the fixed `wpe-agent-headless-control-v1` named pipe served by the running Headless process.

## Security boundary

- The server uses `PipeOptions.CurrentUserOnly`; run the client under the same Windows account as the Headless service.
- The client always connects to the local machine and the fixed pipe name. It cannot select a remote host or alternate pipe.
- Only `health` and confirmed `shutdown` are supported.
- Start/restart remains Windows Service Control Manager authority. The local control protocol cannot start the Agent, change settings, select a provider, submit orders, change authorization mode, or enable Mainnet.

## Health

    WPE-Headless.exe control health

Exit code `0` means the request succeeded. The JSON response contains only the process state/code, observation time, runtime readiness, Agent running state, access freshness, heartbeat freshness, and lease-loss state.

For an operational ready check require all of the following in the response: `success=true`, `processState=ready`, `runtimeReady=true`, `agentRunning=true`, `accessFresh=true`, `heartbeatFresh=true`, and `leaseLost=false`.

## Graceful shutdown

    WPE-Headless.exe control shutdown --confirm shutdown-wpe-headless

The server flushes the acknowledgement before requesting host cancellation. After success, still confirm the Windows Service/process has exited before running backup, restore, upgrades, or filesystem maintenance.

## Exit codes

- `0`: server accepted the command.
- `2`: invalid command-line arguments or missing exact shutdown confirmation.
- `3`: control endpoint unavailable, unsupported platform, timeout, access failure, or invalid response.
- `4`: server returned a valid rejection response.

Do not loop on shutdown or convert a control failure into forced database/file mutation. If the process does not exit, diagnose service/runtime health first; maintenance remains protected by the exclusive data-root lease.

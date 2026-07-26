# Binance Futures Testnet Review Smoke

`eng/smoke-review-testnet.ps1` is a fail-closed, read-only release check for the WPE Review workflow. It never creates, changes, cancels, or tests an order.

## Safety contract

- REST origin is fixed to Binance Futures Testnet's official `https://testnet.binancefuture.com` origin. It cannot be overridden from the command line.
- `Review` is required, Mainnet must remain disabled, and dry-run must remain enabled.
- The script calls only GET endpoints: `/fapi/v1/time`, `/fapi/v1/exchangeInfo`, `/fapi/v2/account`, and `/fapi/v1/apiTradingStatus`.
- Missing credentials, an unknown symbol/capability, clock skew beyond the receive window, failed private reads, missing trade permission, withdrawal permission, or locked/unknown API trading status produces a non-zero exit.
- Secrets are read only from process environment variables. The script does not print them, persist them, accept them as command-line arguments, or include response bodies in failure output.
- A passing result is evidence for connectivity and preflight readiness only. It is not approval to place an order and does not bypass WPE's Review approval, Risk Gate, or `ReliableOrderExecutor`.

## Run

Use a dedicated Binance Futures Testnet API key. Configure only the minimum permissions needed for Futures Testnet trading and do not enable withdrawal permissions.

```powershell
$key = Read-Host "Testnet API key" -AsSecureString
$secret = Read-Host "Testnet API secret" -AsSecureString
try {
    $env:WPE_BINANCE_TESTNET_API_KEY = [Net.NetworkCredential]::new("", $key).Password
    $env:WPE_BINANCE_TESTNET_API_SECRET = [Net.NetworkCredential]::new("", $secret).Password
    & .\eng\smoke-review-testnet.ps1
    $exitCode = $LASTEXITCODE
} finally {
    Remove-Item Env:\WPE_BINANCE_TESTNET_API_KEY, Env:\WPE_BINANCE_TESTNET_API_SECRET -ErrorAction SilentlyContinue
    $key = $null
    $secret = $null
}
exit $exitCode
```

Optional read-only controls are `-Symbol`, `-ReceiveWindow`, and `-TimeoutSeconds`. Safety flags exist so CI can state its assumptions explicitly:

```powershell
& .\eng\smoke-review-testnet.ps1 -AuthorizationMode Review -MainnetEnabled:$false -DryRun:$true -Symbol BTCUSDT
```

Do not pass `-DryRun:$false`, a non-Review authorization mode, or `-MainnetEnabled`; each is rejected before credentials are read or any network request occurs.

## Expected safe failure without credentials

With both credential variables absent, the command performs only public Testnet time and exchange-capability reads, then exits `4`. The output ends with `No order or other exchange mutation was attempted.` This is the required safe behavior for developer machines and CI jobs without injected Testnet credentials.

The command can also exit earlier if the official Testnet endpoint is unavailable or its clock/capability response is not trustworthy. Such a result is a failed smoke, never permission to continue to execution.

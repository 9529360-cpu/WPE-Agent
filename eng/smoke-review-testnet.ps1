[CmdletBinding()]
param(
    [ValidateSet("Review", "Research", "Signal", "Auto")]
    [string]$AuthorizationMode = "Review",
    [switch]$MainnetEnabled,
    [bool]$DryRun = $true,
    [ValidatePattern('^[A-Z0-9]{5,30}$')]
    [string]$Symbol = "BTCUSDT",
    [ValidateRange(1000, 60000)]
    [int]$ReceiveWindow = 5000,
    [ValidateRange(1, 30)]
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$endpoint = "https://testnet.binancefuture.com"
$apiKeyVariable = "WPE_BINANCE_TESTNET_API_KEY"
$apiSecretVariable = "WPE_BINANCE_TESTNET_API_SECRET"
$script:failed = $false

function Write-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $status = if ($Passed) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1}: {2}" -f $status, $Name, $Detail)
    if (-not $Passed) { $script:failed = $true }
}

function Stop-Safely([string]$Message, [int]$Code = 1) {
    Write-Check "safe-stop" $false $Message
    Write-Host "No order or other exchange mutation was attempted."
    exit $Code
}

function Invoke-BinanceGet([string]$Path, [hashtable]$Query = @{}, [bool]$Signed = $false) {
    $pairs = [System.Collections.Generic.List[string]]::new()
    foreach ($key in ($Query.Keys | Sort-Object)) {
        $pairs.Add("$([uri]::EscapeDataString($key))=$([uri]::EscapeDataString([string]$Query[$key]))")
    }
    if ($Signed) {
        $pairs.Add("recvWindow=$ReceiveWindow")
        $pairs.Add("timestamp=$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())")
        $payload = $pairs -join "&"
        $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($script:apiSecret))
        try { $signature = ([BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload)))).Replace("-", "").ToLowerInvariant() }
        finally { $hmac.Dispose() }
        $pairs.Add("signature=$signature")
    }
    $uri = "$endpoint$Path"
    if ($pairs.Count -gt 0) { $uri += "?" + ($pairs -join "&") }
    $headers = if ($Signed) { @{ "X-MBX-APIKEY" = $script:apiKey } } else { @{} }
    Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -TimeoutSec $TimeoutSeconds
}

Write-Host "WPE Binance Futures Testnet Review smoke (read-only)"
Write-Check "official-endpoint" ($endpoint -ceq "https://testnet.binancefuture.com") $endpoint
Write-Check "review-mode" ($AuthorizationMode -ceq "Review") "requested=$AuthorizationMode"
Write-Check "mainnet-disabled" (-not $MainnetEnabled) "enabled=$([bool]$MainnetEnabled)"
Write-Check "dry-run" $DryRun "enabled=$DryRun"
if ($script:failed) { Stop-Safely "Local safety preconditions failed." 2 }

$serverTime = Invoke-BinanceGet "/fapi/v1/time"
$clockSkewMs = [math]::Abs([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [int64]$serverTime.serverTime)
Write-Check "clock" ($clockSkewMs -le $ReceiveWindow) "skewMs=$clockSkewMs receiveWindowMs=$ReceiveWindow"

$exchangeInfo = Invoke-BinanceGet "/fapi/v1/exchangeInfo"
$market = @($exchangeInfo.symbols | Where-Object { $_.symbol -ceq $Symbol })
$marketKnown = $market.Count -eq 1
$marketTrading = $marketKnown -and $market[0].status -ceq "TRADING"
$requiredOrderTypes = @("MARKET", "LIMIT", "STOP_MARKET", "TAKE_PROFIT_MARKET")
$missingOrderTypes = if ($marketKnown) { @($requiredOrderTypes | Where-Object { $_ -notin @($market[0].orderTypes) }) } else { $requiredOrderTypes }
Write-Check "symbol-capability" ($marketKnown -and $marketTrading -and $missingOrderTypes.Count -eq 0) ("symbol={0} status={1} missingOrderTypes={2}" -f $Symbol, $(if ($marketKnown) { $market[0].status } else { "UNKNOWN" }), $(if ($missingOrderTypes.Count) { $missingOrderTypes -join "," } else { "none" }))
if ($script:failed) { Stop-Safely "Public Testnet capability or clock evidence is unsafe or unknown." 3 }

$script:apiKey = [Environment]::GetEnvironmentVariable($apiKeyVariable)
$script:apiSecret = [Environment]::GetEnvironmentVariable($apiSecretVariable)
if ([string]::IsNullOrWhiteSpace($script:apiKey) -or [string]::IsNullOrWhiteSpace($script:apiSecret)) {
    Stop-Safely "Credential environment variables are missing; private checks were not attempted." 4
}

try {
    $account = Invoke-BinanceGet "/fapi/v2/account" @{} $true
    Write-Check "read-permission" ($null -ne $account.totalWalletBalance) "signed account read completed"
    Write-Check "trade-permission" ($account.canTrade -eq $true) "canTrade=$($account.canTrade)"
    Write-Check "withdraw-disabled" ($account.canWithdraw -ne $true) "canWithdraw=$($account.canWithdraw)"

    $tradingStatus = Invoke-BinanceGet "/fapi/v1/apiTradingStatus" @{} $true
    $indicator = $tradingStatus.indicators
    $isLocked = $null -ne $indicator -and $indicator.isLocked -eq $true
    Write-Check "api-trading-status" (-not $isLocked -and $null -ne $indicator) "known=$($null -ne $indicator) locked=$isLocked"
} catch {
    Stop-Safely ("Private permission/capability check failed: {0}" -f $_.Exception.GetType().Name) 5
} finally {
    $script:apiKey = $null
    $script:apiSecret = $null
}

if ($script:failed) { Stop-Safely "Private permission or capability evidence is unsafe or unknown." 6 }
Write-Host "Smoke passed: official Testnet, Review, Mainnet disabled, clock, permissions, and capability are confirmed."
Write-Host "Dry-run completed. No order or other exchange mutation was attempted."
exit 0

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ValidateFiltersOnly
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\WPE.Tests\WPE.Tests.csproj"

$gates = @(
    [ordered]@{ Name = "Authorization UI"; Classes = @("TradingAuthorizationUiBoundaryTests") },
    [ordered]@{ Name = "Authorization policy"; Classes = @("AutoTradingAuthorizationBoundaryTests", "TradingAuthorizationPolicyTests", "TradingAuthorizationSettingsTests") },
    [ordered]@{ Name = "Approval service and stores"; Classes = @("TradingReviewApprovalServiceTests", "TradingApprovalStoreTests", "TradingReviewQueueStoreTests") },
    [ordered]@{ Name = "Runtime authorization projection"; Classes = @("RuntimeAuthorizationProjectionTests", "DesktopRuntimeHostTests") },
    [ordered]@{ Name = "Review execution worker"; Classes = @("TradingReviewExecutionWorkerTests") },
    [ordered]@{ Name = "Review execution processor"; Classes = @("TradingReviewExecutionProcessorTests") },
    [ordered]@{ Name = "Execution gateway and Risk Gate"; Classes = @("TradingExecutionGatewayTests", "ExecutionMutationBoundaryTests", "RiskGateTests") },
    [ordered]@{ Name = "Recovery and idempotency"; Classes = @("UnknownOrderRecoveryTests", "OrderFaultInjectionTests", "OrderIdempotencyTests") },
    [ordered]@{ Name = "Sensitive-data redaction"; Classes = @("SensitiveDataRedactorTests", "AccessRunnerRedactionTests") }
)

function Invoke-DotNetTest([string[]]$Arguments) {
    & dotnet test $project @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE."
    }
}

Write-Host "==> Discovering P0 review tests" -ForegroundColor Cyan
$discovery = & dotnet test $project --configuration $Configuration --nologo --no-restore --list-tests 2>&1 | Out-String
$discoveryExitCode = $LASTEXITCODE
$discovery | Write-Host
if ($discoveryExitCode -ne 0) {
    Write-Error "P0 test discovery failed with exit code $discoveryExitCode. Resolve the build/discovery errors; dependencies must already be restored before running this offline gate."
    exit $discoveryExitCode
}

foreach ($gate in $gates) {
    foreach ($class in $gate.Classes) {
        if ($discovery -notmatch "(?m)^\s*WPE\.Tests\.$([regex]::Escape($class))\.") {
            Write-Error "P0 filter validation failed: WPE.Tests.$class was not discovered."
            exit 1
        }
    }
}

if ($ValidateFiltersOnly) {
    Write-Host "All P0 review filters matched discovered tests. No tests were executed." -ForegroundColor Green
    exit 0
}

foreach ($gate in $gates) {
    Write-Host "==> $($gate.Name)" -ForegroundColor Cyan
    foreach ($class in $gate.Classes) {
        Write-Host "    WPE.Tests.$class"
        try {
            Invoke-DotNetTest @(
                "--configuration", $Configuration,
                "--nologo",
                "--no-build",
                "--no-restore",
                "--filter", "FullyQualifiedName~WPE.Tests.$class."
            )
        } catch {
            Write-Error "P0 release gate failed at '$($gate.Name)' / '$class': $($_.Exception.Message)"
            exit 1
        }
    }
}

Write-Host "P0 review release gate passed. Mainnet, publish, network, and live trading were not invoked." -ForegroundColor Green
exit 0

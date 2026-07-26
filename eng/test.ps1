param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\WPE.Tests\WPE.Tests.csproj"
$output = & dotnet test $project --configuration $Configuration --nologo --list-tests 2>&1 | Out-String
$exitCode = $LASTEXITCODE
$output | Write-Host

if ($exitCode -ne 0) {
    exit $exitCode
}

if ($output -notmatch 'TestDiscoveryGuard_IsDiscovered') {
    Write-Error "Test discovery guard was not found. Treating zero or failed test discovery as an error."
    exit 1
}

& dotnet test $project --configuration $Configuration --nologo --no-restore
exit $LASTEXITCODE

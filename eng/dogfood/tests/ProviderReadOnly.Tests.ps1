$ErrorActionPreference = 'Stop'
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw "ASSERT: $Message" } }

$root = Join-Path ([IO.Path]::GetTempPath()) ('wpe-provider-read-only-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'Test-ProviderReadOnly.ps1'
    . $tool -FixturePath (Join-Path $root 'unused.json')
    function Write-Fixture([string]$Name, [hashtable]$Overrides = @{}) {
        $fixture = [ordered]@{
            schemaVersion = 'wpe.provider-read-only-fixture/1.0'
            provider = 'binance-futures'
            environment = 'Testnet'
            endpointClass = 'TestnetReadOnly'
            endpointTrusted = $true
            sourceTimestampUtc = '2026-07-23T13:00:00.0000000Z'
            observedTimestampUtc = '2026-07-23T13:00:10.0000000Z'
            freshness = 'fixture-input-not-authoritative'
            capability = 'Available'
            readOnly = $true
            mutationCapable = $false
            mutationRequested = $false
            requestedOperation = 'ReadOnlyProbe'
            timedOut = $false
        }
        foreach ($key in $Overrides.Keys) { $fixture[$key] = $Overrides[$key] }
        $path = Join-Path $root "$Name.json"
        $fixture | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8
        $path
    }
    function Check-Rejection([string]$Name, [hashtable]$Overrides, [string]$Code) {
        $result = Invoke-ProviderReadOnlyPreflight (Write-Fixture $Name $Overrides)
        Assert (-not $result.passed) "$Name unexpectedly passed"
        Assert ($result.diagnosticCode -eq $Code) "$Name diagnostic was $($result.diagnosticCode)"
        Assert $result.mutationDisabled "$Name did not prove mutation disabled"
    }

    $golden = Invoke-ProviderReadOnlyPreflight (Write-Fixture 'success')
    Assert $golden.passed 'golden fixture failed'
    Assert (($golden | ConvertTo-Json -Compress) -eq '{"schemaVersion":"wpe.provider-read-only-preflight/1.0","passed":true,"provider":"binance-futures","environment":"Testnet","endpointClass":"TestnetReadOnly","sourceTimestampUtc":"2026-07-23T13:00:00.0000000+00:00","observedTimestampUtc":"2026-07-23T13:00:10.0000000+00:00","freshness":"Fresh","capability":"ReadOnlyAvailable","mutationDisabled":true,"diagnosticCode":"provider.read-only-ready"}') 'golden structured result drifted'

    Check-Rejection 'stale' @{ sourceTimestampUtc = '2026-07-23T12:59:00.0000000Z' } 'provider.stale'
    Check-Rejection 'unsupported' @{ provider = 'unknown-provider' } 'provider.unsupported'
    Check-Rejection 'timeout' @{ timedOut = $true } 'provider.timeout'
    Check-Rejection 'endpoint' @{ endpointClass = 'AccountPrivate' } 'provider.endpoint-unsupported'
    Check-Rejection 'mainnet' @{ environment = 'Mainnet'; endpointClass = 'PublicMarket' } 'provider.mainnet-forbidden'
    Check-Rejection 'mutation-request' @{ mutationRequested = $true; requestedOperation = 'PlaceOrder' } 'provider.mutation-forbidden'
    Check-Rejection 'mutation-capable' @{ mutationCapable = $true } 'provider.mutation-forbidden'
    Check-Rejection 'future' @{ sourceTimestampUtc = '2026-07-23T13:00:11.0000000Z' } 'provider.timestamp-future'
    Check-Rejection 'environment-case' @{ environment = 'testnet' } 'provider.fixture.noncanonical'
    Check-Rejection 'environment-whitespace' @{ environment = 'Testnet ' } 'provider.fixture.noncanonical'
    Check-Rejection 'endpoint-trusted-number' @{ endpointTrusted = 1 } 'provider.fixture.noncanonical'
    Check-Rejection 'read-only-string' @{ readOnly = 'true' } 'provider.fixture.noncanonical'
    Check-Rejection 'mutation-capable-number' @{ mutationCapable = 0 } 'provider.fixture.noncanonical'
    Check-Rejection 'mutation-requested-string' @{ mutationRequested = 'false' } 'provider.fixture.noncanonical'
    Check-Rejection 'timeout-string' @{ timedOut = 'false' } 'provider.fixture.noncanonical'

    $duplicateEnvironment = Join-Path $root 'duplicate-environment.json'
    '{"schemaVersion":"wpe.provider-read-only-fixture/1.0","provider":"binance-futures","environment":"Mainnet","environment":"Testnet","endpointClass":"TestnetReadOnly","endpointTrusted":true,"sourceTimestampUtc":"2026-07-23T13:00:00.0000000Z","observedTimestampUtc":"2026-07-23T13:00:10.0000000Z","capability":"Available","readOnly":true,"mutationCapable":false,"mutationRequested":false,"requestedOperation":"ReadOnlyProbe","timedOut":false}' | Set-Content -LiteralPath $duplicateEnvironment -Encoding utf8
    $duplicateEnvironmentResult = Invoke-ProviderReadOnlyPreflight $duplicateEnvironment
    Assert (-not $duplicateEnvironmentResult.passed) 'duplicate environment unexpectedly passed'
    Assert ($duplicateEnvironmentResult.diagnosticCode -eq 'provider.fixture.duplicate-key') "duplicate environment diagnostic was $($duplicateEnvironmentResult.diagnosticCode)"
    Assert $duplicateEnvironmentResult.mutationDisabled 'duplicate environment did not remain mutation-disabled'

    $duplicateBoolean = Join-Path $root 'duplicate-read-only.json'
    '{"schemaVersion":"wpe.provider-read-only-fixture/1.0","provider":"binance-futures","environment":"Testnet","endpointClass":"TestnetReadOnly","endpointTrusted":true,"sourceTimestampUtc":"2026-07-23T13:00:00.0000000Z","observedTimestampUtc":"2026-07-23T13:00:10.0000000Z","capability":"Available","readOnly":false,"ReadOnly":true,"mutationCapable":false,"mutationRequested":false,"requestedOperation":"ReadOnlyProbe","timedOut":false}' | Set-Content -LiteralPath $duplicateBoolean -Encoding utf8
    $duplicateBooleanResult = Invoke-ProviderReadOnlyPreflight $duplicateBoolean
    Assert (-not $duplicateBooleanResult.passed) 'case-variant duplicate readOnly unexpectedly passed'
    Assert ($duplicateBooleanResult.diagnosticCode -eq 'provider.fixture.duplicate-key') "duplicate readOnly diagnostic was $($duplicateBooleanResult.diagnosticCode)"
    Assert $duplicateBooleanResult.mutationDisabled 'duplicate readOnly did not remain mutation-disabled'

    $malformed = Join-Path $root 'malformed.json'; '{' | Set-Content -LiteralPath $malformed -Encoding utf8
    Assert ((Invoke-ProviderReadOnlyPreflight $malformed).diagnosticCode -eq 'provider.fixture.malformed') 'malformed fixture did not fail closed'
    $sensitive = Write-Fixture 'sensitive'; $raw = Get-Content -Raw $sensitive | ConvertFrom-Json; $raw | Add-Member -NotePropertyName apiKey -NotePropertyValue 'must-not-echo'; $raw | ConvertTo-Json | Set-Content $sensitive
    $sensitiveResult = Invoke-ProviderReadOnlyPreflight $sensitive
    Assert ($sensitiveResult.diagnosticCode -eq 'provider.fixture.sensitive-fields') 'sensitive fixture was not rejected'
    Assert (($sensitiveResult | ConvertTo-Json) -notmatch 'must-not-echo') 'sensitive value leaked'

    $tokens = $null; $errors = $null; $ast = [Management.Automation.Language.Parser]::ParseFile($tool, [ref]$tokens, [ref]$errors)
    Assert ($errors.Count -eq 0) 'preflight script has parse errors'
    $commands = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true) | ForEach-Object GetCommandName)
    foreach ($forbidden in @('Invoke-WebRequest','Invoke-RestMethod','curl','wget','GetAccountAsync','GetPositionsAsync','GetOpenOrdersAsync','PlaceMarketAsync','PlaceLimitAsync','PlaceProtectionAsync','CancelOrderAsync')) {
        Assert ($commands -notcontains $forbidden) "network or mutation command present: $forbidden"
    }
    'PASS ProviderReadOnly canonical-types/duplicate-keys plus adjacent invariants; golden stable; no-network; no-mutation; sensitive-safe'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }

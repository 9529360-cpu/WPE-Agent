[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FixturePath,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$script:ProviderReadOnlySchema = 'wpe.provider-read-only-preflight/1.0'
$script:FixtureSchema = 'wpe.provider-read-only-fixture/1.0'
$script:MaximumAgeSeconds = 30
$script:SupportedProviders = @('binance-futures', 'binance-public', 'okx', 'bybit', 'gate', 'bitget')
$script:RequiredFixtureProperties = @('schemaVersion','provider','environment','endpointClass','endpointTrusted','sourceTimestampUtc','observedTimestampUtc','capability','readOnly','mutationCapable','mutationRequested','requestedOperation','timedOut')
$script:AllowedFixtureProperties = $script:RequiredFixtureProperties + @('freshness')
$script:BooleanFixtureProperties = @('endpointTrusted','readOnly','mutationCapable','mutationRequested','timedOut')
$script:StringFixtureProperties = @('schemaVersion','provider','environment','endpointClass','sourceTimestampUtc','observedTimestampUtc','capability','requestedOperation')

function New-ProviderReadOnlyResult {
    param(
        [bool]$Passed,
        [string]$Provider = 'unknown',
        [string]$Environment = 'Unknown',
        [string]$EndpointClass = 'Unknown',
        [AllowNull()][string]$SourceTimestampUtc,
        [AllowNull()][string]$ObservedTimestampUtc,
        [string]$Freshness = 'Unknown',
        [string]$Capability = 'Unavailable',
        [string]$DiagnosticCode = 'provider.preflight.malformed'
    )
    [pscustomobject][ordered]@{
        schemaVersion = $script:ProviderReadOnlySchema
        passed = $Passed
        provider = $Provider
        environment = $Environment
        endpointClass = $EndpointClass
        sourceTimestampUtc = $SourceTimestampUtc
        observedTimestampUtc = $ObservedTimestampUtc
        freshness = $Freshness
        capability = $Capability
        mutationDisabled = $true
        diagnosticCode = $DiagnosticCode
    }
}

function Test-SafeIdentifier([object]$Value) {
    $Value -is [string] -and $Value -match '^[a-z0-9][a-z0-9._-]{0,63}$'
}

function Test-DuplicateSecurityProperty([string]$Raw) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($match in [regex]::Matches($Raw, '"(?<name>(?:\\.|[^"\\])*)"\s*:')) {
        $name = $match.Groups['name'].Value
        if ($script:RequiredFixtureProperties -icontains $name -and -not $seen.Add($name)) { return $true }
    }
    $false
}

function Test-CanonicalFixture([object]$Fixture) {
    $names = @($Fixture.PSObject.Properties.Name)
    foreach ($required in $script:RequiredFixtureProperties) {
        if ($names -cnotcontains $required) { return $false }
    }
    foreach ($name in $names) {
        if ($script:AllowedFixtureProperties -cnotcontains $name) { return $false }
    }
    foreach ($name in $script:BooleanFixtureProperties) {
        if ($Fixture.PSObject.Properties[$name].Value -isnot [bool]) { return $false }
    }
    foreach ($name in $script:StringFixtureProperties) {
        if ($Fixture.PSObject.Properties[$name].Value -isnot [string]) { return $false }
    }
    $true
}

function Convert-StrictUtc([object]$Value) {
    if ($Value -isnot [string] -or $Value -notmatch 'Z$') { return $null }
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact($Value, 'o', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) { return $null }
    if ($parsed.Offset -ne [TimeSpan]::Zero) { return $null }
    $parsed
}

function Convert-ProviderFixtureJson([string]$Raw) {
    $convert = Get-Command ConvertFrom-Json
    if ($convert.Parameters.ContainsKey('DateKind')) { return ($Raw | ConvertFrom-Json -DateKind String) }
    $fixture = $Raw | ConvertFrom-Json
    foreach ($name in @('sourceTimestampUtc','observedTimestampUtc')) {
        $property = $fixture.PSObject.Properties[$name]
        if ($null -eq $property -or ($property.Value -isnot [DateTime] -and $property.Value -isnot [DateTimeOffset])) { continue }
        $match = [regex]::Match($Raw, '"' + [regex]::Escape($name) + '"\s*:\s*"(?<value>[^"\\]*)"')
        if ($match.Success) { $fixture.$name = $match.Groups['value'].Value }
    }
    $fixture
}

function Invoke-ProviderReadOnlyPreflight {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $provider = 'unknown'; $environment = 'Unknown'; $endpointClass = 'Unknown'
    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return New-ProviderReadOnlyResult $false -DiagnosticCode 'provider.fixture.missing' }
        $raw = Get-Content -Raw -LiteralPath $Path
        if (Test-DuplicateSecurityProperty $raw) {
            return New-ProviderReadOnlyResult $false -DiagnosticCode 'provider.fixture.duplicate-key'
        }
        if ($raw -match '(?i)"(api.?key|secret|signature|account.?id|user.?id|email|phone|private.?key|token)"\s*:') {
            return New-ProviderReadOnlyResult $false -DiagnosticCode 'provider.fixture.sensitive-fields'
        }
        $fixture = Convert-ProviderFixtureJson $raw
        if ($fixture.schemaVersion -isnot [string] -or $fixture.schemaVersion -cne $script:FixtureSchema) { return New-ProviderReadOnlyResult $false -DiagnosticCode 'provider.fixture.schema-unsupported' }
        if (-not (Test-CanonicalFixture $fixture)) { return New-ProviderReadOnlyResult $false -DiagnosticCode 'provider.fixture.noncanonical' }
        $canonicalEnvironments = @('Testnet','Sandbox','PublicReadOnly','Mainnet')
        if ($fixture.environment -cne $fixture.environment.Trim() -or ($canonicalEnvironments -icontains $fixture.environment -and $canonicalEnvironments -cnotcontains $fixture.environment)) {
            return New-ProviderReadOnlyResult $false -DiagnosticCode 'provider.fixture.noncanonical'
        }

        if (Test-SafeIdentifier $fixture.provider) { $provider = [string]$fixture.provider }
        if ($fixture.environment -is [string]) { $environment = [string]$fixture.environment }
        if ($fixture.endpointClass -is [string]) { $endpointClass = [string]$fixture.endpointClass }
        if ($provider -notin $script:SupportedProviders) { return New-ProviderReadOnlyResult $false $provider $environment $endpointClass -DiagnosticCode 'provider.unsupported' }
        if ($environment -ceq 'Mainnet') { return New-ProviderReadOnlyResult $false $provider $environment $endpointClass -DiagnosticCode 'provider.mainnet-forbidden' }
        if (@('Testnet', 'Sandbox', 'PublicReadOnly') -cnotcontains $environment) { return New-ProviderReadOnlyResult $false $provider $environment $endpointClass -DiagnosticCode 'provider.environment-unsupported' }

        $expectedEndpoint = @{ Testnet = 'TestnetReadOnly'; Sandbox = 'SandboxReadOnly'; PublicReadOnly = 'PublicMarket' }[$environment]
        if ($endpointClass -cne $expectedEndpoint -or $fixture.endpointTrusted -ne $true) {
            return New-ProviderReadOnlyResult $false $provider $environment $endpointClass -DiagnosticCode 'provider.endpoint-unsupported'
        }
        if ($fixture.timedOut -eq $true) { return New-ProviderReadOnlyResult $false $provider $environment $endpointClass -DiagnosticCode 'provider.timeout' }
        if ($fixture.requestedOperation -ne 'ReadOnlyProbe' -or $fixture.mutationRequested -eq $true -or $fixture.mutationCapable -ne $false -or $fixture.readOnly -ne $true) {
            return New-ProviderReadOnlyResult $false $provider $environment $endpointClass -Capability 'Rejected' -DiagnosticCode 'provider.mutation-forbidden'
        }
        if ($fixture.capability -ne 'Available') { return New-ProviderReadOnlyResult $false $provider $environment $endpointClass -Capability 'Unavailable' -DiagnosticCode 'provider.capability-unavailable' }

        $source = Convert-StrictUtc $fixture.sourceTimestampUtc
        $observed = Convert-StrictUtc $fixture.observedTimestampUtc
        if ($null -eq $source -or $null -eq $observed) { return New-ProviderReadOnlyResult $false $provider $environment $endpointClass -DiagnosticCode 'provider.timestamp-malformed' }
        if ($source -gt $observed) { return New-ProviderReadOnlyResult $false $provider $environment $endpointClass $source.ToString('o') $observed.ToString('o') 'Invalid' -DiagnosticCode 'provider.timestamp-future' }
        if (($observed - $source).TotalSeconds -gt $script:MaximumAgeSeconds) { return New-ProviderReadOnlyResult $false $provider $environment $endpointClass $source.ToString('o') $observed.ToString('o') 'Stale' -DiagnosticCode 'provider.stale' }

        New-ProviderReadOnlyResult $true $provider $environment $endpointClass $source.ToString('o') $observed.ToString('o') 'Fresh' 'ReadOnlyAvailable' 'provider.read-only-ready'
    }
    catch {
        New-ProviderReadOnlyResult $false $provider $environment $endpointClass -DiagnosticCode 'provider.fixture.malformed'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $result = Invoke-ProviderReadOnlyPreflight -Path $FixturePath
    if ($AsJson) { $result | ConvertTo-Json -Compress } else { $result }
    if (-not $result.passed) { exit 2 }
}

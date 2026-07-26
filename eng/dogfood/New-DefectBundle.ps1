[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputPath,
    [Parameter(Mandatory)][string]$OutputRoot,
    [int]$MaximumInputBytes = 65536
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$schemaVersion = 'wpe.dogfood-defect-bundle/1.0'
$rollbackClasses = @('safety', 'authority', 'runtime-bridge', 'startup', 'permission')
$sensitiveKeys = '(?i)(credential|secret|password|token|signature|private.?key|api.?key|account.?id|user.?id|email|phone|destination|recipient|address|session.?id|device.?id)'

function Get-Sha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function ConvertTo-SafeText([object]$Value, [int]$MaximumLength) {
    $text = if ($null -eq $Value) { '' } else { [string]$Value }
    $text = $text -replace '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b', '[redacted-email]'
    $text = $text -replace '(?<![A-Za-z0-9])(?:\+?\d[\d .()-]{7,}\d)(?![A-Za-z0-9])', '[redacted-phone]'
    $text = $text -replace '(?i)\b(?:bearer|token|secret|signature|password|api.?key)\s*[:=]?\s*[^\s,;]+', '[redacted-sensitive]'
    $text = $text -replace '(?i)\b(?:account|user|recipient|destination|address)(?:[-_ ]?id)?\s*[:=]\s*[^\s,;]+', '[redacted-identifier]'
    $text = $text -replace '(?<![0-9])(?:\d{1,3}\.){3}\d{1,3}(?![0-9])', '[redacted-ip]'
    $text = $text -replace '[\x00-\x08\x0B\x0C\x0E-\x1F]', ''
    if ($text.Length -gt $MaximumLength) { return $text.Substring(0, $MaximumLength) }
    return $text
}

function Get-RequiredText([object]$InputObject, [string]$Name, [int]$MaximumLength) {
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) { throw "input.$Name-required" }
    return ConvertTo-SafeText $property.Value $MaximumLength
}

function Get-SafeReferences([object]$InputObject, [string]$Name) {
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return @() }
    $items = @($property.Value)
    if ($items.Count -gt 20) { throw "input.$Name-too-many" }
    $result = @()
    foreach ($item in $items) {
        $reference = [string]$item
        if ([string]::IsNullOrWhiteSpace($reference) -or $reference.Length -gt 256 -or
            $reference -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($reference) -or
            $reference -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]*$') { throw "input.$Name-invalid-reference" }
        $result += $reference.Replace('\', '/')
    }
    return @($result)
}

$resolvedInput = (Resolve-Path -LiteralPath $InputPath).Path
$inputFile = Get-Item -LiteralPath $resolvedInput
if (-not $inputFile.PSIsContainer -and $inputFile.Length -gt 0 -and $inputFile.Length -le $MaximumInputBytes) { }
else { throw 'input.size-invalid' }

try { $inputObject = Get-Content -LiteralPath $resolvedInput -Raw | ConvertFrom-Json }
catch { throw 'input.malformed-json' }
if ($null -eq $inputObject -or $inputObject -is [Array]) { throw 'input.object-required' }

foreach ($property in $inputObject.PSObject.Properties) {
    if ($property.Name -match $sensitiveKeys) { $property.Value = '[redacted]' }
}

$version = Get-RequiredText $inputObject 'version' 64
$sourceIdentity = Get-RequiredText $inputObject 'sourceIdentity' 128
$manifestSha256 = (Get-RequiredText $inputObject 'manifestSha256' 64).ToLowerInvariant()
$environment = Get-RequiredText $inputObject 'environment' 16
$diagnosticCode = Get-RequiredText $inputObject 'diagnosticCode' 128
$failureClass = (Get-RequiredText $inputObject 'failureClass' 32).ToLowerInvariant()
$severity = (Get-RequiredText $inputObject 'severity' 2).ToUpperInvariant()
$observedAtUtc = Get-RequiredText $inputObject 'observedAtUtc' 64
if ($sourceIdentity -notmatch '^[A-Za-z0-9._/-]{1,128}$' -or $manifestSha256 -notmatch '^[a-f0-9]{64}$' -or
    $environment -ne 'Testnet' -or $diagnosticCode -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+){1,7}$' -or
    $failureClass -notin @('safety','authority','runtime-bridge','startup','permission','health','data','ui','other') -or
    $severity -notin @('P0','P1','P2','P3')) { throw 'input.contract-invalid' }
$timestamp = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse($observedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$timestamp) -or
    $timestamp.Offset -ne [TimeSpan]::Zero) { throw 'input.observedAtUtc-invalid' }
$observedAtUtc = $timestamp.ToUniversalTime().ToString('O')

$logs = @()
$logsProperty = $inputObject.PSObject.Properties['logs']
if ($null -ne $logsProperty) {
    $rawLogs = @($logsProperty.Value)
    if ($rawLogs.Count -gt 20) { throw 'input.logs-too-many' }
    foreach ($line in $rawLogs) { $logs += (ConvertTo-SafeText $line 512) }
}

$route = if ($severity -in @('P0','P1') -or $failureClass -in $rollbackClasses) { 'immediate-rollback' } else { 'next-batch' }
$payload = [ordered]@{
    schemaVersion = $schemaVersion
    release = [ordered]@{ version=$version; sourceIdentity=$sourceIdentity; manifestSha256=$manifestSha256; environment=$environment }
    diagnosticCode = $diagnosticCode
    failureClass = $failureClass
    severity = $severity
    routing = $route
    observedAtUtc = $observedAtUtc
    runtimeHealth = Get-RequiredText $inputObject 'runtimeHealth' 512
    minimalInput = Get-RequiredText $inputObject 'minimalInput' 2048
    expected = Get-RequiredText $inputObject 'expected' 2048
    observed = Get-RequiredText $inputObject 'observed' 2048
    boundedLogs = @($logs)
    evidenceReferences = @(Get-SafeReferences $inputObject 'evidenceReferences')
    screenshotReferences = @(Get-SafeReferences $inputObject 'screenshotReferences')
}
$utf8 = New-Object Text.UTF8Encoding($false)
$payloadBytes = $utf8.GetBytes(($payload | ConvertTo-Json -Depth 8 -Compress))
$payloadHash = Get-Sha256 $payloadBytes
$defectId = 'WPE-' + $payloadHash.Substring(0,16).ToUpperInvariant()
$bundle = [ordered]@{
    schemaVersion=$schemaVersion; defectId=$defectId; bundleHash=$payloadHash; release=$payload.release
    diagnosticCode=$payload.diagnosticCode; failureClass=$payload.failureClass; severity=$payload.severity; routing=$payload.routing
    observedAtUtc=$payload.observedAtUtc; runtimeHealth=$payload.runtimeHealth; minimalInput=$payload.minimalInput
    expected=$payload.expected; observed=$payload.observed; boundedLogs=$payload.boundedLogs
    evidenceReferences=$payload.evidenceReferences; screenshotReferences=$payload.screenshotReferences
    permanentRegression=[ordered]@{required=$true;regressionId=('REG-' + $defectId)}
}
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
[IO.Directory]::CreateDirectory($outputFull) | Out-Null
$outputPath = Join-Path $outputFull ($defectId + '.json')
if (-not ([IO.Path]::GetFullPath($outputPath)).StartsWith($outputFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'output.path-invalid' }
[IO.File]::WriteAllText($outputPath, ($bundle | ConvertTo-Json -Depth 8 -Compress), $utf8)
[pscustomobject]@{ DefectId=$defectId; BundleHash=$payloadHash; Routing=$route; Path=$outputPath }

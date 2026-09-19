[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$ExpectedSourceIdentity,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}
    [ValidateRange(0,1000)][int]$MaximumUnhealthySamples = 0
)
$ErrorActionPreference='Stop'

function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Parse-Utc($value,[string]$code){
    try {
        $parsed=[DateTimeOffset]::Parse([string]$value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
        if($parsed.Offset -ne [TimeSpan]::Zero){throw $code}
        return $parsed
    } catch { throw $code }
}

$path=[IO.Path]::GetFullPath($EvidencePath)
if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'soak.evidence-missing'}
try{$evidence=Get-Content -Raw -LiteralPath $path|ConvertFrom-Json}catch{throw 'soak.evidence-malformed'}
$allowed=@(
 'schemaVersion','status','sourceIdentity','candidateManifestSha256','startedAtUtc','completedAtUtc','requestedDurationSeconds','observedDurationSeconds',
 'pollSeconds','startupGraceSeconds','maximumHealthAgeSeconds','sampleCount','expectedMinimumSamples',
 'readySamples','graceSamples','unhealthySamples','invalidSamples','maximumObservedHealthAgeSeconds',
 'samplesFile','samplesSha256','reasonCounts'
)
if(@($evidence.PSObject.Properties.Name|Where-Object{$allowed -notcontains $_}).Count){throw 'soak.evidence-unknown-field'}
if($evidence.schemaVersion -ne 'wpe.headless-soak-evidence/1.0'){throw 'soak.evidence-schema-invalid'}
if($evidence.status -ne 'passed'){throw 'soak.evidence-not-passed'}
if([string]$evidence.sourceIdentity -ne $ExpectedSourceIdentity){throw 'soak.source-identity-mismatch'}
if([string]$evidence.candidateManifestSha256 -ne $ExpectedCandidateManifestSha256.ToLowerInvariant()){throw 'soak.candidate-manifest-mismatch'}
$started=Parse-Utc $evidence.startedAtUtc 'soak.started-at-invalid'
$completed=Parse-Utc $evidence.completedAtUtc 'soak.completed-at-invalid'
if($completed -le $started){throw 'soak.time-order-invalid'}
$minimum=[TimeSpan]::FromMinutes($MinimumDurationMinutes)
if(($completed-$started) -lt $minimum){throw 'soak.duration-insufficient'}
if([int]$evidence.sampleCount -lt [int]$evidence.expectedMinimumSamples){throw 'soak.sample-coverage-insufficient'}
if([int]$evidence.unhealthySamples -gt $MaximumUnhealthySamples){throw 'soak.unhealthy-samples-exceeded'}
if([int]$evidence.invalidSamples -ne 0){throw 'soak.invalid-samples-present'}
if([double]$evidence.maximumObservedHealthAgeSeconds -gt [double]$evidence.maximumHealthAgeSeconds){throw 'soak.health-age-exceeded'}
if([string]::IsNullOrWhiteSpace([string]$evidence.samplesFile) -or [IO.Path]::IsPathRooted([string]$evidence.samplesFile)){throw 'soak.samples-path-invalid'}
if(([string]$evidence.samplesFile).Contains('..')){throw 'soak.samples-path-invalid'}
if(([string]$evidence.samplesSha256 -notmatch '^[a-f0-9]{64}$')){throw 'soak.samples-hash-invalid'}

$directory=Split-Path -Parent $path
$samples=[IO.Path]::GetFullPath((Join-Path $directory ([string]$evidence.samplesFile)))
$prefix=[IO.Path]::GetFullPath($directory).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
if(-not $samples.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'soak.samples-path-escape'}
if(-not(Test-Path -LiteralPath $samples -PathType Leaf)){throw 'soak.samples-missing'}
if((Hash $samples) -ne [string]$evidence.samplesSha256){throw 'soak.samples-hash-mismatch'}

$sampleAllowed=@(
 'schemaVersion','sampledAtUtc','observedAtUtc','healthAgeSeconds','processState','processCode',
 'runtimeReady','agentRunning','accessFresh','heartbeatFresh','leaseLost','inStartupGrace','accepted','reason'
)
$count=0
Get-Content -LiteralPath $samples | ForEach-Object {
    if([string]::IsNullOrWhiteSpace($_)){throw 'soak.sample-empty'}
    try{$sample=$_|ConvertFrom-Json}catch{throw 'soak.sample-malformed'}
    if(@($sample.PSObject.Properties.Name|Where-Object{$sampleAllowed -notcontains $_}).Count){throw 'soak.sample-unknown-field'}
    if($sample.schemaVersion -ne 'wpe.headless-soak-sample/1.0'){throw 'soak.sample-schema-invalid'}
    if([bool]$sample.leaseLost){throw 'soak.sample-lease-lost'}
    $count++
}
if($count -ne [int]$evidence.sampleCount){throw 'soak.sample-count-mismatch'}

[pscustomobject]@{
    Valid=$true
    Status='passed'
    SourceIdentity=[string]$evidence.sourceIdentity
    CandidateManifestSha256=[string]$evidence.candidateManifestSha256
    DurationSeconds=[Math]::Round(($completed-$started).TotalSeconds,3)
    SampleCount=$count
    SamplesSha256=[string]$evidence.samplesSha256
}
)][string]$ExpectedCandidateManifestSha256,
    [ValidateRange(1,2880)][int]$MinimumDurationMinutes = 60,
    [ValidateRange(0,1000)][int]$MaximumUnhealthySamples = 0
)
$ErrorActionPreference='Stop'

function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Parse-Utc($value,[string]$code){
    try {
        $parsed=[DateTimeOffset]::Parse([string]$value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
        if($parsed.Offset -ne [TimeSpan]::Zero){throw $code}
        return $parsed
    } catch { throw $code }
}

$path=[IO.Path]::GetFullPath($EvidencePath)
if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'soak.evidence-missing'}
try{$evidence=Get-Content -Raw -LiteralPath $path|ConvertFrom-Json}catch{throw 'soak.evidence-malformed'}
$allowed=@(
 'schemaVersion','status','startedAtUtc','completedAtUtc','requestedDurationSeconds','observedDurationSeconds',
 'pollSeconds','startupGraceSeconds','maximumHealthAgeSeconds','sampleCount','expectedMinimumSamples',
 'readySamples','graceSamples','unhealthySamples','invalidSamples','maximumObservedHealthAgeSeconds',
 'samplesFile','samplesSha256','reasonCounts'
)
if(@($evidence.PSObject.Properties.Name|Where-Object{$allowed -notcontains $_}).Count){throw 'soak.evidence-unknown-field'}
if($evidence.schemaVersion -ne 'wpe.headless-soak-evidence/1.0'){throw 'soak.evidence-schema-invalid'}
if($evidence.status -ne 'passed'){throw 'soak.evidence-not-passed'}
$started=Parse-Utc $evidence.startedAtUtc 'soak.started-at-invalid'
$completed=Parse-Utc $evidence.completedAtUtc 'soak.completed-at-invalid'
if($completed -le $started){throw 'soak.time-order-invalid'}
$minimum=[TimeSpan]::FromMinutes($MinimumDurationMinutes)
if(($completed-$started) -lt $minimum){throw 'soak.duration-insufficient'}
if([int]$evidence.sampleCount -lt [int]$evidence.expectedMinimumSamples){throw 'soak.sample-coverage-insufficient'}
if([int]$evidence.unhealthySamples -gt $MaximumUnhealthySamples){throw 'soak.unhealthy-samples-exceeded'}
if([int]$evidence.invalidSamples -ne 0){throw 'soak.invalid-samples-present'}
if([double]$evidence.maximumObservedHealthAgeSeconds -gt [double]$evidence.maximumHealthAgeSeconds){throw 'soak.health-age-exceeded'}
if([string]::IsNullOrWhiteSpace([string]$evidence.samplesFile) -or [IO.Path]::IsPathRooted([string]$evidence.samplesFile)){throw 'soak.samples-path-invalid'}
if(([string]$evidence.samplesFile).Contains('..')){throw 'soak.samples-path-invalid'}
if(([string]$evidence.samplesSha256 -notmatch '^[a-f0-9]{64}$')){throw 'soak.samples-hash-invalid'}

$directory=Split-Path -Parent $path
$samples=[IO.Path]::GetFullPath((Join-Path $directory ([string]$evidence.samplesFile)))
$prefix=[IO.Path]::GetFullPath($directory).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
if(-not $samples.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'soak.samples-path-escape'}
if(-not(Test-Path -LiteralPath $samples -PathType Leaf)){throw 'soak.samples-missing'}
if((Hash $samples) -ne [string]$evidence.samplesSha256){throw 'soak.samples-hash-mismatch'}

$sampleAllowed=@(
 'schemaVersion','sampledAtUtc','observedAtUtc','healthAgeSeconds','processState','processCode',
 'runtimeReady','agentRunning','accessFresh','heartbeatFresh','leaseLost','inStartupGrace','accepted','reason'
)
$count=0
Get-Content -LiteralPath $samples | ForEach-Object {
    if([string]::IsNullOrWhiteSpace($_)){throw 'soak.sample-empty'}
    try{$sample=$_|ConvertFrom-Json}catch{throw 'soak.sample-malformed'}
    if(@($sample.PSObject.Properties.Name|Where-Object{$sampleAllowed -notcontains $_}).Count){throw 'soak.sample-unknown-field'}
    if($sample.schemaVersion -ne 'wpe.headless-soak-sample/1.0'){throw 'soak.sample-schema-invalid'}
    if([bool]$sample.leaseLost){throw 'soak.sample-lease-lost'}
    $count++
}
if($count -ne [int]$evidence.sampleCount){throw 'soak.sample-count-mismatch'}

[pscustomobject]@{
    Valid=$true
    Status='passed'
    DurationSeconds=[Math]::Round(($completed-$started).TotalSeconds,3)
    SampleCount=$count
    SamplesSha256=[string]$evidence.samplesSha256
}

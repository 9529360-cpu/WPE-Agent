[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$ExpectedSourceIdentity,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedCandidateManifestSha256,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedExecutableSha256,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedEvidenceSha256,
    [ValidateRange(1,2880)][int]$MinimumDurationMinutes = 60,
    [ValidateRange(0,1000)][int]$MaximumUnhealthySamples = 0
)

$ErrorActionPreference='Stop'

function Get-FileSnapshot([string]$path){
    $bytes=[IO.File]::ReadAllBytes($path)
    $sha=[Security.Cryptography.SHA256]::Create()
    try{
        $hash=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()
    }finally{
        $sha.Dispose()
    }
    [pscustomobject]@{Bytes=$bytes;Hash=$hash}
}

function Convert-SnapshotToText($snapshot){
    $stream=[IO.MemoryStream]::new($snapshot.Bytes,$false)
    $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true)
    try{
        $reader.ReadToEnd()
    }finally{
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Parse-Utc($value,[string]$code){
    try{
        $parsed=[DateTimeOffset]::Parse(
            [string]$value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind)
        if($parsed.Offset -ne [TimeSpan]::Zero){throw $code}
        return $parsed
    }catch{
        throw $code
    }
}

$path=[IO.Path]::GetFullPath($EvidencePath)
if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'soak.evidence-missing'}

$evidenceSnapshot=Get-FileSnapshot $path
if(-not [string]::IsNullOrWhiteSpace($ExpectedEvidenceSha256) -and
   $evidenceSnapshot.Hash -ne $ExpectedEvidenceSha256.ToLowerInvariant()){
    throw 'soak.evidence-hash-mismatch'
}
try{
    $evidence=(Convert-SnapshotToText $evidenceSnapshot)|ConvertFrom-Json
}catch{
    throw 'soak.evidence-malformed'
}

$allowed=@(
    'schemaVersion','status','sourceIdentity','candidateManifestSha256','executableSha256','startedAtUtc','completedAtUtc',
    'requestedDurationSeconds','observedDurationSeconds','pollSeconds','startupGraceSeconds',
    'maximumHealthAgeSeconds','sampleCount','expectedMinimumSamples','readySamples','graceSamples',
    'unhealthySamples','invalidSamples','maximumObservedHealthAgeSeconds','samplesFile','samplesSha256',
    'reasonCounts'
)
if(@($evidence.PSObject.Properties.Name|Where-Object{$allowed -notcontains $_}).Count){
    throw 'soak.evidence-unknown-field'
}
if($evidence.schemaVersion -ne 'wpe.headless-soak-evidence/1.0'){throw 'soak.evidence-schema-invalid'}
if($evidence.status -ne 'passed'){throw 'soak.evidence-not-passed'}
if([string]$evidence.sourceIdentity -ne $ExpectedSourceIdentity){throw 'soak.source-identity-mismatch'}
if([string]$evidence.candidateManifestSha256 -ne $ExpectedCandidateManifestSha256.ToLowerInvariant()){
    throw 'soak.candidate-manifest-mismatch'
}
if([string]$evidence.executableSha256 -notmatch '^[a-f0-9]{64}$'){throw 'soak.executable-hash-invalid'}
if(-not [string]::IsNullOrWhiteSpace($ExpectedExecutableSha256) -and [string]$evidence.executableSha256 -ne $ExpectedExecutableSha256.ToLowerInvariant()){
    throw 'soak.executable-hash-mismatch'
}

$started=Parse-Utc $evidence.startedAtUtc 'soak.started-at-invalid'
$completed=Parse-Utc $evidence.completedAtUtc 'soak.completed-at-invalid'
if($completed -le $started){throw 'soak.time-order-invalid'}

$minimum=[TimeSpan]::FromMinutes($MinimumDurationMinutes)
$actualDuration=$completed-$started
if($actualDuration -lt $minimum){throw 'soak.duration-insufficient'}

$requestedDuration=[int]$evidence.requestedDurationSeconds
$pollSeconds=[int]$evidence.pollSeconds
$startupGraceSeconds=[int]$evidence.startupGraceSeconds
$maximumHealthAgeSeconds=[int]$evidence.maximumHealthAgeSeconds
if($requestedDuration -lt 60 -or $requestedDuration -gt 172800){throw 'soak.requested-duration-invalid'}
if($pollSeconds -lt 2 -or $pollSeconds -gt 60){throw 'soak.poll-interval-invalid'}
if($startupGraceSeconds -lt 0 -or $startupGraceSeconds -gt 600){throw 'soak.startup-grace-invalid'}
if($maximumHealthAgeSeconds -lt 5 -or $maximumHealthAgeSeconds -gt 120){throw 'soak.maximum-health-age-invalid'}
if($actualDuration.TotalSeconds + 1 -lt $requestedDuration){throw 'soak.requested-duration-not-observed'}

$durationToleranceSeconds=[Math]::Max(2,$pollSeconds)
if(($actualDuration.TotalSeconds-$requestedDuration) -gt $durationToleranceSeconds){
    throw 'soak.requested-duration-mismatch'
}

$reportedObserved=[double]$evidence.observedDurationSeconds
if([Math]::Abs($reportedObserved-$actualDuration.TotalSeconds) -gt [Math]::Max(2,$pollSeconds)){
    throw 'soak.observed-duration-mismatch'
}

$computedExpected=[Math]::Max(1,[int][Math]::Floor(($requestedDuration/$pollSeconds)*0.90))
if([int]$evidence.expectedMinimumSamples -ne $computedExpected){throw 'soak.expected-sample-count-invalid'}
if([int]$evidence.sampleCount -lt $computedExpected){throw 'soak.sample-coverage-insufficient'}
if([int]$evidence.unhealthySamples -gt $MaximumUnhealthySamples){throw 'soak.unhealthy-samples-exceeded'}
if([int]$evidence.invalidSamples -ne 0){throw 'soak.invalid-samples-present'}
if([double]$evidence.maximumObservedHealthAgeSeconds -gt [double]$evidence.maximumHealthAgeSeconds){
    throw 'soak.health-age-exceeded'
}
if([string]::IsNullOrWhiteSpace([string]$evidence.samplesFile) -or
   [IO.Path]::IsPathRooted([string]$evidence.samplesFile)){
    throw 'soak.samples-path-invalid'
}
if(([string]$evidence.samplesFile).Contains('..')){throw 'soak.samples-path-invalid'}
if(([string]$evidence.samplesSha256 -notmatch '^[a-f0-9]{64}$')){throw 'soak.samples-hash-invalid'}

$directory=Split-Path -Parent $path
$samples=[IO.Path]::GetFullPath((Join-Path $directory ([string]$evidence.samplesFile)))
$prefix=[IO.Path]::GetFullPath($directory).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
if(-not $samples.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'soak.samples-path-escape'}
if(-not(Test-Path -LiteralPath $samples -PathType Leaf)){throw 'soak.samples-missing'}

$samplesSnapshot=Get-FileSnapshot $samples
if($samplesSnapshot.Hash -ne [string]$evidence.samplesSha256){throw 'soak.samples-hash-mismatch'}

$sampleAllowed=@(
    'schemaVersion','sampledAtUtc','observedAtUtc','healthAgeSeconds','processState','processCode','executableSha256',
    'runtimeReady','agentRunning','accessFresh','heartbeatFresh','leaseLost','inStartupGrace','accepted','reason'
)
$booleanFields=@('runtimeReady','agentRunning','accessFresh','heartbeatFresh','leaseLost','inStartupGrace','accepted')
$count=0
$recomputedReady=0
$recomputedGrace=0
$maximumSampleHealthAge=0.0
$firstSampledAt=$null
$lastSampledAt=$null
$maximumSampleGapSeconds=[Math]::Max(($pollSeconds*3),($pollSeconds+5))

$sampleReader=[IO.StringReader]::new((Convert-SnapshotToText $samplesSnapshot))
try{
    while($null -ne ($sampleLine=$sampleReader.ReadLine())){
        if([string]::IsNullOrWhiteSpace($sampleLine)){throw 'soak.sample-empty'}
        try{
            $sample=$sampleLine|ConvertFrom-Json
        }catch{
            throw 'soak.sample-malformed'
        }
        if(@($sample.PSObject.Properties.Name|Where-Object{$sampleAllowed -notcontains $_}).Count){
            throw 'soak.sample-unknown-field'
        }
        if($sample.schemaVersion -ne 'wpe.headless-soak-sample/1.0'){throw 'soak.sample-schema-invalid'}
        foreach($field in $booleanFields){
            if($sample.$field -isnot [bool]){throw 'soak.sample-boolean-invalid'}
        }
        if([string]$sample.executableSha256 -ne [string]$evidence.executableSha256){throw 'soak.sample-executable-hash-mismatch'}

        $sampledAt=Parse-Utc $sample.sampledAtUtc 'soak.sample-sampled-at-invalid'
        if($sampledAt -lt $started -or $sampledAt -gt $completed.AddSeconds(1)){
            throw 'soak.sample-time-outside-window'
        }
        if($null -eq $firstSampledAt){$firstSampledAt=$sampledAt}
        if($null -ne $lastSampledAt){
            if($sampledAt -le $lastSampledAt){throw 'soak.sample-time-not-strictly-increasing'}
            if(($sampledAt-$lastSampledAt).TotalSeconds -gt $maximumSampleGapSeconds){
                throw 'soak.sample-gap-exceeded'
            }
        }
        $lastSampledAt=$sampledAt

        $expectedGrace=(($sampledAt-$started).TotalSeconds -lt $startupGraceSeconds)
        if([bool]$sample.inStartupGrace -ne $expectedGrace){throw 'soak.sample-grace-window-mismatch'}
        if([bool]$sample.leaseLost){throw 'soak.sample-lease-lost'}
        if(-not [bool]$sample.accepted){throw 'soak.sample-not-accepted'}

        $observedAt=$null
        $computedAge=$null
        if($null -ne $sample.observedAtUtc -and
           -not [string]::IsNullOrWhiteSpace([string]$sample.observedAtUtc)){
            $observedAt=Parse-Utc $sample.observedAtUtc 'soak.sample-observed-at-invalid'
            if($observedAt -gt $sampledAt.AddSeconds(5)){throw 'soak.sample-observation-future'}
            $computedAge=[Math]::Max(0.0,($sampledAt-$observedAt).TotalSeconds)
            if($computedAge -gt $maximumHealthAgeSeconds){throw 'soak.sample-health-age-invalid'}
        }

        $sampleAge=$null
        if($null -ne $sample.healthAgeSeconds){
            try{
                $sampleAge=[double]$sample.healthAgeSeconds
            }catch{
                throw 'soak.sample-health-age-invalid'
            }
            if($sampleAge -lt 0 -or $sampleAge -gt $maximumHealthAgeSeconds){
                throw 'soak.sample-health-age-invalid'
            }
            $maximumSampleHealthAge=[Math]::Max($maximumSampleHealthAge,$sampleAge)
        }
        if(($null -eq $observedAt) -ne ($null -eq $sampleAge)){throw 'soak.sample-health-age-mismatch'}
        if($null -ne $computedAge -and [Math]::Abs($computedAge-$sampleAge) -gt 0.01){
            throw 'soak.sample-health-age-mismatch'
        }

        $ready=([string]$sample.processState -eq 'ready') -and
            [bool]$sample.runtimeReady -and
            [bool]$sample.agentRunning -and
            [bool]$sample.accessFresh -and
            [bool]$sample.heartbeatFresh -and
            -not [bool]$sample.leaseLost
        if($ready){
            if($null -eq $observedAt -or $null -eq $sampleAge){throw 'soak.sample-ready-health-missing'}
            $recomputedReady++
        }elseif([bool]$sample.inStartupGrace){
            $recomputedGrace++
        }else{
            throw 'soak.sample-runtime-not-ready'
        }
        $count++
    }
}finally{
    $sampleReader.Dispose()
}

if($count -ne [int]$evidence.sampleCount){throw 'soak.sample-count-mismatch'}
if($count -eq 0 -or $null -eq $firstSampledAt -or $null -eq $lastSampledAt){
    throw 'soak.sample-coverage-empty'
}

$endpointToleranceSeconds=[Math]::Max(($pollSeconds*2),($pollSeconds+5))
if($firstSampledAt -gt $started.AddSeconds($endpointToleranceSeconds)){
    throw 'soak.sample-start-coverage-missing'
}
if($lastSampledAt -lt $completed.AddSeconds(-$endpointToleranceSeconds)){
    throw 'soak.sample-end-coverage-missing'
}
if($recomputedReady -ne [int]$evidence.readySamples){throw 'soak.ready-sample-count-mismatch'}
if($recomputedGrace -ne [int]$evidence.graceSamples){throw 'soak.grace-sample-count-mismatch'}
if(($recomputedReady+$recomputedGrace) -ne $count){throw 'soak.accepted-sample-count-mismatch'}
if([Math]::Abs($maximumSampleHealthAge-[double]$evidence.maximumObservedHealthAgeSeconds) -gt 0.01){
    throw 'soak.maximum-health-age-mismatch'
}

[pscustomobject]@{
    Valid=$true
    Status='passed'
    EvidenceSha256=$evidenceSnapshot.Hash
    SourceIdentity=[string]$evidence.sourceIdentity
    CandidateManifestSha256=[string]$evidence.candidateManifestSha256
    ExecutableSha256=[string]$evidence.executableSha256
    DurationSeconds=[Math]::Round(($completed-$started).TotalSeconds,3)
    SampleCount=$count
    SamplesSha256=[string]$evidence.samplesSha256
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._/-]{0,127}
    [ValidateRange(2,60)][int]$PollSeconds = 5,
    [ValidateRange(0,600)][int]$StartupGraceSeconds = 60,
    [ValidateRange(5,120)][int]$MaximumHealthAgeSeconds = 15
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Full([string]$path) { [IO.Path]::GetFullPath($path).TrimEnd([IO.Path]::DirectorySeparatorChar) }
function Utc([DateTimeOffset]$value) { $value.ToUniversalTime().ToString('O') }
function Is-True($value) { $value -eq $true }

if (-not [IO.Path]::IsPathRooted($DataRoot)) { throw 'soak.data-root-must-be-absolute' }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { throw 'soak.output-must-be-absolute' }

$dataRootFull = Full $DataRoot
$outputFull = Full $OutputDirectory
$dataDirectory = (Join-Path $dataRootFull 'Data').TrimEnd([IO.Path]::DirectorySeparatorChar)
$dataPrefix = $dataDirectory + [IO.Path]::DirectorySeparatorChar
if ($outputFull -eq $dataDirectory -or $outputFull.StartsWith($dataPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'soak.output-inside-authoritative-data-forbidden'
}

$healthPath = Join-Path $dataRootFull 'Runtime/headless-health-v1.json'
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
$samplesPath = Join-Path $outputFull 'headless-soak-samples.jsonl'
$summaryPath = Join-Path $outputFull 'headless-soak-evidence.json'
if (Test-Path -LiteralPath $samplesPath) { throw 'soak.samples-output-exists' }
if (Test-Path -LiteralPath $summaryPath) { throw 'soak.summary-output-exists' }

$started = [DateTimeOffset]::UtcNow
$deadline = $started.AddSeconds($DurationSeconds)
$sampleCount = 0
$readySamples = 0
$graceSamples = 0
$unhealthySamples = 0
$invalidSamples = 0
$maximumObservedAge = 0.0
$reasonCounts = [ordered]@{}
$allowedStates = @('starting','ready','degraded','blocked','failed','stopping')

function Add-Reason([string]$code) {
    if ([string]::IsNullOrWhiteSpace($code)) { return }
    if (-not $script:reasonCounts.Contains($code)) { $script:reasonCounts[$code] = 0 }
    $script:reasonCounts[$code] = [int]$script:reasonCounts[$code] + 1
}

$writer = [IO.StreamWriter]::new($samplesPath, $false, [Text.UTF8Encoding]::new($false))
try {
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $sampledAt = [DateTimeOffset]::UtcNow
        $inGrace = ($sampledAt - $started).TotalSeconds -lt $StartupGraceSeconds
        $reason = $null
        $processState = 'unavailable'
        $processCode = 'headless.health-unavailable'
        $observedAt = $null
        $healthAgeSeconds = $null
        $runtimeReady = $false
        $agentRunning = $false
        $accessFresh = $false
        $heartbeatFresh = $false
        $leaseLost = $false

        try {
            $info = Get-Item -LiteralPath $healthPath -ErrorAction Stop
            if ($info.Length -le 0 -or $info.Length -gt 65536) { throw 'health.length-invalid' }
            $health = Get-Content -Raw -LiteralPath $healthPath | ConvertFrom-Json
            if ($health.schema -ne 'wpe.headless-process-health/1.0') { throw 'health.schema-invalid' }
            $parsedObserved = [DateTimeOffset]::Parse(
                [string]$health.observedAtUtc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind)
            $futureTolerance = [TimeSpan]::FromSeconds(5)
            if ($parsedObserved -gt $sampledAt.Add($futureTolerance)) { throw 'health.future-observation' }
            $age = [Math]::Max(0.0, ($sampledAt - $parsedObserved).TotalSeconds)
            if ($age -gt $MaximumHealthAgeSeconds) { throw 'health.stale' }

            $processState = [string]$health.state
            $processCode = [string]$health.code
            if ($allowedStates -notcontains $processState) { throw 'health.state-invalid' }
            if ([string]::IsNullOrWhiteSpace($processCode) -or $processCode.Length -gt 128) { throw 'health.code-invalid' }

            $observedAt = $parsedObserved
            $healthAgeSeconds = [Math]::Round($age, 3)
            $maximumObservedAge = [Math]::Max($maximumObservedAge, $age)
            if ($null -ne $health.runtime) {
                $runtimeReady = Is-True $health.runtime.ready
                $agentRunning = Is-True $health.runtime.agentRunning
                $accessFresh = Is-True $health.runtime.accessFresh
                $heartbeatFresh = Is-True $health.runtime.heartbeatFresh
                $leaseLost = Is-True $health.runtime.leaseLost
            }

            $ready = $processState -eq 'ready' -and $runtimeReady -and $agentRunning -and
                $accessFresh -and $heartbeatFresh -and -not $leaseLost
            if ($ready) {
                $readySamples++
            } elseif ($inGrace) {
                $graceSamples++
                $reason = 'soak.startup-grace'
            } else {
                $unhealthySamples++
                $reason = if ($leaseLost) { 'soak.lease-lost' } elseif ($processState -eq 'failed') {
                    'soak.process-failed'
                } elseif ($processState -eq 'blocked') {
                    'soak.process-blocked'
                } elseif (-not $agentRunning) {
                    'soak.agent-not-running'
                } elseif (-not $accessFresh) {
                    'soak.access-not-fresh'
                } elseif (-not $heartbeatFresh) {
                    'soak.heartbeat-not-fresh'
                } else {
                    'soak.runtime-not-ready'
                }
            }
        } catch {
            $invalidSamples++
            if ($inGrace) { $graceSamples++ } else { $unhealthySamples++ }
            $reason = if ($_.Exception.Message -like 'health.*') {
                $_.Exception.Message
            } else {
                'health.unavailable-or-malformed'
            }
        }

        Add-Reason $reason
        $sample = [ordered]@{
            schemaVersion = 'wpe.headless-soak-sample/1.0'
            sampledAtUtc = Utc $sampledAt
            observedAtUtc = if ($observedAt) { Utc $observedAt } else { $null }
            healthAgeSeconds = $healthAgeSeconds
            processState = $processState
            processCode = $processCode
            runtimeReady = $runtimeReady
            agentRunning = $agentRunning
            accessFresh = $accessFresh
            heartbeatFresh = $heartbeatFresh
            leaseLost = $leaseLost
            inStartupGrace = $inGrace
            accepted = ($reason -eq $null -or $reason -eq 'soak.startup-grace')
            reason = $reason
        }
        $writer.WriteLine(($sample | ConvertTo-Json -Compress -Depth 4))
        $writer.Flush()
        $sampleCount++

        $remaining = ($deadline - [DateTimeOffset]::UtcNow).TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($PollSeconds, [Math]::Max(1, [int][Math]::Ceiling($remaining))))
    }
} finally {
    $writer.Dispose()
}

$completed = [DateTimeOffset]::UtcNow
$samplesHash = (Get-FileHash -LiteralPath $samplesPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedMinimumSamples = [Math]::Max(1, [int][Math]::Floor(($DurationSeconds / $PollSeconds) * 0.90))
$status = if ($sampleCount -ge $expectedMinimumSamples -and $unhealthySamples -eq 0 -and $invalidSamples -eq 0) { 'passed' } else { 'failed' }

$summary = [ordered]@{
    schemaVersion = 'wpe.headless-soak-evidence/1.0'
    status = $status
    sourceIdentity = $SourceIdentity
    candidateManifestSha256 = $CandidateManifestSha256.ToLowerInvariant()
    startedAtUtc = Utc $started
    completedAtUtc = Utc $completed
    requestedDurationSeconds = $DurationSeconds
    observedDurationSeconds = [Math]::Round(($completed - $started).TotalSeconds, 3)
    pollSeconds = $PollSeconds
    startupGraceSeconds = $StartupGraceSeconds
    maximumHealthAgeSeconds = $MaximumHealthAgeSeconds
    sampleCount = $sampleCount
    expectedMinimumSamples = $expectedMinimumSamples
    readySamples = $readySamples
    graceSamples = $graceSamples
    unhealthySamples = $unhealthySamples
    invalidSamples = $invalidSamples
    maximumObservedHealthAgeSeconds = [Math]::Round($maximumObservedAge, 3)
    samplesFile = 'headless-soak-samples.jsonl'
    samplesSha256 = $samplesHash
    reasonCounts = $reasonCounts
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary | ConvertTo-Json -Depth 6
if ($status -ne 'passed') { exit 1 }
)][string]$SourceIdentity,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}
    [ValidateRange(2,60)][int]$PollSeconds = 5,
    [ValidateRange(0,600)][int]$StartupGraceSeconds = 60,
    [ValidateRange(5,120)][int]$MaximumHealthAgeSeconds = 15
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Full([string]$path) { [IO.Path]::GetFullPath($path).TrimEnd([IO.Path]::DirectorySeparatorChar) }
function Utc([DateTimeOffset]$value) { $value.ToUniversalTime().ToString('O') }
function Is-True($value) { $value -eq $true }

if (-not [IO.Path]::IsPathRooted($DataRoot)) { throw 'soak.data-root-must-be-absolute' }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { throw 'soak.output-must-be-absolute' }

$dataRootFull = Full $DataRoot
$outputFull = Full $OutputDirectory
$dataDirectory = (Join-Path $dataRootFull 'Data').TrimEnd([IO.Path]::DirectorySeparatorChar)
$dataPrefix = $dataDirectory + [IO.Path]::DirectorySeparatorChar
if ($outputFull -eq $dataDirectory -or $outputFull.StartsWith($dataPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'soak.output-inside-authoritative-data-forbidden'
}

$healthPath = Join-Path $dataRootFull 'Runtime/headless-health-v1.json'
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
$samplesPath = Join-Path $outputFull 'headless-soak-samples.jsonl'
$summaryPath = Join-Path $outputFull 'headless-soak-evidence.json'
if (Test-Path -LiteralPath $samplesPath) { throw 'soak.samples-output-exists' }
if (Test-Path -LiteralPath $summaryPath) { throw 'soak.summary-output-exists' }

$started = [DateTimeOffset]::UtcNow
$deadline = $started.AddSeconds($DurationSeconds)
$sampleCount = 0
$readySamples = 0
$graceSamples = 0
$unhealthySamples = 0
$invalidSamples = 0
$maximumObservedAge = 0.0
$reasonCounts = [ordered]@{}
$allowedStates = @('starting','ready','degraded','blocked','failed','stopping')

function Add-Reason([string]$code) {
    if ([string]::IsNullOrWhiteSpace($code)) { return }
    if (-not $script:reasonCounts.Contains($code)) { $script:reasonCounts[$code] = 0 }
    $script:reasonCounts[$code] = [int]$script:reasonCounts[$code] + 1
}

$writer = [IO.StreamWriter]::new($samplesPath, $false, [Text.UTF8Encoding]::new($false))
try {
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $sampledAt = [DateTimeOffset]::UtcNow
        $inGrace = ($sampledAt - $started).TotalSeconds -lt $StartupGraceSeconds
        $reason = $null
        $processState = 'unavailable'
        $processCode = 'headless.health-unavailable'
        $observedAt = $null
        $healthAgeSeconds = $null
        $runtimeReady = $false
        $agentRunning = $false
        $accessFresh = $false
        $heartbeatFresh = $false
        $leaseLost = $false

        try {
            $info = Get-Item -LiteralPath $healthPath -ErrorAction Stop
            if ($info.Length -le 0 -or $info.Length -gt 65536) { throw 'health.length-invalid' }
            $health = Get-Content -Raw -LiteralPath $healthPath | ConvertFrom-Json
            if ($health.schema -ne 'wpe.headless-process-health/1.0') { throw 'health.schema-invalid' }
            $parsedObserved = [DateTimeOffset]::Parse(
                [string]$health.observedAtUtc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind)
            $futureTolerance = [TimeSpan]::FromSeconds(5)
            if ($parsedObserved -gt $sampledAt.Add($futureTolerance)) { throw 'health.future-observation' }
            $age = [Math]::Max(0.0, ($sampledAt - $parsedObserved).TotalSeconds)
            if ($age -gt $MaximumHealthAgeSeconds) { throw 'health.stale' }

            $processState = [string]$health.state
            $processCode = [string]$health.code
            if ($allowedStates -notcontains $processState) { throw 'health.state-invalid' }
            if ([string]::IsNullOrWhiteSpace($processCode) -or $processCode.Length -gt 128) { throw 'health.code-invalid' }

            $observedAt = $parsedObserved
            $healthAgeSeconds = [Math]::Round($age, 3)
            $maximumObservedAge = [Math]::Max($maximumObservedAge, $age)
            if ($null -ne $health.runtime) {
                $runtimeReady = Is-True $health.runtime.ready
                $agentRunning = Is-True $health.runtime.agentRunning
                $accessFresh = Is-True $health.runtime.accessFresh
                $heartbeatFresh = Is-True $health.runtime.heartbeatFresh
                $leaseLost = Is-True $health.runtime.leaseLost
            }

            $ready = $processState -eq 'ready' -and $runtimeReady -and $agentRunning -and
                $accessFresh -and $heartbeatFresh -and -not $leaseLost
            if ($ready) {
                $readySamples++
            } elseif ($inGrace) {
                $graceSamples++
                $reason = 'soak.startup-grace'
            } else {
                $unhealthySamples++
                $reason = if ($leaseLost) { 'soak.lease-lost' } elseif ($processState -eq 'failed') {
                    'soak.process-failed'
                } elseif ($processState -eq 'blocked') {
                    'soak.process-blocked'
                } elseif (-not $agentRunning) {
                    'soak.agent-not-running'
                } elseif (-not $accessFresh) {
                    'soak.access-not-fresh'
                } elseif (-not $heartbeatFresh) {
                    'soak.heartbeat-not-fresh'
                } else {
                    'soak.runtime-not-ready'
                }
            }
        } catch {
            $invalidSamples++
            if ($inGrace) { $graceSamples++ } else { $unhealthySamples++ }
            $reason = if ($_.Exception.Message -like 'health.*') {
                $_.Exception.Message
            } else {
                'health.unavailable-or-malformed'
            }
        }

        Add-Reason $reason
        $sample = [ordered]@{
            schemaVersion = 'wpe.headless-soak-sample/1.0'
            sampledAtUtc = Utc $sampledAt
            observedAtUtc = if ($observedAt) { Utc $observedAt } else { $null }
            healthAgeSeconds = $healthAgeSeconds
            processState = $processState
            processCode = $processCode
            runtimeReady = $runtimeReady
            agentRunning = $agentRunning
            accessFresh = $accessFresh
            heartbeatFresh = $heartbeatFresh
            leaseLost = $leaseLost
            inStartupGrace = $inGrace
            accepted = ($reason -eq $null -or $reason -eq 'soak.startup-grace')
            reason = $reason
        }
        $writer.WriteLine(($sample | ConvertTo-Json -Compress -Depth 4))
        $writer.Flush()
        $sampleCount++

        $remaining = ($deadline - [DateTimeOffset]::UtcNow).TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($PollSeconds, [Math]::Max(1, [int][Math]::Ceiling($remaining))))
    }
} finally {
    $writer.Dispose()
}

$completed = [DateTimeOffset]::UtcNow
$samplesHash = (Get-FileHash -LiteralPath $samplesPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedMinimumSamples = [Math]::Max(1, [int][Math]::Floor(($DurationSeconds / $PollSeconds) * 0.90))
$status = if ($sampleCount -ge $expectedMinimumSamples -and $unhealthySamples -eq 0 -and $invalidSamples -eq 0) { 'passed' } else { 'failed' }

$summary = [ordered]@{
    schemaVersion = 'wpe.headless-soak-evidence/1.0'
    status = $status
    startedAtUtc = Utc $started
    completedAtUtc = Utc $completed
    requestedDurationSeconds = $DurationSeconds
    observedDurationSeconds = [Math]::Round(($completed - $started).TotalSeconds, 3)
    pollSeconds = $PollSeconds
    startupGraceSeconds = $StartupGraceSeconds
    maximumHealthAgeSeconds = $MaximumHealthAgeSeconds
    sampleCount = $sampleCount
    expectedMinimumSamples = $expectedMinimumSamples
    readySamples = $readySamples
    graceSamples = $graceSamples
    unhealthySamples = $unhealthySamples
    invalidSamples = $invalidSamples
    maximumObservedHealthAgeSeconds = [Math]::Round($maximumObservedAge, 3)
    samplesFile = 'headless-soak-samples.jsonl'
    samplesSha256 = $samplesHash
    reasonCounts = $reasonCounts
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary | ConvertTo-Json -Depth 6
if ($status -ne 'passed') { exit 1 }
)][string]$CandidateManifestSha256,
    [ValidateRange(60,172800)][int]$DurationSeconds = 3600,
    [ValidateRange(2,60)][int]$PollSeconds = 5,
    [ValidateRange(0,600)][int]$StartupGraceSeconds = 60,
    [ValidateRange(5,120)][int]$MaximumHealthAgeSeconds = 15
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Full([string]$path) { [IO.Path]::GetFullPath($path).TrimEnd([IO.Path]::DirectorySeparatorChar) }
function Utc([DateTimeOffset]$value) { $value.ToUniversalTime().ToString('O') }
function Is-True($value) { $value -eq $true }

if (-not [IO.Path]::IsPathRooted($DataRoot)) { throw 'soak.data-root-must-be-absolute' }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { throw 'soak.output-must-be-absolute' }

$dataRootFull = Full $DataRoot
$outputFull = Full $OutputDirectory
$dataDirectory = (Join-Path $dataRootFull 'Data').TrimEnd([IO.Path]::DirectorySeparatorChar)
$dataPrefix = $dataDirectory + [IO.Path]::DirectorySeparatorChar
if ($outputFull -eq $dataDirectory -or $outputFull.StartsWith($dataPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'soak.output-inside-authoritative-data-forbidden'
}

$healthPath = Join-Path $dataRootFull 'Runtime/headless-health-v1.json'
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
$samplesPath = Join-Path $outputFull 'headless-soak-samples.jsonl'
$summaryPath = Join-Path $outputFull 'headless-soak-evidence.json'
if (Test-Path -LiteralPath $samplesPath) { throw 'soak.samples-output-exists' }
if (Test-Path -LiteralPath $summaryPath) { throw 'soak.summary-output-exists' }

$started = [DateTimeOffset]::UtcNow
$deadline = $started.AddSeconds($DurationSeconds)
$sampleCount = 0
$readySamples = 0
$graceSamples = 0
$unhealthySamples = 0
$invalidSamples = 0
$maximumObservedAge = 0.0
$reasonCounts = [ordered]@{}
$allowedStates = @('starting','ready','degraded','blocked','failed','stopping')

function Add-Reason([string]$code) {
    if ([string]::IsNullOrWhiteSpace($code)) { return }
    if (-not $script:reasonCounts.Contains($code)) { $script:reasonCounts[$code] = 0 }
    $script:reasonCounts[$code] = [int]$script:reasonCounts[$code] + 1
}

$writer = [IO.StreamWriter]::new($samplesPath, $false, [Text.UTF8Encoding]::new($false))
try {
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $sampledAt = [DateTimeOffset]::UtcNow
        $inGrace = ($sampledAt - $started).TotalSeconds -lt $StartupGraceSeconds
        $reason = $null
        $processState = 'unavailable'
        $processCode = 'headless.health-unavailable'
        $observedAt = $null
        $healthAgeSeconds = $null
        $runtimeReady = $false
        $agentRunning = $false
        $accessFresh = $false
        $heartbeatFresh = $false
        $leaseLost = $false

        try {
            $info = Get-Item -LiteralPath $healthPath -ErrorAction Stop
            if ($info.Length -le 0 -or $info.Length -gt 65536) { throw 'health.length-invalid' }
            $health = Get-Content -Raw -LiteralPath $healthPath | ConvertFrom-Json
            if ($health.schema -ne 'wpe.headless-process-health/1.0') { throw 'health.schema-invalid' }
            $parsedObserved = [DateTimeOffset]::Parse(
                [string]$health.observedAtUtc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind)
            $futureTolerance = [TimeSpan]::FromSeconds(5)
            if ($parsedObserved -gt $sampledAt.Add($futureTolerance)) { throw 'health.future-observation' }
            $age = [Math]::Max(0.0, ($sampledAt - $parsedObserved).TotalSeconds)
            if ($age -gt $MaximumHealthAgeSeconds) { throw 'health.stale' }

            $processState = [string]$health.state
            $processCode = [string]$health.code
            if ($allowedStates -notcontains $processState) { throw 'health.state-invalid' }
            if ([string]::IsNullOrWhiteSpace($processCode) -or $processCode.Length -gt 128) { throw 'health.code-invalid' }

            $observedAt = $parsedObserved
            $healthAgeSeconds = [Math]::Round($age, 3)
            $maximumObservedAge = [Math]::Max($maximumObservedAge, $age)
            if ($null -ne $health.runtime) {
                $runtimeReady = Is-True $health.runtime.ready
                $agentRunning = Is-True $health.runtime.agentRunning
                $accessFresh = Is-True $health.runtime.accessFresh
                $heartbeatFresh = Is-True $health.runtime.heartbeatFresh
                $leaseLost = Is-True $health.runtime.leaseLost
            }

            $ready = $processState -eq 'ready' -and $runtimeReady -and $agentRunning -and
                $accessFresh -and $heartbeatFresh -and -not $leaseLost
            if ($ready) {
                $readySamples++
            } elseif ($inGrace) {
                $graceSamples++
                $reason = 'soak.startup-grace'
            } else {
                $unhealthySamples++
                $reason = if ($leaseLost) { 'soak.lease-lost' } elseif ($processState -eq 'failed') {
                    'soak.process-failed'
                } elseif ($processState -eq 'blocked') {
                    'soak.process-blocked'
                } elseif (-not $agentRunning) {
                    'soak.agent-not-running'
                } elseif (-not $accessFresh) {
                    'soak.access-not-fresh'
                } elseif (-not $heartbeatFresh) {
                    'soak.heartbeat-not-fresh'
                } else {
                    'soak.runtime-not-ready'
                }
            }
        } catch {
            $invalidSamples++
            if ($inGrace) { $graceSamples++ } else { $unhealthySamples++ }
            $reason = if ($_.Exception.Message -like 'health.*') {
                $_.Exception.Message
            } else {
                'health.unavailable-or-malformed'
            }
        }

        Add-Reason $reason
        $sample = [ordered]@{
            schemaVersion = 'wpe.headless-soak-sample/1.0'
            sampledAtUtc = Utc $sampledAt
            observedAtUtc = if ($observedAt) { Utc $observedAt } else { $null }
            healthAgeSeconds = $healthAgeSeconds
            processState = $processState
            processCode = $processCode
            runtimeReady = $runtimeReady
            agentRunning = $agentRunning
            accessFresh = $accessFresh
            heartbeatFresh = $heartbeatFresh
            leaseLost = $leaseLost
            inStartupGrace = $inGrace
            accepted = ($reason -eq $null -or $reason -eq 'soak.startup-grace')
            reason = $reason
        }
        $writer.WriteLine(($sample | ConvertTo-Json -Compress -Depth 4))
        $writer.Flush()
        $sampleCount++

        $remaining = ($deadline - [DateTimeOffset]::UtcNow).TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($PollSeconds, [Math]::Max(1, [int][Math]::Ceiling($remaining))))
    }
} finally {
    $writer.Dispose()
}

$completed = [DateTimeOffset]::UtcNow
$samplesHash = (Get-FileHash -LiteralPath $samplesPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedMinimumSamples = [Math]::Max(1, [int][Math]::Floor(($DurationSeconds / $PollSeconds) * 0.90))
$status = if ($sampleCount -ge $expectedMinimumSamples -and $unhealthySamples -eq 0 -and $invalidSamples -eq 0) { 'passed' } else { 'failed' }

$summary = [ordered]@{
    schemaVersion = 'wpe.headless-soak-evidence/1.0'
    status = $status
    startedAtUtc = Utc $started
    completedAtUtc = Utc $completed
    requestedDurationSeconds = $DurationSeconds
    observedDurationSeconds = [Math]::Round(($completed - $started).TotalSeconds, 3)
    pollSeconds = $PollSeconds
    startupGraceSeconds = $StartupGraceSeconds
    maximumHealthAgeSeconds = $MaximumHealthAgeSeconds
    sampleCount = $sampleCount
    expectedMinimumSamples = $expectedMinimumSamples
    readySamples = $readySamples
    graceSamples = $graceSamples
    unhealthySamples = $unhealthySamples
    invalidSamples = $invalidSamples
    maximumObservedHealthAgeSeconds = [Math]::Round($maximumObservedAge, 3)
    samplesFile = 'headless-soak-samples.jsonl'
    samplesSha256 = $samplesHash
    reasonCounts = $reasonCounts
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary | ConvertTo-Json -Depth 6
if ($status -ne 'passed') { exit 1 }

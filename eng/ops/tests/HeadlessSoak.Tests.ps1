$ErrorActionPreference='Stop'
function Assert($condition,[string]$message){if(-not $condition){throw "ASSERT: $message"}}
function Throws([scriptblock]$action,[string]$contains){
    try{& $action|Out-Null;throw "expected:$contains"}
    catch{if($_.Exception.Message -notlike "*$contains*"){throw}}
}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}

$tool=Split-Path $PSScriptRoot -Parent
$verify=Join-Path $tool 'Test-HeadlessSoakEvidence.ps1'
$root=Join-Path ([IO.Path]::GetTempPath()) ('wpe-soak-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force|Out-Null
try{
    $samplesPath=Join-Path $root 'headless-soak-samples.jsonl'
    $now=[DateTimeOffset]::UtcNow
    $started=$now.AddMinutes(-61)

    function Write-ValidSamples {
        $lines=[Collections.Generic.List[string]]::new()
        for($index=0;$index -lt 54;$index++){
            $sampledAt=$started.AddSeconds(3660.0*($index/53.0))
            $sample=[ordered]@{
                schemaVersion='wpe.headless-soak-sample/1.0'
                sampledAtUtc=$sampledAt.ToString('O')
                observedAtUtc=$sampledAt.AddSeconds(-1).ToString('O')
                healthAgeSeconds=1
                processState='ready'
                processCode='headless.ready'
                runtimeReady=$true
                agentRunning=$true
                accessFresh=$true
                heartbeatFresh=$true
                leaseLost=$false
                inStartupGrace=(($sampledAt-$started).TotalSeconds -lt 60)
                accepted=$true
                reason=$null
            }
            $lines.Add(($sample|ConvertTo-Json -Compress))
        }
        $lines|Set-Content -LiteralPath $samplesPath -Encoding utf8
    }

    function Rewrite-Samples([scriptblock]$mutator){
        $rewritten=Get-Content -LiteralPath $samplesPath|ForEach-Object{
            $sample=$_|ConvertFrom-Json
            & $mutator $sample
            $sample|ConvertTo-Json -Compress
        }
        $rewritten|Set-Content -LiteralPath $samplesPath -Encoding utf8
    }

    $evidencePath=Join-Path $root 'headless-soak-evidence.json'
    function Write-Evidence([hashtable]$overrides){
        $e=[ordered]@{
            schemaVersion='wpe.headless-soak-evidence/1.0'
            status='passed'
            sourceIdentity='commit/test-candidate'
            candidateManifestSha256=('a'*64)
            startedAtUtc=$started.ToString('O')
            completedAtUtc=$now.ToString('O')
            requestedDurationSeconds=3660
            observedDurationSeconds=3660
            pollSeconds=60
            startupGraceSeconds=60
            maximumHealthAgeSeconds=15
            sampleCount=54
            expectedMinimumSamples=54
            readySamples=54
            graceSamples=0
            unhealthySamples=0
            invalidSamples=0
            maximumObservedHealthAgeSeconds=1
            samplesFile='headless-soak-samples.jsonl'
            samplesSha256=Hash $samplesPath
            reasonCounts=[ordered]@{}
        }
        foreach($key in $overrides.Keys){$e[$key]=$overrides[$key]}
        $e|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $evidencePath -Encoding utf8
    }

    Write-ValidSamples
    Write-Evidence @{}
    $result=& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60
    Assert $result.Valid 'valid evidence rejected'
    Assert ($result.SampleCount -eq 54) 'sample count not verified'
    Assert ($result.SourceIdentity -eq 'commit/test-candidate') 'source identity not returned'
    Assert ($result.CandidateManifestSha256 -eq ('a'*64)) 'candidate manifest identity not returned'

    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/other' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.source-identity-mismatch'

    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('b'*64) -MinimumDurationMinutes 60} 'soak.candidate-manifest-mismatch'

    Write-Evidence @{samplesSha256=('0'*64)}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.samples-hash-mismatch'

    Write-Evidence @{unhealthySamples=1}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.unhealthy-samples-exceeded'

    Write-Evidence @{startedAtUtc=$now.AddMinutes(-5).ToString('O')}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.duration-insufficient'

    Write-Evidence @{requestedDurationSeconds=60;expectedMinimumSamples=1}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.requested-duration-mismatch'

    Write-Evidence @{requestedDurationSeconds=60;expectedMinimumSamples=1}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.requested-duration-mismatch'

    Write-Evidence @{}
    $tampered=Get-Content -Raw -LiteralPath $evidencePath|ConvertFrom-Json
    $tampered|Add-Member -NotePropertyName unexpected -NotePropertyValue 'x'
    $tampered|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $evidencePath -Encoding utf8
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.evidence-unknown-field'

    Write-Evidence @{}
    Add-Content -LiteralPath $samplesPath -Value (Get-Content -LiteralPath $samplesPath -TotalCount 1)
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.samples-hash-mismatch'

    Write-ValidSamples
    Rewrite-Samples {param($sample) $sample.accepted=$false}
    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.sample-not-accepted'

    Write-ValidSamples
    Rewrite-Samples {param($sample) $sample.processState='failed'}
    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.sample-runtime-not-ready'

    Write-ValidSamples
    Rewrite-Samples {param($sample) $sample.inStartupGrace=$true}
    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.sample-grace-window-mismatch'

    Write-ValidSamples
    Rewrite-Samples {param($sample) $sample.healthAgeSeconds=2}
    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.sample-health-age-mismatch'

    Write-ValidSamples
    $lines=@(Get-Content -LiteralPath $samplesPath)
    $first=$lines[0]|ConvertFrom-Json
    $second=$lines[1]|ConvertFrom-Json
    $second.sampledAtUtc=$first.sampledAtUtc
    $second.observedAtUtc=$first.observedAtUtc
    $lines[1]=$second|ConvertTo-Json -Compress
    $lines|Set-Content -LiteralPath $samplesPath -Encoding utf8
    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.sample-time-not-strictly-increasing'

    Write-ValidSamples
    $lines=@(Get-Content -LiteralPath $samplesPath)
    $previous=$lines[9]|ConvertFrom-Json
    $previousAt=[DateTimeOffset]::Parse([string]$previous.sampledAtUtc)
    $gapStart=$previousAt.AddSeconds(181)
    for($index=10;$index -lt 54;$index++){
        $sample=$lines[$index]|ConvertFrom-Json
        $fraction=($index-10)/43.0
        $sampledAt=$gapStart.AddSeconds(($now-$gapStart).TotalSeconds*$fraction)
        $sample.sampledAtUtc=$sampledAt.ToString('O')
        $sample.observedAtUtc=$sampledAt.AddSeconds(-1).ToString('O')
        $sample.inStartupGrace=$false
        $lines[$index]=$sample|ConvertTo-Json -Compress
    }
    $lines|Set-Content -LiteralPath $samplesPath -Encoding utf8
    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.sample-gap-exceeded'

    'PASS headless soak evidence verification'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

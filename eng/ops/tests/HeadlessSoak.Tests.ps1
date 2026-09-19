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
    $sample=[ordered]@{
        schemaVersion='wpe.headless-soak-sample/1.0'
        sampledAtUtc=$now.AddSeconds(-5).ToString('O')
        observedAtUtc=$now.AddSeconds(-6).ToString('O')
        healthAgeSeconds=1
        processState='ready'
        processCode='headless.ready'
        runtimeReady=$true
        agentRunning=$true
        accessFresh=$true
        heartbeatFresh=$true
        leaseLost=$false
        inStartupGrace=$false
        accepted=$true
        reason=$null
    }
    $sampleJson=$sample|ConvertTo-Json -Compress
    @(1..54|ForEach-Object{$sampleJson})|Set-Content -LiteralPath $samplesPath -Encoding utf8
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

    Write-Evidence @{}
    $tampered=Get-Content -Raw -LiteralPath $evidencePath|ConvertFrom-Json
    $tampered|Add-Member -NotePropertyName unexpected -NotePropertyValue 'x'
    $tampered|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $evidencePath -Encoding utf8
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.evidence-unknown-field'

    Write-Evidence @{}
    Add-Content -LiteralPath $samplesPath -Value (($sample|ConvertTo-Json -Compress))
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.samples-hash-mismatch'

    $notAcceptedJson=$sampleJson.Replace('"accepted":true','"accepted":false')
    @(1..54|ForEach-Object{$notAcceptedJson})|Set-Content -LiteralPath $samplesPath -Encoding utf8
    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.sample-not-accepted'

    $notReadyJson=$sampleJson.Replace('"processState":"ready"','"processState":"failed"')
    @(1..54|ForEach-Object{$notReadyJson})|Set-Content -LiteralPath $samplesPath -Encoding utf8
    Write-Evidence @{}
    Throws {& $verify -EvidencePath $evidencePath -ExpectedSourceIdentity 'commit/test-candidate' -ExpectedCandidateManifestSha256 ('a'*64) -MinimumDurationMinutes 60} 'soak.sample-runtime-not-ready'

    'PASS headless soak evidence verification'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

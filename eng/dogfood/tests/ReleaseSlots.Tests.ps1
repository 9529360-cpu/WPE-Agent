$ErrorActionPreference='Stop'
function Assert($condition,[string]$message){if(-not $condition){throw "ASSERT: $message"}}
function Throws([scriptblock]$action,[string]$contains){try{& $action|Out-Null;throw "expected:$contains"}catch{if($_.Exception.Message -notlike "*$contains*"){throw}}}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function New-TestCandidate {
    param([string]$SourceRoot,[string]$SlotsRoot,[string]$Version,[string]$SourceIdentity,[string]$ConfigurationSchema,[string]$MigrationVersion,[string[]]$Gates,[string]$ProbeScript='dogfood-probe.ps1',[switch]$Dirty,[switch]$Unsigned,[switch]$ReviewRequired,[switch]$NotDistributable,[switch]$BadVerificationHash,[switch]$BadManifestHash)
    $signedStatus=if($Unsigned){'unsigned'}else{'Valid'};$licenseStatus=if($ReviewRequired){'review_required'}else{'passed'};$licenseReview=if($ReviewRequired){1}else{0}
    $metadata=[ordered]@{source=[ordered]@{dirty=[bool]$Dirty};signing=[ordered]@{status=$signedStatus;distributable=(-not $NotDistributable)};contents=[ordered]@{licenseGateStatus=$licenseStatus;licenseReviewCount=$licenseReview}}
    $metadata|ConvertTo-Json -Depth 6|Set-Content (Join-Path $SourceRoot 'RELEASE-METADATA.json') -Encoding utf8
    $manifest=@(Get-ChildItem $SourceRoot -Recurse -File|Where-Object {$_.Name -ne 'FILE-MANIFEST.json'}|Sort-Object FullName|ForEach-Object {[ordered]@{path=$_.FullName.Substring($SourceRoot.Length+1).Replace('\\','/');bytes=$_.Length;sha256=Hash $_.FullName}})
    $manifest|ConvertTo-Json -Depth 4|Set-Content (Join-Path $SourceRoot 'FILE-MANIFEST.json') -Encoding utf8
    $verification=Join-Path (Split-Path $SlotsRoot -Parent) ("verification-$Version.json")
    [ordered]@{schemaVersion='wpe.beta-package-verification.v1';status='passed';distributable=(-not $NotDistributable);signingStatus=$signedStatus;licenseGateStatus=$licenseStatus;licenseReview=$licenseReview;manifestFailures=0;forbiddenFiles=0}|ConvertTo-Json|Set-Content $verification -Encoding utf8
    $manifestHash=if($BadManifestHash){'0'*64}else{Hash (Join-Path $SourceRoot 'FILE-MANIFEST.json')};$verificationHash=if($BadVerificationHash){'0'*64}else{Hash $verification}
    & $script:new -SourceRoot $SourceRoot -SlotsRoot $SlotsRoot -Version $Version -SourceIdentity $SourceIdentity -ConfigurationSchema $ConfigurationSchema -MigrationVersion $MigrationVersion -Gates $Gates -ProbeScript $ProbeScript -ExpectedSourceManifestHash $manifestHash -PackageVerificationPath $verification -ExpectedPackageVerificationHash $verificationHash
}
function Write-Proof([string]$path,$candidate,$lkg,[hashtable]$overrides){
    $now=[DateTimeOffset]::UtcNow
    $proof=[ordered]@{
        schemaVersion='wpe.trusted-runtime-proof/1.0';proofId='proof/current-001';sourceIdentity='runtime/snapshot-authority-v1'
        sourceTimestampUtc=$now.AddSeconds(-2).ToString('O');snapshotTimestampUtc=$now.ToString('O');environment='Testnet';bridgeState='connected'
        authority=[ordered]@{id='authority/dogfood-001';status='active';current=$true;expiresUtc=$now.AddMinutes(5).ToString('O')}
        evidenceGates=@([ordered]@{reference='gate/build';status='passed'},[ordered]@{reference='gate/tests';status='passed'})
        candidateManifestSha256=$candidate.ManifestSha256;lastKnownGoodManifestSha256=$lkg.ManifestSha256
    }
    foreach($key in $overrides.Keys){$proof[$key]=$overrides[$key]}
    $proof|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding utf8
    Hash $path
}
function Write-SoakEvidence([string]$path,$candidate,[hashtable]$overrides){
    $manifest=Get-Content -Raw -LiteralPath (Join-Path $candidate.CandidateRoot 'release-manifest.json')|ConvertFrom-Json
    $now=[DateTimeOffset]::UtcNow
    $samplePath=Join-Path (Split-Path -Parent $path) 'headless-soak-samples.jsonl'
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
    @(
        ($sample|ConvertTo-Json -Compress),
        ($sample|ConvertTo-Json -Compress)
    )|Set-Content -LiteralPath $samplePath -Encoding utf8
    $e=[ordered]@{
        schemaVersion='wpe.headless-soak-evidence/1.0'
        status='passed'
        sourceIdentity=[string]$manifest.sourceIdentity
        candidateManifestSha256=$candidate.ManifestSha256
        startedAtUtc=$now.AddHours(-25).ToString('O')
        completedAtUtc=$now.ToString('O')
        requestedDurationSeconds=90000
        observedDurationSeconds=90000
        pollSeconds=5
        startupGraceSeconds=60
        maximumHealthAgeSeconds=15
        sampleCount=2
        expectedMinimumSamples=2
        readySamples=2
        graceSamples=0
        unhealthySamples=0
        invalidSamples=0
        maximumObservedHealthAgeSeconds=1
        samplesFile='headless-soak-samples.jsonl'
        samplesSha256=Hash $samplePath
        reasonCounts=[ordered]@{}
    }
    foreach($key in $overrides.Keys){$e[$key]=$overrides[$key]}
    $e|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $path -Encoding utf8
    Hash $path
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('wpe-dogfood-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $root|Out-Null
try{
    $tool=Split-Path $PSScriptRoot -Parent;$new=Join-Path $tool 'New-Candidate.ps1';$script:new=$new;$test=Join-Path $tool 'Test-DogfoodRelease.ps1';$switch=Join-Path $tool 'Switch-DogfoodSlot.ps1'
    $fixtureRoot=Join-Path $PSScriptRoot 'fixtures';$f01=Get-Content -Raw (Join-Path $fixtureRoot 'F01-trusted-runtime-proof-not-consumed.json')|ConvertFrom-Json;$f02=Get-Content -Raw (Join-Path $fixtureRoot 'F02-manifest-hash-unanchored.json')|ConvertFrom-Json
    Assert ($f01.expectedRejection -eq 'runtime.proof-source-untrusted') 'F01 fixture contract changed';Assert ($f02.expectedRejection -eq 'candidate.manifest-hash-mismatch') 'F02 fixture contract changed'
    $command=(Get-Command $test).Parameters.Keys;[string[]]$contract='CandidateRoot','LastKnownGoodRoot','ProviderReadOnly','RequireTrustedRuntimeFresh','VerifyInstall','VerifyStartup','VerifyRestartRecovery','VerifyRollback','RejectMainnet','ExpectedCandidateManifestHash','ExpectedLastKnownGoodManifestHash','TrustedRuntimeProofPath','ExpectedTrustedRuntimeProofHash','ExpectedRuntimeProofId','ExpectedRuntimeSourceIdentity','ExpectedEvidenceGateRefs','SoakEvidencePath','ExpectedSoakEvidenceHash','MinimumSoakDurationMinutes','MaximumRuntimeAgeSeconds','TestnetMutationSmoke','VerifiedGateEvidenceRef','Mainnet'
    foreach($name in $contract){Assert ($command -contains $name) "missing parameter $name"}
    $tokens=$null;$parseErrors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($test,[ref]$tokens,[ref]$parseErrors);Assert ($parseErrors.Count -eq 0) 'validator AST parse failed'
    $declared=@($ast.ParamBlock.Parameters|ForEach-Object {$_.Name.VariablePath.UserPath});Assert ((Compare-Object $contract $declared -SyncWindow 0).Count -eq 0) 'stable parameter order changed'
    $source=Join-Path $root 'package';New-Item -ItemType Directory -Path $source|Out-Null
    @'
param([ValidateSet('install','startup','restart','rollback')]$Mode,[switch]$ProviderReadOnly,[string]$Environment)
if(-not $ProviderReadOnly -or $Environment -ne 'Testnet'){exit 9}
exit 0
'@.Trim()|Set-Content -LiteralPath (Join-Path $source 'dogfood-probe.ps1') -Encoding utf8
    'payload'|Set-Content -LiteralPath (Join-Path $source 'app.bin') -Encoding ascii
    $missingProbe=Join-Path $root 'missing-probe-package';Copy-Item $source $missingProbe -Recurse;Remove-Item (Join-Path $missingProbe 'dogfood-probe.ps1')
    Throws {New-TestCandidate -SourceRoot $missingProbe -SlotsRoot (Join-Path $root 'missing-probe-slots') -Version '0.8.0' -SourceIdentity 'commit/missing-probe' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build'} 'probe.missing'
    $canonicalPackage=Join-Path $root 'artifacts/beta-packages/verified-package';New-Item -ItemType Directory -Path (Split-Path $canonicalPackage -Parent) -Force|Out-Null;Copy-Item $source $canonicalPackage -Recurse
    $canonical=New-TestCandidate -SourceRoot $canonicalPackage -SlotsRoot (Join-Path $root 'canonical-slots') -Version '0.9.0' -SourceIdentity 'commit/canonical' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build'
    Assert ($canonical.ManifestSha256 -match '^[a-f0-9]{64}$') 'canonical verified artifacts package was rejected solely for its path'
    Throws {New-TestCandidate -SourceRoot $source -SlotsRoot (Join-Path $root 'reject-slots') -Version '0.9.1' -SourceIdentity 'commit/unsigned' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build' -Unsigned} 'package.verification-not-distributable'
    Throws {New-TestCandidate -SourceRoot $source -SlotsRoot (Join-Path $root 'reject-slots') -Version '0.9.2' -SourceIdentity 'commit/dirty' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build' -Dirty} 'package.metadata-not-distributable'
    Throws {New-TestCandidate -SourceRoot $source -SlotsRoot (Join-Path $root 'reject-slots') -Version '0.9.3' -SourceIdentity 'commit/review' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build' -ReviewRequired} 'package.verification-not-distributable'
    Throws {New-TestCandidate -SourceRoot $source -SlotsRoot (Join-Path $root 'reject-slots') -Version '0.9.4' -SourceIdentity 'commit/nondistributable' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build' -NotDistributable} 'package.verification-not-distributable'
    Throws {New-TestCandidate -SourceRoot $source -SlotsRoot (Join-Path $root 'reject-slots') -Version '0.9.5' -SourceIdentity 'commit/unanchored' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build' -BadVerificationHash} 'package.verification-hash-mismatch'
    Throws {New-TestCandidate -SourceRoot $source -SlotsRoot (Join-Path $root 'reject-slots') -Version '0.9.6' -SourceIdentity 'commit/hash-mismatch' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build' -BadManifestHash} 'package.manifest-hash-mismatch'
    $slots=Join-Path $root 'slots';$lkg=New-TestCandidate -SourceRoot $source -SlotsRoot $slots -Version '1.0.0' -SourceIdentity 'commit/aaa' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build','tests'
    $candidate=New-TestCandidate -SourceRoot $source -SlotsRoot $slots -Version '1.0.1' -SourceIdentity 'commit/bbb' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build','tests'
    Assert (@(Get-ChildItem -LiteralPath $candidate.CandidateRoot,$lkg.CandidateRoot -Recurse -File|Where-Object {-not $_.IsReadOnly}).Count -eq 0) 'candidate or LKG contains mutable staged files'
    Assert ($candidate.ManifestSha256 -eq (Hash (Join-Path $candidate.CandidateRoot 'release-manifest.json'))) 'candidate identity is not deterministic over staged bytes'
    $proofPath=Join-Path $root 'trusted-runtime-proof.json';$proofHash=Write-Proof $proofPath $candidate $lkg @{}
    $soakPath=Join-Path $root 'headless-soak-evidence.json';$soakHash=Write-SoakEvidence $soakPath $candidate @{}
    $acceptance=@{CandidateRoot=$candidate.CandidateRoot;LastKnownGoodRoot=$lkg.CandidateRoot;ProviderReadOnly=$true;RequireTrustedRuntimeFresh=$true;VerifyInstall=$true;VerifyStartup=$true;VerifyRestartRecovery=$true;VerifyRollback=$true;RejectMainnet=$true;ExpectedCandidateManifestHash=$candidate.ManifestSha256;ExpectedLastKnownGoodManifestHash=$lkg.ManifestSha256;TrustedRuntimeProofPath=$proofPath;ExpectedTrustedRuntimeProofHash=$proofHash;ExpectedRuntimeProofId='proof/current-001';ExpectedRuntimeSourceIdentity='runtime/snapshot-authority-v1';ExpectedEvidenceGateRefs=@('gate/build','gate/tests');SoakEvidencePath=$soakPath;ExpectedSoakEvidenceHash=$soakHash;MinimumSoakDurationMinutes=1440}
    $result=& $test @acceptance;Assert $result.Valid 'full acceptance failed';Assert ($result.Transcript -contains 'testnet-mutation:not-run') 'mutation default changed';Assert ($result.UserDataRollback -eq 'not-performed') 'user data rollback changed';Assert ($result.SoakEvidenceSha256 -eq $soakHash) 'soak evidence identity missing'
    Assert (($result.Transcript[2..5] -join ',') -eq 'install:pass,startup:pass,restart:pass,rollback-preflight:pass') 'verification order changed'
    Throws {& $test @acceptance -Mainnet} 'mainnet.always-forbidden';Throws {& $test @acceptance -TestnetMutationSmoke} 'mutation.gate-evidence-required'

    $bad=$acceptance.Clone();$bad.TrustedRuntimeProofPath=Join-Path $root 'missing.json';Throws {& $test @bad} 'runtime.proof-missing'
    '{bad-json'|Set-Content -LiteralPath $proofPath -Encoding ascii;$bad=$acceptance.Clone();$bad.ExpectedTrustedRuntimeProofHash=Hash $proofPath;Throws {& $test @bad} 'runtime.proof-malformed'
    $badHash=Write-Proof $proofPath $candidate $lkg @{unexpected='not-authorized'};$bad=$acceptance.Clone();$bad.ExpectedTrustedRuntimeProofHash=$badHash;Throws {& $test @bad} 'runtime.proof-unknown-field'
    $incomplete=[ordered]@{schemaVersion='wpe.trusted-runtime-proof/1.0';proofId='proof/current-001'};$incomplete|ConvertTo-Json|Set-Content $proofPath -Encoding utf8;$bad=$acceptance.Clone();$bad.ExpectedTrustedRuntimeProofHash=Hash $proofPath;Throws {& $test @bad} 'runtime.proof-source-untrusted'
    Add-Content -LiteralPath $proofPath -Value 'tamper';Throws {& $test @acceptance} 'runtime.proof-hash-mismatch'
    $proofHash=Write-Proof $proofPath $candidate $lkg @{};$acceptance.ExpectedTrustedRuntimeProofHash=$proofHash
    $bad=$acceptance.Clone();$bad.ExpectedRuntimeProofId='proof/new-invocation';Throws {& $test @bad} 'runtime.proof-replay'
    $bad=$acceptance.Clone();$bad.ExpectedRuntimeSourceIdentity='runtime/other';Throws {& $test @bad} $f01.expectedRejection
    $old=[DateTimeOffset]::UtcNow.AddMinutes(-10);$badHash=Write-Proof $proofPath $candidate $lkg @{sourceTimestampUtc=$old.AddSeconds(-1).ToString('O');snapshotTimestampUtc=$old.ToString('O')};$bad=$acceptance.Clone();$bad.ExpectedTrustedRuntimeProofHash=$badHash;Throws {& $test @bad} 'runtime.proof-stale'
    $badHash=Write-Proof $proofPath $candidate $lkg @{bridgeState='unavailable'};$bad=$acceptance.Clone();$bad.ExpectedTrustedRuntimeProofHash=$badHash;Throws {& $test @bad} 'runtime.bridge-disconnected'
    $authority=[ordered]@{id='authority/dogfood-001';status='revoked';current=$false;expiresUtc=[DateTimeOffset]::UtcNow.AddMinutes(5).ToString('O')};$badHash=Write-Proof $proofPath $candidate $lkg @{authority=$authority};$bad=$acceptance.Clone();$bad.ExpectedTrustedRuntimeProofHash=$badHash;Throws {& $test @bad} 'runtime.authority-not-current'
    $badHash=Write-Proof $proofPath $candidate $lkg @{evidenceGates=@([ordered]@{reference='gate/build';status='passed'})};$bad=$acceptance.Clone();$bad.ExpectedTrustedRuntimeProofHash=$badHash;Throws {& $test @bad} 'runtime.evidence-gate-missing'
    $badHash=Write-Proof $proofPath $candidate $lkg @{candidateManifestSha256=('0'*64)};$bad=$acceptance.Clone();$bad.ExpectedTrustedRuntimeProofHash=$badHash;Throws {& $test @bad} 'runtime.proof-manifest-binding-mismatch'

    $proofHash=Write-Proof $proofPath $candidate $lkg @{};$acceptance.ExpectedTrustedRuntimeProofHash=$proofHash
    $bad=$acceptance.Clone();$bad.SoakEvidencePath=Join-Path $root 'missing-soak.json';Throws {& $test @bad} 'soak.evidence-missing'
    $bad=$acceptance.Clone();$bad.ExpectedSoakEvidenceHash=('0'*64);Throws {& $test @bad} 'soak.evidence-hash-mismatch'
    $badSoakHash=Write-SoakEvidence $soakPath $candidate @{sourceIdentity='commit/other'};$bad=$acceptance.Clone();$bad.ExpectedSoakEvidenceHash=$badSoakHash;Throws {& $test @bad} 'soak.source-identity-mismatch'
    $soakHash=Write-SoakEvidence $soakPath $candidate @{};$acceptance.ExpectedSoakEvidenceHash=$soakHash

    $proofHash=Write-Proof $proofPath $candidate $lkg @{};$acceptance.ExpectedTrustedRuntimeProofHash=$proofHash
    $manifestPath=Join-Path $candidate.CandidateRoot 'release-manifest.json';(Get-Item $manifestPath).IsReadOnly=$false;$manifest=Get-Content -Raw $manifestPath|ConvertFrom-Json;$manifest.gates=@($f02.attack.rewriteManifestGate);$manifest|ConvertTo-Json -Depth 10|Set-Content $manifestPath -Encoding utf8
    Throws {& $test @acceptance} $f02.expectedRejection
    $candidate=New-TestCandidate -SourceRoot $source -SlotsRoot $slots -Version '1.0.2' -SourceIdentity 'commit/ccc' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build','tests';$acceptance.CandidateRoot=$candidate.CandidateRoot;$acceptance.ExpectedCandidateManifestHash=$candidate.ManifestSha256;$acceptance.ExpectedTrustedRuntimeProofHash=Write-Proof $proofPath $candidate $lkg @{}
    (Get-Item -LiteralPath (Join-Path $candidate.CandidateRoot 'app.bin')).IsReadOnly=$false;'tamper'|Set-Content -LiteralPath (Join-Path $candidate.CandidateRoot 'app.bin');Throws {& $test @acceptance} 'candidate.hash-drift'
    $candidate=New-TestCandidate -SourceRoot $source -SlotsRoot $slots -Version '1.0.3' -SourceIdentity 'commit/ddd' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build','tests';$acceptance.CandidateRoot=$candidate.CandidateRoot;$acceptance.ExpectedCandidateManifestHash=$candidate.ManifestSha256;$acceptance.ExpectedTrustedRuntimeProofHash=Write-Proof $proofPath $candidate $lkg @{};$acceptance.ExpectedSoakEvidenceHash=Write-SoakEvidence $soakPath $candidate @{}
    $operator=Join-Path $root 'operator';$switchArgs=@{CandidateRoot=$candidate.CandidateRoot;LastKnownGoodRoot=$lkg.CandidateRoot;OperatorRoot=$operator;ExpectedCandidateManifestHash=$candidate.ManifestSha256;ExpectedLastKnownGoodManifestHash=$lkg.ManifestSha256;TrustedRuntimeProofPath=$proofPath;ExpectedTrustedRuntimeProofHash=$acceptance.ExpectedTrustedRuntimeProofHash;ExpectedRuntimeProofId='proof/current-001';ExpectedRuntimeSourceIdentity='runtime/snapshot-authority-v1';ExpectedEvidenceGateRefs=@('gate/build','gate/tests');SoakEvidencePath=$soakPath;ExpectedSoakEvidenceHash=$acceptance.ExpectedSoakEvidenceHash;MinimumSoakDurationMinutes=1440}
    $switched=& $switch @switchArgs;Assert (Test-Path (Join-Path $operator 'current.json')) 'atomic pointer missing';Assert $switched.Valid 'switch validation failed';$current=Get-Content -Raw -LiteralPath (Join-Path $operator 'current.json')|ConvertFrom-Json;Assert ($current.soakEvidenceSha256 -eq $acceptance.ExpectedSoakEvidenceHash) 'atomic pointer omitted soak evidence identity'
    $mutableRoot=Join-Path $root 'bin';Copy-Item -LiteralPath $candidate.CandidateRoot -Destination $mutableRoot -Recurse;$bad=$acceptance.Clone();$bad.CandidateRoot=$mutableRoot;Throws {& $test @bad} 'candidate.mutable-root'
    $otherSource=Join-Path $root 'other-package';Copy-Item -LiteralPath $source -Destination $otherSource -Recurse
    $other=New-TestCandidate -SourceRoot $otherSource -SlotsRoot $slots -Version '1.1.0' -SourceIdentity 'commit/other' -ConfigurationSchema 'cfg/2' -MigrationVersion 'db/1' -Gates 'build','tests';$bad=$acceptance.Clone();$bad.CandidateRoot=$other.CandidateRoot;$bad.ExpectedCandidateManifestHash=$other.ManifestSha256;$bad.ExpectedTrustedRuntimeProofHash=Write-Proof $proofPath $other $lkg @{};Throws {& $test @bad} 'configuration.schema-mismatch'
    $badSource=Join-Path $root 'bad-package';New-Item -ItemType Directory $badSource|Out-Null;'payload'|Set-Content (Join-Path $badSource 'app.bin') -Encoding ascii
    "param([string]`$Mode,[switch]`$ProviderReadOnly,[string]`$Environment);if(`$Mode -eq 'startup'){exit 7};exit 0"|Set-Content (Join-Path $badSource 'dogfood-probe.ps1') -Encoding utf8
    $failed=New-TestCandidate -SourceRoot $badSource -SlotsRoot $slots -Version '1.2.0' -SourceIdentity 'commit/fail' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build','tests';$failedProofHash=Write-Proof $proofPath $failed $lkg @{};$failedSoakHash=Write-SoakEvidence $soakPath $failed @{}
    $failedArgs=$switchArgs.Clone();$failedArgs.CandidateRoot=$failed.CandidateRoot;$failedArgs.ExpectedCandidateManifestHash=$failed.ManifestSha256;$failedArgs.ExpectedTrustedRuntimeProofHash=$failedProofHash;$failedArgs.ExpectedSoakEvidenceHash=$failedSoakHash;$failedArgs.OperatorRoot=Join-Path $root 'failed-operator'
    Throws {& $switch @failedArgs} 'probe.startup.failed';Assert (-not(Test-Path (Join-Path $failedArgs.OperatorRoot 'current.json'))) 'failed candidate switched operator pointer'
    Throws {& $new -SourceRoot (Join-Path $root 'release') -SlotsRoot $slots -Version '2.0.0' -SourceIdentity 'commit/eee' -ConfigurationSchema 'cfg/1' -MigrationVersion 'db/1' -Gates 'build' -ExpectedSourceManifestHash ('0'*64) -PackageVerificationPath (Join-Path $root 'missing-verification.json') -ExpectedPackageVerificationHash ('0'*64)} 'source.missing'
    'PASS ReleaseSlots anchored-manifest/trusted-runtime/install/start/restart/rollback/atomic-switch/mainnet/mutation-default/user-data'
}finally{Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}

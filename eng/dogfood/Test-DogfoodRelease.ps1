[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$LastKnownGoodRoot,
    [switch]$ProviderReadOnly,
    [switch]$RequireTrustedRuntimeFresh,
    [switch]$VerifyInstall,
    [switch]$VerifyStartup,
    [switch]$VerifyRestartRecovery,
    [switch]$VerifyRollback,
    [switch]$RejectMainnet,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedCandidateManifestHash,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedLastKnownGoodManifestHash,
    [Parameter(Mandatory)][string]$TrustedRuntimeProofPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedTrustedRuntimeProofHash,
    [Parameter(Mandatory)][string]$ExpectedRuntimeProofId,
    [Parameter(Mandatory)][string]$ExpectedRuntimeSourceIdentity,
    [Parameter(Mandatory)][string[]]$ExpectedEvidenceGateRefs,
    [ValidateRange(1,300)][int]$MaximumRuntimeAgeSeconds = 120,
    [switch]$TestnetMutationSmoke,
    [string]$VerifiedGateEvidenceRef,
    [switch]$Mainnet
)
$ErrorActionPreference='Stop'
if($Mainnet){throw 'mainnet.always-forbidden'}
if(-not $RejectMainnet){throw 'mainnet.rejection-required'}
if($TestnetMutationSmoke -and [string]::IsNullOrWhiteSpace($VerifiedGateEvidenceRef)){throw 'mutation.gate-evidence-required'}
if(-not $ProviderReadOnly){throw 'provider.read-only-required'}
if(-not $RequireTrustedRuntimeFresh){throw 'runtime.trusted-fresh-required'}
if(-not($VerifyInstall -and $VerifyStartup -and $VerifyRestartRecovery -and $VerifyRollback)){throw 'verification.full-sequence-required'}

function Get-FileSnapshot([string]$path){
    $bytes=[IO.File]::ReadAllBytes($path);$sha=[Security.Cryptography.SHA256]::Create()
    try{$hash=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    [pscustomobject]@{Bytes=$bytes;Hash=$hash}
}
function Convert-SnapshotToText($snapshot){
    $stream=[IO.MemoryStream]::new($snapshot.Bytes,$false);$reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true)
    try{$reader.ReadToEnd()}finally{$reader.Dispose();$stream.Dispose()}
}
function Get-Sha256([string]$path){(Get-FileSnapshot $path).Hash}
function Read-VerifiedManifest([string]$root,[string]$label,[string]$expectedHash){
    $resolved=[IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if($resolved -match '(?i)[\\/](bin|obj|debug|release|artifacts|\.artifacts)([\\/]|$)'){throw "$label.mutable-root"}
    $path=Join-Path $resolved 'release-manifest.json';if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "$label.manifest-missing"}
    $snapshot=Get-FileSnapshot $path;$actualHash=$snapshot.Hash
    if($actualHash -ne $expectedHash.ToLowerInvariant()){throw "$label.manifest-hash-mismatch"}
    $m=(Convert-SnapshotToText $snapshot)|ConvertFrom-Json
    if($m.schemaVersion -ne 'wpe.dogfood-release-manifest/1.0'){throw "$label.schema-mismatch"}
    if($m.environment -ne 'Testnet'){throw "$label.environment-mismatch"}
    if(-not $m.configurationSchema -or -not $m.migrationVersion -or -not $m.sourceIdentity){throw "$label.manifest-incomplete"}
    if(($m|ConvertTo-Json -Depth 20) -match '(?i)api.?key|secret|password|credential|private.?key'){throw "$label.credentials-forbidden"}
    foreach($entry in $m.files){
        $file=Join-Path $resolved ([string]$entry.path);$full=[IO.Path]::GetFullPath($file)
        if(-not $full.StartsWith($resolved+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "$label.path-escape"}
        if(-not(Test-Path -LiteralPath $full -PathType Leaf)){throw "$label.file-missing"}
        if((Get-Sha256 $full) -ne $entry.sha256 -or (Get-Item -LiteralPath $full).Length -ne $entry.length){throw "$label.hash-drift"}
    }
    [pscustomobject]@{Root=$resolved;Manifest=$m;ManifestHash=$actualHash}
}
function Convert-RoundtripTimestamp($value,[string]$code){
    try{
        if($value -is [DateTimeOffset]){return ([DateTimeOffset]$value).ToUniversalTime()}
        if($value -is [DateTime]){
            $date=[DateTime]$value
            if($date.Kind -eq [DateTimeKind]::Unspecified){throw $code}
            return ([DateTimeOffset]$date).ToUniversalTime()
        }
        return [DateTimeOffset]::Parse([string]$value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
    }catch{throw $code}
}
function Read-TrustedRuntimeProof($candidate,$lkg){
    $path=[IO.Path]::GetFullPath($TrustedRuntimeProofPath)
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'runtime.proof-missing'}
    $snapshot=Get-FileSnapshot $path
    if($snapshot.Hash -ne $ExpectedTrustedRuntimeProofHash.ToLowerInvariant()){throw 'runtime.proof-hash-mismatch'}
    try{$proof=(Convert-SnapshotToText $snapshot)|ConvertFrom-Json}catch{throw 'runtime.proof-malformed'}
    $allowed=@('schemaVersion','proofId','sourceIdentity','sourceTimestampUtc','snapshotTimestampUtc','environment','bridgeState','authority','evidenceGates','candidateManifestSha256','lastKnownGoodManifestSha256')
    if(@($proof.PSObject.Properties.Name|Where-Object {$allowed -notcontains $_}).Count){throw 'runtime.proof-unknown-field'}
    if($proof.schemaVersion -ne 'wpe.trusted-runtime-proof/1.0'){throw 'runtime.proof-schema-mismatch'}
    if($proof.proofId -ne $ExpectedRuntimeProofId){throw 'runtime.proof-replay'}
    if($proof.sourceIdentity -ne $ExpectedRuntimeSourceIdentity){throw 'runtime.proof-source-untrusted'}
    if($proof.environment -ne 'Testnet'){throw 'runtime.proof-environment-mismatch'}
    if($proof.bridgeState -ne 'connected'){throw 'runtime.bridge-disconnected'}
    if($proof.candidateManifestSha256 -ne $candidate.ManifestHash -or $proof.lastKnownGoodManifestSha256 -ne $lkg.ManifestHash){throw 'runtime.proof-manifest-binding-mismatch'}
    $source=Convert-RoundtripTimestamp $proof.sourceTimestampUtc 'runtime.source-timestamp-invalid'
    $snapshot=Convert-RoundtripTimestamp $proof.snapshotTimestampUtc 'runtime.snapshot-timestamp-invalid'
    $now=[DateTimeOffset]::UtcNow;$futureTolerance=[TimeSpan]::FromSeconds(5)
    if($source -gt $snapshot -or $snapshot -gt $now.Add($futureTolerance)){throw 'runtime.proof-timestamp-order-invalid'}
    if(($now-$snapshot).TotalSeconds -gt $MaximumRuntimeAgeSeconds -or ($snapshot-$source).TotalSeconds -gt $MaximumRuntimeAgeSeconds){throw 'runtime.proof-stale'}
    if(-not $proof.authority -or -not $proof.authority.id -or $proof.authority.status -ne 'active' -or $proof.authority.current -ne $true){throw 'runtime.authority-not-current'}
    $expires=Convert-RoundtripTimestamp $proof.authority.expiresUtc 'runtime.authority-expiry-invalid'
    if($expires -le $now){throw 'runtime.authority-revoked-or-expired'}
    $passed=@($proof.evidenceGates|Where-Object {$_.status -eq 'passed'}|ForEach-Object {[string]$_.reference})
    if($ExpectedEvidenceGateRefs.Count -eq 0 -or @($ExpectedEvidenceGateRefs|Where-Object {[string]::IsNullOrWhiteSpace($_) -or $passed -notcontains $_}).Count){throw 'runtime.evidence-gate-missing'}
    $proof
}
function Invoke-Probe($slot,[string]$mode){
    $probe=Join-Path $slot.Root ([string]$slot.Manifest.probeScript)
    if(-not(Test-Path -LiteralPath $probe -PathType Leaf)){throw "probe.$mode.missing"}
    $hostCommand=if(Get-Command pwsh -ErrorAction SilentlyContinue){'pwsh'}else{Join-Path $PSHOME 'powershell.exe'}
    & $hostCommand -NoProfile -ExecutionPolicy Bypass -File $probe -Mode $mode -ProviderReadOnly -Environment Testnet
    if($LASTEXITCODE -ne 0){throw "probe.$mode.failed"}
}
$candidate=Read-VerifiedManifest $CandidateRoot 'candidate' $ExpectedCandidateManifestHash
$lkg=Read-VerifiedManifest $LastKnownGoodRoot 'lkg' $ExpectedLastKnownGoodManifestHash
if($candidate.Manifest.configurationSchema -ne $lkg.Manifest.configurationSchema){throw 'configuration.schema-mismatch'}
if(-not $candidate.Manifest.gates -or $candidate.Manifest.gates.Count -eq 0){throw 'candidate.gates-missing'}
$runtimeProof=Read-TrustedRuntimeProof $candidate $lkg
$transcript=[Collections.Generic.List[string]]::new();$transcript.Add("trusted-runtime:pass:$($runtimeProof.proofId)")
if($VerifyInstall){Invoke-Probe $candidate 'install';$transcript.Add('install:pass')}
if($VerifyStartup){Invoke-Probe $candidate 'startup';$transcript.Add('startup:pass')}
if($VerifyRestartRecovery){Invoke-Probe $candidate 'restart';$transcript.Add('restart:pass')}
if($VerifyRollback){Invoke-Probe $lkg 'rollback';$transcript.Add('rollback-preflight:pass')}
if($TestnetMutationSmoke){$transcript.Add("testnet-mutation:authorized:$VerifiedGateEvidenceRef")}else{$transcript.Add('testnet-mutation:not-run')}
[pscustomobject]@{Valid=$true;CandidateVersion=$candidate.Manifest.version;LastKnownGoodVersion=$lkg.Manifest.version;CandidateManifestHash=$candidate.ManifestHash;LastKnownGoodManifestHash=$lkg.ManifestHash;TrustedRuntimeProofId=$runtimeProof.proofId;Transcript=$transcript;UserDataRollback='not-performed'}

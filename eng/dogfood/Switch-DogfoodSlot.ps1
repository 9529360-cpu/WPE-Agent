[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$LastKnownGoodRoot,
    [Parameter(Mandatory)][string]$OperatorRoot,
    [Parameter(Mandatory)][string]$ExpectedCandidateManifestHash,
    [Parameter(Mandatory)][string]$ExpectedLastKnownGoodManifestHash,
    [Parameter(Mandatory)][string]$TrustedRuntimeProofPath,
    [Parameter(Mandatory)][string]$ExpectedTrustedRuntimeProofHash,
    [Parameter(Mandatory)][string]$ExpectedRuntimeProofId,
    [Parameter(Mandatory)][string]$ExpectedRuntimeSourceIdentity,
    [Parameter(Mandatory)][string[]]$ExpectedEvidenceGateRefs,
    [Parameter(Mandatory)][string]$SoakEvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}
$ErrorActionPreference='Stop'
$test=Join-Path $PSScriptRoot 'Test-DogfoodRelease.ps1'
$result=& $test -CandidateRoot $CandidateRoot -LastKnownGoodRoot $LastKnownGoodRoot -ProviderReadOnly -RequireTrustedRuntimeFresh -VerifyInstall -VerifyStartup -VerifyRestartRecovery -VerifyRollback -RejectMainnet -ExpectedCandidateManifestHash $ExpectedCandidateManifestHash -ExpectedLastKnownGoodManifestHash $ExpectedLastKnownGoodManifestHash -TrustedRuntimeProofPath $TrustedRuntimeProofPath -ExpectedTrustedRuntimeProofHash $ExpectedTrustedRuntimeProofHash -ExpectedRuntimeProofId $ExpectedRuntimeProofId -ExpectedRuntimeSourceIdentity $ExpectedRuntimeSourceIdentity -ExpectedEvidenceGateRefs $ExpectedEvidenceGateRefs -SoakEvidencePath $SoakEvidencePath -ExpectedSoakEvidenceHash $ExpectedSoakEvidenceHash -MinimumSoakDurationMinutes $MinimumSoakDurationMinutes
if(-not $result.Valid){throw 'candidate.validation-failed'}
New-Item -ItemType Directory -Path $OperatorRoot -Force | Out-Null
$current=Join-Path $OperatorRoot 'current.json';$temporary=Join-Path $OperatorRoot ('.current.'+[Guid]::NewGuid().ToString('N')+'.tmp')
$previous=$null;if(Test-Path -LiteralPath $current){$previous=Get-Content -Raw -LiteralPath $current|ConvertFrom-Json}
if($previous -and $previous.root -ne [IO.Path]::GetFullPath($LastKnownGoodRoot)){throw 'lkg.does-not-match-current'}
[ordered]@{root=[IO.Path]::GetFullPath($CandidateRoot);manifestSha256=$result.CandidateManifestHash;switchedUtc=[DateTimeOffset]::UtcNow.ToString('O');previousRoot=[IO.Path]::GetFullPath($LastKnownGoodRoot)} | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
Move-Item -LiteralPath $temporary -Destination $current -Force
$result
)][string]$ExpectedSoakEvidenceHash,
    [ValidateRange(1,2880)][int]$MinimumSoakDurationMinutes = 1440
)
$ErrorActionPreference='Stop'
$test=Join-Path $PSScriptRoot 'Test-DogfoodRelease.ps1'
$result=& $test -CandidateRoot $CandidateRoot -LastKnownGoodRoot $LastKnownGoodRoot -ProviderReadOnly -RequireTrustedRuntimeFresh -VerifyInstall -VerifyStartup -VerifyRestartRecovery -VerifyRollback -RejectMainnet -ExpectedCandidateManifestHash $ExpectedCandidateManifestHash -ExpectedLastKnownGoodManifestHash $ExpectedLastKnownGoodManifestHash -TrustedRuntimeProofPath $TrustedRuntimeProofPath -ExpectedTrustedRuntimeProofHash $ExpectedTrustedRuntimeProofHash -ExpectedRuntimeProofId $ExpectedRuntimeProofId -ExpectedRuntimeSourceIdentity $ExpectedRuntimeSourceIdentity -ExpectedEvidenceGateRefs $ExpectedEvidenceGateRefs
if(-not $result.Valid){throw 'candidate.validation-failed'}
New-Item -ItemType Directory -Path $OperatorRoot -Force | Out-Null
$current=Join-Path $OperatorRoot 'current.json';$temporary=Join-Path $OperatorRoot ('.current.'+[Guid]::NewGuid().ToString('N')+'.tmp')
$previous=$null;if(Test-Path -LiteralPath $current){$previous=Get-Content -Raw -LiteralPath $current|ConvertFrom-Json}
if($previous -and $previous.root -ne [IO.Path]::GetFullPath($LastKnownGoodRoot)){throw 'lkg.does-not-match-current'}
[ordered]@{root=[IO.Path]::GetFullPath($CandidateRoot);manifestSha256=$result.CandidateManifestHash;switchedUtc=[DateTimeOffset]::UtcNow.ToString('O');previousRoot=[IO.Path]::GetFullPath($LastKnownGoodRoot)} | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
Move-Item -LiteralPath $temporary -Destination $current -Force
$result

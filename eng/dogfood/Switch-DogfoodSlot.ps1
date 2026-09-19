[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$LastKnownGoodRoot,
    [Parameter(Mandatory)][string]$OperatorRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedCandidateManifestHash,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedLastKnownGoodManifestHash,
    [Parameter(Mandatory)][string]$TrustedRuntimeProofPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedTrustedRuntimeProofHash,
    [Parameter(Mandatory)][string]$ExpectedRuntimeProofId,
    [Parameter(Mandatory)][string]$ExpectedRuntimeSourceIdentity,
    [Parameter(Mandatory)][string[]]$ExpectedEvidenceGateRefs,
    [Parameter(Mandatory)][string]$SoakEvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedSoakEvidenceHash,
    [ValidateRange(1,2880)][int]$MinimumSoakDurationMinutes = 1440
)

$ErrorActionPreference='Stop'

function Resolve-OperatorRoot([string]$Path){
    if([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path)){
        throw 'operator.root-invalid'
    }

    $resolved=[IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $root=[IO.Path]::GetPathRoot($resolved)
    if([string]::IsNullOrWhiteSpace($root) -or $root -notmatch '^[A-Za-z]:\\$'){
        throw 'operator.root-not-local-fixed'
    }

    try{$drive=[IO.DriveInfo]::new($root)}catch{throw 'operator.root-not-local-fixed'}
    if($drive.DriveType -ne [IO.DriveType]::Fixed){
        throw 'operator.root-not-local-fixed'
    }

    $current=$root
    $relative=$resolved.Substring($root.Length)
    foreach($part in $relative.Split([IO.Path]::DirectorySeparatorChar,[StringSplitOptions]::RemoveEmptyEntries)){
        $current=Join-Path $current $part
        if(Test-Path -LiteralPath $current){
            $item=Get-Item -LiteralPath $current -Force
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){
                throw 'operator.root-reparse-forbidden'
            }
        }
    }

    $resolved
}

function Is-Inside([string]$Path,[string]$Root){
    $pathFull=[IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $rootFull=[IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $pathFull.Equals($rootFull,[StringComparison]::OrdinalIgnoreCase) -or
        $pathFull.StartsWith($rootFull+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
}

$operator=Resolve-OperatorRoot $OperatorRoot
if((Is-Inside $operator $CandidateRoot) -or (Is-Inside $operator $LastKnownGoodRoot)){
    throw 'operator.root-inside-immutable-candidate'
}

New-Item -ItemType Directory -Path $operator -Force | Out-Null
$operator=Resolve-OperatorRoot $operator
$current=Join-Path $operator 'current.json'
$temporary=Join-Path $operator ('.current.'+[Guid]::NewGuid().ToString('N')+'.tmp')
$lockPath=Join-Path $operator 'switch.lock'

if(Test-Path -LiteralPath $lockPath){
    $lockItem=Get-Item -LiteralPath $lockPath -Force
    if(($lockItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){
        throw 'operator.lock-reparse-forbidden'
    }
}

$lockStream=$null
try{
    try{
        $lockStream=[IO.FileStream]::new(
            $lockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }catch [IO.IOException]{
        throw 'operator.switch-in-progress'
    }

    $test=Join-Path $PSScriptRoot 'Test-DogfoodRelease.ps1'
    $result=& $test -CandidateRoot $CandidateRoot -LastKnownGoodRoot $LastKnownGoodRoot -ProviderReadOnly -RequireTrustedRuntimeFresh -VerifyInstall -VerifyStartup -VerifyRestartRecovery -VerifyRollback -RejectMainnet -ExpectedCandidateManifestHash $ExpectedCandidateManifestHash -ExpectedLastKnownGoodManifestHash $ExpectedLastKnownGoodManifestHash -TrustedRuntimeProofPath $TrustedRuntimeProofPath -ExpectedTrustedRuntimeProofHash $ExpectedTrustedRuntimeProofHash -ExpectedRuntimeProofId $ExpectedRuntimeProofId -ExpectedRuntimeSourceIdentity $ExpectedRuntimeSourceIdentity -ExpectedEvidenceGateRefs $ExpectedEvidenceGateRefs -SoakEvidencePath $SoakEvidencePath -ExpectedSoakEvidenceHash $ExpectedSoakEvidenceHash -MinimumSoakDurationMinutes $MinimumSoakDurationMinutes
    if(-not $result.Valid){
        throw 'candidate.validation-failed'
    }

    if(Test-Path -LiteralPath $current){
        $currentItem=Get-Item -LiteralPath $current -Force
        if(($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){
            throw 'operator.current-reparse-forbidden'
        }
    }

    $previous=$null
    if(Test-Path -LiteralPath $current){
        $previous=Get-Content -Raw -LiteralPath $current | ConvertFrom-Json
    }
    if($previous -and $previous.root -ne [IO.Path]::GetFullPath($LastKnownGoodRoot)){
        throw 'lkg.does-not-match-current'
    }

    $pointer=[ordered]@{
        root=[IO.Path]::GetFullPath($CandidateRoot)
        manifestSha256=$result.CandidateManifestHash
        soakEvidenceSha256=$result.SoakEvidenceSha256
        switchedUtc=[DateTimeOffset]::UtcNow.ToString('O')
        previousRoot=[IO.Path]::GetFullPath($LastKnownGoodRoot)
    }

    $pointerJson=$pointer | ConvertTo-Json
    $pointerBytes=[Text.UTF8Encoding]::new($false).GetBytes($pointerJson)
    $pointerStream=$null
    try{
        $pointerStream=[IO.FileStream]::new(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $pointerStream.Write($pointerBytes,0,$pointerBytes.Length)
        $pointerStream.Flush($true)
    }finally{
        if($null -ne $pointerStream){$pointerStream.Dispose()}
    }
    [IO.File]::Move($temporary,$current,$true)
}finally{
    if(Test-Path -LiteralPath $temporary){
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
    if($null -ne $lockStream){$lockStream.Dispose()}
}

$result

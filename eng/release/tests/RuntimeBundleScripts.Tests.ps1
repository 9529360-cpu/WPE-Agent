$ErrorActionPreference='Stop'

function Throws([scriptblock]$action,[string]$contains){
    try{& $action|Out-Null;throw "expected:$contains"}
    catch{if($_.Exception.Message -notlike "*$contains*"){throw}}
}

$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$paths=@(
    'eng/release-readiness.ps1',
    'eng/sign-beta.ps1',
    'eng/package-beta.ps1',
    'eng/verify-beta-package.ps1',
    'eng/dogfood/New-Candidate.ps1',
    'eng/dogfood/Test-DogfoodRelease.ps1',
    'eng/dogfood/Switch-DogfoodSlot.ps1',
    'eng/dogfood/Invoke-V1CandidateGate.ps1',
    'eng/ops/Watch-HeadlessSoak.ps1',
    'eng/ops/Test-HeadlessSoakEvidence.ps1'
)

foreach($relative in $paths){
    $path=Join-Path $root $relative
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "release-script.missing:$relative"}
    $tokens=$null
    $errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
    if($errors.Count -gt 0){
        $details=@($errors|ForEach-Object{"$($_.Extent.StartLineNumber):$($_.Message)"}) -join '; '
        throw "release-script.parse-failed:${relative}:$details"
    }
}

$package=Get-Content -Raw -LiteralPath (Join-Path $root 'eng/package-beta.ps1')
$verify=Get-Content -Raw -LiteralPath (Join-Path $root 'eng/verify-beta-package.ps1')
$sign=Get-Content -Raw -LiteralPath (Join-Path $root 'eng/sign-beta.ps1')

foreach($required in @(
    'HeadlessPublishPath','MaintenancePublishPath','SigningResultPath',
    'Runtime bundle signature states must be all Valid or all NotSigned.',
    'Runtime bundle signer identity does not match the approved publisher.',
    'Runtime bundle signing attestation is missing.',
    'SIGNING-RESULT.p7s'
)){
    if($package.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "package.contract-missing:$required"}
}
foreach($required in @(
    'Payload manifest does not exactly cover the runtime bundle files.',
    'Archive entry escapes verification root.',
    'Archive contains duplicate normalized entry paths.',
    'Package metadata path escapes package root.',
    'Archive must contain exactly one top-level package directory.',
    'PAYLOAD-SHA256SUMS does not match FILE-MANIFEST.json.',
    '$expectedSumLine = "$zipHash  $([System.IO.Path]::GetFileName($zip))"',
    '(Get-Item -LiteralPath $path).Length -ne [long]$entry.size',
    'Runtime package signature states are mixed.',
    'Runtime package executable timestamp is missing',
    'Signed runtime bundle verification requires the approved signer subject and thumbprint.',
    'Signing transition signature hash mismatch.',
    'Unsigned runtime bundle must not contain a signing transition signature.'
)){
    if($verify.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "verify.contract-missing:$required"}
}
foreach($required in @(
    'wpe.runtime-bundle-signing/1.1',
    'cms-detached-sha256',
    'Write-AndVerifyDetachedAttestation',
    'Signing staging does not match release-readiness artifact',
    'Authenticode timestamp is missing',
    'Signing and verification passed for $($executables.Count) runtime executable(s).'
)){
    if($sign.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "sign.contract-missing:$required"}
}

$testRoot=Join-Path $root ('artifacts/release-script-security-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force|Out-Null
try{
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=Join-Path $testRoot 'zip-slip.zip'
    $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try{
        $entry=$archive.CreateEntry('../escape.txt')
        $writer=[IO.StreamWriter]::new($entry.Open())
        try{$writer.Write('escape')}finally{$writer.Dispose()}
    }finally{$archive.Dispose()}

    $zipHash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $resultPath=Join-Path $testRoot 'package-result.json'
    [ordered]@{package=$zip;sha256=$zipHash}|ConvertTo-Json|Set-Content -LiteralPath $resultPath -Encoding utf8
    "$zipHash  $([IO.Path]::GetFileName($zip))"|Set-Content -LiteralPath (Join-Path $testRoot 'SHA256SUMS') -Encoding ascii
    $verifyPath=Join-Path $root 'eng/verify-beta-package.ps1'
    $verification=Join-Path $testRoot 'verification'
    Throws {& $verifyPath -ResultPath $resultPath -VerificationDirectory $verification} 'Archive entry escapes verification root.'
    if(Test-Path -LiteralPath (Join-Path $testRoot 'escape.txt')){throw 'verify.zip-slip-created-escaped-file'}

    $zip=Join-Path $testRoot 'extra-top-level.zip'
    $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try{
        foreach($name in @('package/placeholder.txt','extra.txt')){
            $entry=$archive.CreateEntry($name)
            $writer=[IO.StreamWriter]::new($entry.Open())
            try{$writer.Write('x')}finally{$writer.Dispose()}
        }
    }finally{$archive.Dispose()}
    $zipHash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{package=$zip;sha256=$zipHash}|ConvertTo-Json|Set-Content -LiteralPath $resultPath -Encoding utf8
    "$zipHash  $([IO.Path]::GetFileName($zip))"|Set-Content -LiteralPath (Join-Path $testRoot 'SHA256SUMS') -Encoding ascii
    if(Test-Path -LiteralPath $verification){Remove-Item -LiteralPath $verification -Recurse -Force}
    Throws {& $verifyPath -ResultPath $resultPath -VerificationDirectory $verification} 'Archive must contain exactly one top-level package directory.'
}finally{
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

'PASS runtime bundle release scripts parse and preserve required contracts'

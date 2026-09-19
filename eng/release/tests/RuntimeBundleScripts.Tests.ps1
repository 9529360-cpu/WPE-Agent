$ErrorActionPreference='Stop'

$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$paths=@(
    'eng/release-readiness.ps1',
    'eng/sign-beta.ps1',
    'eng/package-beta.ps1',
    'eng/verify-beta-package.ps1',
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
        throw "release-script.parse-failed:$relative:$details"
    }
}

$package=Get-Content -Raw -LiteralPath (Join-Path $root 'eng/package-beta.ps1')
$verify=Get-Content -Raw -LiteralPath (Join-Path $root 'eng/verify-beta-package.ps1')
$sign=Get-Content -Raw -LiteralPath (Join-Path $root 'eng/sign-beta.ps1')

foreach($required in @(
    'HeadlessPublishPath','MaintenancePublishPath','SigningResultPath',
    'Runtime bundle signature states must be all Valid or all NotSigned.',
    'Runtime bundle signer identity does not match the approved publisher.'
)){
    if($package.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "package.contract-missing:$required"}
}
foreach($required in @(
    'Payload manifest does not exactly cover the runtime bundle files.',
    'Archive entry escapes verification root.',
    'Archive contains duplicate normalized entry paths.',
    'Package metadata path escapes package root.',
    'PAYLOAD-SHA256SUMS does not match FILE-MANIFEST.json.',
    '$expectedSumLine = "$zipHash  $([System.IO.Path]::GetFileName($zip))"',
    '(Get-Item -LiteralPath $path).Length -ne [long]$entry.length',
    'Runtime package signature states are mixed.',
    'Runtime package executable timestamp is missing',
    'Signed runtime bundle verification requires the approved signer subject and thumbprint.'
)){
    if($verify.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "verify.contract-missing:$required"}
}
foreach($required in @(
    'wpe.runtime-bundle-signing/1.0',
    'Signing staging does not match release-readiness artifact',
    'Authenticode timestamp is missing',
    'Signing and verification passed for $($executables.Count) runtime executable(s).'
)){
    if($sign.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "sign.contract-missing:$required"}
}

'PASS runtime bundle release scripts parse and preserve required contracts'

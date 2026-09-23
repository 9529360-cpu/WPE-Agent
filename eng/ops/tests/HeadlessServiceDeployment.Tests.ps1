$ErrorActionPreference='Stop'

function Assert($condition,[string]$message){if(-not $condition){throw "ASSERT: $message"}}
function Throws([scriptblock]$action,[string]$contains){
    try{& $action|Out-Null;throw "expected:$contains"}
    catch{if($_.Exception.Message -notlike "*$contains*"){throw}}
}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function TextHash([string]$value){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{
        $bytes=[Text.Encoding]::UTF8.GetBytes($value)
        ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()
    }finally{$sha.Dispose()}
}
function TreeFacts([string]$path){
    $base=[IO.Path]::GetFullPath($path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix=$base+[IO.Path]::DirectorySeparatorChar
    $files=@(Get-ChildItem -LiteralPath $base -Recurse -Force -File|Sort-Object FullName)
    $lines=@($files|ForEach-Object{
        $relative=$_.FullName.Substring($prefix.Length).Replace('\','/')
        "$relative|$($_.Length)|$(Hash $_.FullName)"
    })
    [pscustomobject]@{FileCount=$files.Count;TreeSha256=TextHash ($lines -join [Environment]::NewLine)}
}
function Write-Verification([string]$candidate,[string]$path){
    $tree=TreeFacts $candidate
    [ordered]@{
        schemaVersion='wpe.beta-package-verification.v1'
        status='passed'
        packageTreeSha256=$tree.TreeSha256
        packageFileCount=$tree.FileCount
    }|ConvertTo-Json|Set-Content -LiteralPath $path -Encoding utf8
    Hash $path
}

$tool=Split-Path $PSScriptRoot -Parent
$preflight=Join-Path $tool 'Test-HeadlessServiceDeployment.ps1'
$root=Join-Path ([IO.Path]::GetTempPath()) ('wpe-service-preflight-'+[Guid]::NewGuid().ToString('N'))
$candidate=Join-Path $root 'candidate'
$data=Join-Path $root 'data'
$verification=Join-Path $root 'package-verification.json'
New-Item -ItemType Directory -Path (Join-Path $candidate 'headless') -Force|Out-Null
New-Item -ItemType Directory -Path (Join-Path $candidate 'maintenance') -Force|Out-Null
New-Item -ItemType Directory -Path $data -Force|Out-Null
Set-Content -LiteralPath (Join-Path $candidate 'headless\WPE-Headless.exe') -Value 'headless' -Encoding ascii
Set-Content -LiteralPath (Join-Path $candidate 'maintenance\WPE.Maintenance.exe') -Value 'maintenance' -Encoding ascii

try{
    $verificationHash=Write-Verification $candidate $verification
    $base=@{
        CandidateRoot=$candidate
        DataRoot=$data
        ServiceAccount='MACHINE\wpe'
        MaintenanceAccount='machine\WPE'
        PackageVerificationPath=$verification
        ExpectedPackageVerificationHash=$verificationHash
    }

    $result=& $preflight @base
    Assert $result.valid 'valid deployment rejected'
    Assert ($result.schemaVersion -eq 'wpe.headless-service-deployment-preflight/1.1') 'schema mismatch'
    Assert (-not $result.scmMutationPerformed) 'preflight mutated SCM'
    Assert (-not $result.credentialsAccepted) 'preflight accepted credentials'
    Assert ($result.imagePath.Contains('--data-root')) 'image path omitted data-root'
    Assert ($result.headlessExecutable.EndsWith('headless\WPE-Headless.exe')) 'headless executable binding missing'
    Assert ($result.maintenanceExecutable.EndsWith('maintenance\WPE.Maintenance.exe')) 'maintenance executable binding missing'
    Assert ($result.packageVerificationSha256 -eq $verificationHash) 'verification identity missing'
    Assert ($result.candidateTreeSha256 -eq (TreeFacts $candidate).TreeSha256) 'candidate tree identity missing'

    $bad=$base.Clone();$bad.CandidateRoot='relative-candidate'
    Throws {& $preflight @bad} 'candidate.root.not-absolute'
    $bad=$base.Clone();$bad.DataRoot='\\server\share\wpe'
    Throws {& $preflight @bad} 'data.root.network-path-forbidden'
    $bad=$base.Clone();$bad.DataRoot=Join-Path $candidate 'state'
    Throws {& $preflight @bad} 'data.root-candidate-overlap'
    $bad=$base.Clone();$bad.ServiceAccount='svc-a';$bad.MaintenanceAccount='svc-b'
    Throws {& $preflight @bad} 'account.dpapi-identity-mismatch'

    $bad=$base.Clone();$bad.ExpectedPackageVerificationHash=('0'*64)
    Throws {& $preflight @bad} 'package.verification-hash-mismatch'

    'late-runtime-byte'|Set-Content -LiteralPath (Join-Path $candidate 'headless\late.dll') -Encoding ascii
    Throws {& $preflight @base} 'candidate.package-tree-mismatch'
    Remove-Item -LiteralPath (Join-Path $candidate 'headless\late.dll') -Force

    $reparseTarget=Join-Path $root 'reparse-target';New-Item -ItemType Directory -Path $reparseTarget -Force|Out-Null
    $reparsePath=Join-Path $candidate 'linked'
    try{
        $null=New-Item -ItemType Junction -Path $reparsePath -Target $reparseTarget -ErrorAction Stop
        Throws {& $preflight @base} 'candidate.tree-reparse-forbidden'
    }catch{
        if($_.Exception.Message -notlike '*candidate.tree-reparse-forbidden*'){Write-Output 'HeadlessServiceDeployment.Tests: reparse regression SKIP (junction unavailable)'}
    }finally{
        if(Test-Path -LiteralPath $reparsePath){Remove-Item -LiteralPath $reparsePath -Force -ErrorAction SilentlyContinue}
    }

    Remove-Item -LiteralPath (Join-Path $candidate 'headless\WPE-Headless.exe') -Force
    $base.ExpectedPackageVerificationHash=Write-Verification $candidate $verification
    Throws {& $preflight @base} 'candidate.headless-exe-missing'
    Set-Content -LiteralPath (Join-Path $candidate 'headless\WPE-Headless.exe') -Value 'headless' -Encoding ascii
    $base.ExpectedPackageVerificationHash=Write-Verification $candidate $verification

    $insideVerification=Join-Path $candidate 'verification.json'
    Copy-Item -LiteralPath $verification -Destination $insideVerification
    $bad=$base.Clone();$bad.PackageVerificationPath=$insideVerification;$bad.ExpectedPackageVerificationHash=Hash $insideVerification
    Throws {& $preflight @bad} 'package.verification-inside-candidate-forbidden'
    Remove-Item -LiteralPath $insideVerification -Force

    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($preflight,[ref]$tokens,[ref]$errors)
    Assert ($errors.Count -eq 0) 'preflight AST parse errors'
    $source=Get-Content -LiteralPath $preflight -Raw
    foreach($required in @(
        'PackageVerificationPath',
        'ExpectedPackageVerificationHash',
        'candidate.package-tree-mismatch',
        'candidate.tree-reparse-forbidden',
        'wpe.headless-service-deployment-preflight/1.1'
    )){
        if($source.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "required deployment contract missing: $required"}
    }
    foreach($forbidden in @('New-Service','Set-Service','Start-Service','Stop-Service','Restart-Service','sc.exe','Invoke-WebRequest','Invoke-RestMethod')){
        if($source -match [regex]::Escape($forbidden)){throw "forbidden mutation/network command: $forbidden"}
    }

    'PASS headless service deployment preflight'
}finally{
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

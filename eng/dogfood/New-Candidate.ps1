[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$SlotsRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$')][string]$Version,
    [Parameter(Mandatory)][string]$SourceIdentity,
    [Parameter(Mandatory)][string]$ConfigurationSchema,
    [Parameter(Mandatory)][string]$MigrationVersion,
    [Parameter(Mandatory)][string[]]$Gates,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedSourceManifestHash,
    [Parameter(Mandatory)][string]$PackageVerificationPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedPackageVerificationHash,
    [ValidateSet('Testnet')][string]$Environment = 'Testnet'
)
$ErrorActionPreference='Stop'
function Full([string]$p){[IO.Path]::GetFullPath($p).TrimEnd([IO.Path]::DirectorySeparatorChar)}
function Is-SameOrDescendant([string]$Path,[string]$Root){
    $pathFull=Full $Path
    $rootFull=Full $Root
    if($pathFull.Equals($rootFull,[StringComparison]::OrdinalIgnoreCase)){return $true}
    $prefix=$rootFull+[IO.Path]::DirectorySeparatorChar
    $pathFull.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)
}
function Assert-NoReparsePath([string]$Path,[string]$Code){
    $full=[IO.Path]::GetFullPath($Path)
    $root=[IO.Path]::GetPathRoot($full)
    $current=$root
    $relative=$full.Substring($root.Length)
    foreach($part in $relative.Split([IO.Path]::DirectorySeparatorChar,[StringSplitOptions]::RemoveEmptyEntries)){
        $current=Join-Path $current $part
        if(Test-Path -LiteralPath $current){
            $item=Get-Item -LiteralPath $current -Force
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw $Code}
        }
    }
}
$source=Full $SourceRoot;$slots=Full $SlotsRoot
if(-not(Test-Path -LiteralPath $source -PathType Container)){throw 'source.missing'}
if((Is-SameOrDescendant $source $slots) -or (Is-SameOrDescendant $slots $source)){throw 'source.slots-overlap'}
Assert-NoReparsePath $source 'source.reparse-forbidden'
Assert-NoReparsePath $slots 'slots.reparse-forbidden'
if(@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
}).Count -gt 0){throw 'source.reparse-forbidden'}
if($source -match '(?i)[\\/](bin|obj|debug|release|\.artifacts)([\\/]|$)'){throw 'source.mutable-output-forbidden'}
if($source -match '(?i)[\\/]artifacts([\\/]|$)' -and $source -notmatch '(?i)[\\/]artifacts[\\/]beta-packages[\\/]'){throw 'source.unverified-artifact-root'}
if($SourceIdentity -notmatch '^[0-9A-Za-z][0-9A-Za-z._/-]{0,127}$'){throw 'source.identity-invalid'}
if($Gates.Count -eq 0 -or $Gates | Where-Object {[string]::IsNullOrWhiteSpace($_)}){throw 'gates.invalid'}

function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Get-TreeSnapshot([string]$basePath){
    $base=Full $basePath
    $prefix=$base+[IO.Path]::DirectorySeparatorChar
    @(
        Get-ChildItem -LiteralPath $base -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
            $relative=$_.FullName.Substring($prefix.Length).Replace('\\','/')
            "$relative|$($_.Length)|$(Hash $_.FullName)"
        }
    )
}
$verificationPath=[IO.Path]::GetFullPath($PackageVerificationPath)
if(-not(Test-Path -LiteralPath $verificationPath -PathType Leaf)){throw 'package.verification-missing'}
if((Hash $verificationPath) -ne $ExpectedPackageVerificationHash.ToLowerInvariant()){throw 'package.verification-hash-mismatch'}
try{$verification=Get-Content -Raw -LiteralPath $verificationPath|ConvertFrom-Json}catch{throw 'package.verification-malformed'}
if($verification.schemaVersion -ne 'wpe.beta-package-verification.v1' -or $verification.status -ne 'passed' -or
   $verification.distributable -ne $true -or $verification.signingStatus -notin @('Valid','Signed') -or
   $verification.licenseGateStatus -ne 'passed' -or [int]$verification.licenseReview -ne 0 -or
   [int]$verification.manifestFailures -ne 0 -or [int]$verification.forbiddenFiles -ne 0){throw 'package.verification-not-distributable'}
$metadataPath=Join-Path $source 'RELEASE-METADATA.json'
if(-not(Test-Path -LiteralPath $metadataPath -PathType Leaf)){throw 'package.metadata-missing'}
try{$metadata=Get-Content -Raw -LiteralPath $metadataPath|ConvertFrom-Json}catch{throw 'package.metadata-malformed'}
if($metadata.source.dirty -ne $false -or $metadata.signing.distributable -ne $true -or $metadata.signing.status -notin @('Valid','Signed') -or
   $metadata.contents.licenseGateStatus -ne 'passed' -or [int]$metadata.contents.licenseReviewCount -ne 0){throw 'package.metadata-not-distributable'}
$sourceManifestPath=Join-Path $source 'FILE-MANIFEST.json'
if(-not(Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)){throw 'package.manifest-missing'}
if((Hash $sourceManifestPath) -ne $ExpectedSourceManifestHash.ToLowerInvariant()){throw 'package.manifest-hash-mismatch'}
try{$parsedManifest=Get-Content -Raw -LiteralPath $sourceManifestPath|ConvertFrom-Json;$sourceManifest=@($parsedManifest|ForEach-Object {$_})}catch{throw 'package.manifest-malformed'}
if($sourceManifest.Count -eq 0){throw 'package.manifest-empty'}
$sourcePrefix=$source+[IO.Path]::DirectorySeparatorChar
$manifestPaths=[Collections.Generic.List[string]]::new()
$seenManifestPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $sourceManifest){
    $relative=([string]$entry.path).Replace('\\','/')
    if([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative)){throw 'package.manifest-path-invalid'}
    if(-not $seenManifestPaths.Add($relative)){throw 'package.manifest-path-duplicate'}
    if($entry.PSObject.Properties.Name -notcontains 'size'){throw 'package.manifest-size-missing'}
    try{$expectedSize=[long]$entry.size}catch{throw 'package.manifest-size-invalid'}
    if($expectedSize -lt 0){throw 'package.manifest-size-invalid'}
    $file=[IO.Path]::GetFullPath((Join-Path $source ($relative.Replace('/',[IO.Path]::DirectorySeparatorChar))))
    if(-not $file.StartsWith($sourcePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'package.manifest-path-escape'}
    if(-not(Test-Path -LiteralPath $file -PathType Leaf) -or (Hash $file) -ne [string]$entry.sha256 -or (Get-Item -LiteralPath $file).Length -ne $expectedSize){throw "package.manifest-drift:$relative"}
    $manifestPaths.Add($relative)
}
$bundlePattern='^(app|headless|maintenance)/'
$bundleManifestPaths=@($manifestPaths|Where-Object{$_ -match $bundlePattern})
if($bundleManifestPaths.Count -gt 0 -and $bundleManifestPaths.Count -ne $manifestPaths.Count){throw 'package.manifest-mixed-root-model'}
if($bundleManifestPaths.Count -gt 0){
    $actualPayloadPaths=@(
        foreach($bundleRoot in @('app','headless','maintenance')){
            $bundlePath=Join-Path $source $bundleRoot
            if(-not(Test-Path -LiteralPath $bundlePath -PathType Container)){throw "package.runtime-root-missing:$bundleRoot"}
            Get-ChildItem -LiteralPath $bundlePath -Recurse -Force -File|ForEach-Object{
                $_.FullName.Substring($sourcePrefix.Length).Replace('\\','/')
            }
        }
    )
}else{
    $actualPayloadPaths=@(Get-ChildItem -LiteralPath $source -Recurse -Force -File|Where-Object{
        -not $_.FullName.Equals($sourceManifestPath,[StringComparison]::OrdinalIgnoreCase)
    }|ForEach-Object{$_.FullName.Substring($sourcePrefix.Length).Replace('\\','/')})
}
if($actualPayloadPaths.Count -ne $manifestPaths.Count -or
   (Compare-Object @($actualPayloadPaths|Sort-Object) @($manifestPaths|Sort-Object) -SyncWindow 0).Count -ne 0){
    throw 'package.manifest-inventory-drift'
}
$destination=Join-Path $slots (Join-Path 'versions' $Version)
if(Test-Path -LiteralPath $destination){throw 'candidate.version-exists'}
$versionsRoot=Split-Path -Parent $destination
New-Item -ItemType Directory -Path $versionsRoot -Force | Out-Null
Assert-NoReparsePath $versionsRoot 'slots.reparse-forbidden'
$staging=Join-Path $versionsRoot ('.'+$Version+'.'+[Guid]::NewGuid().ToString('N')+'.tmp')
$sourceSnapshot=@(Get-TreeSnapshot $source)

try{
    New-Item -ItemType Directory -Path $staging -ErrorAction Stop | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $staging -Recurse -Force -ErrorAction Stop

    $stagedSnapshot=@(Get-TreeSnapshot $staging)
    if($sourceSnapshot.Count -ne $stagedSnapshot.Count -or
       (Compare-Object $sourceSnapshot $stagedSnapshot -SyncWindow 0).Count -ne 0){
        throw 'candidate.copy-drift'
    }

    $files=@(Get-ChildItem -LiteralPath $staging -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path=$_.FullName.Substring($staging.Length+1).Replace('\\','/')
            sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            length=$_.Length
        }
    })
    $manifest=[ordered]@{
        schemaVersion='wpe.dogfood-release-manifest/1.1'
        version=$Version
        sourceIdentity=$SourceIdentity
        environment=$Environment
        configurationSchema=$ConfigurationSchema
        migrationVersion=$MigrationVersion
        createdUtc=[DateTimeOffset]::UtcNow.ToString('O')
        files=$files
        gates=@($Gates)
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $staging 'release-manifest.json') -Encoding utf8

    Get-ChildItem -LiteralPath $staging -Recurse -Force -File | ForEach-Object {$_.IsReadOnly=$true}
    Move-Item -LiteralPath $staging -Destination $destination -ErrorAction Stop
}catch{
    if(Test-Path -LiteralPath $staging){
        Get-ChildItem -LiteralPath $staging -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object {$_.IsReadOnly=$false}
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw
}

[pscustomobject]@{
    CandidateRoot=$destination
    ManifestSha256=(Get-FileHash -LiteralPath (Join-Path $destination 'release-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
}

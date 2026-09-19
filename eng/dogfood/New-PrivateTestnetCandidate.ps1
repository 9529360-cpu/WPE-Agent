[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$PayloadManifestPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedPayloadManifestHash,
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$')][string]$Version,
    [ValidateRange(1,86400)][int]$MaximumManifestAgeSeconds = 900,
    [switch]$CommercialDistribution,
    [switch]$Export,
    [switch]$Mainnet
)
$ErrorActionPreference='Stop'
function Stop-Candidate([string]$code){throw $code}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Resolve-LocalFixed([string]$path,[string]$label){
    if([string]::IsNullOrWhiteSpace($path) -or $path.StartsWith('\\')){Stop-Candidate "$label.network-path-forbidden"}
    if(-not[IO.Path]::IsPathRooted($path)){Stop-Candidate "$label.absolute-local-path-required"}
    $full=[IO.Path]::GetFullPath($path);$driveRoot=[IO.Path]::GetPathRoot($full)
    if($driveRoot -notmatch '^[A-Za-z]:\\$' -or [IO.DriveInfo]::new($driveRoot).DriveType -ne [IO.DriveType]::Fixed){Stop-Candidate "$label.fixed-volume-required"}
    $full
}
function Assert-NoReparse([string]$path,[string]$label){
    $current=[IO.Path]::GetPathRoot($path)
    foreach($part in $path.Substring($current.Length).Split([IO.Path]::DirectorySeparatorChar,[StringSplitOptions]::RemoveEmptyEntries)){
        $current=Join-Path $current $part
        if(Test-Path -LiteralPath $current){$item=Get-Item -LiteralPath $current -Force;if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){Stop-Candidate "$label.reparse-forbidden"}}
    }
}
if($CommercialDistribution -or $Export){Stop-Candidate 'candidate.local-only-export-forbidden'}
if($Mainnet){Stop-Candidate 'candidate.mainnet-forbidden'}
$payload=Resolve-LocalFixed $PayloadRoot 'payload';$manifestPath=Resolve-LocalFixed $PayloadManifestPath 'manifest';$destination=Resolve-LocalFixed $CandidateRoot 'candidate'
Assert-NoReparse $payload 'payload';Assert-NoReparse $manifestPath 'manifest';Assert-NoReparse (Split-Path $destination -Parent) 'candidate'
if(-not(Test-Path -LiteralPath $payload -PathType Container)){Stop-Candidate 'payload.missing'}
if($payload -match '(?i)[\\/](bin|obj|debug|release|staging|export)([\\/]|$)'){Stop-Candidate 'payload.mutable-root-forbidden'}
if(-not(Test-Path -LiteralPath $manifestPath -PathType Leaf)){Stop-Candidate 'manifest.missing'}
if((Hash $manifestPath) -ne $ExpectedPayloadManifestHash.ToLowerInvariant()){Stop-Candidate 'manifest.external-anchor-mismatch'}
try{$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json}catch{Stop-Candidate 'manifest.malformed'}
$allowed=@('schemaVersion','version','createdUtc','localOnly','commercialDistribution','mainnet','probeScript','files')
if(@($manifest.PSObject.Properties.Name|Where-Object {$allowed -notcontains $_}).Count){Stop-Candidate 'manifest.unknown-field'}
if($manifest.schemaVersion -ne 'wpe.private-testnet-payload/1.0' -or $manifest.version -ne $Version -or $manifest.localOnly -ne $true -or $manifest.commercialDistribution -ne $false -or $manifest.mainnet -ne $false){Stop-Candidate 'manifest.boundary-mismatch'}
try{$created=[DateTimeOffset]::ParseExact([string]$manifest.createdUtc,'o',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None)}catch{Stop-Candidate 'manifest.timestamp-invalid'}
$age=([DateTimeOffset]::UtcNow-$created).TotalSeconds;if($age -lt -5 -or $age -gt $MaximumManifestAgeSeconds){Stop-Candidate 'manifest.stale'}
if([string]::IsNullOrWhiteSpace([string]$manifest.probeScript) -or $manifest.probeScript -notmatch '^[^:\\]+\.ps1$'){Stop-Candidate 'probe.invalid'}
$entries=@($manifest.files|ForEach-Object {$_});if($entries.Count -eq 0){Stop-Candidate 'manifest.files-empty'}
$actual=@(Get-ChildItem -LiteralPath $payload -Recurse -Force -File|Sort-Object FullName)
if($actual.Count -ne $entries.Count){Stop-Candidate 'manifest.incomplete'}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($entry in $entries){
    $relative=[string]$entry.path;if([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or -not $seen.Add($relative)){Stop-Candidate 'manifest.path-invalid'}
    $file=[IO.Path]::GetFullPath((Join-Path $payload $relative));if(-not $file.StartsWith($payload+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){Stop-Candidate 'manifest.path-escape'}
    if(-not(Test-Path -LiteralPath $file -PathType Leaf) -or (Hash $file) -ne $entry.sha256 -or (Get-Item $file).Length -ne $entry.length){Stop-Candidate 'manifest.hash-mismatch'}
    if($relative -match '(?i)(^|[\\/])(api|secret|credential|private[-_]?key|\.env)([.\\/]|$)' -or (Get-Content -LiteralPath $file -Raw -ErrorAction SilentlyContinue) -match '(?i)(api[_-]?key|secret|password|bearer|private[_-]?key)\s*[:=]\s*["''][^"'']+'){Stop-Candidate 'payload.credentials-forbidden'}
}
if($seen.Count -ne $actual.Count){Stop-Candidate 'manifest.incomplete'}
$probeRelative=[string]$manifest.probeScript;if(-not $seen.Contains($probeRelative) -or -not(Test-Path -LiteralPath (Join-Path $payload $probeRelative) -PathType Leaf)){Stop-Candidate 'probe.missing-or-unanchored'}
if(Test-Path -LiteralPath $destination){Stop-Candidate 'candidate.version-exists'}
$parent=Split-Path $destination -Parent;New-Item -ItemType Directory -Path $parent -Force|Out-Null
$temporary=Join-Path $parent ('.private-candidate-'+[Guid]::NewGuid().ToString('N')+'.tmp')
try{
    New-Item -ItemType Directory -Path $temporary|Out-Null;Copy-Item -Path (Join-Path $payload '*') -Destination $temporary -Recurse -Force
    foreach($entry in $entries){$file=Join-Path $temporary ([string]$entry.path);if((Hash $file) -ne $entry.sha256){Stop-Candidate 'candidate.copy-hash-mismatch'}}
    $candidateManifest=[ordered]@{schemaVersion='wpe.private-testnet-candidate/1.0';version=$Version;createdUtc=[DateTimeOffset]::UtcNow.ToString('o');localOnly=$true;commercialDistribution=$false;mainnet=$false;payloadManifestSha256=$ExpectedPayloadManifestHash.ToLowerInvariant();probeScript=$probeRelative;files=$entries}
    $candidateManifest|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $temporary 'private-candidate-manifest.json') -Encoding utf8
    Get-ChildItem -LiteralPath $temporary -Recurse -Force -File|ForEach-Object {$_.IsReadOnly=$true}
    Move-Item -LiteralPath $temporary -Destination $destination
}catch{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force};throw}
[pscustomobject]@{CandidateRoot=$destination;ManifestSha256=Hash (Join-Path $destination 'private-candidate-manifest.json');LocalOnly=$true;CommercialDistribution=$false;Mainnet=$false}

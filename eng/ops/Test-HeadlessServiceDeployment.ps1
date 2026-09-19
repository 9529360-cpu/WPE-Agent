[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$DataRoot,
    [Parameter(Mandatory)][string]$ServiceAccount,
    [Parameter(Mandatory)][string]$MaintenanceAccount,
    [Parameter(Mandatory)][string]$PackageVerificationPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}
)

$ErrorActionPreference = 'Stop'

function Stop-Preflight([string]$Code) { throw $Code }

function Resolve-FixedLocalPath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('\\')) {
        Stop-Preflight "$Label.network-path-forbidden"
    }
    if (-not [IO.Path]::IsPathRooted($Path)) {
        Stop-Preflight "$Label.not-absolute"
    }

    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $resolved = if ($full.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        $root
    } else {
        $full.TrimEnd([IO.Path]::DirectorySeparatorChar)
    }
    if ([string]::IsNullOrWhiteSpace($root) -or $root -notmatch '^[A-Za-z]:\\$') {
        Stop-Preflight "$Label.network-path-forbidden"
    }

    try { $drive = [IO.DriveInfo]::new($root) }
    catch { Stop-Preflight "$Label.drive-invalid" }
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        Stop-Preflight "$Label.network-path-forbidden"
    }

    $resolved
}

function Assert-NoReparsePath([string]$Path, [string]$Code) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $current = $root
    $relative = $full.Substring($root.Length)

    foreach ($part in $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-Preflight $Code
            }
        }
    }
}

function Is-SameOrDescendant([string]$Path, [string]$Root) {
    $pathFull = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($pathFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $pathFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Normalize-Account([string]$Value, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Value)) { Stop-Preflight "$Label.missing" }
    $trimmed = $Value.Trim()
    if ($trimmed.IndexOfAny([char[]]@('"', "'")) -ge 0) { Stop-Preflight "$Label.invalid" }
    $trimmed
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
        ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-TreeFacts([string]$Path) {
    $base = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $base + [IO.Path]::DirectorySeparatorChar
    $files = @(Get-ChildItem -LiteralPath $base -Recurse -Force -File | Sort-Object FullName)
    $lines = @($files | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        "$relative|$($_.Length)|$(Get-Sha256 $_.FullName)"
    })
    [pscustomobject]@{
        FileCount = $files.Count
        TreeSha256 = Get-TextSha256 ($lines -join [Environment]::NewLine)
    }
}

$candidate = Resolve-FixedLocalPath $CandidateRoot 'candidate.root'
$data = Resolve-FixedLocalPath $DataRoot 'data.root'
Assert-NoReparsePath $candidate 'candidate.root-reparse-forbidden'
Assert-NoReparsePath $data 'data.root-reparse-forbidden'

if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
    Stop-Preflight 'candidate.root-missing'
}
$reparseEntries = @(Get-ChildItem -LiteralPath $candidate -Recurse -Force | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
})
if ($reparseEntries.Count -gt 0) {
    Stop-Preflight 'candidate.tree-reparse-forbidden'
}
if ((Is-SameOrDescendant $data $candidate) -or (Is-SameOrDescendant $candidate $data)) {
    Stop-Preflight 'data.root-candidate-overlap'
}

$verificationPath = Resolve-FixedLocalPath $PackageVerificationPath 'package.verification'
Assert-NoReparsePath $verificationPath 'package.verification-reparse-forbidden'
if (-not (Test-Path -LiteralPath $verificationPath -PathType Leaf)) {
    Stop-Preflight 'package.verification-missing'
}
if (Is-SameOrDescendant $verificationPath $candidate) {
    Stop-Preflight 'package.verification-inside-candidate-forbidden'
}
$verificationHash = Get-Sha256 $verificationPath
if ($verificationHash -ne $ExpectedPackageVerificationHash.ToLowerInvariant()) {
    Stop-Preflight 'package.verification-hash-mismatch'
}
try {
    $verification = Get-Content -Raw -LiteralPath $verificationPath | ConvertFrom-Json
} catch {
    Stop-Preflight 'package.verification-malformed'
}
if ($verification.schemaVersion -ne 'wpe.beta-package-verification.v1' -or
    $verification.status -ne 'passed' -or
    [string]$verification.packageTreeSha256 -notmatch '^[a-f0-9]{64}
$headlessExe = Join-Path $candidate 'headless\WPE-Headless.exe'
$maintenanceExe = Join-Path $candidate 'maintenance\WPE.Maintenance.exe'
foreach ($required in @(
    @{ Path=$headlessExe; Code='candidate.headless-exe-missing' },
    @{ Path=$maintenanceExe; Code='candidate.maintenance-exe-missing' }
)) {
    if (-not (Test-Path -LiteralPath $required.Path -PathType Leaf)) {
        Stop-Preflight $required.Code
    }
    Assert-NoReparsePath $required.Path 'candidate.executable-reparse-forbidden'
}

$serviceIdentity = Normalize-Account $ServiceAccount 'service.account'
$maintenanceIdentity = Normalize-Account $MaintenanceAccount 'maintenance.account'
if (-not $serviceIdentity.Equals($maintenanceIdentity, [StringComparison]::OrdinalIgnoreCase)) {
    Stop-Preflight 'account.dpapi-identity-mismatch'
}

$quotedExe = '"' + $headlessExe + '"'
$quotedRoot = '"' + $data + '"'
$imagePath = "$quotedExe --data-root $quotedRoot"

[pscustomobject][ordered]@{
    schemaVersion = 'wpe.headless-service-deployment-preflight/1.1'
    valid = $true
    serviceName = 'WPE Agent Headless'
    candidateRoot = $candidate
    dataRoot = $data
    serviceAccount = $serviceIdentity
    maintenanceAccount = $maintenanceIdentity
    headlessExecutable = $headlessExe
    maintenanceExecutable = $maintenanceExe
    packageVerificationSha256 = $verificationHash
    candidateTreeSha256 = $candidateTree.TreeSha256
    candidateFileCount = $candidateTree.FileCount
    imagePath = $imagePath
    scmMutationPerformed = $false
    credentialsAccepted = $false
    mainnetEnabled = $false
}
)][string]$ExpectedPackageVerificationHash
)

$ErrorActionPreference = 'Stop'

function Stop-Preflight([string]$Code) { throw $Code }

function Resolve-FixedLocalPath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('\\')) {
        Stop-Preflight "$Label.network-path-forbidden"
    }
    if (-not [IO.Path]::IsPathRooted($Path)) {
        Stop-Preflight "$Label.not-absolute"
    }

    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $resolved = if ($full.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        $root
    } else {
        $full.TrimEnd([IO.Path]::DirectorySeparatorChar)
    }
    if ([string]::IsNullOrWhiteSpace($root) -or $root -notmatch '^[A-Za-z]:\\$') {
        Stop-Preflight "$Label.network-path-forbidden"
    }

    try { $drive = [IO.DriveInfo]::new($root) }
    catch { Stop-Preflight "$Label.drive-invalid" }
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        Stop-Preflight "$Label.network-path-forbidden"
    }

    $resolved
}

function Assert-NoReparsePath([string]$Path, [string]$Code) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $current = $root
    $relative = $full.Substring($root.Length)

    foreach ($part in $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-Preflight $Code
            }
        }
    }
}

function Is-SameOrDescendant([string]$Path, [string]$Root) {
    $pathFull = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($pathFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $pathFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Normalize-Account([string]$Value, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Value)) { Stop-Preflight "$Label.missing" }
    $trimmed = $Value.Trim()
    if ($trimmed.IndexOfAny([char[]]@('"', "'")) -ge 0) { Stop-Preflight "$Label.invalid" }
    $trimmed
}

$candidate = Resolve-FixedLocalPath $CandidateRoot 'candidate.root'
$data = Resolve-FixedLocalPath $DataRoot 'data.root'
Assert-NoReparsePath $candidate 'candidate.root-reparse-forbidden'
Assert-NoReparsePath $data 'data.root-reparse-forbidden'

if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
    Stop-Preflight 'candidate.root-missing'
}
if ((Is-SameOrDescendant $data $candidate) -or (Is-SameOrDescendant $candidate $data)) {
    Stop-Preflight 'data.root-candidate-overlap'
}

$headlessExe = Join-Path $candidate 'headless\WPE-Headless.exe'
$maintenanceExe = Join-Path $candidate 'maintenance\WPE.Maintenance.exe'
foreach ($required in @(
    @{ Path=$headlessExe; Code='candidate.headless-exe-missing' },
    @{ Path=$maintenanceExe; Code='candidate.maintenance-exe-missing' }
)) {
    if (-not (Test-Path -LiteralPath $required.Path -PathType Leaf)) {
        Stop-Preflight $required.Code
    }
    Assert-NoReparsePath $required.Path 'candidate.executable-reparse-forbidden'
}

$serviceIdentity = Normalize-Account $ServiceAccount 'service.account'
$maintenanceIdentity = Normalize-Account $MaintenanceAccount 'maintenance.account'
if (-not $serviceIdentity.Equals($maintenanceIdentity, [StringComparison]::OrdinalIgnoreCase)) {
    Stop-Preflight 'account.dpapi-identity-mismatch'
}

$quotedExe = '"' + $headlessExe + '"'
$quotedRoot = '"' + $data + '"'
$imagePath = "$quotedExe --data-root $quotedRoot"

[pscustomobject][ordered]@{
    schemaVersion = 'wpe.headless-service-deployment-preflight/1.0'
    valid = $true
    serviceName = 'WPE Agent Headless'
    candidateRoot = $candidate
    dataRoot = $data
    serviceAccount = $serviceIdentity
    maintenanceAccount = $maintenanceIdentity
    headlessExecutable = $headlessExe
    maintenanceExecutable = $maintenanceExe
    imagePath = $imagePath
    scmMutationPerformed = $false
    credentialsAccepted = $false
    mainnetEnabled = $false
}
) {
    Stop-Preflight 'package.verification-not-passed'
}
try { $verifiedFileCount = [int]$verification.packageFileCount }
catch { Stop-Preflight 'package.verification-tree-binding-invalid' }
if ($verifiedFileCount -le 0) {
    Stop-Preflight 'package.verification-tree-binding-invalid'
}
$candidateTree = Get-TreeFacts $candidate
if ($candidateTree.FileCount -ne $verifiedFileCount -or
    $candidateTree.TreeSha256 -ne [string]$verification.packageTreeSha256) {
    Stop-Preflight 'candidate.package-tree-mismatch'
}

$headlessExe = Join-Path $candidate 'headless\WPE-Headless.exe'
$maintenanceExe = Join-Path $candidate 'maintenance\WPE.Maintenance.exe'
foreach ($required in @(
    @{ Path=$headlessExe; Code='candidate.headless-exe-missing' },
    @{ Path=$maintenanceExe; Code='candidate.maintenance-exe-missing' }
)) {
    if (-not (Test-Path -LiteralPath $required.Path -PathType Leaf)) {
        Stop-Preflight $required.Code
    }
    Assert-NoReparsePath $required.Path 'candidate.executable-reparse-forbidden'
}

$serviceIdentity = Normalize-Account $ServiceAccount 'service.account'
$maintenanceIdentity = Normalize-Account $MaintenanceAccount 'maintenance.account'
if (-not $serviceIdentity.Equals($maintenanceIdentity, [StringComparison]::OrdinalIgnoreCase)) {
    Stop-Preflight 'account.dpapi-identity-mismatch'
}

$quotedExe = '"' + $headlessExe + '"'
$quotedRoot = '"' + $data + '"'
$imagePath = "$quotedExe --data-root $quotedRoot"

[pscustomobject][ordered]@{
    schemaVersion = 'wpe.headless-service-deployment-preflight/1.0'
    valid = $true
    serviceName = 'WPE Agent Headless'
    candidateRoot = $candidate
    dataRoot = $data
    serviceAccount = $serviceIdentity
    maintenanceAccount = $maintenanceIdentity
    headlessExecutable = $headlessExe
    maintenanceExecutable = $maintenanceExe
    imagePath = $imagePath
    scmMutationPerformed = $false
    credentialsAccepted = $false
    mainnetEnabled = $false
}
)][string]$ExpectedPackageVerificationHash
)

$ErrorActionPreference = 'Stop'

function Stop-Preflight([string]$Code) { throw $Code }

function Resolve-FixedLocalPath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('\\')) {
        Stop-Preflight "$Label.network-path-forbidden"
    }
    if (-not [IO.Path]::IsPathRooted($Path)) {
        Stop-Preflight "$Label.not-absolute"
    }

    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $resolved = if ($full.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        $root
    } else {
        $full.TrimEnd([IO.Path]::DirectorySeparatorChar)
    }
    if ([string]::IsNullOrWhiteSpace($root) -or $root -notmatch '^[A-Za-z]:\\$') {
        Stop-Preflight "$Label.network-path-forbidden"
    }

    try { $drive = [IO.DriveInfo]::new($root) }
    catch { Stop-Preflight "$Label.drive-invalid" }
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        Stop-Preflight "$Label.network-path-forbidden"
    }

    $resolved
}

function Assert-NoReparsePath([string]$Path, [string]$Code) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $current = $root
    $relative = $full.Substring($root.Length)

    foreach ($part in $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-Preflight $Code
            }
        }
    }
}

function Is-SameOrDescendant([string]$Path, [string]$Root) {
    $pathFull = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($pathFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $pathFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Normalize-Account([string]$Value, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Value)) { Stop-Preflight "$Label.missing" }
    $trimmed = $Value.Trim()
    if ($trimmed.IndexOfAny([char[]]@('"', "'")) -ge 0) { Stop-Preflight "$Label.invalid" }
    $trimmed
}

$candidate = Resolve-FixedLocalPath $CandidateRoot 'candidate.root'
$data = Resolve-FixedLocalPath $DataRoot 'data.root'
Assert-NoReparsePath $candidate 'candidate.root-reparse-forbidden'
Assert-NoReparsePath $data 'data.root-reparse-forbidden'

if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
    Stop-Preflight 'candidate.root-missing'
}
if ((Is-SameOrDescendant $data $candidate) -or (Is-SameOrDescendant $candidate $data)) {
    Stop-Preflight 'data.root-candidate-overlap'
}

$headlessExe = Join-Path $candidate 'headless\WPE-Headless.exe'
$maintenanceExe = Join-Path $candidate 'maintenance\WPE.Maintenance.exe'
foreach ($required in @(
    @{ Path=$headlessExe; Code='candidate.headless-exe-missing' },
    @{ Path=$maintenanceExe; Code='candidate.maintenance-exe-missing' }
)) {
    if (-not (Test-Path -LiteralPath $required.Path -PathType Leaf)) {
        Stop-Preflight $required.Code
    }
    Assert-NoReparsePath $required.Path 'candidate.executable-reparse-forbidden'
}

$serviceIdentity = Normalize-Account $ServiceAccount 'service.account'
$maintenanceIdentity = Normalize-Account $MaintenanceAccount 'maintenance.account'
if (-not $serviceIdentity.Equals($maintenanceIdentity, [StringComparison]::OrdinalIgnoreCase)) {
    Stop-Preflight 'account.dpapi-identity-mismatch'
}

$quotedExe = '"' + $headlessExe + '"'
$quotedRoot = '"' + $data + '"'
$imagePath = "$quotedExe --data-root $quotedRoot"

[pscustomobject][ordered]@{
    schemaVersion = 'wpe.headless-service-deployment-preflight/1.0'
    valid = $true
    serviceName = 'WPE Agent Headless'
    candidateRoot = $candidate
    dataRoot = $data
    serviceAccount = $serviceIdentity
    maintenanceAccount = $maintenanceIdentity
    headlessExecutable = $headlessExe
    maintenanceExecutable = $maintenanceExe
    imagePath = $imagePath
    scmMutationPerformed = $false
    credentialsAccepted = $false
    mainnetEnabled = $false
}

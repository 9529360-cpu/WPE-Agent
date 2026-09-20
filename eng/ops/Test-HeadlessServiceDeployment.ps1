[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$DataRoot,
    [Parameter(Mandatory)][string]$ServiceAccount,
    [Parameter(Mandatory)][string]$MaintenanceAccount
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

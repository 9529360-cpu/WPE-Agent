param(
    [string]$ResultPath = "artifacts/beta-packages/package-result.json",
    [string]$VerificationDirectory = "artifacts/beta-packages/verification"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Resolve-InRoot([string]$Path) {
    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) { [System.IO.Path]::GetFullPath($Path) } else { [System.IO.Path]::GetFullPath((Join-Path $root $Path)) }
    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $root) { throw "Verification paths must be inside the project root." }
    return $resolved
}

function Get-TextSha256([string]$Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-PackageTreeFacts([string]$Path) {
    $prefix = $Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File | Sort-Object FullName)
    $lines = @($files | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relative|$($_.Length)|$hash"
    })
    [pscustomobject]@{
        FileCount = $files.Count
        TreeSha256 = Get-TextSha256 ($lines -join [Environment]::NewLine)
    }
}

$resultFile = Resolve-InRoot $ResultPath
$verify = Resolve-InRoot $VerificationDirectory
$result = Get-Content -Raw -LiteralPath $resultFile | ConvertFrom-Json
$zip = Resolve-InRoot ([string]$result.package)
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$sumsPath = Join-Path (Split-Path -Parent $resultFile) "SHA256SUMS"
$sumLine = (Get-Content -Raw -LiteralPath $sumsPath).Trim()
if ($zipHash -ne $result.sha256 -or -not $sumLine.StartsWith($zipHash, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Outer ZIP SHA-256 mismatch." }

if (Test-Path -LiteralPath $verify) { Remove-Item -LiteralPath $verify -Recurse -Force }
Expand-Archive -LiteralPath $zip -DestinationPath $verify
$packageDirectories = @(Get-ChildItem -LiteralPath $verify -Directory -Force)
if ($packageDirectories.Count -ne 1) { throw "Expected one package root in the archive." }
$packageRoot = $packageDirectories[0].FullName
$payloadRoot = Join-Path $packageRoot "app"

$metadata = Get-Content -Raw -LiteralPath (Join-Path $packageRoot "RELEASE-METADATA.json") | ConvertFrom-Json
$readiness = Get-Content -Raw -LiteralPath (Join-Path $packageRoot "RELEASE-READINESS.json") | ConvertFrom-Json
$sbom = Get-Content -Raw -LiteralPath (Join-Path $packageRoot "sbom.cdx.json") | ConvertFrom-Json
$licenseReportPath = Join-Path $packageRoot "license/license-report.json"
$policyDecisionPath = Join-Path $packageRoot "license/policy-decision.json"
if (-not (Test-Path -LiteralPath $licenseReportPath -PathType Leaf) -or -not (Test-Path -LiteralPath $policyDecisionPath -PathType Leaf)) {
    throw "Embedded license gate reports are missing."
}
$licenseReport = Get-Content -Raw -LiteralPath $licenseReportPath | ConvertFrom-Json
$policyDecision = Get-Content -Raw -LiteralPath $policyDecisionPath | ConvertFrom-Json
$noticePath = Join-Path $packageRoot ([string]$metadata.contents.thirdPartyNoticePath).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) { throw "Distribution THIRD-PARTY-NOTICES.txt is missing." }
$noticeHash = (Get-FileHash -LiteralPath $noticePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($noticeHash -ne [string]$metadata.contents.thirdPartyNoticeSha256) { throw "Distribution third-party notice SHA-256 mismatch." }
$noticeText = Get-Content -Raw -LiteralPath $noticePath
foreach ($requiredNotice in @("Microsoft.Web.WebView2 1.0.2903.40", "OpenTK 4.3.0", "OpenTK.redist.glfw 3.3.0-pre20200830200122")) {
    if ($noticeText.IndexOf($requiredNotice, [System.StringComparison]::Ordinal) -lt 0) { throw "Required runtime notice is missing: $requiredNotice" }
}
[object[]]$manifest = Get-Content -Raw -LiteralPath (Join-Path $packageRoot "FILE-MANIFEST.json") | ConvertFrom-Json
[object[]]$sbomComponents = $sbom.components

$manifestFailures = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $manifest) {
    $path = Join-Path $payloadRoot ([string]$entry.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        $manifestFailures.Add([string]$entry.path)
    }
}
if ($manifestFailures.Count -gt 0) { throw "Payload manifest verification failed for $($manifestFailures.Count) file(s)." }

$actualPayloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force -File)
if ($actualPayloadFiles.Count -ne $manifest.Count) { throw "Payload file count does not match the manifest." }
$forbidden = @($actualPayloadFiles | Where-Object {
    $_.Extension -match '^(?i:\.pdb|\.cs|\.csproj|\.sln|\.ps1|\.db|\.db-wal|\.db-shm|\.sqlite|\.sqlite3|\.log)$' -or
    $_.Name -match '^(?i:agent-settings.*\.json|appsettings.*\.json|order-state.*\.json|api\.txt)$'
})
if ($forbidden.Count -gt 0) { throw "Forbidden source/runtime artifacts were found in the archive." }

$executables = @(Get-ChildItem -LiteralPath $payloadRoot -File -Filter "*.exe")
if ($executables.Count -ne 1) { throw "Expected one application executable in the archive." }
$signature = Get-AuthenticodeSignature -LiteralPath $executables[0].FullName
if ($metadata.signing.status -eq "unsigned" -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) { throw "Unsigned package metadata does not match the executable." }
if ($metadata.signing.status -eq "valid" -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) { throw "Signed package metadata does not match the executable." }
if ($readiness.status -ne "passed") { throw "Embedded release-readiness report did not pass." }
if ($metadata.productVersion -ne $readiness.productVersion) { throw "Package and release-readiness product versions do not match." }
$binaryFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executables[0].FullName).FileVersion
$parsedBinaryVersion = [Version]$binaryFileVersion
$binaryProductVersion = "$($parsedBinaryVersion.Major).$($parsedBinaryVersion.Minor).$($parsedBinaryVersion.Build)"
if ($metadata.productVersion -ne $binaryProductVersion) { throw "Package metadata and executable product versions do not match." }
if ($sbom.bomFormat -ne "CycloneDX" -or $sbom.specVersion -ne "1.5" -or $sbomComponents.Count -eq 0) { throw "CycloneDX SBOM is missing or invalid." }
if ($licenseReport.releaseIntent -ne "evaluation" -or -not $licenseReport.gatePassed -or $licenseReport.summary.components -ne $sbomComponents.Count) {
    throw "Embedded evaluation license report does not match the SBOM."
}
if ($policyDecision.status -ne $licenseReport.status -or -not $policyDecision.gatePassed) { throw "Embedded license policy decision is inconsistent." }
if ($licenseReport.summary.deny -gt 0) { throw "Denied licenses cannot enter an evaluation package." }
if (@($sbomComponents | Where-Object { $_.name -eq "@vercel/analytics" }).Count -gt 0) { throw "Removed Vercel Analytics dependency remains in the SBOM." }

& (Join-Path $root "publish.ps1") -Configuration Release -Runtime ([string]$metadata.runtime) -Output $packageRoot -ValidateOnly
if ($LASTEXITCODE -ne 0) { throw "Expanded package secret/source/preview scan failed." }

$unknownLicenses = [int]$licenseReport.summary.noAssertion
$packageTreeFacts = Get-PackageTreeFacts $packageRoot
$verificationResult = [ordered]@{
    schemaVersion = "wpe.beta-package-verification.v1"
    status = "passed"
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    package = $zip
    sha256 = $zipHash
    zipBytes = [long](Get-Item -LiteralPath $zip).Length
    packageTreeSha256 = $packageTreeFacts.TreeSha256
    packageFileCount = $packageTreeFacts.FileCount
    payloadFiles = $manifest.Count
    sbomComponents = $sbomComponents.Count
    unknownLicenses = $unknownLicenses
    licenseReview = [int]$licenseReport.summary.review
    licenseDenied = [int]$licenseReport.summary.deny
    licenseUnknownCustom = [int]$licenseReport.summary.unknownCustom
    licenseGateStatus = [string]$licenseReport.status
    thirdPartyNoticeSha256 = $noticeHash
    signingStatus = [string]$signature.Status
    distributable = [bool]$metadata.signing.distributable
    releaseReadiness = [string]$readiness.status
    manifestFailures = $manifestFailures.Count
    forbiddenFiles = $forbidden.Count
}
$verificationResult | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $resultFile) "verification-result.json") -Encoding UTF8
$verificationResult | ConvertTo-Json -Depth 5

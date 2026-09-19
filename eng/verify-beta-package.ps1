param(
    [string]$ResultPath = "artifacts/beta-packages/package-result.json",
    [string]$VerificationDirectory = "artifacts/beta-packages/verification",
    [string]$ExpectedSignerSubject,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$ExpectedSignerThumbprint
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


function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-ZipEntriesSafe([string]$ZipPath, [string]$DestinationRoot) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $destination = [System.IO.Path]::GetFullPath($DestinationRoot)
    $prefix = $destination.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $targets = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            $entryName = ([string]$entry.FullName).Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($entryName) -or
                $entryName.StartsWith('/', [System.StringComparison]::Ordinal) -or
                $entryName.Contains(':')) {
                throw "Archive entry path is invalid."
            }

            $relative = $entryName.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
            if ([System.IO.Path]::IsPathRooted($relative)) {
                throw "Archive entry path is rooted."
            }

            $target = [System.IO.Path]::GetFullPath((Join-Path $destination $relative))
            if (-not $target.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Archive entry escapes verification root."
            }
            if (-not $targets.Add($target)) {
                throw "Archive contains duplicate normalized entry paths."
            }
        }
    } finally {
        $archive.Dispose()
    }
}

function Resolve-InPackageRoot([string]$PackageRoot, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw "Package metadata path is missing."
    }
    $relative = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    if ([System.IO.Path]::IsPathRooted($relative)) {
        throw "Package metadata path must be relative."
    }
    $package = [System.IO.Path]::GetFullPath($PackageRoot)
    $prefix = $package.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolved = [System.IO.Path]::GetFullPath((Join-Path $package $relative))
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Package metadata path escapes package root."
    }
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

function Get-ArtifactTreeFacts([string]$Path) {
    $prefix = $Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File | Sort-Object FullName)
    $lines = @($files | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        $hash = Get-Sha256 $_.FullName
        "$relative|$($_.Length)|$hash"
    })
    [pscustomobject]@{
        FileCount = $files.Count
        TotalBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
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
$expectedSumLine = "$zipHash  $([System.IO.Path]::GetFileName($zip))"
if ($zipHash -ne [string]$result.sha256 -or
    -not $sumLine.Equals($expectedSumLine, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Outer ZIP SHA-256 mismatch."
}

if (Test-Path -LiteralPath $verify) { Remove-Item -LiteralPath $verify -Recurse -Force }
Assert-ZipEntriesSafe $zip $verify
Expand-Archive -LiteralPath $zip -DestinationPath $verify
$topLevelItems = @(Get-ChildItem -LiteralPath $verify -Force)
if ($topLevelItems.Count -ne 1 -or -not $topLevelItems[0].PSIsContainer) {
    throw "Archive must contain exactly one top-level package directory."
}
$packageRoot = $topLevelItems[0].FullName
$payloadRoot = Join-Path $packageRoot "app"
$headlessRoot = Join-Path $packageRoot "headless"
$maintenanceRoot = Join-Path $packageRoot "maintenance"
foreach ($runtimeRoot in @($payloadRoot, $headlessRoot, $maintenanceRoot)) {
    if (-not (Test-Path -LiteralPath $runtimeRoot -PathType Container)) { throw "Runtime bundle directory is missing: $runtimeRoot" }
}

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
$noticePath = Resolve-InPackageRoot $packageRoot ([string]$metadata.contents.thirdPartyNoticePath)
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
$packagePrefix = $packageRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$manifestPaths = @($manifest | ForEach-Object { [string]$_.path })
if (@($manifestPaths | Select-Object -Unique).Count -ne $manifestPaths.Count) { throw "Payload manifest contains duplicate paths." }
if (@($manifestPaths | Where-Object { $_ -notmatch '^(app|headless|maintenance)/' }).Count -gt 0) { throw "Payload manifest contains a path outside the runtime bundle roots." }
foreach ($entry in $manifest) {
    $relative = ([string]$entry.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $path = [System.IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    if (-not $path.StartsWith($packagePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -ne [long]$entry.length -or
        (Get-Sha256 $path) -ne [string]$entry.sha256) {
        $manifestFailures.Add([string]$entry.path)
    }
}
if ($manifestFailures.Count -gt 0) { throw "Payload manifest verification failed for $($manifestFailures.Count) file(s)." }

$payloadSumsPath = Join-Path $packageRoot "PAYLOAD-SHA256SUMS"
if (-not (Test-Path -LiteralPath $payloadSumsPath -PathType Leaf)) {
    throw "PAYLOAD-SHA256SUMS is missing."
}
$actualPayloadSums = @(Get-Content -LiteralPath $payloadSumsPath)
$expectedPayloadSums = @($manifest | ForEach-Object {
    "$([string]$_.sha256)  $([string]$_.path)"
})
if ((Compare-Object $expectedPayloadSums $actualPayloadSums -SyncWindow 0).Count -ne 0) {
    throw "PAYLOAD-SHA256SUMS does not match FILE-MANIFEST.json."
}

$actualPayloadFiles = @(
    @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force -File) +
    @(Get-ChildItem -LiteralPath $headlessRoot -Recurse -Force -File) +
    @(Get-ChildItem -LiteralPath $maintenanceRoot -Recurse -Force -File)
)
if ($actualPayloadFiles.Count -ne $manifest.Count) { throw "Runtime bundle file count does not match the manifest." }
$actualRelativePaths = @($actualPayloadFiles | ForEach-Object {
    $_.FullName.Substring($packagePrefix.Length).Replace('\', '/')
} | Sort-Object)
$declaredRelativePaths = @($manifestPaths | Sort-Object)
if ((Compare-Object $actualRelativePaths $declaredRelativePaths -SyncWindow 0).Count -ne 0) {
    throw "Payload manifest does not exactly cover the runtime bundle files."
}
$forbiddenExtensions = @(".pdb", ".cs", ".csproj", ".sln", ".ps1", ".db", ".sqlite", ".sqlite3", ".log", ".pem", ".key", ".p12", ".pfx", ".env")
$forbiddenSuffixes = @(".db-wal", ".db-shm", ".sqlite-wal", ".sqlite-shm", ".sqlite3-wal", ".sqlite3-shm")
$forbidden = @($actualPayloadFiles | Where-Object {
    $name = $_.Name.ToLowerInvariant()
    $extension = $_.Extension.ToLowerInvariant()
    $knownState = $name -match '^(agent-settings|appsettings|order-state|local-accounts|local-session|device-license|llm-calls|llm-cache)'
    $secretData = ($name.Contains("secrets") -or $name.Contains("credentials")) -and
        @(".json", ".dat", ".txt", ".xml", ".yaml", ".yml") -contains $extension
    $knownState -or $secretData -or $forbiddenExtensions -contains $extension -or
        ($forbiddenSuffixes | Where-Object { $name.EndsWith($_) })
})
if ($forbidden.Count -gt 0) { throw "Forbidden source/runtime artifacts were found in the archive." }

if ($readiness.schemaVersion -ne "wpe.release-readiness.v1" -or $readiness.status -ne "passed") { throw "Embedded release-readiness report did not pass." }
if ($metadata.productVersion -ne $readiness.productVersion -or
    [string]$metadata.source.commit -ne [string]$readiness.source.commit) {
    throw "Package and release-readiness source identity do not match."
}
if (-not $readiness.artifacts -or -not $readiness.artifacts.desktop -or -not $readiness.artifacts.headless -or -not $readiness.artifacts.maintenance) {
    throw "Embedded release-readiness runtime bundle facts are incomplete."
}

$runtimeArtifacts = @(
    [pscustomobject]@{ Label = "desktop"; Root = $payloadRoot; Executable = "WPE-Agent.exe"; Readiness = $readiness.artifacts.desktop },
    [pscustomobject]@{ Label = "headless"; Root = $headlessRoot; Executable = "WPE-Headless.exe"; Readiness = $readiness.artifacts.headless },
    [pscustomobject]@{ Label = "maintenance"; Root = $maintenanceRoot; Executable = "WPE.Maintenance.exe"; Readiness = $readiness.artifacts.maintenance }
)
$artifactStates = [System.Collections.Generic.List[object]]::new()
foreach ($artifact in $runtimeArtifacts) {
    $executables = @(Get-ChildItem -LiteralPath $artifact.Root -File -Filter "*.exe")
    if ($executables.Count -ne 1 -or $executables[0].Name -ne $artifact.Executable) {
        throw "Runtime package artifact has the wrong executable identity: $($artifact.Label)"
    }
    $binaryFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executables[0].FullName).FileVersion
    $parsedBinaryVersion = [Version]$binaryFileVersion
    $binaryProductVersion = "$($parsedBinaryVersion.Major).$($parsedBinaryVersion.Minor).$($parsedBinaryVersion.Build)"
    if ($metadata.productVersion -ne $binaryProductVersion) { throw "Runtime package binary version mismatch: $($artifact.Label)" }
    $facts = Get-ArtifactTreeFacts $artifact.Root
    $signature = Get-AuthenticodeSignature -LiteralPath $executables[0].FullName
    if ($signature.Status -notin @([System.Management.Automation.SignatureStatus]::Valid, [System.Management.Automation.SignatureStatus]::NotSigned)) {
        throw "Runtime package executable has invalid Authenticode state: $($artifact.Label):$($signature.Status)"
    }
    if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and $null -eq $signature.TimeStamperCertificate) {
        throw "Runtime package executable timestamp is missing: $($artifact.Label)"
    }
    $artifactStates.Add([pscustomobject]@{ Label=$artifact.Label; Root=$artifact.Root; Executable=$executables[0]; Readiness=$artifact.Readiness; Facts=$facts; Signature=$signature })
}
$allSigned = @($artifactStates | Where-Object { $_.Signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid }).Count -eq 3
$allUnsigned = @($artifactStates | Where-Object { $_.Signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned }).Count -eq 3
if (-not ($allSigned -or $allUnsigned)) { throw "Runtime package signature states are mixed." }

$embeddedReadinessPath = Join-Path $packageRoot "RELEASE-READINESS.json"
$embeddedReadinessHash = Get-Sha256 $embeddedReadinessPath
if ([string]$metadata.signing.readinessReportSha256 -ne $embeddedReadinessHash) { throw "Package metadata readiness hash mismatch." }

if ($allSigned) {
    if ([string]::IsNullOrWhiteSpace($ExpectedSignerSubject) -or [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
        throw "Signed runtime bundle verification requires the approved signer subject and thumbprint."
    }
    if ([string]$metadata.signing.status -ne "valid" -or -not [bool]$metadata.signing.distributable) { throw "Signed runtime bundle metadata is not distributable." }
    if ([bool]$metadata.source.dirty -or [bool]$readiness.source.dirty) { throw "Signed runtime bundle cannot come from dirty source." }
    if ([string]$metadata.signing.transitionResultPath -ne "SIGNING-RESULT.json") { throw "Signed runtime bundle transition path is invalid." }
    $signingPath = Join-Path $packageRoot "SIGNING-RESULT.json"
    if (-not (Test-Path -LiteralPath $signingPath -PathType Leaf)) { throw "Signed runtime bundle is missing SIGNING-RESULT.json." }
    if ((Get-Sha256 $signingPath) -ne [string]$metadata.signing.transitionResultSha256) { throw "Signing transition result hash mismatch." }
    $signingResult = Get-Content -Raw -LiteralPath $signingPath | ConvertFrom-Json
    if ($signingResult.schemaVersion -ne "wpe.runtime-bundle-signing/1.0" -or
        [string]$signingResult.readinessReportSha256 -ne $embeddedReadinessHash -or
        [string]$signingResult.sourceCommit -ne [string]$metadata.source.commit -or
        [string]$signingResult.productVersion -ne [string]$metadata.productVersion -or
        [string]$signingResult.publisherSubject -ne [string]$metadata.signing.subject -or
        ([string]$signingResult.certificateThumbprint).ToUpperInvariant() -ne ([string]$metadata.signing.thumbprint).ToUpperInvariant() -or
        [string]$metadata.signing.subject -ne $ExpectedSignerSubject -or
        ([string]$metadata.signing.thumbprint).ToUpperInvariant() -ne $ExpectedSignerThumbprint.ToUpperInvariant()) {
        throw "Signing transition result is not bound to the package."
    }
    if (@($signingResult.artifacts).Count -ne 3) { throw "Signing transition result must contain three artifacts." }
    foreach ($state in $artifactStates) {
        $entry = @($signingResult.artifacts | Where-Object { $_.label -eq $state.Label })
        if ($entry.Count -ne 1) { throw "Signing transition artifact identity is invalid: $($state.Label)" }
        $entry = $entry[0]
        if ([string]$entry.inputTreeSha256 -ne [string]$state.Readiness.treeSha256 -or
            [string]$entry.outputTreeSha256 -ne $state.Facts.TreeSha256 -or
            [int]$entry.outputFileCount -ne $state.Facts.FileCount -or
            [string]$entry.executableSha256 -ne (Get-Sha256 $state.Executable.FullName) -or
            [string]$entry.executable -ne $state.Executable.Name -or
            [string]$entry.signatureStatus -ne "Valid") {
            throw "Signing transition artifact facts do not match package bytes: $($state.Label)"
        }
        if ($state.Signature.SignerCertificate.Subject -ne [string]$metadata.signing.subject -or
            $state.Signature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne ([string]$metadata.signing.thumbprint).ToUpperInvariant()) {
            throw "Runtime package signer identity mismatch: $($state.Label)"
        }
    }
} else {
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerSubject) -or -not [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
        throw "Unsigned runtime bundle verification must not declare an approved signer."
    }
    if ([string]$metadata.signing.status -ne "unsigned" -or [bool]$metadata.signing.distributable) { throw "Unsigned runtime bundle metadata is invalid." }
    if (Test-Path -LiteralPath (Join-Path $packageRoot "SIGNING-RESULT.json")) { throw "Unsigned runtime bundle must not contain a signing transition result." }
    foreach ($state in $artifactStates) {
        if ($state.Facts.TreeSha256 -ne [string]$state.Readiness.treeSha256 -or
            $state.Facts.FileCount -ne [int]$state.Readiness.fileCount) {
            throw "Unsigned runtime package bytes do not match release readiness: $($state.Label)"
        }
    }
}

if (@($metadata.contents.runtimeArtifacts).Count -ne 3) { throw "Package runtime artifact metadata is incomplete." }
foreach ($state in $artifactStates) {
    $entry = @($metadata.contents.runtimeArtifacts | Where-Object { $_.label -eq $state.Label })
    if ($entry.Count -ne 1 -or
        [string]$entry[0].treeSha256 -ne $state.Facts.TreeSha256 -or
        [int]$entry[0].fileCount -ne $state.Facts.FileCount -or
        [string]$entry[0].executableSha256 -ne (Get-Sha256 $state.Executable.FullName)) {
        throw "Package runtime artifact metadata mismatch: $($state.Label)"
    }
}
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
$verificationResult = [ordered]@{
    schemaVersion = "wpe.beta-package-verification.v1"
    status = "passed"
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    package = $zip
    sha256 = $zipHash
    zipBytes = [long](Get-Item -LiteralPath $zip).Length
    payloadFiles = $manifest.Count
    sbomComponents = $sbomComponents.Count
    unknownLicenses = $unknownLicenses
    licenseReview = [int]$licenseReport.summary.review
    licenseDenied = [int]$licenseReport.summary.deny
    licenseUnknownCustom = [int]$licenseReport.summary.unknownCustom
    licenseGateStatus = [string]$licenseReport.status
    thirdPartyNoticeSha256 = $noticeHash
    signingStatus = if ($allSigned) { "Valid" } else { "NotSigned" }
    bundleSigningStatus = [string]$metadata.signing.status
    runtimeArtifacts = $artifactStates.Count
    distributable = [bool]$metadata.signing.distributable
    releaseReadiness = [string]$readiness.status
    manifestFailures = $manifestFailures.Count
    forbiddenFiles = $forbidden.Count
}
$verificationResult | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $resultFile) "verification-result.json") -Encoding UTF8
$verificationResult | ConvertTo-Json -Depth 5

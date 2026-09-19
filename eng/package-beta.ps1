param(
    [string]$PublishPath = "artifacts/release-readiness/publish",
    [string]$HeadlessPublishPath = "artifacts/release-readiness/headless",
    [string]$MaintenancePublishPath = "artifacts/release-readiness/maintenance",
    [string]$SigningResultPath,
    [string]$OutputDirectory = "artifacts/beta-packages",
    [ValidatePattern('^[A-Za-z0-9.-]+$')]
    [string]$Channel = "beta",
    [ValidatePattern('^win-(x64|arm64)$')]
    [string]$Runtime = "win-x64",
    [string]$PackageVersion,
    [string]$LicenseEvidencePath = "Docs/research/sbom-noassertion-license-evidence-draft.json"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Resolve-InRoot([string]$Path) {
    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $root $Path))
    }
    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $root) {
        throw "Beta package paths must be inside the project root."
    }
    return $resolved
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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

function Assert-CleanRuntimeArtifact([string]$Path, [string]$Label) {
    $forbiddenExtensions = @(".db", ".sqlite", ".sqlite3", ".pem", ".key", ".p12", ".pfx", ".env", ".pdb", ".cs", ".csproj", ".sln", ".ps1", ".log")
    $forbiddenSuffixes = @(".db-wal", ".db-shm", ".sqlite-wal", ".sqlite-shm", ".sqlite3-wal", ".sqlite3-shm")
    $forbidden = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File | Where-Object {
        $name = $_.Name.ToLowerInvariant()
        $extension = $_.Extension.ToLowerInvariant()
        $knownState = $name -match '^(agent-settings|appsettings|order-state|local-accounts|local-session|device-license|llm-calls|llm-cache)'
        $secretData = ($name.Contains("secrets") -or $name.Contains("credentials")) -and
            @(".json", ".dat", ".txt", ".xml", ".yaml", ".yml") -contains $extension
        $knownState -or $secretData -or
        $forbiddenExtensions -contains $extension -or
        ($forbiddenSuffixes | Where-Object { $name.EndsWith($_) })
    })
    if ($forbidden.Count -gt 0) {
        throw "$Label runtime artifact contains forbidden files: $($forbidden.FullName -join ', ')"
    }
}

function Get-LicenseExpression([string]$ManifestPath) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { return "NOASSERTION" }
    try {
        $manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
        if ($manifest.license -is [string] -and -not [string]::IsNullOrWhiteSpace($manifest.license)) { return $manifest.license }
        if ($manifest.licenses -and $manifest.licenses.Count -gt 0) {
            $values = @($manifest.licenses | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.type } } | Where-Object { $_ })
            if ($values.Count -gt 0) { return ($values -join " OR ") }
        }
    } catch { }
    return "NOASSERTION"
}

function New-CycloneDxComponent(
    [string]$Type,
    [string]$Name,
    [string]$Version,
    [string]$Purl,
    [string]$License,
    [string]$Scope,
    [string]$DistributionScope,
    [bool]$IncludedInPayload,
    [string]$EvidenceSource,
    [string]$EvidenceConfidence,
    [string]$EvidenceNote
) {
    $component = [ordered]@{
        type = $Type
        name = $Name
        version = $Version
        scope = $Scope
        purl = $Purl
        'bom-ref' = $Purl
    }
    $properties = [System.Collections.Generic.List[object]]::new()
    $properties.Add([pscustomobject][ordered]@{ name = "wpe:distributionScope"; value = $DistributionScope })
    $properties.Add([pscustomobject][ordered]@{ name = "wpe:includedInPayload"; value = $IncludedInPayload.ToString().ToLowerInvariant() })
    if ($EvidenceSource) { $properties.Add([pscustomobject][ordered]@{ name = "wpe:licenseEvidenceSource"; value = $EvidenceSource }) }
    if ($EvidenceConfidence) { $properties.Add([pscustomobject][ordered]@{ name = "wpe:licenseEvidenceConfidence"; value = $EvidenceConfidence }) }
    if ($EvidenceNote) { $properties.Add([pscustomobject][ordered]@{ name = "wpe:licenseEvidence"; value = $EvidenceNote }) }
    if ($License -and $License -ne "NOASSERTION") {
        $component.licenses = @([ordered]@{ expression = $License })
    } else {
        $properties.Add([pscustomobject][ordered]@{ name = "wpe:license"; value = "NOASSERTION" })
    }
    $component.properties = @($properties)
    return [pscustomobject]$component
}

function Resolve-LicenseRecord([string]$Purl, [string]$DeclaredLicense, [string]$DefaultSource, [string]$DefaultConfidence) {
    $license = if ($DeclaredLicense) { $DeclaredLicense } else { "NOASSERTION" }
    $source = $DefaultSource
    $confidence = $DefaultConfidence
    $note = "Declared by restored package metadata."
    if ($script:LicenseEvidence.ContainsKey($Purl)) {
        $evidence = $script:LicenseEvidence[$Purl]
        if ($evidence.license) { $license = [string]$evidence.license }
        if ($evidence.sourceUrl) { $source = [string]$evidence.sourceUrl }
        if ($evidence.confidence) { $confidence = [string]$evidence.confidence }
        if ($evidence.evidence) { $note = @($evidence.evidence) -join "; " }
    }
    return [pscustomobject]@{ license = $license; source = $source; confidence = $confidence; note = $note }
}

function Add-NpmDependencies([object]$Dependencies, [hashtable]$Components) {
    if (-not $Dependencies) { return }
    foreach ($property in $Dependencies.PSObject.Properties) {
        $dependency = $property.Value
        if (-not $dependency.version) { continue }
        $name = [string]$property.Name
        $version = [string]$dependency.version
        $key = "npm:$name@$version"
        if (-not $Components.ContainsKey($key)) {
            $manifestPath = if ($dependency.path) { Join-Path ([string]$dependency.path) "package.json" } else { $null }
            $license = if ($manifestPath) { Get-LicenseExpression $manifestPath } else { "NOASSERTION" }
            $escapedName = [System.Uri]::EscapeDataString($name).Replace('%2F', '/')
            $purl = "pkg:npm/$escapedName@$version"
            $registryUrl = "https://registry.npmjs.org/$([System.Uri]::EscapeDataString($name))/$version"
            $metadataConfidence = if ($license -ne "NOASSERTION") { "high" } else { "none" }
            $record = Resolve-LicenseRecord $purl $license $registryUrl $metadataConfidence
            $Components[$key] = New-CycloneDxComponent "library" $name $version $purl $record.license "required" "build" $false $record.source $record.confidence $record.note
        }
        Add-NpmDependencies $dependency.dependencies $Components
    }
}

$publish = Resolve-InRoot $PublishPath
$headlessPublish = Resolve-InRoot $HeadlessPublishPath
$maintenancePublish = Resolve-InRoot $MaintenancePublishPath
$signingResultInput = if ([string]::IsNullOrWhiteSpace($SigningResultPath)) { $null } else { Resolve-InRoot $SigningResultPath }
$output = Resolve-InRoot $OutputDirectory
$releaseReportPath = Join-Path $root "artifacts/release-readiness/report/release-readiness.json"
$webRoot = Join-Path $root "WebUi"
$projectFiles = @(Get-ChildItem -LiteralPath $root -File -Filter "*.csproj")

& (Join-Path $PSScriptRoot "web-egress-gate.ps1") -WebRoot $webRoot

foreach ($runtimeInput in @(
    [pscustomobject]@{ Label = "Desktop"; Path = $publish },
    [pscustomobject]@{ Label = "Headless"; Path = $headlessPublish },
    [pscustomobject]@{ Label = "Maintenance"; Path = $maintenancePublish }
)) {
    if (-not (Test-Path -LiteralPath $runtimeInput.Path -PathType Container)) {
        throw "$($runtimeInput.Label) publish directory is missing: $($runtimeInput.Path)"
    }
    Assert-CleanRuntimeArtifact $runtimeInput.Path $runtimeInput.Label
}
if (-not (Test-Path -LiteralPath $releaseReportPath -PathType Leaf)) { throw "Release-readiness report is missing." }
if ($projectFiles.Count -ne 1) { throw "Expected exactly one application project file; found $($projectFiles.Count)." }
$projectFile = $projectFiles[0].FullName
$evidenceFile = Resolve-InRoot $LicenseEvidencePath
$script:LicenseEvidence = @{}
$evidenceDocument = Get-Content -Raw -LiteralPath $evidenceFile | ConvertFrom-Json
foreach ($entry in @($evidenceDocument.components)) {
    if ($entry.purl -and -not $script:LicenseEvidence.ContainsKey([string]$entry.purl)) { $script:LicenseEvidence[[string]$entry.purl] = $entry }
}
[xml]$projectXml = Get-Content -Raw -LiteralPath $projectFile
$projectVersionNodes = @($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })
if ($projectVersionNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$projectVersionNodes[0])) {
    throw "The application project must define exactly one authoritative Version."
}
$projectVersion = ([string]$projectVersionNodes[0]).Trim()

$releaseReport = Get-Content -Raw -LiteralPath $releaseReportPath | ConvertFrom-Json
if ($releaseReport.status -ne "passed" -or $releaseReport.runtime -ne $Runtime) {
    throw "A passed release-readiness report for $Runtime is required."
}
& (Join-Path $root "publish.ps1") -Configuration Release -Runtime $Runtime -Output $publish -ValidateOnly
if ($LASTEXITCODE -ne 0) { throw "Publish artifact validation failed." }

$exe = @(Get-ChildItem -LiteralPath $publish -File -Filter "*.exe")
if ($exe.Count -ne 1) { throw "Expected exactly one application executable; found $($exe.Count)." }
$fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe[0].FullName)
$binaryVersion = if ($fileInfo.FileVersion) { $fileInfo.FileVersion } else { "0.0.0.0" }
$parsedBinaryVersion = [Version]$binaryVersion
$binaryPackageVersion = "$($parsedBinaryVersion.Major).$($parsedBinaryVersion.Minor).$($parsedBinaryVersion.Build)"
if ($binaryPackageVersion -ne $projectVersion) { throw "Published binary version $binaryPackageVersion does not match project Version $projectVersion." }
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw "Unable to resolve the source commit." }
$shortCommit = $commit.Substring(0, 12)
$dirty = (@(& git -C $root status --porcelain=v1 --untracked-files=normal)).Count -gt 0
if (-not $PackageVersion) { $PackageVersion = "$projectVersion-$Channel.1+$shortCommit" }
$safeVersion = $PackageVersion.Replace('+', '-').Replace('/', '-').Replace('\', '-')
$packageName = "WPE-Agent-$safeVersion-$Runtime-portable"
$packageRoot = Join-Path $output $packageName
$payloadRoot = Join-Path $packageRoot "app"
$zipPath = Join-Path $output "$packageName.zip"

if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
Copy-Item -Path (Join-Path $publish "*") -Destination $payloadRoot -Recurse -Force
# Portable Beta packages exclude debug symbols; the verified publish directory remains unchanged.
Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force -File -Filter "*.pdb" | Remove-Item -Force

$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $payloadRoot $exe[0].Name)
$isSigned = $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid
$signatureStatus = if ($isSigned) { "valid" } elseif ($signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned) { "unsigned" } else { "invalid:$($signature.Status)" }

$payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force -File | Sort-Object FullName)
$payloadPrefix = $payloadRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$manifest = @($payloadFiles | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($payloadPrefix.Length).Replace('\', '/')
        size = [long]$_.Length
        sha256 = Get-Sha256 $_.FullName
    }
})
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot "FILE-MANIFEST.json") -Encoding UTF8
$manifest | ForEach-Object { "$($_.sha256)  app/$($_.path)" } | Set-Content -LiteralPath (Join-Path $packageRoot "PAYLOAD-SHA256SUMS") -Encoding ASCII

$components = @{}
$runtimeLibraries = @{}
$assetsFile = Join-Path $root "obj/project.assets.json"
if (-not (Test-Path -LiteralPath $assetsFile -PathType Leaf)) { throw "NuGet project.assets.json is required for distribution-scope classification." }
$runtimeAssetJson = (& node (Join-Path $PSScriptRoot "classify-nuget-assets.mjs") $assetsFile $Runtime | Out-String)
if ($LASTEXITCODE -ne 0) { throw "Unable to classify NuGet runtime assets from project.assets.json." }
$parsedRuntimeLibraries = $runtimeAssetJson | ConvertFrom-Json
foreach ($library in $parsedRuntimeLibraries) {
    $runtimeLibraries[[string]$library] = $true
}
$dotnetPackageJson = (& dotnet list $projectFile package --include-transitive --format json | Out-String)
if ($LASTEXITCODE -ne 0) { throw "dotnet dependency enumeration failed." }
$dotnetPackages = $dotnetPackageJson | ConvertFrom-Json
$packageFolder = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME ".nuget/packages" }
$resolvedNuget = [System.Collections.Generic.List[object]]::new()
foreach ($project in @($dotnetPackages.projects)) {
    foreach ($framework in @($project.frameworks)) {
        foreach ($package in @($framework.topLevelPackages)) { $resolvedNuget.Add([pscustomobject]@{ id = $package.id; version = $package.resolvedVersion }) }
        foreach ($package in @($framework.transitivePackages)) { $resolvedNuget.Add([pscustomobject]@{ id = $package.id; version = $package.resolvedVersion }) }
    }
}
foreach ($package in $resolvedNuget) {
    $name = [string]$package.id
    $version = [string]$package.version
    if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($version)) { continue }
    $nuspec = Join-Path $packageFolder ($name.ToLowerInvariant() + "/" + $version.ToLowerInvariant() + "/" + $name.ToLowerInvariant() + ".nuspec")
    $license = "NOASSERTION"
    if (Test-Path -LiteralPath $nuspec -PathType Leaf) {
        try {
            [xml]$nuspecXml = Get-Content -Raw -LiteralPath $nuspec
            $licenseNode = $nuspecXml.package.metadata.license
            if ($licenseNode -and -not [string]::IsNullOrWhiteSpace([string]$licenseNode.'#text')) { $license = [string]$licenseNode.'#text' }
        } catch { }
    }
    $purl = "pkg:nuget/$([System.Uri]::EscapeDataString($name))@$version"
    $included = $runtimeLibraries.ContainsKey("$name/$version".ToLowerInvariant())
    $distributionScope = if ($included) { "runtime" } else { "build" }
    $nugetId = $name.ToLowerInvariant()
    $nugetVersion = $version.ToLowerInvariant()
    $nugetMetadataUrl = "https://api.nuget.org/v3-flatcontainer/$nugetId/$nugetVersion/$nugetId.nuspec"
    $metadataConfidence = if ($license -ne "NOASSERTION") { "high" } else { "none" }
    $record = Resolve-LicenseRecord $purl $license $nugetMetadataUrl $metadataConfidence
    $components["nuget:$name@$version"] = New-CycloneDxComponent "library" $name $version $purl $record.license "required" $distributionScope $included $record.source $record.confidence $record.note
}

Push-Location $webRoot
try {
    $npmJson = (& pnpm list --prod --json --depth Infinity | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "pnpm production dependency enumeration failed." }
    $npmProjects = $npmJson | ConvertFrom-Json
    foreach ($project in @($npmProjects)) { Add-NpmDependencies $project.dependencies $components }
} finally { Pop-Location }

$sbom = [ordered]@{
    bomFormat = "CycloneDX"
    specVersion = "1.5"
    serialNumber = "urn:uuid:$([Guid]::NewGuid())"
    version = 1
    metadata = [ordered]@{
        timestamp = [DateTimeOffset]::UtcNow.ToString("O")
        component = [ordered]@{
            type = "application"
            name = "WPE Agent"
            version = $PackageVersion
            properties = @(
                [ordered]@{ name = "wpe:binaryVersion"; value = $binaryVersion },
                [ordered]@{ name = "wpe:runtime"; value = $Runtime },
                [ordered]@{ name = "wpe:sourceCommit"; value = $commit }
            )
        }
        properties = @(
            [ordered]@{ name = "wpe:nugetSource"; value = "restored NuGet graph via dotnet list package --include-transitive --format json" },
            [ordered]@{ name = "wpe:npmSource"; value = "WebUi/pnpm-lock.yaml via pnpm list --prod" },
            [ordered]@{ name = "wpe:licenseSemantics"; value = "NOASSERTION means local package metadata did not provide a license expression" }
        )
    }
    components = @($components.Values | Sort-Object purl)
}
$sbom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $packageRoot "sbom.cdx.json") -Encoding UTF8

$licenseOutput = Join-Path $packageRoot "license"
$licenseGateOutput = & (Join-Path $PSScriptRoot "license-gate.ps1") -SbomPath (Join-Path $packageRoot "sbom.cdx.json") -OutputDirectory $licenseOutput -ReleaseIntent Evaluation
if ($LASTEXITCODE -ne 0) { throw "Evaluation license gate failed." }
$licenseReport = Get-Content -Raw -LiteralPath (Join-Path $licenseOutput "license-report.json") | ConvertFrom-Json
$distributionNotice = Join-Path $licenseOutput "THIRD-PARTY-NOTICES.txt"
& (Join-Path $PSScriptRoot "new-third-party-notices.ps1") -OutputPath $distributionNotice
if (-not (Test-Path -LiteralPath $distributionNotice -PathType Leaf)) { throw "Distribution third-party notice generation failed." }
$distributionNoticeHash = Get-Sha256 $distributionNotice

$metadata = [ordered]@{
    schemaVersion = "wpe.beta-package.v1"
    product = "WPE Agent (working name)"
    packageVersion = $PackageVersion
    productVersion = $projectVersion
    binaryVersion = $binaryVersion
    channel = $Channel
    configuration = "Release"
    runtime = $Runtime
    architecture = $Runtime.Substring(4)
    packageType = "portable-zip"
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    source = [ordered]@{ commit = $commit; dirty = $dirty }
    safety = [ordered]@{
        testnetOnly = $true
        mainnetEnabled = $false
        previewFixturesPresent = $false
        secretScanPassed = $true
        uploadPerformed = $false
        deploymentPerformed = $false
    }
    signing = [ordered]@{
        status = $signatureStatus
        subject = if ($isSigned) { $signature.SignerCertificate.Subject } else { $null }
        thumbprint = if ($isSigned) { $signature.SignerCertificate.Thumbprint } else { $null }
        distributable = ($isSigned -and -not $dirty)
        note = if (-not $isSigned) { "Unsigned Beta package; internal evaluation only and non-distributable." } elseif ($dirty) { "Signed payload came from a dirty source tree; non-distributable." } else { "Signature valid; commercial release still requires the documented go/no-go gates." }
    }
    prerequisites = @("Windows 11 x64 (Beta-certified)", ".NET 8 Desktop Runtime", "Microsoft Edge WebView2 Runtime")
    unsupported = @("Mainnet", "Windows 10 certification", "Windows ARM64 certification", "Installer upgrade/uninstall semantics")
    rollback = [ordered]@{ strategy = "side-by-side portable directory"; preservePreviousVersion = $true; databaseDowngradeAllowed = $false }
    contents = [ordered]@{
        payloadFileCount = $manifest.Count
        sbomComponentCount = $components.Count
        licenseAllowCount = $licenseReport.summary.allow
        licenseReviewCount = $licenseReport.summary.review
        licenseDenyCount = $licenseReport.summary.deny
        licenseNoAssertionCount = $licenseReport.summary.noAssertion
        licenseGateStatus = $licenseReport.status
        thirdPartyNoticePath = "license/THIRD-PARTY-NOTICES.txt"
        thirdPartyNoticeSha256 = $distributionNoticeHash
    }
    exclusions = @("Debug symbol files (*.pdb) are retained in build artifacts but excluded from the portable distribution package.")
}
$metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $packageRoot "RELEASE-METADATA.json") -Encoding UTF8
Copy-Item -LiteralPath $releaseReportPath -Destination (Join-Path $packageRoot "RELEASE-READINESS.json") -Force

& (Join-Path $root "publish.ps1") -Configuration Release -Runtime $Runtime -Output $packageRoot -ValidateOnly
if ($LASTEXITCODE -ne 0) { throw "Staged package validation failed." }

Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = Get-Sha256 $zipPath
"$zipHash  $([System.IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath (Join-Path $output "SHA256SUMS") -Encoding ASCII

$result = [ordered]@{
    package = $zipPath
    productVersion = $projectVersion
    sha256 = $zipHash
    signingStatus = $signatureStatus
    distributable = ($isSigned -and -not $dirty)
    sourceDirty = $dirty
    fileCount = $manifest.Count
    sbomComponents = $components.Count
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "package-result.json") -Encoding UTF8
$result | ConvertTo-Json -Depth 5

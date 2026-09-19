param(
    [string]$PublishPath = "artifacts/release-readiness/publish",
    [string]$HeadlessPublishPath = "artifacts/release-readiness/headless",
    [string]$MaintenancePublishPath = "artifacts/release-readiness/maintenance",
    [string]$SigningResultPath,
    [string]$ExpectedSignerSubject,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$ExpectedSignerThumbprint,
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
$headlessProject = Join-Path $root "WPE.Headless/WPE.Headless.csproj"
$maintenanceProject = Join-Path $root "WPE.Maintenance/WPE.Maintenance.csproj"
foreach ($dependencyProject in @($projectFile, $headlessProject, $maintenanceProject)) {
    if (-not (Test-Path -LiteralPath $dependencyProject -PathType Leaf)) { throw "Runtime bundle project is missing: $dependencyProject" }
}
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
if ($releaseReport.schemaVersion -ne "wpe.release-readiness.v1" -or $releaseReport.status -ne "passed" -or $releaseReport.runtime -ne $Runtime) {
    throw "A passed release-readiness report for $Runtime is required."
}
if (-not $releaseReport.artifacts -or -not $releaseReport.artifacts.desktop -or -not $releaseReport.artifacts.headless -or -not $releaseReport.artifacts.maintenance) {
    throw "Release-readiness runtime bundle facts are incomplete."
}
if ([string]$releaseReport.productVersion -ne $projectVersion) { throw "Release-readiness product version does not match the project." }

$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw "Unable to resolve the source commit." }
$dirty = (@(& git -C $root status --porcelain=v1 --untracked-files=normal)).Count -gt 0
if ([string]$releaseReport.source.commit -ne $commit) { throw "Release-readiness source commit does not match the packaging source." }
if ([bool]$releaseReport.source.dirty -ne $dirty) { throw "Release-readiness dirty-state does not match the packaging source." }
$shortCommit = $commit.Substring(0, 12)

& (Join-Path $root "publish.ps1") -Configuration Release -Runtime $Runtime -Output $publish -ValidateOnly
if ($LASTEXITCODE -ne 0) { throw "Desktop publish artifact validation failed." }

$runtimeArtifacts = @(
    [pscustomobject]@{ Label = "desktop"; Root = $publish; Executable = "WPE-Agent.exe"; Readiness = $releaseReport.artifacts.desktop },
    [pscustomobject]@{ Label = "headless"; Root = $headlessPublish; Executable = "WPE-Headless.exe"; Readiness = $releaseReport.artifacts.headless },
    [pscustomobject]@{ Label = "maintenance"; Root = $maintenancePublish; Executable = "WPE.Maintenance.exe"; Readiness = $releaseReport.artifacts.maintenance }
)
$artifactStates = [System.Collections.Generic.List[object]]::new()
foreach ($artifact in $runtimeArtifacts) {
    $executables = @(Get-ChildItem -LiteralPath $artifact.Root -File -Filter "*.exe")
    if ($executables.Count -ne 1 -or $executables[0].Name -ne $artifact.Executable) {
        throw "Runtime artifact $($artifact.Label) must contain exactly the expected top-level executable $($artifact.Executable)."
    }
    $fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executables[0].FullName)
    $binaryVersion = if ($fileInfo.FileVersion) { $fileInfo.FileVersion } else { "0.0.0.0" }
    $parsedBinaryVersion = [Version]$binaryVersion
    $binaryPackageVersion = "$($parsedBinaryVersion.Major).$($parsedBinaryVersion.Minor).$($parsedBinaryVersion.Build)"
    if ($binaryPackageVersion -ne $projectVersion) { throw "Runtime artifact version mismatch: $($artifact.Label)." }
    $facts = Get-ArtifactTreeFacts $artifact.Root
    $signature = Get-AuthenticodeSignature -LiteralPath $executables[0].FullName
    if ($signature.Status -notin @([System.Management.Automation.SignatureStatus]::Valid, [System.Management.Automation.SignatureStatus]::NotSigned)) {
        throw "Runtime artifact has an invalid Authenticode state: $($artifact.Label):$($signature.Status)"
    }
    if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and $null -eq $signature.TimeStamperCertificate) {
        throw "Runtime bundle executable timestamp is missing: $($artifact.Label)"
    }
    $artifactStates.Add([pscustomobject]@{
        Label = $artifact.Label
        Root = $artifact.Root
        Executable = $executables[0]
        Readiness = $artifact.Readiness
        Facts = $facts
        Signature = $signature
    })
}

$allSigned = @($artifactStates | Where-Object { $_.Signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid }).Count -eq 3
$allUnsigned = @($artifactStates | Where-Object { $_.Signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned }).Count -eq 3
if (-not ($allSigned -or $allUnsigned)) { throw "Runtime bundle signature states must be all Valid or all NotSigned." }

$readinessReportHash = Get-Sha256 $releaseReportPath
$signingResult = $null
$signingResultHash = $null
$signatureSubject = $null
$signatureThumbprint = $null
if ($allSigned) {
    if ([string]::IsNullOrWhiteSpace($ExpectedSignerSubject) -or [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
        throw "A signed runtime bundle requires the approved signer subject and thumbprint."
    }
    if ($null -eq $signingResultInput -or -not (Test-Path -LiteralPath $signingResultInput -PathType Leaf)) {
        throw "A signed runtime bundle requires SigningResultPath."
    }
    $signingResultHash = Get-Sha256 $signingResultInput
    try { $signingResult = Get-Content -Raw -LiteralPath $signingResultInput | ConvertFrom-Json } catch { throw "Runtime bundle signing result is malformed." }
    if ($signingResult.schemaVersion -ne "wpe.runtime-bundle-signing/1.0") { throw "Runtime bundle signing result schema is unsupported." }
    if ([string]$signingResult.readinessReportSha256 -ne $readinessReportHash -or
        [string]$signingResult.sourceCommit -ne $commit -or
        [string]$signingResult.productVersion -ne $projectVersion) {
        throw "Runtime bundle signing result is not bound to this readiness candidate."
    }
    $signatureSubject = [string]$signingResult.publisherSubject
    $signatureThumbprint = ([string]$signingResult.certificateThumbprint).ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($signatureSubject) -or $signatureThumbprint -notmatch '^[A-F0-9]{40}$') {
        throw "Runtime bundle signing identity is invalid."
    }
    if ($signatureSubject -ne $ExpectedSignerSubject -or $signatureThumbprint -ne $ExpectedSignerThumbprint.ToUpperInvariant()) {
        throw "Runtime bundle signer identity does not match the approved publisher."
    }
    if (@($signingResult.artifacts).Count -ne 3) { throw "Runtime bundle signing result must contain exactly three artifacts." }
    foreach ($state in $artifactStates) {
        $entries = @($signingResult.artifacts | Where-Object { $_.label -eq $state.Label })
        if ($entries.Count -ne 1) { throw "Runtime bundle signing result artifact identity is invalid: $($state.Label)" }
        $entry = $entries[0]
        if ([string]$entry.executable -ne $state.Executable.Name -or
            [string]$entry.inputTreeSha256 -ne [string]$state.Readiness.treeSha256 -or
            [string]$entry.outputTreeSha256 -ne $state.Facts.TreeSha256 -or
            [int]$entry.outputFileCount -ne $state.Facts.FileCount -or
            [string]$entry.executableSha256 -ne (Get-Sha256 $state.Executable.FullName) -or
            [string]$entry.signatureStatus -ne "Valid") {
            throw "Runtime bundle signing result artifact facts do not match: $($state.Label)"
        }
        if ($state.Signature.SignerCertificate.Subject -ne $signatureSubject -or
            $state.Signature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $signatureThumbprint) {
            throw "Runtime bundle executable signer identity does not match the signing result: $($state.Label)"
        }
    }
} else {
    if ($null -ne $signingResultInput) { throw "Unsigned runtime bundle must not consume a signing result." }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerSubject) -or -not [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
        throw "Unsigned runtime bundle must not declare an approved signer."
    }
    foreach ($state in $artifactStates) {
        if ($state.Facts.TreeSha256 -ne [string]$state.Readiness.treeSha256 -or
            $state.Facts.FileCount -ne [int]$state.Readiness.fileCount) {
            throw "Unsigned runtime artifact does not match release-readiness bytes: $($state.Label)"
        }
    }
}
$signatureStatus = if ($allSigned) { "valid" } else { "unsigned" }
$isSigned = $allSigned
$desktopState = @($artifactStates | Where-Object { $_.Label -eq "desktop" })[0]
$binaryVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($desktopState.Executable.FullName).FileVersion
if (-not $PackageVersion) { $PackageVersion = "$projectVersion-$Channel.1+$shortCommit" }
$safeVersion = $PackageVersion.Replace('+', '-').Replace('/', '-').Replace('\', '-')
$packageName = "WPE-Agent-$safeVersion-$Runtime-portable"
$packageRoot = Join-Path $output $packageName
$payloadRoot = Join-Path $packageRoot "app"
$headlessPayloadRoot = Join-Path $packageRoot "headless"
$maintenancePayloadRoot = Join-Path $packageRoot "maintenance"
$zipPath = Join-Path $output "$packageName.zip"

if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
foreach ($directory in @($payloadRoot, $headlessPayloadRoot, $maintenancePayloadRoot)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$copyPlan = @(
    [pscustomobject]@{ Label = "desktop"; Source = $publish; Destination = $payloadRoot },
    [pscustomobject]@{ Label = "headless"; Source = $headlessPublish; Destination = $headlessPayloadRoot },
    [pscustomobject]@{ Label = "maintenance"; Source = $maintenancePublish; Destination = $maintenancePayloadRoot }
)
foreach ($copy in $copyPlan) {
    Copy-Item -Path (Join-Path $copy.Source "*") -Destination $copy.Destination -Recurse -Force
    $sourceState = @($artifactStates | Where-Object { $_.Label -eq $copy.Label })[0]
    $copiedFacts = Get-ArtifactTreeFacts $copy.Destination
    if ($copiedFacts.TreeSha256 -ne $sourceState.Facts.TreeSha256 -or $copiedFacts.FileCount -ne $sourceState.Facts.FileCount) {
        throw "Runtime bundle copy changed artifact bytes: $($copy.Label)"
    }
}
if ($null -ne $signingResultInput) {
    Copy-Item -LiteralPath $signingResultInput -Destination (Join-Path $packageRoot "SIGNING-RESULT.json") -Force
}

$manifest = @()
foreach ($copy in $copyPlan) {
    $prefix = $copy.Destination.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $manifest += @(Get-ChildItem -LiteralPath $copy.Destination -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        $packageRelative = if ($copy.Label -eq "desktop") { "app/$relative" } else { "$($copy.Label)/$relative" }
        [ordered]@{
            path = $packageRelative
            size = [long]$_.Length
            sha256 = Get-Sha256 $_.FullName
        }
    })
}
$manifest = @($manifest | Sort-Object path)
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot "FILE-MANIFEST.json") -Encoding UTF8
$manifest | ForEach-Object { "$($_.sha256)  $($_.path)" } | Set-Content -LiteralPath (Join-Path $packageRoot "PAYLOAD-SHA256SUMS") -Encoding ASCII

$components = @{}
$runtimeLibraries = @{}
$assetFiles = @(
    (Join-Path $root "obj/project.assets.json"),
    (Join-Path $root "WPE.Headless/obj/project.assets.json"),
    (Join-Path $root "WPE.Maintenance/obj/project.assets.json")
)
foreach ($assetsFile in $assetFiles) {
    if (-not (Test-Path -LiteralPath $assetsFile -PathType Leaf)) { throw "Runtime bundle NuGet project.assets.json is required: $assetsFile" }
    $runtimeAssetJson = (& node (Join-Path $PSScriptRoot "classify-nuget-assets.mjs") $assetsFile $Runtime | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Unable to classify NuGet runtime assets from $assetsFile." }
    $parsedRuntimeLibraries = $runtimeAssetJson | ConvertFrom-Json
    foreach ($library in $parsedRuntimeLibraries) {
        $runtimeLibraries[[string]$library] = $true
    }
}
$packageFolder = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME ".nuget/packages" }
$resolvedNuget = [System.Collections.Generic.List[object]]::new()
foreach ($dependencyProject in @($projectFile, $headlessProject, $maintenanceProject)) {
    $dotnetPackageJson = (& dotnet list $dependencyProject package --include-transitive --format json | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "dotnet dependency enumeration failed for $dependencyProject." }
    $dotnetPackages = $dotnetPackageJson | ConvertFrom-Json
    foreach ($project in @($dotnetPackages.projects)) {
        foreach ($framework in @($project.frameworks)) {
            foreach ($package in @($framework.topLevelPackages)) { $resolvedNuget.Add([pscustomobject]@{ id = $package.id; version = $package.resolvedVersion }) }
            foreach ($package in @($framework.transitivePackages)) { $resolvedNuget.Add([pscustomobject]@{ id = $package.id; version = $package.resolvedVersion }) }
        }
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
        subject = if ($isSigned) { $signatureSubject } else { $null }
        thumbprint = if ($isSigned) { $signatureThumbprint } else { $null }
        readinessReportSha256 = $readinessReportHash
        transitionResultPath = if ($isSigned) { "SIGNING-RESULT.json" } else { $null }
        transitionResultSha256 = if ($isSigned) { $signingResultHash } else { $null }
        distributable = ($isSigned -and -not $dirty -and $releaseReport.source.dirty -eq $false)
        note = if (-not $isSigned) { "Unsigned three-artifact Beta bundle; internal evaluation only and non-distributable." } elseif ($dirty) { "Signed runtime bundle came from a dirty packaging source; non-distributable." } else { "All runtime executables have one verified publisher identity; commercial release still requires the documented go/no-go gates." }
    }
    prerequisites = @("Windows 11 x64 (Beta-certified)", ".NET 8 Desktop Runtime", "Microsoft Edge WebView2 Runtime")
    unsupported = @("Mainnet", "Windows 10 certification", "Windows ARM64 certification", "Installer upgrade/uninstall semantics")
    rollback = [ordered]@{ strategy = "side-by-side portable directory"; preservePreviousVersion = $true; databaseDowngradeAllowed = $false }
    contents = [ordered]@{
        payloadFileCount = $manifest.Count
        runtimeArtifacts = @($artifactStates | Sort-Object Label | ForEach-Object {
            [ordered]@{
                label = $_.Label
                fileCount = $_.Facts.FileCount
                treeSha256 = $_.Facts.TreeSha256
                executable = $_.Executable.Name
                executableSha256 = Get-Sha256 $_.Executable.FullName
            }
        })
        sbomComponentCount = $components.Count
        licenseAllowCount = $licenseReport.summary.allow
        licenseReviewCount = $licenseReport.summary.review
        licenseDenyCount = $licenseReport.summary.deny
        licenseNoAssertionCount = $licenseReport.summary.noAssertion
        licenseGateStatus = $licenseReport.status
        thirdPartyNoticePath = "license/THIRD-PARTY-NOTICES.txt"
        thirdPartyNoticeSha256 = $distributionNoticeHash
    }
    exclusions = @("Debug symbols and source files are prohibited from all three runtime artifact roots before packaging.")
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
    distributable = ($isSigned -and -not $dirty -and $releaseReport.source.dirty -eq $false)
    sourceDirty = $dirty
    readinessReportSha256 = $readinessReportHash
    signingResultSha256 = if ($isSigned) { $signingResultHash } else { $null }
    runtimeArtifactCount = $artifactStates.Count
    fileCount = $manifest.Count
    sbomComponents = $components.Count
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "package-result.json") -Encoding UTF8
$result | ConvertTo-Json -Depth 5

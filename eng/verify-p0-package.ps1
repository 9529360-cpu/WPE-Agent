param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [ValidatePattern('^[a-z0-9-]+$')]
    [string]$Runtime = "win-x64",
    [string]$BuildPath,
    [string]$PublishPath = "artifacts/release-readiness/publish",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Resolve-InRoot([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label path is required." }
    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $root $Path))
    }
    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $root) {
        throw "$Label path must be inside the project root."
    }
    return $resolved
}

function Assert-Contains([string]$RelativePath, [string]$Pattern, [string]$Rule) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or -not (Select-String -LiteralPath $path -Pattern $Pattern -Quiet)) {
        throw "Source invariant failed: $Rule."
    }
}

function Get-BinaryVersion([System.IO.FileInfo]$Executable) {
    $rawVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Executable.FullName).FileVersion
    $parsed = [Version]$rawVersion
    return "$($parsed.Major).$($parsed.Minor).$($parsed.Build)"
}

function Assert-NoForbiddenFiles([string]$ArtifactPath, [string]$Label) {
    $files = @(Get-ChildItem -LiteralPath $ArtifactPath -Recurse -Force -File)
    $forbidden = @($files | Where-Object {
        $_.Extension -match '^(?i:\.db|\.db-wal|\.db-shm|\.sqlite|\.sqlite-wal|\.sqlite-shm|\.sqlite3|\.sqlite3-wal|\.sqlite3-shm|\.log|\.pdb|\.cs|\.csproj|\.sln|\.ps1|\.pem|\.key|\.pfx|\.snk)$' -or
        $_.Name -match '^(?i:API\.txt|agent-settings.*\.json|appsettings\.(Local|Development)\.json|order-state.*\.json|\.env(?:\..*)?)$' -or
        $_.FullName.Substring($ArtifactPath.Length).TrimStart('\', '/') -match '^(?i:Tests|WPE\.Tests|logs)(?:[\/]|$)'
    })
    if ($forbidden.Count -gt 0) {
        $relative = @($forbidden | ForEach-Object { $_.FullName.Substring($ArtifactPath.Length).TrimStart('\', '/') })
        throw "$Label contains forbidden source, secret, database, or log files:`n$($relative -join [Environment]::NewLine)"
    }

    $privateKeyHeaders = @($files | Where-Object { $_.Length -le 10MB } | Select-String -Pattern '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' -List -ErrorAction SilentlyContinue)
    if ($privateKeyHeaders.Count -gt 0) { throw "$Label contains private-key material." }
    return $files
}

function Assert-Artifact([string]$ArtifactPath, [string]$Label, [string]$ExpectedVersion) {
    if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Container)) { throw "$Label directory is missing: $ArtifactPath" }
    $files = @(Assert-NoForbiddenFiles $ArtifactPath $Label)
    $executables = @(Get-ChildItem -LiteralPath $ArtifactPath -Recurse -File -Filter "*.exe" | Where-Object {
        [System.Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName).ProductName -like "WPE Agent*"
    })
    if ($executables.Count -ne 1) { throw "$Label must contain exactly one WPE Agent executable; found $($executables.Count)." }
    $binaryVersion = Get-BinaryVersion $executables[0]
    if ($binaryVersion -ne $ExpectedVersion) { throw "$Label binary version $binaryVersion does not match project Version $ExpectedVersion." }
    if (-not (Test-Path -LiteralPath (Join-Path $ArtifactPath "WebUi\out\index.html") -PathType Leaf)) {
        throw "$Label is missing WebUi/out/index.html."
    }
    return [ordered]@{
        path = $ArtifactPath
        files = $files.Count
        executable = $executables[0].Name
        version = $binaryVersion
        executableSha256 = (Get-FileHash -LiteralPath $executables[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        signatureStatus = [string](Get-AuthenticodeSignature -LiteralPath $executables[0].FullName).Status
    }
}

function Assert-InternalRcEvidence([string]$ArtifactPath, [object]$ArtifactResult, [string]$ExpectedVersion) {
    $metadataPath = Join-Path $ArtifactPath "INTERNAL-RC-METADATA.json"
    $manifestPath = Join-Path $ArtifactPath "FILE-MANIFEST.json"
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf) -or -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Internal RC metadata or file manifest is missing."
    }
    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    if ($metadata.schemaVersion -ne "wpe.internal-unsigned-rc.v1" -or $metadata.productVersion -ne $ExpectedVersion) { throw "Internal RC metadata is invalid or has the wrong version." }
    if ($metadata.signingStatus -ne "unsigned" -or $metadata.distributable -ne $false -or $metadata.commercialReleaseApproved -ne $false) { throw "Internal RC must be explicitly unsigned, non-distributable, and not commercially approved." }
    if ($ArtifactResult.signatureStatus -ne "NotSigned") { throw "Internal RC metadata says unsigned but the executable signature state differs." }

    [object[]]$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $actual = @(Get-ChildItem -LiteralPath $ArtifactPath -Recurse -Force -File | Where-Object { $_.FullName -ne $manifestPath })
    if ($manifest.Count -ne $actual.Count) { throw "Internal RC manifest file count does not match the staging payload." }
    foreach ($entry in $manifest) {
        $path = Join-Path $ArtifactPath ([string]$entry.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Manifest entry is missing: $($entry.path)" }
        $file = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([long]$entry.bytes -ne [long]$file.Length -or [string]$entry.sha256 -ne $hash) { throw "Manifest verification failed: $($entry.path)" }
    }
    return [ordered]@{
        status = [string]$metadata.status
        signingStatus = [string]$metadata.signingStatus
        distributable = [bool]$metadata.distributable
        commercialReleaseApproved = [bool]$metadata.commercialReleaseApproved
        sourceCommit = [string]$metadata.sourceCommit
        sourceDirty = [bool]$metadata.sourceDirty
        manifestEntries = $manifest.Count
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$projectFiles = @(Get-ChildItem -LiteralPath $root -File -Filter "*.csproj")
if ($projectFiles.Count -ne 1) { throw "Expected exactly one application project file; found $($projectFiles.Count)." }
[xml]$project = Get-Content -Raw -LiteralPath $projectFiles[0].FullName
$versions = @($project.Project.PropertyGroup.Version | Where-Object { $_ })
$frameworks = @($project.Project.PropertyGroup.TargetFramework | Where-Object { $_ })
if ($versions.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$versions[0])) { throw "The application project must define exactly one Version." }
if ($frameworks.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$frameworks[0])) { throw "The application project must define exactly one TargetFramework." }
$productVersion = ([string]$versions[0]).Trim()
$targetFramework = ([string]$frameworks[0]).Trim()

$build = if ([string]::IsNullOrWhiteSpace($BuildPath)) { $null } else { Resolve-InRoot $BuildPath "Build" }
$publish = Resolve-InRoot $PublishPath "Publish"

$plan = [ordered]@{
    schemaVersion = "wpe.p0-package-verification.v1"
    mode = if ($DryRun) { "dry-run" } else { "verify" }
    offline = $true
    configuration = $Configuration
    runtime = $Runtime
    productVersion = $productVersion
    buildPath = $build
    publishPath = $publish
    invokesBuild = $false
    invokesTests = $false
    invokesApplication = $false
    invokesNetwork = $false
}

if ($DryRun) {
    $plan.status = "planned"
    $plan | ConvertTo-Json -Depth 5
    exit 0
}

Assert-Contains "Services/AppDataPaths.cs" 'Environment\.SpecialFolder\.LocalApplicationData' "mutable data root uses LocalAppData"
Assert-Contains "Services/AppDataPaths.cs" '"WPE Agent"' "mutable data uses the WPE Agent subdirectory"
Assert-Contains "Services/AutoTradingAgent.cs" '!profile\.IsTestnet' "agent startup rejects Mainnet"
Assert-Contains "Services/AutoTradingAgent.cs" '!exchangeProfile\.IsTestnet' "agent runtime rejects Mainnet"
Assert-Contains "SetupWindow.xaml.cs" 'tag=="Mainnet"' "Setup refuses Mainnet selection"
Assert-Contains "SetupWindow.xaml.cs" 'SelectedIndex=0' "Setup returns to Testnet"

$sourceWebIndex = Join-Path $root "WebUi\out\index.html"
if (-not (Test-Path -LiteralPath $sourceWebIndex -PathType Leaf)) { throw "WebUi/out/index.html is missing; the static export was not prepared for packaging." }

$buildResult = if ($build) { Assert-Artifact $build "Release build artifact" $productVersion } else { $null }
$publishResult = Assert-Artifact $publish "Release publish artifact" $productVersion
$internalRc = Assert-InternalRcEvidence $publish $publishResult $productVersion
$plan.status = "passed"
$plan.webUiSourceExport = $sourceWebIndex
$plan.build = $buildResult
$plan.publish = $publishResult
$plan.internalRc = $internalRc
$plan.safety = [ordered]@{ localAppData = $true; mainnetEnabled = $false; forbiddenFiles = 0 }
$plan | ConvertTo-Json -Depth 6
Write-Host "P0 package verification passed offline. No tests, trading, application launch, or network access were invoked." -ForegroundColor Green

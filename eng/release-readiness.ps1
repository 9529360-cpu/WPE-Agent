param(
    [ValidatePattern('^[a-z0-9-]+$')]
    [string]$Runtime = "win-x64",
    [string]$Output = "artifacts/release-readiness/publish",
    [string]$HeadlessOutput = "artifacts/release-readiness/headless",
    [string]$MaintenanceOutput = "artifacts/release-readiness/maintenance",
    [string]$ReportDirectory = "artifacts/release-readiness/report",
    [switch]$PreviewGateOnly,
    [switch]$StaticGatesOnly,
    [string]$PreviewGateRoot
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
if ($PreviewGateOnly) {
    if ([string]::IsNullOrWhiteSpace($PreviewGateRoot) -or $PreviewGateRoot.StartsWith('\\') -or -not [System.IO.Path]::IsPathRooted($PreviewGateRoot)) {
        throw "PreviewGateRoot must be an absolute local path."
    }
    $previewDriveRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($PreviewGateRoot))
    if ($previewDriveRoot -notmatch '^[A-Za-z]:\\$' -or [System.IO.DriveInfo]::new($previewDriveRoot).DriveType -ne [System.IO.DriveType]::Fixed) {
        throw "PreviewGateRoot must be on a fixed local drive."
    }
}
$root = if ($PreviewGateOnly -and -not [string]::IsNullOrWhiteSpace($PreviewGateRoot)) {
    [System.IO.Path]::GetFullPath($PreviewGateRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$startedAt = [DateTimeOffset]::UtcNow
$steps = [System.Collections.Generic.List[object]]::new()
$overallStatus = "running"
$failure = $null

function Resolve-InRoot([string]$Path) {
    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $root $Path))
    }
    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $root) {
        throw "Release-readiness paths must be inside the project root."
    }
    return $resolved
}

function Assert-ProductionNoPreview([string]$SourceRoot) {
    $previewPath = Join-Path $SourceRoot "WebUi/lib/runtime-preview.ts"
    $configPath = Join-Path $SourceRoot "WebUi/next.config.mjs"
    $bridgePath = Join-Path $SourceRoot "WebUi/components/runtime-bridge.tsx"
    $statusPath = Join-Path $SourceRoot "WebUi/components/shell/status-bar.tsx"
    foreach ($path in @($previewPath, $configPath, $bridgePath, $statusPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Static release gate failed: required no-preview source is missing." }
    }

    $preview = Get-Content -LiteralPath $previewPath -Raw
    $config = Get-Content -LiteralPath $configPath -Raw
    $bridge = Get-Content -LiteralPath $bridgePath -Raw
    $status = Get-Content -LiteralPath $statusPath -Raw
    if ($preview -notmatch 'browserPreviewEnabled\s*=\s*false' -or $preview -notmatch 'previewState\s*:\s*WpeRuntimeState\s*=\s*\{\s*\}') {
        throw "Static release gate failed: production browser preview must be disabled and empty."
    }
    if ($config -notmatch 'productionPreviewModule\s*=\s*[''"]\./lib/runtime-preview\.ts[''"]' -or
        $config -notmatch 'developmentPreviewModule\s*=\s*[''"]\./lib/runtime-preview\.development\.ts[''"]' -or
        $config -notmatch 'process\.env\.NODE_ENV\s*===\s*[''"]development[''"]' -or
        $config -notmatch ':\s*productionPreviewModule') {
        throw "Static release gate failed: preview alias must be development-only."
    }
    if ($bridge -notmatch 'previewMode\s*:\s*false' -or $bridge -match 'previewMode\s*:\s*true' -or
        $bridge -notmatch 'setState\(normalized\s*\?\s*\{\.\.\.normalized,hostConnected:true\}\s*:\s*unavailableRuntimeState') {
        throw "Static release gate failed: host runtime must replace prior state with preview disabled."
    }
    if (($preview + "`n" + $status) -match '(?i)PREVIEW\s+DATA|cached\s+fallback|fixture\s+data|fake\s+data') {
        throw "Static release gate failed: production source contains a preview or fallback marker."
    }

    $exportRoot = Join-Path $SourceRoot "WebUi/out"
    if (Test-Path -LiteralPath $exportRoot -PathType Container) {
        foreach ($htmlFile in Get-ChildItem -LiteralPath $exportRoot -Recurse -File -Filter "*.html") {
            $visible = (Get-Content -LiteralPath $htmlFile.FullName -Raw) -replace '(?is)<script\b[^>]*>.*?</script>', '' -replace '(?is)<style\b[^>]*>.*?</style>', ''
            if ($visible -match '(?i)>\s*(?:PREVIEW\s+DATA|fixture|fake|cached\s+fallback)\s*<') {
                throw "Static release gate failed: production export contains a visible preview or fallback marker."
            }
        }
    }
}

if ($PreviewGateOnly) {
    if ([string]::IsNullOrWhiteSpace($PreviewGateRoot)) { throw "PreviewGateRoot is required for PreviewGateOnly." }
    Assert-ProductionNoPreview $root
    Write-Output "Production no-preview gate passed."
    exit 0
}

$outputPath = Resolve-InRoot $Output
$headlessOutputPath = Resolve-InRoot $HeadlessOutput
$maintenanceOutputPath = Resolve-InRoot $MaintenanceOutput
$reportPath = Resolve-InRoot $ReportDirectory
$projectFiles = @(Get-ChildItem -LiteralPath $root -File -Filter "*.csproj")
if ($projectFiles.Count -ne 1) { throw "Expected exactly one application project file; found $($projectFiles.Count)." }
$projectFile = $projectFiles[0].FullName
$solutionFile = Join-Path $root "币安量化机器人.sln"
$headlessProject = Join-Path $root "WPE.Headless/WPE.Headless.csproj"
$maintenanceProject = Join-Path $root "WPE.Maintenance/WPE.Maintenance.csproj"
foreach ($requiredProject in @($solutionFile, $headlessProject, $maintenanceProject)) {
    if (-not (Test-Path -LiteralPath $requiredProject -PathType Leaf)) {
        throw "Release runtime bundle project is missing: $requiredProject"
    }
}
$webRoot = Join-Path $root "WebUi"
[xml]$projectXml = Get-Content -Raw -LiteralPath $projectFile
$productVersionNodes = @($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })
if ($productVersionNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$productVersionNodes[0])) {
    throw "The application project must define exactly one authoritative Version."
}
$productVersion = ([string]$productVersionNodes[0]).Trim()
$parsedProductVersion = [Version]$productVersion
$assemblyVersion = "$($parsedProductVersion.Major).$($parsedProductVersion.Minor).$($parsedProductVersion.Build).0"
$sourceCommit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "==> $Name" -ForegroundColor Cyan
    try {
        & $Action
        $watch.Stop()
        $steps.Add([ordered]@{ name = $Name; status = "passed"; durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3) })
    } catch {
        $watch.Stop()
        $steps.Add([ordered]@{ name = $Name; status = "failed"; durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3) })
        throw
    }
}


function Reset-ArtifactDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $resolved = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
        if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $root) {
            throw "Refusing to clean unsafe release artifact path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
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

function Get-ArtifactFacts([string]$Path) {
    $relativePath = $Path.Substring($rootPrefix.Length).Replace('\', '/')
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return [ordered]@{ relativePath = $relativePath; exists = $false; fileCount = 0; totalBytes = 0; treeSha256 = $null }
    }
    $prefix = $Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File | Sort-Object FullName)
    $lines = @($files | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relative|$($_.Length)|$hash"
    })
    return [ordered]@{
        relativePath = $relativePath
        exists = $true
        fileCount = $files.Count
        totalBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
        treeSha256 = Get-TextSha256 ($lines -join [Environment]::NewLine)
    }
}

function Assert-NoRuntimeStateOrSecrets([string]$Path, [string]$Label) {
    $forbiddenExtensions = @(".db", ".sqlite", ".sqlite3", ".pem", ".key", ".p12", ".pfx", ".env", ".pdb", ".cs", ".csproj", ".sln", ".ps1")
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
        throw "$Label artifact contains forbidden runtime, source, debug, or secret files: $($forbidden.FullName -join ', ')"
    }
}

function Assert-HeadlessArtifact {
    if (-not (Test-Path -LiteralPath $headlessOutputPath -PathType Container)) { throw "Headless artifact directory is missing." }
    foreach ($required in @("WPE-Headless.exe", "WPE-Headless.dll", "WPE-Headless.deps.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $headlessOutputPath $required) -PathType Leaf)) {
            throw "Headless artifact is missing $required."
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $headlessOutputPath "Resources/i18n/zh_CN.json") -PathType Leaf)) {
        throw "Headless localization resources are missing."
    }
    if (Test-Path -LiteralPath (Join-Path $headlessOutputPath "WebUi")) { throw "Desktop Web UI entered the headless artifact." }
    $deps = Get-Content -Raw -LiteralPath (Join-Path $headlessOutputPath "WPE-Headless.deps.json")
    foreach ($forbiddenDependency in @("Microsoft.WindowsDesktop.App", "Microsoft.Web.WebView2", "ScottPlot.WPF", "PresentationFramework")) {
        if ($deps.Contains($forbiddenDependency, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Headless artifact contains forbidden presentation dependency: $forbiddenDependency"
        }
    }
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $headlessOutputPath "WPE-Headless.exe")).FileVersion
    $parsed = [Version]$fileVersion
    if ("$($parsed.Major).$($parsed.Minor).$($parsed.Build)" -ne $productVersion) {
        throw "Headless binary version does not match product Version $productVersion."
    }
    Assert-NoRuntimeStateOrSecrets $headlessOutputPath "Headless"
}

function Assert-MaintenanceArtifact {
    if (-not (Test-Path -LiteralPath $maintenanceOutputPath -PathType Container)) { throw "Maintenance artifact directory is missing." }
    foreach ($required in @("WPE.Maintenance.exe", "WPE.Maintenance.dll", "WPE.Maintenance.deps.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $maintenanceOutputPath $required) -PathType Leaf)) {
            throw "Maintenance artifact is missing $required."
        }
    }
    if (Test-Path -LiteralPath (Join-Path $maintenanceOutputPath "WebUi")) { throw "Desktop Web UI entered the maintenance artifact." }
    $deps = Get-Content -Raw -LiteralPath (Join-Path $maintenanceOutputPath "WPE.Maintenance.deps.json")
    foreach ($forbiddenDependency in @("Microsoft.WindowsDesktop.App", "Microsoft.Web.WebView2", "ScottPlot.WPF", "PresentationFramework")) {
        if ($deps.Contains($forbiddenDependency, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Maintenance artifact contains forbidden presentation dependency: $forbiddenDependency"
        }
    }
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $maintenanceOutputPath "WPE.Maintenance.exe")).FileVersion
    $parsed = [Version]$fileVersion
    if ("$($parsed.Major).$($parsed.Minor).$($parsed.Build)" -ne $productVersion) {
        throw "Maintenance binary version does not match product Version $productVersion."
    }
    Assert-NoRuntimeStateOrSecrets $maintenanceOutputPath "Maintenance"
}

function Assert-FileContains([string]$RelativePath, [string]$Pattern, [string]$Rule) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or -not (Select-String -LiteralPath $path -Pattern $Pattern -Quiet)) {
        throw "Static release gate failed: $Rule."
    }
}

function Assert-SourceGates {
    Assert-FileContains "Services/AutoTradingAgent.cs" '!profile\.IsTestnet' "agent startup rejects Mainnet"
    Assert-FileContains "Services/AutoTradingAgent.cs" '!exchangeProfile\.IsTestnet' "agent runtime rejects Mainnet"
    Assert-FileContains "Services/Agent/EvidenceAndExecution.cs" 'Exchange mutation is restricted to Testnet' "executor mutations are Testnet-only"
    Assert-FileContains "Services/Agent/EvidenceAndExecution.cs" '_capabilityPrecondition\.Check' "execution capability gate is present"
    Assert-FileContains "Core/Contracts/ExchangeCapability.cs" 'capability unknown' "unknown exchange capability fails closed"
    Assert-FileContains "Services/Plugins/PluginManifestValidator.cs" 'Phase 0 exchange adapters must be Testnet-only and disabled by default' "unsupported plugin execution is disabled"
    Assert-ProductionNoPreview $root
    Assert-FileContains "WebUi/components/runtime-bridge.tsx" 'Unknown contracts fail closed' "Web runtime contracts fail closed"

    $tracked = @(git -C $root ls-files)
    $forbiddenTracked = @($tracked | Where-Object {
        $_ -match '(?i)(^|/)(API\.txt|agent-settings[^/]*\.json|appsettings\.(Local|Development)\.json|[^/]+\.(db|db-wal|db-shm|sqlite|sqlite3|log))$'
    })
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed." }
    if ($forbiddenTracked.Count -gt 0) { throw "Tracked secret/runtime artifact gate failed ($($forbiddenTracked.Count) prohibited path(s))." }

    $untrackedSources = @(git -C $root ls-files --others --exclude-standard -- '*.cs')
    if ($LASTEXITCODE -ne 0) { throw "git untracked source enumeration failed." }
    $productionSources = @(@($tracked) + @($untrackedSources) | Sort-Object -Unique | Where-Object {
        $_ -match '\.cs$' -and $_ -notmatch '^(WPE\.Tests|Tests)/' -and
        $_ -notmatch '(^|/)(AppDataPaths|AiForecastService)\.cs$'
    } | ForEach-Object { Join-Path $root $_ })
    $installWriteCandidates = @($productionSources | Select-String -Pattern 'AppContext\.BaseDirectory.{0,100}"(Data|data|Logs|logs|Configs)"|WriteTo\.File\("(logs|Logs)[\\/]' )
    if ($installWriteCandidates.Count -gt 0) { throw "Install-directory write gate failed ($($installWriteCandidates.Count) prohibited source line(s))." }
}

function Assert-PublishArtifact {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) { throw "Publish artifact directory is missing." }
    & (Join-Path $root "publish.ps1") -Configuration Release -Runtime $Runtime -Output $outputPath -ValidateOnly
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 validation failed." }
    $exe = @(Get-ChildItem -LiteralPath $outputPath -File -Filter "*.exe")
    if ($exe.Count -ne 1) { throw "Expected exactly one application executable in the publish artifact; found $($exe.Count)." }
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe[0].FullName).FileVersion
    $parsedFileVersion = [Version]$fileVersion
    $binaryProductVersion = "$($parsedFileVersion.Major).$($parsedFileVersion.Minor).$($parsedFileVersion.Build)"
    if ($binaryProductVersion -ne $productVersion) { throw "Published binary version $binaryProductVersion does not match project Version $productVersion." }
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath "WebUi/out/index.html") -PathType Leaf)) {
        throw "Published WebUi export is missing."
    }
}

if ($StaticGatesOnly) {
    Assert-SourceGates
    Write-Output "Release readiness static gates passed."
    exit 0
}

function Write-Reports {
    New-Item -ItemType Directory -Path $reportPath -Force | Out-Null
    $finishedAt = [DateTimeOffset]::UtcNow
    $files = if (Test-Path -LiteralPath $outputPath) { @(Get-ChildItem -LiteralPath $outputPath -Recurse -Force -File) } else { @() }
    $report = [ordered]@{
        schemaVersion = "wpe.release-readiness.v1"
        status = $overallStatus
        configuration = "Release"
        runtime = $Runtime
        productVersion = $productVersion
        startedAtUtc = $startedAt.ToString("O")
        finishedAtUtc = $finishedAt.ToString("O")
        durationSeconds = [Math]::Round(($finishedAt - $startedAt).TotalSeconds, 3)
        safety = [ordered]@{
            testnetOnly = $true
            mainnetEnabled = $false
            deploymentPerformed = $false
            uploadPerformed = $false
            artifactSecretScanPassed = ($overallStatus -eq "passed")
            productionPreviewDataPresent = $false
        }
        artifact = [ordered]@{
            relativePath = $outputPath.Substring($rootPrefix.Length).Replace('\', '/')
            fileCount = $files.Count
            totalBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
        }
        steps = @($steps)
        failure = $failure
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportPath "release-readiness.json") -Encoding UTF8

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# WPE Beta Release Readiness")
    $lines.Add("")
    $lines.Add("- Status: **$($overallStatus.ToUpperInvariant())**")
    $lines.Add("- Configuration/runtime: Release / $Runtime")
    $lines.Add("- Product version (project/assembly): $productVersion")
    $lines.Add("- Testnet-only: yes; Mainnet enabled: no")
    $lines.Add("- Deployment/upload performed: no")
    $lines.Add("- Artifact files: $($files.Count)")
    $lines.Add("")
    $lines.Add("## Checks")
    $lines.Add("")
    foreach ($step in $steps) { $lines.Add("- $($step.name): $($step.status) ($($step.durationSeconds)s)") }
    if ($failure) { $lines.Add(""); $lines.Add("Failure: a release gate failed. See console output; sensitive values are not written to this report.") }
    $lines | Set-Content -LiteralPath (Join-Path $reportPath "release-readiness.md") -Encoding UTF8
}

try {
    Invoke-Step "Static Testnet/Mainnet/secret/unsupported gates" { Assert-SourceGates }
    Push-Location $webRoot
    try {
        Invoke-Step "Web lint" { Invoke-External "pnpm" @("lint") }
        Invoke-Step "Web typecheck" { Invoke-External "pnpm" @("typecheck") }
        Invoke-Step "Web production build" {
            $webOutput = Join-Path $webRoot "out"
            if (Test-Path -LiteralPath $webOutput) {
                $resolvedWebOutput = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $webOutput).Path)
                $expectedWebOutput = [System.IO.Path]::GetFullPath((Join-Path $root "WebUi\out"))
                if (-not $resolvedWebOutput.Equals($expectedWebOutput, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "Refusing to clean unexpected WebUi output path."
                }
                Remove-Item -LiteralPath $resolvedWebOutput -Recurse -Force
            }
            Invoke-External "pnpm" @("build")
            Assert-ProductionNoPreview $root
        }
    } finally { Pop-Location }
    Invoke-Step ".NET tests" { & (Join-Path $root "eng/test.ps1") -Configuration Release; if ($LASTEXITCODE -ne 0) { throw ".NET tests failed." } }
    Invoke-Step ".NET Release build" { Invoke-External "dotnet" @("build", $projectFile, "--configuration", "Release", "--no-restore", "--nologo") }
    Invoke-Step "Release publish" { & (Join-Path $root "publish.ps1") -Configuration Release -Runtime $Runtime -Output $outputPath -SkipWebBuild; if ($LASTEXITCODE -ne 0) { throw "Release publish failed." } }
    Invoke-Step "Published artifact validation" { Assert-PublishArtifact }
    $overallStatus = "passed"
} catch {
    $overallStatus = "failed"
    $failure = [ordered]@{ category = "release-gate"; message = "A release-readiness gate failed; inspect console output." }
    Write-Error $_
} finally {
    Write-Reports
}

if ($overallStatus -ne "passed") { exit 1 }
Write-Host "Beta release-readiness passed. Reports: $reportPath" -ForegroundColor Green
) { throw "Unable to resolve release source commit." }
$sourceDirty = @(& git -C $root status --porcelain=v1 --untracked-files=normal).Count -gt 0

function Invoke-External([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "==> $Name" -ForegroundColor Cyan
    try {
        & $Action
        $watch.Stop()
        $steps.Add([ordered]@{ name = $Name; status = "passed"; durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3) })
    } catch {
        $watch.Stop()
        $steps.Add([ordered]@{ name = $Name; status = "failed"; durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3) })
        throw
    }
}

function Assert-FileContains([string]$RelativePath, [string]$Pattern, [string]$Rule) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or -not (Select-String -LiteralPath $path -Pattern $Pattern -Quiet)) {
        throw "Static release gate failed: $Rule."
    }
}

function Assert-SourceGates {
    Assert-FileContains "Services/AutoTradingAgent.cs" '!profile\.IsTestnet' "agent startup rejects Mainnet"
    Assert-FileContains "Services/AutoTradingAgent.cs" '!exchangeProfile\.IsTestnet' "agent runtime rejects Mainnet"
    Assert-FileContains "Services/Agent/EvidenceAndExecution.cs" 'Exchange mutation is restricted to Testnet' "executor mutations are Testnet-only"
    Assert-FileContains "Services/Agent/EvidenceAndExecution.cs" '_capabilityPrecondition\.Check' "execution capability gate is present"
    Assert-FileContains "Core/Contracts/ExchangeCapability.cs" 'capability unknown' "unknown exchange capability fails closed"
    Assert-FileContains "Services/Plugins/PluginManifestValidator.cs" 'Phase 0 exchange adapters must be Testnet-only and disabled by default' "unsupported plugin execution is disabled"
    Assert-ProductionNoPreview $root
    Assert-FileContains "WebUi/components/runtime-bridge.tsx" 'Unknown contracts fail closed' "Web runtime contracts fail closed"

    $tracked = @(git -C $root ls-files)
    $forbiddenTracked = @($tracked | Where-Object {
        $_ -match '(?i)(^|/)(API\.txt|agent-settings[^/]*\.json|appsettings\.(Local|Development)\.json|[^/]+\.(db|db-wal|db-shm|sqlite|sqlite3|log))$'
    })
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed." }
    if ($forbiddenTracked.Count -gt 0) { throw "Tracked secret/runtime artifact gate failed ($($forbiddenTracked.Count) prohibited path(s))." }

    $untrackedSources = @(git -C $root ls-files --others --exclude-standard -- '*.cs')
    if ($LASTEXITCODE -ne 0) { throw "git untracked source enumeration failed." }
    $productionSources = @(@($tracked) + @($untrackedSources) | Sort-Object -Unique | Where-Object {
        $_ -match '\.cs$' -and $_ -notmatch '^(WPE\.Tests|Tests)/' -and
        $_ -notmatch '(^|/)(AppDataPaths|AiForecastService)\.cs$'
    } | ForEach-Object { Join-Path $root $_ })
    $installWriteCandidates = @($productionSources | Select-String -Pattern 'AppContext\.BaseDirectory.{0,100}"(Data|data|Logs|logs|Configs)"|WriteTo\.File\("(logs|Logs)[\\/]' )
    if ($installWriteCandidates.Count -gt 0) { throw "Install-directory write gate failed ($($installWriteCandidates.Count) prohibited source line(s))." }
}

function Assert-PublishArtifact {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) { throw "Publish artifact directory is missing." }
    & (Join-Path $root "publish.ps1") -Configuration Release -Runtime $Runtime -Output $outputPath -ValidateOnly
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 validation failed." }
    $exe = @(Get-ChildItem -LiteralPath $outputPath -File -Filter "*.exe")
    if ($exe.Count -ne 1) { throw "Expected exactly one application executable in the publish artifact; found $($exe.Count)." }
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe[0].FullName).FileVersion
    $parsedFileVersion = [Version]$fileVersion
    $binaryProductVersion = "$($parsedFileVersion.Major).$($parsedFileVersion.Minor).$($parsedFileVersion.Build)"
    if ($binaryProductVersion -ne $productVersion) { throw "Published binary version $binaryProductVersion does not match project Version $productVersion." }
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath "WebUi/out/index.html") -PathType Leaf)) {
        throw "Published WebUi export is missing."
    }
}

if ($StaticGatesOnly) {
    Assert-SourceGates
    Write-Output "Release readiness static gates passed."
    exit 0
}

function Write-Reports {
    New-Item -ItemType Directory -Path $reportPath -Force | Out-Null
    $finishedAt = [DateTimeOffset]::UtcNow
    $files = if (Test-Path -LiteralPath $outputPath) { @(Get-ChildItem -LiteralPath $outputPath -Recurse -Force -File) } else { @() }
    $report = [ordered]@{
        schemaVersion = "wpe.release-readiness.v1"
        status = $overallStatus
        configuration = "Release"
        runtime = $Runtime
        productVersion = $productVersion
        startedAtUtc = $startedAt.ToString("O")
        finishedAtUtc = $finishedAt.ToString("O")
        durationSeconds = [Math]::Round(($finishedAt - $startedAt).TotalSeconds, 3)
        safety = [ordered]@{
            testnetOnly = $true
            mainnetEnabled = $false
            deploymentPerformed = $false
            uploadPerformed = $false
            artifactSecretScanPassed = ($overallStatus -eq "passed")
            productionPreviewDataPresent = $false
        }
        artifact = [ordered]@{
            relativePath = $outputPath.Substring($rootPrefix.Length).Replace('\', '/')
            fileCount = $files.Count
            totalBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
        }
        steps = @($steps)
        failure = $failure
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportPath "release-readiness.json") -Encoding UTF8

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# WPE Beta Release Readiness")
    $lines.Add("")
    $lines.Add("- Status: **$($overallStatus.ToUpperInvariant())**")
    $lines.Add("- Configuration/runtime: Release / $Runtime")
    $lines.Add("- Product version (project/assembly): $productVersion")
    $lines.Add("- Testnet-only: yes; Mainnet enabled: no")
    $lines.Add("- Deployment/upload performed: no")
    $lines.Add("- Artifact files: $($files.Count)")
    $lines.Add("")
    $lines.Add("## Checks")
    $lines.Add("")
    foreach ($step in $steps) { $lines.Add("- $($step.name): $($step.status) ($($step.durationSeconds)s)") }
    if ($failure) { $lines.Add(""); $lines.Add("Failure: a release gate failed. See console output; sensitive values are not written to this report.") }
    $lines | Set-Content -LiteralPath (Join-Path $reportPath "release-readiness.md") -Encoding UTF8
}

try {
    Invoke-Step "Static Testnet/Mainnet/secret/unsupported gates" { Assert-SourceGates }
    Push-Location $webRoot
    try {
        Invoke-Step "Web lint" { Invoke-External "pnpm" @("lint") }
        Invoke-Step "Web typecheck" { Invoke-External "pnpm" @("typecheck") }
        Invoke-Step "Web production build" {
            $webOutput = Join-Path $webRoot "out"
            if (Test-Path -LiteralPath $webOutput) {
                $resolvedWebOutput = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $webOutput).Path)
                $expectedWebOutput = [System.IO.Path]::GetFullPath((Join-Path $root "WebUi\out"))
                if (-not $resolvedWebOutput.Equals($expectedWebOutput, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "Refusing to clean unexpected WebUi output path."
                }
                Remove-Item -LiteralPath $resolvedWebOutput -Recurse -Force
            }
            Invoke-External "pnpm" @("build")
            Assert-ProductionNoPreview $root
        }
    } finally { Pop-Location }
    Invoke-Step ".NET tests" { & (Join-Path $root "eng/test.ps1") -Configuration Release; if ($LASTEXITCODE -ne 0) { throw ".NET tests failed." } }
    Invoke-Step ".NET Release build" { Invoke-External "dotnet" @("build", $projectFile, "--configuration", "Release", "--no-restore", "--nologo") }
    Invoke-Step "Release publish" { & (Join-Path $root "publish.ps1") -Configuration Release -Runtime $Runtime -Output $outputPath -SkipWebBuild; if ($LASTEXITCODE -ne 0) { throw "Release publish failed." } }
    Invoke-Step "Published artifact validation" { Assert-PublishArtifact }
    $overallStatus = "passed"
} catch {
    $overallStatus = "failed"
    $failure = [ordered]@{ category = "release-gate"; message = "A release-readiness gate failed; inspect console output." }
    Write-Error $_
} finally {
    Write-Reports
}

if ($overallStatus -ne "passed") { exit 1 }
Write-Host "Beta release-readiness passed. Reports: $reportPath" -ForegroundColor Green

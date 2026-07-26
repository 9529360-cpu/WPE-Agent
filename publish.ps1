param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Output = "publish",
    [switch]$ValidateOnly,
    [switch]$SkipWebBuild,
    [switch]$InternalUnsignedRc,
    [switch]$ValidateRuntimeRestoreOnly
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$projectFiles = @(Get-ChildItem -LiteralPath $projectRoot -File -Filter "*.csproj")
if ($projectFiles.Count -ne 1) {
    throw "Expected exactly one project file in $projectRoot; found $($projectFiles.Count)."
}
$projectFile = $projectFiles[0].FullName
$outputPath = if ([System.IO.Path]::IsPathRooted($Output)) {
    [System.IO.Path]::GetFullPath($Output)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
}
$rootPrefix = $projectRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $outputPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must be inside the project directory: $projectRoot"
}
if ($outputPath -eq $projectRoot) {
    throw "Publish output cannot be the project root."
}

$forbiddenNames = @(
    "agent-settings.json",
    "appsettings.json",
    "order-state.json",
    "api.txt"
)
$forbiddenExtensions = @(
    ".db", ".db-wal", ".db-shm",
    ".sqlite", ".sqlite-wal", ".sqlite-shm",
    ".sqlite3", ".sqlite3-wal", ".sqlite3-shm",
    ".log"
)
$sourceExtensions = @(".cs", ".csproj", ".sln", ".ps1", ".user", ".env", ".pdb", ".pem", ".key", ".pfx", ".snk")
$textExtensions = @(".json", ".config", ".xml", ".txt", ".ini", ".yaml", ".yml", ".js", ".html")
$secretPatterns = @(
    '(?i)"?(ApiKey|SecretKey|ApiSecret|EncryptedApiKey|EncryptedApiSecret|EncryptedKey)"?\s*[:=]\s*"?[^"\s,}]{8,}',
    '(?i)Authorization\s*[:=]\s*Bearer\s+[A-Za-z0-9._-]{8,}',
    '(?i)(api[_-]?key|api[_-]?secret)\s*[=:]\s*[A-Za-z0-9_./+\-=]{12,}'
)
$previewPatterns = @(
    '(?i)PREVIEW - TESTNET',
    'preview-10428',
    'preview-10429',
    '118420\.5',
    '116850',
    '(?i)illustrative governance metrics only'
)

function Assert-CleanPublishDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Publish directory does not exist: $Path"
    }

    $violations = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -LiteralPath $Path -Recurse -Force -File | ForEach-Object {
        $name = $_.Name.ToLowerInvariant()
        $extension = $_.Extension.ToLowerInvariant()
        $scanRoot = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $relative = $_.FullName.Substring($scanRoot.Length)
        if ($forbiddenNames -contains $name -or
            $name -like "agent-settings*.json" -or
            $name -like "appsettings*.json" -or
            $name -like "order-state*.json" -or
            $name -like "*api*.txt" -or
            $forbiddenExtensions -contains $extension -or
            $sourceExtensions -contains $extension -or
            $relative -match '(^|[\\/])(WPE\.Tests|Tests)([\\/]|$)' -or
            $name -match '^(?i:WPE\.Tests|testhost|xunit\.|Microsoft\.TestPlatform)') {
            $violations.Add($_.FullName)
            return
        }
        if ($textExtensions -contains $extension) {
            foreach ($pattern in $secretPatterns) {
                if (Select-String -LiteralPath $_.FullName -Pattern $pattern -Quiet) {
                    $violations.Add("$($_.FullName) (credential-like content)")
                    break
                }
            }
            foreach ($pattern in $previewPatterns) {
                if (Select-String -LiteralPath $_.FullName -Pattern $pattern -Quiet) {
                    $violations.Add("$($_.FullName) (browser preview fixture content)")
                    break
                }
            }
        }
    }

    Get-ChildItem -LiteralPath $Path -Recurse -Force -Directory |
        Where-Object { $_.Name -match '^(?i:logs?|WPE\.Tests|Tests)$' } |
        ForEach-Object { $violations.Add($_.FullName) }

    if ($violations.Count -gt 0) {
        throw "Sensitive or test files were found in the publish output:`n$($violations -join [Environment]::NewLine)"
    }
}

if ($ValidateOnly) {
    Assert-CleanPublishDirectory $outputPath
    Write-Host "Publish validation passed: $outputPath" -ForegroundColor Green
    exit 0
}

$webUiRoot = Join-Path $projectRoot "WebUi"
if (-not (Test-Path -LiteralPath (Join-Path $webUiRoot "package.json") -PathType Leaf)) {
    throw "WebUi package.json was not found: $webUiRoot"
}

if (-not $SkipWebBuild) {
    Write-Host "Building WebUi static export ..." -ForegroundColor Cyan
    $webOutput = Join-Path $webUiRoot "out"
    if (Test-Path -LiteralPath $webOutput) {
        $resolvedWebOutput = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $webOutput).Path)
        $expectedWebOutput = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "WebUi\out"))
        if (-not $resolvedWebOutput.Equals($expectedWebOutput, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected WebUi output path."
        }
        Remove-Item -LiteralPath $resolvedWebOutput -Recurse -Force
    }
    Push-Location $webUiRoot
    try {
        & pnpm build
        if ($LASTEXITCODE -ne 0) { throw "pnpm build failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $webUiRoot "out\index.html") -PathType Leaf)) {
    throw "WebUi static export is incomplete: WebUi/out/index.html was not produced."
}

Write-Host "Restoring exact runtime graph ($Runtime) ..." -ForegroundColor Cyan
& dotnet restore $projectFile --runtime $Runtime --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for runtime $Runtime with exit code $LASTEXITCODE"
}
$assetsPath = Join-Path $projectRoot "obj\project.assets.json"
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw "Runtime restore did not produce project.assets.json."
}
Add-Type -AssemblyName System.Web.Extensions
$serializer = [System.Web.Script.Serialization.JavaScriptSerializer]::new()
$serializer.MaxJsonLength = [int]::MaxValue
$assets = $serializer.DeserializeObject([System.IO.File]::ReadAllText($assetsPath))
$runtimeSuffix = "/$Runtime"
$runtimeTargets = @($assets['targets'].Keys | Where-Object {
    $_.StartsWith('net8.0-windows', [System.StringComparison]::Ordinal) -and
    $_.EndsWith($runtimeSuffix, [System.StringComparison]::Ordinal)
})
if ($runtimeTargets.Count -ne 1) {
    throw "Runtime restore must contain exactly one net8.0-windows target ending in ${runtimeSuffix}; found $($runtimeTargets.Count)."
}
$expectedTarget = $runtimeTargets[0]
if ($ValidateRuntimeRestoreOnly) {
    Write-Host "Runtime restore validation passed: $expectedTarget" -ForegroundColor Green
    exit 0
}

Write-Host "Publishing WPE Agent ($Configuration, $Runtime) to $outputPath ..." -ForegroundColor Cyan
if (Test-Path -LiteralPath $outputPath) {
    $resolvedOutput = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $outputPath).Path)
    if (-not $resolvedOutput.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or $resolvedOutput -eq $projectRoot) {
        throw "Refusing to clean unsafe publish path: $resolvedOutput"
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

& dotnet publish $projectFile -c $Configuration -r $Runtime -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false --self-contained false --no-restore -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Assert-CleanPublishDirectory $outputPath

if ($InternalUnsignedRc) {
    if ($Configuration -ne "Release") { throw "Internal unsigned RC staging requires Release configuration." }
    $projectXml = [xml](Get-Content -Raw -LiteralPath $projectFile)
    $productVersion = ([string](@($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0])).Trim()
    $sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Unable to resolve the source commit for RC metadata." }
    $sourceDirty = @(& git -C $projectRoot status --porcelain).Count -gt 0
    $executables = @(Get-ChildItem -LiteralPath $outputPath -File -Filter "*.exe")
    if ($executables.Count -ne 1) { throw "Internal unsigned RC requires exactly one top-level executable." }
    $signature = Get-AuthenticodeSignature -LiteralPath $executables[0].FullName
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "Internal unsigned RC mode cannot label a signed executable as unsigned."
    }

    $metadata = [ordered]@{
        schemaVersion = "wpe.internal-unsigned-rc.v1"
        status = "internal-evaluation-only"
        productVersion = $productVersion
        configuration = $Configuration
        runtime = $Runtime
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        signingStatus = "unsigned"
        distributable = $false
        commercialReleaseApproved = $false
        networkValidationPerformed = $false
        liveTradingPerformed = $false
        manifest = "FILE-MANIFEST.json"
    }
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath "INTERNAL-RC-METADATA.json") -Encoding UTF8

    $manifest = @(Get-ChildItem -LiteralPath $outputPath -Recurse -Force -File |
        Where-Object { $_.Name -ne "FILE-MANIFEST.json" } |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($outputPath.Length).TrimStart('\', '/').Replace('\', '/')
                bytes = [long]$_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath "FILE-MANIFEST.json") -Encoding UTF8
    Assert-CleanPublishDirectory $outputPath
    Write-Host "Internal unsigned RC metadata and manifest generated. This staging output is non-distributable." -ForegroundColor Yellow
}
Write-Host "Publish completed and sensitive-file validation passed: $outputPath" -ForegroundColor Green

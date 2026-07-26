param(
    [string]$WebRoot = "WebUi"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$web = if ([System.IO.Path]::IsPathRooted($WebRoot)) {
    [System.IO.Path]::GetFullPath($WebRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $root $WebRoot))
}
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $web.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $web -PathType Container)) {
    throw "Web root must be an existing directory inside the project root."
}

function Get-ProjectRelativePath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Scanned path escaped the project root." }
    return $full.Substring($rootPrefix.Length).Replace('\', '/')
}

$sourceRoots = @("app", "components", "hooks", "lib") | ForEach-Object { Join-Path $web $_ } | Where-Object { Test-Path -LiteralPath $_ }
$sourceFiles = @($sourceRoots | ForEach-Object {
    Get-ChildItem -LiteralPath $_ -Recurse -File | Where-Object { $_.Extension -in @(".js", ".jsx", ".mjs", ".ts", ".tsx") }
})
$violations = [System.Collections.Generic.List[string]]::new()
$patterns = [ordered]@{
    "remote analytics" = '(?i)@vercel/analytics|\b(?:analytics|gtag)\s*\('
    "browser request" = '(?i)\bfetch\s*\(|\bXMLHttpRequest\s*\(|\bsendBeacon\s*\('
    "browser socket" = '(?i)\bnew\s+(?:WebSocket|EventSource)\s*\('
    "absolute network URL" = '(?i)(?:https?:|wss?:)//'
    "remote script element" = '(?i)<script\b[^>]*\bsrc\s*='
    "dynamic script element" = '(?i)\bcreateElement\s*\(\s*["'']script["'']|\bnext/script\b'
}
foreach ($file in $sourceFiles) {
    $lines = Get-Content -LiteralPath $file.FullName
    for ($index = 0; $index -lt $lines.Count; $index++) {
        foreach ($entry in $patterns.GetEnumerator()) {
            if ($lines[$index] -match $entry.Value) {
                $relative = Get-ProjectRelativePath $file.FullName
                $violations.Add("${relative}:$($index + 1): $($entry.Key)")
            }
        }
    }
}

$packageFiles = @("package.json", "pnpm-lock.yaml") | ForEach-Object { Join-Path $web $_ } | Where-Object { Test-Path -LiteralPath $_ }
foreach ($file in $packageFiles) {
    if ((Get-Content -Raw -LiteralPath $file) -match '(?i)@vercel/analytics') {
        $violations.Add("$(Get-ProjectRelativePath $file): remote analytics dependency")
    }
}

$outRoot = Join-Path $web "out"
if (Test-Path -LiteralPath $outRoot -PathType Container) {
    foreach ($file in @(Get-ChildItem -LiteralPath $outRoot -Recurse -File | Where-Object { $_.Extension -in @(".html", ".js") })) {
        $content = Get-Content -Raw -LiteralPath $file.FullName
        $hasForbiddenTelemetry = $content -match '(?i)@vercel/analytics|/_vercel/insights|va\.vercel-scripts\.com'
        $hasExternalHtmlResource = $file.Extension -eq ".html" -and $content -match '(?i)<(?:script|link|iframe|img)\b[^>]*(?:src|href)\s*=\s*["''](?:https?:)?//'
        if ($hasForbiddenTelemetry -or $hasExternalHtmlResource) {
            $violations.Add("$(Get-ProjectRelativePath $file.FullName): built output contains remote telemetry or an external resource")
        }
    }
}

if ($violations.Count -gt 0) {
    $violations | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Web egress gate failed closed with $($violations.Count) finding(s)."
}

[ordered]@{
    schemaVersion = "wpe.web-egress-gate.v1"
    status = "passed"
    scannedSourceFiles = $sourceFiles.Count
    remoteRuntimeEndpoints = 0
    hostBridge = "chrome.webview only; no browser network API"
} | ConvertTo-Json

$ErrorActionPreference = 'Stop'
$releaseScript = Join-Path $PSScriptRoot 'release-readiness.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('wpe-preview-gate-' + [guid]::NewGuid().ToString('N'))

function Set-Text([string]$RelativePath, [string]$Content) {
    $path = Join-Path $temp $RelativePath
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force
    Set-Content -LiteralPath $path -Value $Content -Encoding UTF8
}
function Reset-Fixture {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
    Set-Text 'WebUi/lib/runtime-preview.ts' "import type { WpeRuntimeState } from '@/components/runtime-bridge'`nexport const browserPreviewEnabled = false`nexport const previewState: WpeRuntimeState = {}"
    Set-Text 'WebUi/next.config.mjs' "const productionPreviewModule = './lib/runtime-preview.ts'`nconst developmentPreviewModule = './lib/runtime-preview.development.ts'`nconst alias = process.env.NODE_ENV === 'development' ? developmentPreviewModule : productionPreviewModule"
    Set-Text 'WebUi/components/runtime-bridge.tsx' "const normalized = normalizeRuntimeEvent(detail)`nconst state = { previewMode: false }`nsetState(normalized ? {...normalized,hostConnected:true} : unavailableRuntimeState(true,'unsupported'))"
    Set-Text 'WebUi/components/shell/status-bar.tsx' "export function StatusBar(){return <footer>runtime unavailable</footer>}"
    Set-Text 'WebUi/out/index.html' '<html><body><main>runtime unavailable</main></body></html>'
}
function Invoke-Gate {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseScript -PreviewGateOnly -PreviewGateRoot $temp 2>&1
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    }
    finally { $ErrorActionPreference = $previousPreference }
}
function Assert-Pass([string]$Name) {
    $result = Invoke-Gate
    if ($result.ExitCode -ne 0) { throw "$Name expected pass: $($result.Output)" }
}
function Assert-Reject([string]$Name) {
    $result = Invoke-Gate
    if ($result.ExitCode -eq 0) { throw "$Name expected rejection" }
}

try {
    $source = Get-Content -LiteralPath $releaseScript -Raw
    if ($source -match 'Assert-FileContains\s+"WebUi/components/shell/status-bar\.tsx"\s+''PREVIEW DATA''') { throw 'stale PREVIEW DATA presence assertion remains' }
    $ErrorActionPreference = 'Continue'
    try {
        $uncOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseScript -PreviewGateOnly -PreviewGateRoot '\\server\share\preview' 2>&1
        if ($LASTEXITCODE -eq 0) { throw 'UNC preview gate root unexpectedly accepted' }
    }
    finally { $ErrorActionPreference = 'Stop' }

    Reset-Fixture; Assert-Pass 'canonical production no-preview'
    Reset-Fixture; Set-Text 'WebUi/lib/runtime-preview.ts' 'export const browserPreviewEnabled = true; export const previewState = {}'; Assert-Reject 'preview re-enabled'
    Reset-Fixture; Set-Text 'WebUi/lib/runtime-preview.ts' 'export const browserPreviewEnabled = false; export const previewState: WpeRuntimeState = {previewMode:true}'; Assert-Reject 'preview fallback state'
    Reset-Fixture; Set-Text 'WebUi/next.config.mjs' "const productionPreviewModule='./lib/runtime-preview.development.ts'; const developmentPreviewModule='./lib/runtime-preview.development.ts'; const alias=process.env.NODE_ENV === 'development'?developmentPreviewModule:productionPreviewModule"; Assert-Reject 'development alias in production'
    Reset-Fixture; Set-Text 'WebUi/components/runtime-bridge.tsx' "const state={previewMode:true}; setState(previous=>normalized?{...previous,...normalized}:previous)"; Assert-Reject 'preview mode and prior-state merge'
    Reset-Fixture; Set-Text 'WebUi/components/shell/status-bar.tsx' '<footer>PREVIEW DATA</footer>'; Assert-Reject 'status preview marker'
    Reset-Fixture; Set-Text 'WebUi/out/index.html' '<html><body><main>cached fallback</main></body></html>'; Assert-Reject 'visible export fallback'
    Reset-Fixture; Set-Text 'WebUi/out/index.html' '<html><body><main>runtime unavailable</main><script>const text="fixture"</script></body></html>'; Assert-Pass 'non-visible bundle text ignored'

    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($releaseScript, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw "release script AST errors: $($errors.Count)" }
    Write-Output 'ReleaseReadinessPreviewGate.Tests: PASS (10 groups)'
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

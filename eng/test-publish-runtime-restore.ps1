$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = Join-Path $root 'publish.ps1'
$source = Get-Content -LiteralPath $publish -Raw

function Assert-True([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
}

$restoreIndex = $source.IndexOf('& dotnet restore $projectFile --runtime $Runtime --nologo', [StringComparison]::Ordinal)
$publishIndex = $source.IndexOf('& dotnet publish $projectFile', [StringComparison]::Ordinal)
Assert-True ($source -match '\[ValidateSet\("win-x64", "win-arm64"\)\]') 'runtime allowlist missing'
Assert-True ($restoreIndex -ge 0) 'exact RID restore command missing'
Assert-True ($publishIndex -gt $restoreIndex) 'RID restore must precede no-restore publish'
Assert-True ($source -match 'System\.Web\.Script\.Serialization\.JavaScriptSerializer') 'PowerShell 5.1-safe structured assets parser missing'
Assert-True ($source -match '\$runtimeTargets\.Count\s+-ne\s+1') 'unique restored RID target verification missing'
Assert-True ($source -match "StartsWith\('net8\.0-windows'") 'Windows TFM target restriction missing'
Assert-True ($source -match 'EndsWith\(\$runtimeSuffix') 'exact runtime suffix verification missing'
Assert-True ($source -match 'dotnet publish[^\r\n]+--no-restore') 'publish must remain no-restore'

$ErrorActionPreference = 'Continue'
try {
    $invalidOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publish -Runtime linux-x64 -ValidateRuntimeRestoreOnly 2>&1
    $invalidExit = $LASTEXITCODE
}
finally { $ErrorActionPreference = 'Stop' }
Assert-True ($invalidExit -ne 0) 'unexpected runtime accepted'

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publish -Runtime win-x64 -SkipWebBuild -ValidateRuntimeRestoreOnly
Assert-True ($LASTEXITCODE -eq 0) 'win-x64 exact restore validation failed'

$tokens = $null; $errors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($publish, [ref]$tokens, [ref]$errors)
Assert-True ($errors.Count -eq 0) 'publish AST errors'
Write-Output 'PublishRuntimeRestore.Tests: PASS (11 groups)'

$ErrorActionPreference = 'Stop'

$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
$invalid = [System.Collections.Generic.List[string]]::new()

$files = git ls-files -- '*.cs'
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate tracked C# source files.'
}

foreach ($path in $files) {
    if ([string]::IsNullOrWhiteSpace($path)) { continue }

    try {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        [void]$strictUtf8.GetString($bytes)
    }
    catch [System.Text.DecoderFallbackException] {
        $invalid.Add($path)
    }
}

if ($invalid.Count -gt 0) {
    Write-Error ("Tracked C# files must be valid UTF-8. Invalid files:`n - " + ($invalid -join "`n - "))
    exit 1
}

Write-Host "UTF-8 source gate passed for $($files.Count) tracked C# files."

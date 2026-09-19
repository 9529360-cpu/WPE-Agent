param(
    [Parameter(Mandatory = $true)]
    [string]$PublishPath,
    [string[]]$AdditionalPublishPaths = @(),
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedSubject,
    [string]$TimestampUrl = "https://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Resolve-InRoot([string]$Path) {
    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) { [System.IO.Path]::GetFullPath($Path) } else { [System.IO.Path]::GetFullPath((Join-Path $root $Path)) }
    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $root) { throw "Signing staging must be inside the project root." }
    return $resolved
}

$signingRoots = @($PublishPath) + @($AdditionalPublishPaths) | ForEach-Object { Resolve-InRoot $_ } | Select-Object -Unique
if ($signingRoots.Count -eq 0) { throw "At least one signing staging directory is required." }
foreach ($signingRoot in $signingRoots) {
    if (-not (Test-Path -LiteralPath $signingRoot -PathType Container)) {
        throw "Signing staging directory is missing: $signingRoot"
    }
}

$signToolCandidates = @(Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue | Sort-Object @{ Expression = { if ($_.FullName -match '[\\/]x64[\\/]signtool\.exe$') { 0 } else { 1 } } }, @{ Expression = { $_.FullName }; Descending = $true })
if ($signToolCandidates.Count -eq 0) { throw "SignTool was not found. Install a trusted Windows SDK before signing." }
$signTool = $signToolCandidates[0].FullName

$certificatePath = "Cert:\CurrentUser\My\$($CertificateThumbprint.ToUpperInvariant())"
if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) { throw "The requested CurrentUser/My code-signing certificate was not found." }
$certificate = Get-Item -LiteralPath $certificatePath
$now = Get-Date
if (-not $certificate.HasPrivateKey) { throw "The code-signing certificate has no accessible private key." }
if ($certificate.Subject -ne $ExpectedSubject) { throw "The certificate subject does not match the approved legal publisher." }
if ($certificate.Subject -eq $certificate.Issuer) { throw "Self-signed certificates are prohibited for commercial Beta signing." }
if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) { throw "The code-signing certificate is not currently valid." }
$codeSigningOid = "1.3.6.1.5.5.7.3.3"
$eku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.37" } | ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_ })
if (-not ($eku | Where-Object { $_.Value -eq $codeSigningOid })) { throw "The certificate does not include the Code Signing EKU." }

$executables = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
foreach ($signingRoot in $signingRoots) {
    $rootExecutables = @(Get-ChildItem -LiteralPath $signingRoot -File -Filter "*.exe")
    if ($rootExecutables.Count -ne 1) {
        throw "Expected exactly one top-level executable in signing staging: $signingRoot"
    }
    $executables.Add($rootExecutables[0])
}
if (@($executables | Select-Object -ExpandProperty FullName -Unique).Count -ne $executables.Count) {
    throw "Signing staging resolved the same executable more than once."
}

foreach ($executable in $executables) {
    & $signTool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $certificate.Thumbprint /s My $executable.FullName
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $($executable.Name)." }
}
foreach ($executable in $executables) {
    & $signTool verify /pa /all /v $executable.FullName
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $($executable.Name)." }
    $signature = Get-AuthenticodeSignature -LiteralPath $executable.FullName
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "PowerShell did not validate the resulting Authenticode signature for $($executable.Name)."
    }
}
Write-Host "Signing and verification passed for $($executables.Count) runtime executable(s)." -ForegroundColor Green


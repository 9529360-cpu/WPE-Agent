param(
    [Parameter(Mandatory = $true)]
    [string]$PublishPath,
    [string[]]$AdditionalPublishPaths = @(),
    [string]$ReadinessReportPath,
    [string]$SigningResultPath,
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
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relative|$($_.Length)|$hash"
    })
    [pscustomobject]@{
        FileCount = $files.Count
        TotalBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
        TreeSha256 = Get-TextSha256 ($lines -join [Environment]::NewLine)
    }
}

function Write-AndVerifyDetachedAttestation(
    [string]$ContentPath,
    [string]$SignaturePath,
    [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $contentBytes = [System.IO.File]::ReadAllBytes($ContentPath)
    $contentInfo = [System.Security.Cryptography.Pkcs.ContentInfo]::new($contentBytes)
    $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $true)
    $signer = [System.Security.Cryptography.Pkcs.CmsSigner]::new($Certificate)
    $signer.IncludeOption = [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $signer.DigestAlgorithm = [System.Security.Cryptography.Oid]::new("2.16.840.1.101.3.4.2.1")
    $cms.ComputeSignature($signer)

    $signatureDirectory = Split-Path -Parent $SignaturePath
    New-Item -ItemType Directory -Path $signatureDirectory -Force | Out-Null
    $temporarySignature = Join-Path $signatureDirectory ('.signing-attestation.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [System.IO.File]::WriteAllBytes($temporarySignature, $cms.Encode())
    Move-Item -LiteralPath $temporarySignature -Destination $SignaturePath -Force

    $verification = [System.Security.Cryptography.Pkcs.SignedCms]::new(
        [System.Security.Cryptography.Pkcs.ContentInfo]::new($contentBytes),
        $true)
    try {
        $verification.Decode([System.IO.File]::ReadAllBytes($SignaturePath))
        $verification.CheckSignature($true)
    } catch {
        throw "Runtime bundle signing attestation verification failed."
    }
    if ($verification.SignerInfos.Count -ne 1) {
        throw "Runtime bundle signing attestation must contain exactly one signer."
    }
    if ($verification.SignerInfos[0].DigestAlgorithm.Value -ne "2.16.840.1.101.3.4.2.1") {
        throw "Runtime bundle signing attestation must use SHA-256."
    }
    $attestationCertificate = $verification.SignerInfos[0].Certificate
    if ($null -eq $attestationCertificate -or
        $attestationCertificate.Subject -ne $Certificate.Subject -or
        $attestationCertificate.Thumbprint.ToUpperInvariant() -ne $Certificate.Thumbprint.ToUpperInvariant()) {
        throw "Runtime bundle signing attestation identity mismatch."
    }
}

$signingRoots = @($PublishPath) + @($AdditionalPublishPaths) | ForEach-Object { Resolve-InRoot $_ } | Select-Object -Unique
if ($signingRoots.Count -eq 0) { throw "At least one signing staging directory is required." }
$bundleAttestationRequested = -not [string]::IsNullOrWhiteSpace($ReadinessReportPath) -or -not [string]::IsNullOrWhiteSpace($SigningResultPath)
if ($bundleAttestationRequested -and ([string]::IsNullOrWhiteSpace($ReadinessReportPath) -or [string]::IsNullOrWhiteSpace($SigningResultPath))) {
    throw "ReadinessReportPath and SigningResultPath must be supplied together."
}
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

$preSignFacts = @{}
$readiness = $null
$readinessHash = $null
$resultPath = $null
if ($bundleAttestationRequested) {
    $readinessPath = Resolve-InRoot $ReadinessReportPath
    $resultPath = Resolve-InRoot $SigningResultPath
    if (-not (Test-Path -LiteralPath $readinessPath -PathType Leaf)) { throw "Release-readiness report is missing." }
    try { $readiness = Get-Content -Raw -LiteralPath $readinessPath | ConvertFrom-Json } catch { throw "Release-readiness report is malformed." }
    if ($readiness.schemaVersion -ne "wpe.release-readiness.v1" -or $readiness.status -ne "passed") { throw "Release-readiness report did not pass." }
    if ($readiness.source.dirty -ne $false -or [string]$readiness.source.commit -notmatch '^[0-9a-f]{40}$') { throw "Release-readiness source identity is not signable." }
    if (-not $readiness.artifacts -or -not $readiness.artifacts.desktop -or -not $readiness.artifacts.headless -or -not $readiness.artifacts.maintenance) {
        throw "Release-readiness runtime bundle facts are incomplete."
    }
    if ($executables.Count -ne 3) { throw "Runtime-bundle attestation requires Desktop, Headless, and Maintenance staging." }
    $readinessHash = (Get-FileHash -LiteralPath $readinessPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedByExe = @{
        "WPE-Agent.exe" = [pscustomobject]@{ Label = "desktop"; Facts = $readiness.artifacts.desktop }
        "WPE-Headless.exe" = [pscustomobject]@{ Label = "headless"; Facts = $readiness.artifacts.headless }
        "WPE.Maintenance.exe" = [pscustomobject]@{ Label = "maintenance"; Facts = $readiness.artifacts.maintenance }
    }
    foreach ($executable in $executables) {
        if (-not $expectedByExe.ContainsKey($executable.Name)) { throw "Unexpected runtime executable in bundle signing: $($executable.Name)" }
        $expected = $expectedByExe[$executable.Name]
        $facts = Get-ArtifactTreeFacts $executable.Directory.FullName
        if ($facts.TreeSha256 -ne [string]$expected.Facts.treeSha256 -or $facts.FileCount -ne [int]$expected.Facts.fileCount) {
            throw "Signing staging does not match release-readiness artifact: $($expected.Label)"
        }
        $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable.FullName).FileVersion
        $parsedVersion = [Version]$fileVersion
        if ("$($parsedVersion.Major).$($parsedVersion.Minor).$($parsedVersion.Build)" -ne [string]$readiness.productVersion) {
            throw "Signing staging binary version does not match release-readiness product version: $($executable.Name)"
        }
        $preSignFacts[$executable.FullName] = [pscustomobject]@{ Label = $expected.Label; Facts = $facts }
    }
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
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "Authenticode timestamp is missing for $($executable.Name)."
    }
}

if ($bundleAttestationRequested) {
    $signedArtifacts = @($executables | ForEach-Object {
        $before = $preSignFacts[$_.FullName]
        $after = Get-ArtifactTreeFacts $_.Directory.FullName
        [ordered]@{
            label = $before.Label
            executable = $_.Name
            inputTreeSha256 = $before.Facts.TreeSha256
            outputTreeSha256 = $after.TreeSha256
            outputFileCount = $after.FileCount
            executableSha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            signatureStatus = "Valid"
        }
    } | Sort-Object label)
    $result = [ordered]@{
        schemaVersion = "wpe.runtime-bundle-signing/1.1"
        signedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        sourceCommit = [string]$readiness.source.commit
        productVersion = [string]$readiness.productVersion
        readinessReportSha256 = $readinessHash
        publisherSubject = $certificate.Subject
        certificateThumbprint = $certificate.Thumbprint.ToUpperInvariant()
        timestampUrl = $TimestampUrl
        attestationFormat = "cms-detached-sha256"
        artifacts = $signedArtifacts
    }
    $resultDirectory = Split-Path -Parent $resultPath
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $temporaryResult = Join-Path $resultDirectory ('.signing-result.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryResult -Encoding utf8
    Move-Item -LiteralPath $temporaryResult -Destination $resultPath -Force
    $attestationPath = [System.IO.Path]::ChangeExtension($resultPath, "p7s")
    Write-AndVerifyDetachedAttestation -ContentPath $resultPath -SignaturePath $attestationPath -Certificate $certificate
}
Write-Host "Signing and verification passed for $($executables.Count) runtime executable(s)." -ForegroundColor Green


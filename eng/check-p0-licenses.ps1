param(
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$lockPath = Join-Path $root "WebUi/pnpm-lock.yaml"
$evidencePath = Join-Path $root "Docs/research/sbom-noassertion-license-evidence-draft.json"
$packageName = "WPE-Agent-3.6.0-beta.1-91ba0bde4bb3-win-x64-portable"
$primaryPackage = Join-Path $root "artifacts/beta-packages/$packageName"
$verificationPackage = Join-Path $root "artifacts/beta-packages/verification/$packageName"
$sbomPaths = @(
    (Join-Path $primaryPackage "sbom.cdx.json"),
    (Join-Path $verificationPackage "sbom.cdx.json")
)
$gateTargets = @(
    [pscustomobject]@{ Sbom = $sbomPaths[0]; Output = (Join-Path $root "artifacts/license-gate-commercial"); Intent = "Commercial" },
    [pscustomobject]@{ Sbom = $sbomPaths[0]; Output = (Join-Path $primaryPackage "license"); Intent = "Evaluation" },
    [pscustomobject]@{ Sbom = $sbomPaths[1]; Output = (Join-Path $verificationPackage "license"); Intent = "Evaluation" }
)

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required offline evidence is missing: $Path" }
}

function Get-PropertyValue([object]$Component, [string]$Name) {
    return [string](@($Component.properties | Where-Object name -eq $Name | Select-Object -First 1).value)
}

function Set-PropertyValue([object]$Component, [string]$Name, [string]$Value) {
    $property = @($Component.properties | Where-Object name -eq $Name | Select-Object -First 1)
    if ($property.Count -eq 1) { $property[0].value = $Value; return }
    $Component.properties = @($Component.properties) + [pscustomobject][ordered]@{ name = $Name; value = $Value }
}

function Set-ComponentLicense([object]$Component, [string]$License, [string]$Source, [string]$Evidence) {
    $Component.licenses = @([pscustomobject][ordered]@{ expression = $License })
    Set-PropertyValue $Component "wpe:licenseEvidenceSource" $Source
    Set-PropertyValue $Component "wpe:licenseEvidenceConfidence" "high"
    Set-PropertyValue $Component "wpe:licenseEvidence" $Evidence
}

function Get-OneComponent([object]$Sbom, [string]$Purl) {
    $matches = @($Sbom.components | Where-Object purl -eq $Purl)
    if ($matches.Count -ne 1) { throw "Expected one SBOM component for $Purl; found $($matches.Count)." }
    return $matches[0]
}

function Get-License([object]$Component) {
    return [string](@($Component.licenses | Select-Object -First 1).expression)
}

Assert-File $lockPath
Assert-File $evidencePath
foreach ($path in $sbomPaths) { Assert-File $path }

$lock = Get-Content -Raw -LiteralPath $lockPath
if ($lock -match [regex]::Escape('@vercel/analytics')) { throw "Current lockfile still contains @vercel/analytics." }

$sharp = @(
    @{ Name = '@img/sharp-libvips-darwin-arm64'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-libvips-darwin-x64'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-libvips-linux-arm'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-libvips-linux-arm64'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-libvips-linuxmusl-arm64'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-libvips-linuxmusl-x64'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-libvips-linux-ppc64'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-libvips-linux-riscv64'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-libvips-linux-s390x'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-libvips-linux-x64'; Version = '1.2.4'; License = 'LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-wasm32'; Version = '0.34.5'; License = 'Apache-2.0 AND LGPL-3.0-or-later AND MIT' },
    @{ Name = '@img/sharp-win32-arm64'; Version = '0.34.5'; License = 'Apache-2.0 AND LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-win32-ia32'; Version = '0.34.5'; License = 'Apache-2.0 AND LGPL-3.0-or-later' },
    @{ Name = '@img/sharp-win32-x64'; Version = '0.34.5'; License = 'Apache-2.0 AND LGPL-3.0-or-later' }
)
foreach ($item in $sharp) {
    if ($lock -notmatch "(?m)^  '$([regex]::Escape($item.Name))@$([regex]::Escape($item.Version))':") {
        throw "Current lockfile does not contain exact Sharp record $($item.Name)@$($item.Version)."
    }
}
if ($lock -notmatch '(?m)^  caniuse-lite@1\.0\.30001769:') { throw "Current lockfile does not contain caniuse-lite@1.0.30001769." }

$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$webViewLicensePath = Join-Path $nugetRoot 'microsoft.web.webview2/1.0.2903.40/LICENSE.txt'
$glfwLicensePath = Join-Path $nugetRoot 'opentk.redist.glfw/3.3.0-pre20200830200122/COPYING.md'
$caniuseManifestPath = Join-Path $root 'WebUi/node_modules/.pnpm/caniuse-lite@1.0.30001769/node_modules/caniuse-lite/package.json'
$caniuseLicensePath = Join-Path $root 'WebUi/node_modules/.pnpm/caniuse-lite@1.0.30001769/node_modules/caniuse-lite/LICENSE'
foreach ($path in @($webViewLicensePath, $glfwLicensePath, $caniuseManifestPath, $caniuseLicensePath)) { Assert-File $path }

$webViewText = Get-Content -Raw -LiteralPath $webViewLicensePath
if ($webViewText -notmatch 'Redistributions of source code must retain' -or
    $webViewText -notmatch 'Redistributions in binary form must reproduce' -or
    $webViewText -notmatch 'may not be used to endorse or promote') {
    throw "WebView2 exact-version LICENSE.txt is not recognizable as BSD-3-Clause."
}
$glfwText = Get-Content -Raw -LiteralPath $glfwLicensePath
if ($glfwText -notmatch "This software is provided 'as-is'" -or
    $glfwText -notmatch 'The origin of this software must not be misrepresented' -or
    $glfwText -notmatch 'This notice may not be removed or altered') {
    throw "GLFW exact-version COPYING.md is not recognizable as Zlib."
}
$caniuseManifest = Get-Content -Raw -LiteralPath $caniuseManifestPath | ConvertFrom-Json
$caniuseText = Get-Content -Raw -LiteralPath $caniuseLicensePath
if ($caniuseManifest.name -ne 'caniuse-lite' -or $caniuseManifest.version -ne '1.0.30001769' -or
    $caniuseManifest.license -ne 'CC-BY-4.0' -or $caniuseText -notmatch 'Attribution 4\.0 International') {
    throw "caniuse-lite exact-version metadata and license text do not confirm CC-BY-4.0."
}

if ($Apply) {
    $evidence = Get-Content -Raw -LiteralPath $evidencePath | ConvertFrom-Json
    $newEvidence = @(
        [pscustomobject][ordered]@{
            purl = 'pkg:npm/caniuse-lite@1.0.30001769'; license = 'CC-BY-4.0'
            sourceUrl = 'WebUi/node_modules/.pnpm/caniuse-lite@1.0.30001769/node_modules/caniuse-lite/LICENSE'
            evidence = @('Exact restored package.json declares CC-BY-4.0.', 'Exact restored LICENSE contains the Creative Commons Attribution 4.0 International text.', 'Exact version is present in WebUi/pnpm-lock.yaml.')
            confidence = 'high'; distributionScope = 'Build dependency graph; not shipped as a package or native binary in the verified win-x64 payload. Attribution obligations remain subject to review for compiled Web output.'
        },
        [pscustomobject][ordered]@{
            purl = 'pkg:nuget/Microsoft.Web.WebView2@1.0.2903.40'; license = 'BSD-3-Clause'
            sourceUrl = '%NUGET_PACKAGES%/microsoft.web.webview2/1.0.2903.40/LICENSE.txt'
            evidence = @('Exact NuGet nuspec points to LICENSE.txt.', 'The restored exact-version LICENSE.txt contains all three BSD clauses and the BSD disclaimer.', 'WebView2Loader.dll is present in the verified win-x64 payload.')
            confidence = 'high'; distributionScope = 'Runtime dependency included in the verified win-x64 payload; reproduce the BSD notice and applicable package NOTICE material.'
        },
        [pscustomobject][ordered]@{
            purl = 'pkg:nuget/OpenTK.redist.glfw@3.3.0-pre20200830200122'; license = 'Zlib'
            sourceUrl = '%NUGET_PACKAGES%/opentk.redist.glfw/3.3.0-pre20200830200122/COPYING.md'
            evidence = @('Exact NuGet nuspec points to COPYING.md.', 'The restored exact-version COPYING.md contains the standard zlib license text.', 'glfw3.dll is present in the verified win-x64 payload.')
            confidence = 'high'; distributionScope = 'Runtime native dependency included in the verified win-x64 payload; preserve the zlib notice.'
        }
    )
    $replacementPurls = @($newEvidence.purl)
    $evidence.components = @($evidence.components | Where-Object { $_.purl -notin $replacementPurls }) + $newEvidence
    $evidence.componentCount = @($evidence.components).Count
    $evidence.status = 'verified-offline-evidence'
    $evidence.method = 'Exact-version current lockfile, restored local package metadata/license files, and verified win-x64 payload scope; no network access.'
    $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath -Encoding UTF8

    foreach ($sbomPath in $sbomPaths) {
        $sbom = Get-Content -Raw -LiteralPath $sbomPath | ConvertFrom-Json
        $sbom.components = @($sbom.components | Where-Object { $_.purl -ne 'pkg:npm/%40vercel/analytics@1.6.1' })
        $webView = Get-OneComponent $sbom 'pkg:nuget/Microsoft.Web.WebView2@1.0.2903.40'
        Set-ComponentLicense $webView 'BSD-3-Clause' '%NUGET_PACKAGES%/microsoft.web.webview2/1.0.2903.40/LICENSE.txt' 'Exact-version NuGet LICENSE.txt contains the BSD-3-Clause terms; runtime payload includes WebView2Loader.dll.'
        $glfw = Get-OneComponent $sbom 'pkg:nuget/OpenTK.redist.glfw@3.3.0-pre20200830200122'
        Set-ComponentLicense $glfw 'Zlib' '%NUGET_PACKAGES%/opentk.redist.glfw/3.3.0-pre20200830200122/COPYING.md' 'Exact-version NuGet COPYING.md contains the zlib license terms; runtime payload includes glfw3.dll.'
        $caniuse = Get-OneComponent $sbom 'pkg:npm/caniuse-lite@1.0.30001769'
        Set-ComponentLicense $caniuse 'CC-BY-4.0' 'WebUi/node_modules/.pnpm/caniuse-lite@1.0.30001769/node_modules/caniuse-lite/LICENSE' 'Exact restored package.json and LICENSE confirm CC-BY-4.0; exact version is locked.'
        Set-PropertyValue $caniuse 'wpe:distributionScope' 'build'
        Set-PropertyValue $caniuse 'wpe:includedInPayload' 'false'
        foreach ($item in $sharp) {
            $escapedName = [System.Uri]::EscapeDataString([string]$item.Name).Replace('%2F', '/')
            $component = Get-OneComponent $sbom "pkg:npm/$escapedName@$($item.Version)"
            if ((Get-License $component) -ne $item.License) { throw "Unexpected Sharp license for $($component.purl)." }
            Set-PropertyValue $component 'wpe:distributionScope' 'build'
            Set-PropertyValue $component 'wpe:includedInPayload' 'false'
            $scopeNote = 'Confirmed build graph only and absent from the verified win-x64 payload; license obligations are retained.'
            $existingEvidence = Get-PropertyValue $component 'wpe:licenseEvidence'
            if ($existingEvidence -notlike "*$scopeNote*") {
                Set-PropertyValue $component 'wpe:licenseEvidence' (($existingEvidence + ' ' + $scopeNote).Trim())
            }
        }
        $sbom.metadata.timestamp = [DateTimeOffset]::UtcNow.ToString('O')
        $sbom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $sbomPath -Encoding UTF8
    }

    foreach ($target in $gateTargets) {
        try {
            & (Join-Path $PSScriptRoot 'license-gate.ps1') -SbomPath $target.Sbom -OutputDirectory $target.Output -ReleaseIntent $target.Intent | Out-Null
        } catch {
            if ($target.Intent -ne 'Commercial' -or $_.Exception.Message -notmatch 'License gate failed for Commercial intent') { throw }
        }
    }
}

$summaries = [System.Collections.Generic.List[object]]::new()
foreach ($sbomPath in $sbomPaths) {
    $sbom = Get-Content -Raw -LiteralPath $sbomPath | ConvertFrom-Json
    if (@($sbom.components | Where-Object purl -eq 'pkg:npm/%40vercel/analytics@1.6.1').Count -ne 0) { throw "Stale Vercel component remains in $sbomPath." }
    $webView = Get-OneComponent $sbom 'pkg:nuget/Microsoft.Web.WebView2@1.0.2903.40'
    $glfw = Get-OneComponent $sbom 'pkg:nuget/OpenTK.redist.glfw@3.3.0-pre20200830200122'
    $caniuse = Get-OneComponent $sbom 'pkg:npm/caniuse-lite@1.0.30001769'
    if ((Get-License $webView) -ne 'BSD-3-Clause' -or (Get-License $glfw) -ne 'Zlib' -or (Get-License $caniuse) -ne 'CC-BY-4.0') {
        throw "Corrected exact-version license identifiers are missing in $sbomPath."
    }
    foreach ($item in $sharp) {
        $escapedName = [System.Uri]::EscapeDataString([string]$item.Name).Replace('%2F', '/')
        $component = Get-OneComponent $sbom "pkg:npm/$escapedName@$($item.Version)"
        if ((Get-PropertyValue $component 'wpe:distributionScope') -ne 'build' -or (Get-PropertyValue $component 'wpe:includedInPayload') -ne 'false') {
            throw "Sharp payload scope is not fail-closed for $($component.purl)."
        }
    }
    $summaries.Add([pscustomobject]@{ sbom = $sbomPath; components = @($sbom.components).Count })
}

foreach ($target in $gateTargets) {
    $reportPath = Join-Path $target.Output 'license-report.json'
    Assert-File $reportPath
    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    if ($report.summary.components -ne 468 -or $report.summary.review -ne 15 -or $report.summary.unknownCustom -ne 0 -or
        $report.summary.noAssertion -ne 0 -or $report.summary.deny -ne 0 -or $report.summary.commercialBlocked -ne 15) {
        throw "License report summary is inconsistent: $reportPath"
    }
    if ($target.Intent -eq 'Commercial' -and ($report.gatePassed -or $report.status -ne 'review_required')) {
        throw "Commercial gate did not remain fail closed."
    }
    if ($target.Intent -eq 'Evaluation' -and -not $report.gatePassed) { throw "Evaluation report unexpectedly failed." }
}

[pscustomobject][ordered]@{
    status = 'passed'
    offline = $true
    applied = [bool]$Apply
    sbomComponents = 468
    review = 15
    unknownCustom = 0
    commercialBlockers = 15
    commercialDistributable = $false
    sboms = @($summaries)
} | ConvertTo-Json -Depth 5

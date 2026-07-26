param(
    [Parameter(Mandatory = $true)]
    [string]$SbomPath,
    [string]$OutputDirectory = "artifacts/license-gate",
    [ValidateSet("Evaluation", "Commercial")]
    [string]$ReleaseIntent = "Commercial",
    [string]$PolicyPath = "eng/license-policy.json",
    [string]$OverridesPath = "eng/license-review-overrides.json"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Resolve-InRoot([string]$Path, [bool]$AllowMissing = $false) {
    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) { [System.IO.Path]::GetFullPath($Path) } else { [System.IO.Path]::GetFullPath((Join-Path $root $Path)) }
    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $root) {
        throw "License gate paths must be inside the project root."
    }
    if (-not $AllowMissing -and -not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Required file is missing: $resolved" }
    return $resolved
}

function Get-ComponentProperty([object]$Component, [string]$Name) {
    $property = @($Component.properties | Where-Object { $_.name -eq $Name } | Select-Object -First 1)
    if ($property.Count -eq 1) { return [string]$property[0].value }
    return $null
}

function Get-LicenseExpression([object]$Component) {
    $expressions = @($Component.licenses | ForEach-Object {
        if ($_.expression) { [string]$_.expression }
        elseif ($_.license.id) { [string]$_.license.id }
        elseif ($_.license.name) { [string]$_.license.name }
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($expressions.Count -gt 0) { return ($expressions -join " OR ") }
    $declared = Get-ComponentProperty $Component "wpe:license"
    if ($declared) { return $declared }
    return "NOASSERTION"
}

function Get-LicenseTokens([string]$Expression) {
    if ([string]::IsNullOrWhiteSpace($Expression) -or $Expression -eq "NOASSERTION") { return @() }
    return @($Expression -replace '[()]', ' ' -split '\s+(?:AND|OR|WITH)\s+' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

$sbomFile = Resolve-InRoot $SbomPath
$policyFile = Resolve-InRoot $PolicyPath
$overridesFile = Resolve-InRoot $OverridesPath
$output = Resolve-InRoot $OutputDirectory $true
$sbom = Get-Content -Raw -LiteralPath $sbomFile | ConvertFrom-Json
$policy = Get-Content -Raw -LiteralPath $policyFile | ConvertFrom-Json
$overrides = Get-Content -Raw -LiteralPath $overridesFile | ConvertFrom-Json
if ($sbom.bomFormat -ne "CycloneDX" -or -not $sbom.components) { throw "A CycloneDX SBOM with components is required." }

$allow = @{}; @($policy.allow) | ForEach-Object { $allow[[string]$_] = $true }
$review = @{}; @($policy.review) | ForEach-Object { $review[[string]$_] = $true }
$deny = @{}; @($policy.deny) | ForEach-Object { $deny[[string]$_] = $true }
$approved = @{}
foreach ($item in @($overrides.approvals)) {
    if ($item.purl -and $item.license -and $item.approvedBy -and $item.approvedAtUtc) {
        $approved["$($item.purl)|$($item.license)"] = $item
    }
}

$decisions = [System.Collections.Generic.List[object]]::new()
foreach ($component in @($sbom.components | Sort-Object purl)) {
    $expression = Get-LicenseExpression $component
    $tokens = @(Get-LicenseTokens $expression)
    $classification = "allow"
    $reasons = [System.Collections.Generic.List[string]]::new()
    if ($expression -eq "NOASSERTION" -or $tokens.Count -eq 0) {
        $classification = "missing"
        $reasons.Add("No verifiable SPDX license expression is present.")
    } else {
        foreach ($token in $tokens) {
            if ($deny.ContainsKey($token) -or $token -match '^(?:A?GPL|SSPL|BUSL)-' -or $token -eq "Commons-Clause") {
                $classification = "deny"
                $reasons.Add("Denied license token: $token")
            } elseif ($classification -ne "deny" -and $review.ContainsKey($token)) {
                $classification = "review"
                $reasons.Add("License requires component-specific review: $token")
            } elseif ($classification -notin @("deny", "review") -and -not $allow.ContainsKey($token)) {
                $classification = "unknown"
                $reasons.Add("Unknown or custom license token: $token")
            }
        }
    }
    $override = $approved["$($component.purl)|$expression"]
    $isApproved = $null -ne $override
    $commercialBlocked = $classification -eq "deny" -or (($classification -in @("review", "missing", "unknown")) -and -not $isApproved)
    $decisions.Add([pscustomobject][ordered]@{
        name = [string]$component.name
        version = [string]$component.version
        purl = [string]$component.purl
        license = $expression
        classification = $classification
        approvedReview = $isApproved
        commercialBlocked = $commercialBlocked
        distributionScope = (Get-ComponentProperty $component "wpe:distributionScope")
        includedInPayload = (Get-ComponentProperty $component "wpe:includedInPayload")
        evidenceSource = (Get-ComponentProperty $component "wpe:licenseEvidenceSource")
        evidenceConfidence = (Get-ComponentProperty $component "wpe:licenseEvidenceConfidence")
        reasons = @($reasons)
    })
}

$denyCount = @($decisions | Where-Object classification -eq "deny").Count
$reviewCount = @($decisions | Where-Object classification -eq "review").Count
$missingCount = @($decisions | Where-Object classification -eq "missing").Count
$unknownCount = @($decisions | Where-Object classification -eq "unknown").Count
$blockedCount = @($decisions | Where-Object commercialBlocked).Count
$status = if ($denyCount -gt 0) { "denied" } elseif ($blockedCount -gt 0) { "review_required" } else { "passed" }
$gatePassed = if ($ReleaseIntent -eq "Commercial") { $blockedCount -eq 0 } else { $denyCount -eq 0 }

New-Item -ItemType Directory -Path $output -Force | Out-Null
$result = [ordered]@{
    schemaVersion = "wpe.license-gate.v1"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    releaseIntent = $ReleaseIntent.ToLowerInvariant()
    status = $status
    gatePassed = $gatePassed
    legalAdvice = $false
    summary = [ordered]@{
        components = $decisions.Count
        allow = @($decisions | Where-Object classification -eq "allow").Count
        review = $reviewCount
        deny = $denyCount
        noAssertion = $missingCount
        unknownCustom = $unknownCount
        commercialBlocked = $blockedCount
    }
    components = @($decisions)
}
$jsonPath = Join-Path $output "license-report.json"
$decisionPath = Join-Path $output "policy-decision.json"
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
([ordered]@{
    schemaVersion = "wpe.license-policy-decision.v1"
    releaseIntent = $ReleaseIntent.ToLowerInvariant()
    status = $status
    gatePassed = $gatePassed
    blockers = @($decisions | Where-Object commercialBlocked | Select-Object purl, license, classification, reasons)
}) | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $decisionPath -Encoding UTF8

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# License Gate Report")
$lines.Add("")
$lines.Add("Engineering release control only; this report is not legal advice.")
$lines.Add("")
$lines.Add("- Intent: $ReleaseIntent")
$lines.Add("- Status: $status")
$lines.Add("- Components: $($decisions.Count)")
$lines.Add("- Allow: $($result.summary.allow)")
$lines.Add("- Review: $reviewCount")
$lines.Add("- Deny: $denyCount")
$lines.Add("- NOASSERTION: $missingCount")
$lines.Add("- Unknown/custom: $unknownCount")
$lines.Add("- Commercial blockers: $blockedCount")
$lines.Add("")
$lines.Add("## Review And Blocker List")
$lines.Add("")
$lines.Add("| Component | License | Class | Scope | Evidence |")
$lines.Add("| --- | --- | --- | --- | --- |")
foreach ($item in @($decisions | Where-Object { $_.classification -ne "allow" })) {
    $evidence = if ($item.evidenceSource) { $item.evidenceSource } else { "none" }
    $lines.Add("| $($item.name)@$($item.version) | $($item.license) | $($item.classification) | $($item.distributionScope) | $evidence |")
}
$lines | Set-Content -LiteralPath (Join-Path $output "license-report.md") -Encoding UTF8

$notices = [System.Collections.Generic.List[string]]::new()
$notices.Add("# Third-Party Notices")
$notices.Add("")
$notices.Add("Generated from package metadata and recorded evidence. Consult the upstream license text before distribution.")
foreach ($item in $decisions) {
    $notices.Add("")
    $notices.Add("## $($item.name) $($item.version)")
    $notices.Add("")
    $notices.Add("- License: $($item.license)")
    $notices.Add("- Package: $($item.purl)")
    if ($item.evidenceSource) { $notices.Add("- Evidence: $($item.evidenceSource)") }
}
$notices | Set-Content -LiteralPath (Join-Path $output "THIRD_PARTY_NOTICES.md") -Encoding UTF8
"WPE Agent third-party notices are provided in THIRD_PARTY_NOTICES.md.`nThis notice does not modify upstream license terms." | Set-Content -LiteralPath (Join-Path $output "NOTICE") -Encoding UTF8

$result | ConvertTo-Json -Depth 5
if (-not $gatePassed) { throw "License gate failed for $ReleaseIntent intent: status=$status, commercialBlockers=$blockedCount, denied=$denyCount." }

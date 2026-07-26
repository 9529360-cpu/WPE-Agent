$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$temp = Join-Path $root "artifacts/license-gate-tests"
if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Write-TestSbom([string]$Name, [string[]]$Licenses) {
    $components = @()
    $i = 0
    foreach ($license in $Licenses) {
        $i++
        $component = [ordered]@{ type = "library"; name = "fixture-$i"; version = "1.0.0"; purl = "pkg:generic/fixture-$i@1.0.0"; 'bom-ref' = "fixture-$i" }
        if ($license -eq "NOASSERTION") { $component.properties = @([ordered]@{ name = "wpe:license"; value = "NOASSERTION" }) }
        else { $component.licenses = @([ordered]@{ expression = $license }) }
        $components += [pscustomobject]$component
    }
    $path = Join-Path $temp "$Name.json"
    ([ordered]@{ bomFormat = "CycloneDX"; specVersion = "1.5"; version = 1; components = $components }) | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

function Invoke-GateProcess([string]$Sbom, [string]$Intent, [string]$Output) {
    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "license-gate.ps1"), "-SbomPath", $Sbom, "-OutputDirectory", $Output, "-ReleaseIntent", $Intent)
    $process = Start-Process -FilePath "powershell" -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    return $process.ExitCode
}

$allowSbom = Write-TestSbom "allow" @("MIT", "Apache-2.0", "BSD-3-Clause")
if ((Invoke-GateProcess $allowSbom "Commercial" (Join-Path $temp "allow-output")) -ne 0) { throw "Allow-list fixture should pass commercial gate." }

$copyleftSbom = Write-TestSbom "copyleft" @("GPL-3.0-only")
if ((Invoke-GateProcess $copyleftSbom "Commercial" (Join-Path $temp "copyleft-output")) -eq 0) { throw "Copyleft fixture should fail commercial gate." }

$networkCopyleftSbom = Write-TestSbom "network-copyleft" @("AGPL-3.0-only", "SSPL-1.0")
if ((Invoke-GateProcess $networkCopyleftSbom "Commercial" (Join-Path $temp "network-copyleft-output")) -eq 0) { throw "AGPL/SSPL fixture should fail commercial gate." }

$commonsClauseSbom = Write-TestSbom "commons-clause" @("Commons-Clause")
if ((Invoke-GateProcess $commonsClauseSbom "Commercial" (Join-Path $temp "commons-clause-output")) -eq 0) { throw "Commons Clause fixture should fail commercial gate." }

$unknownSbom = Write-TestSbom "unknown" @("NOASSERTION")
if ((Invoke-GateProcess $unknownSbom "Commercial" (Join-Path $temp "unknown-output")) -eq 0) { throw "NOASSERTION fixture should fail commercial gate." }
if ((Invoke-GateProcess $unknownSbom "Evaluation" (Join-Path $temp "evaluation-output")) -ne 0) { throw "NOASSERTION fixture should produce a review-required evaluation report without blocking internal packaging." }

$customSbom = Write-TestSbom "custom" @("SEE-LICENSE-IN-LICENSE.txt")
if ((Invoke-GateProcess $customSbom "Commercial" (Join-Path $temp "custom-output")) -eq 0) { throw "Unknown custom license fixture should fail commercial gate." }

[ordered]@{ status = "passed"; tests = 7; fixtures = @("MIT/Apache/BSD allow", "GPL deny", "AGPL/SSPL deny", "Commons Clause deny", "NOASSERTION commercial deny", "NOASSERTION evaluation review", "unknown custom deny") } | ConvertTo-Json -Depth 4

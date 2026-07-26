param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$output = if ([System.IO.Path]::IsPathRooted($OutputPath)) { [System.IO.Path]::GetFullPath($OutputPath) } else { [System.IO.Path]::GetFullPath((Join-Path $root $OutputPath)) }
if (-not $output.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Notice output must be inside the project root." }

$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME ".nuget/packages" }
$webViewLicense = Join-Path $packages "microsoft.web.webview2/1.0.2903.40/LICENSE.txt"
$webViewNotice = Join-Path $packages "microsoft.web.webview2/1.0.2903.40/NOTICE.txt"
$glfwLicense = Join-Path $packages "opentk.redist.glfw/3.3.0-pre20200830200122/COPYING.md"
foreach ($file in @($webViewLicense, $webViewNotice, $glfwLicense)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required restored-package notice is missing: $file" }
}

$sections = [System.Collections.Generic.List[string]]::new()
$sections.Add("WPE Agent Third-Party Notices")
$sections.Add("=============================")
$sections.Add("")
$sections.Add("This file reproduces notices from exact restored runtime packages without modification. It is not legal advice and does not alter upstream terms.")
$sections.Add("")
$sections.Add("OpenTK 4.3.0 runtime assemblies and OpenTK.GLWpfControl 4.2.3")
$sections.Add("License: MIT")
$sections.Add("Exact-version license pointers:")
$sections.Add("- https://raw.githubusercontent.com/opentk/opentk/4.3.0/LICENSE.md")
$sections.Add("- https://raw.githubusercontent.com/opentk/GLWpfControl/master/LICENSE.md")
$sections.Add("The restored split-package nuspecs do not contain license text; these pointers are retained rather than reconstructing or rewriting it.")
$sections.Add("")
$sections.Add("Microsoft.Web.WebView2 1.0.2903.40 - LICENSE.txt (verbatim)")
$sections.Add("-----------------------------------------------------------")
$sections.Add((Get-Content -Raw -LiteralPath $webViewLicense -Encoding UTF8).TrimEnd())
$sections.Add("")
$sections.Add("Microsoft.Web.WebView2 1.0.2903.40 - NOTICE.txt (verbatim)")
$sections.Add("----------------------------------------------------------")
$sections.Add((Get-Content -Raw -LiteralPath $webViewNotice -Encoding UTF8).TrimEnd())
$sections.Add("")
$sections.Add("OpenTK.redist.glfw 3.3.0-pre20200830200122 - COPYING.md (verbatim)")
$sections.Add("------------------------------------------------------------------------")
$sections.Add((Get-Content -Raw -LiteralPath $glfwLicense -Encoding UTF8).TrimEnd())
$sections.Add("")
$sections.Add("Distribution scope note")
$sections.Add("-----------------------")
$sections.Add("Sharp, platform libvips packages, and caniuse data remain represented in the complete build SBOM. They are build-only dependency-graph components and are not present in the verified win-x64 application payload. This statement does not remove them from review or approve their licenses.")

$parent = Split-Path -Parent $output
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$sections | Set-Content -LiteralPath $output -Encoding UTF8

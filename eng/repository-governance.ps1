$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Require-File([string]$RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Repository governance file is missing: $RelativePath"
    }
    return $path
}

function Require-Match([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -notmatch $Pattern) { throw $Message }
}

$workflowPath = Require-File ".github/workflows/dotnet.yml"
$codeOwnersPath = Require-File ".github/CODEOWNERS"
$pullRequestTemplatePath = Require-File ".github/pull_request_template.md"
$governancePath = Require-File "GOVERNANCE.md"
$contributingPath = Require-File "CONTRIBUTING.md"
$securityPath = Require-File "SECURITY.md"

$workflow = Get-Content -Raw -LiteralPath $workflowPath
$codeOwners = Get-Content -Raw -LiteralPath $codeOwnersPath
$pullRequestTemplate = Get-Content -Raw -LiteralPath $pullRequestTemplatePath
$governance = Get-Content -Raw -LiteralPath $governancePath
$contributing = Get-Content -Raw -LiteralPath $contributingPath
$security = Get-Content -Raw -LiteralPath $securityPath

Require-Match $workflow '(?m)^name:\s*Product CI\s*$' "Product CI workflow name changed; update repository protection and governance together."
Require-Match $workflow '(?m)^\s*pull_request:\s*$' "Product CI must run on pull requests."
Require-Match $workflow '(?m)^\s*branches:\s*\[\s*main\s*,\s*develop\s*\]\s*$' "Product CI must cover main and develop."
Require-Match $workflow '(?m)^\s*build-and-test:\s*$' "Required status job build-and-test is missing."
Require-Match $workflow '(?m)^permissions:\s*\r?\n\s+contents:\s*read\s*$' "Product CI must keep top-level contents permission read-only."
Require-Match $workflow '(?m)^\s*persist-credentials:\s*false\s*$' "Checkout credentials must not persist in Product CI."

if ($workflow -match '(?m)^\s*pull_request_target\s*:') {
    throw "pull_request_target is prohibited for the required Product CI workflow."
}
if ($workflow -match '(?m)^\s*(contents|actions|checks|deployments|packages|pull-requests|security-events|statuses):\s*write\s*$' -or
    $workflow -match '(?m)^\s*permissions:\s*write-all\s*$') {
    throw "Required Product CI must not gain write permissions."
}

$usesMatches = [regex]::Matches($workflow, '(?m)^\s*-?\s*uses:\s*([^\s#]+)')
foreach ($match in $usesMatches) {
    $action = $match.Groups[1].Value
    if ($action -notmatch '@[0-9a-fA-F]{40}$') {
        throw "GitHub Action is not pinned to a full commit SHA: $action"
    }
}

Require-Match $codeOwners '(?m)^\*\s+@9529360-cpu\s*$' "Repository-wide CODEOWNERS entry is missing."
Require-Match $governance 'build-and-test' "Governance policy must name the required status check."
Require-Match $governance '(?i)squash merge only' "Governance policy must define squash-only history."
Require-Match $governance '(?i)force pushes.*prohibited' "Governance policy must prohibit force pushes."
Require-Match $contributing '(?i)Do not push directly to `main`' "Contribution policy must prohibit direct main pushes."
Require-Match $pullRequestTemplate 'Product CI / build-and-test' "Pull request template must require the remote CI gate."
Require-Match $pullRequestTemplate '(?i)Mainnet remains disabled' "Pull request template must preserve the Mainnet boundary."
Require-Match $security '(?i)private vulnerability reporting' "Security policy must direct sensitive reports to a private channel."

Write-Output "Repository governance contract passed."

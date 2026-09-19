[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedCandidateManifestHash,
    [Parameter(Mandatory)][string]$LastKnownGoodRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedLastKnownGoodManifestHash,
    [Parameter(Mandatory)][string]$TrustedRuntimeProofPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedTrustedRuntimeProofHash,
    [Parameter(Mandatory)][string]$ExpectedRuntimeProofId,
    [Parameter(Mandatory)][string]$ExpectedRuntimeSourceIdentity,
    [Parameter(Mandatory)][string[]]$ExpectedEvidenceGateRefs,
    [Parameter(Mandatory)][string]$SoakEvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedFullReleaseEvidenceHash,
    [Parameter(Mandatory)][string[]]$ProducerVerdictPaths,
    [Parameter(Mandatory)][string[]]$ExpectedProducerVerdictHashes,
    [Parameter(Mandatory)][string]$AuthorizedProviderFixtureRoot,
    [Parameter(Mandatory)][string]$ProviderFixturePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedProviderFixtureHash,
    [Parameter(Mandatory)][switch]$DryRun,
    [Parameter(Mandatory)][switch]$RejectMainnet,
    [switch]$TestnetMutationSmoke,
    [string]$VerifiedMutationGateEvidencePath,
    [string]$ExpectedVerifiedMutationGateEvidenceHash,
    [switch]$Mainnet
)

$ErrorActionPreference = 'Stop'
$requiredProducers = @('SEC-0027', 'DOR-0005', 'CLI-0009')

function Stop-Gate([string]$Code) { throw $Code }
function Get-AnchoredJson([string]$Path, [string]$ExpectedHash, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Stop-Gate "$Label.missing" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $ExpectedHash.ToLowerInvariant()) { Stop-Gate "$Label.hash-mismatch" }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) }
    catch { Stop-Gate "$Label.malformed" }
}
function Convert-RoundtripTimestamp($Value, [string]$Code) {
    try {
        return [DateTimeOffset]::Parse(
            [string]$Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind)
    } catch {
        Stop-Gate $Code
    }
}
function Assert-Manifest([string]$Root, [string]$ExpectedHash, [string]$Label) {
    if (-not [IO.Path]::IsPathRooted($Root) -or -not (Test-Path -LiteralPath $Root -PathType Container)) { Stop-Gate "$Label.root-invalid" }
    $resolved = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $manifestPath = Join-Path $resolved 'release-manifest.json'
    $manifest = Get-AnchoredJson $manifestPath $ExpectedHash "$Label.manifest"
    if ($manifest.schemaVersion -cne 'wpe.dogfood-release-manifest/1.1' -or
        $manifest.environment -cne 'Testnet' -or
        [string]::IsNullOrWhiteSpace([string]$manifest.sourceIdentity) -or
        [string]::IsNullOrWhiteSpace([string]$manifest.configurationSchema) -or
        [string]::IsNullOrWhiteSpace([string]$manifest.migrationVersion)) {
        Stop-Gate "$Label.manifest-contract-invalid"
    }
    [pscustomobject]@{ Root=$resolved; Manifest=$manifest }
}
function New-Command([string]$Id, [string]$File, [string[]]$Arguments, [string]$Phase) {
    [pscustomobject][ordered]@{ id = $Id; phase = $Phase; file = $File; arguments = @($Arguments) }
}
function Resolve-LocalFixedPath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('\\')) { Stop-Gate "$Label.network-path-forbidden" }
    if (-not [IO.Path]::IsPathRooted($Path)) { Stop-Gate "$Label.not-absolute" }
    $resolved = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($resolved)
    if ($pathRoot -notmatch '^[A-Za-z]:\\$') { Stop-Gate "$Label.network-path-forbidden" }
    try { $drive = [IO.DriveInfo]::new($pathRoot) } catch { Stop-Gate "$Label.drive-invalid" }
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { Stop-Gate "$Label.network-path-forbidden" }
    $resolved
}
function Assert-NoReparsePoint([string]$Root, [string]$Path) {
    $current = $Root
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-Gate 'provider.fixture-reparse-forbidden' }
        }
        if ($current -ceq $Path) { break }
        $relative = $Path.Substring($Root.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
        $nextPart = $relative.Split([IO.Path]::DirectorySeparatorChar)[0]
        $current = Join-Path $current $nextPart
        $Root = $current
    }
}

if (-not $DryRun) { Stop-Gate 'mode.dry-run-required' }
if ($Mainnet -or -not $RejectMainnet) { Stop-Gate 'environment.mainnet-forbidden' }
if ($ProducerVerdictPaths.Count -ne $ExpectedProducerVerdictHashes.Count) { Stop-Gate 'producer.anchor-count-mismatch' }

$candidate = Assert-Manifest $CandidateRoot $ExpectedCandidateManifestHash 'candidate'
$lkg = Assert-Manifest $LastKnownGoodRoot $ExpectedLastKnownGoodManifestHash 'lkg'

$runtime = Get-AnchoredJson $TrustedRuntimeProofPath $ExpectedTrustedRuntimeProofHash 'runtime.proof'
$runtimeAllowed = @('schemaVersion','proofId','sourceIdentity','sourceTimestampUtc','snapshotTimestampUtc','environment','bridgeState','authority','evidenceGates','candidateManifestSha256','lastKnownGoodManifestSha256','probes')
if (@($runtime.PSObject.Properties.Name | Where-Object { $runtimeAllowed -notcontains $_ }).Count) { Stop-Gate 'runtime.proof-unknown-field' }
if ($runtime.schemaVersion -cne 'wpe.trusted-runtime-proof/1.1') { Stop-Gate 'runtime.proof-schema-mismatch' }
if ($runtime.proofId -cne $ExpectedRuntimeProofId -or $runtime.sourceIdentity -cne $ExpectedRuntimeSourceIdentity) { Stop-Gate 'runtime.proof-identity-mismatch' }
if ($runtime.environment -cne 'Testnet') { Stop-Gate 'runtime.proof-environment-mismatch' }
if ($runtime.bridgeState -cne 'connected') { Stop-Gate 'runtime.bridge-disconnected' }
if ([string]$runtime.candidateManifestSha256 -cne $ExpectedCandidateManifestHash.ToLowerInvariant() -or
    [string]$runtime.lastKnownGoodManifestSha256 -cne $ExpectedLastKnownGoodManifestHash.ToLowerInvariant()) {
    Stop-Gate 'runtime.proof-manifest-binding-mismatch'
}
$sourceTimestamp = Convert-RoundtripTimestamp $runtime.sourceTimestampUtc 'runtime.source-timestamp-invalid'
$snapshotTimestamp = Convert-RoundtripTimestamp $runtime.snapshotTimestampUtc 'runtime.snapshot-timestamp-invalid'
$now = [DateTimeOffset]::UtcNow
if ($sourceTimestamp -gt $snapshotTimestamp -or $snapshotTimestamp -gt $now.AddSeconds(5)) { Stop-Gate 'runtime.proof-timestamp-order-invalid' }
if (($now-$snapshotTimestamp).TotalSeconds -gt 120 -or ($snapshotTimestamp-$sourceTimestamp).TotalSeconds -gt 120) { Stop-Gate 'runtime.proof-stale' }
if ($null -eq $runtime.authority -or [string]::IsNullOrWhiteSpace([string]$runtime.authority.id) -or
    $runtime.authority.status -cne 'active' -or $runtime.authority.current -ne $true) {
    Stop-Gate 'runtime.authority-not-current'
}
$authorityExpires = Convert-RoundtripTimestamp $runtime.authority.expiresUtc 'runtime.authority-expiry-invalid'
if ($authorityExpires -le $now) { Stop-Gate 'runtime.authority-revoked-or-expired' }
$passedGateRefs = @($runtime.evidenceGates | Where-Object { $_.status -ceq 'passed' } | ForEach-Object { [string]$_.reference })
if ($ExpectedEvidenceGateRefs.Count -eq 0 -or @($ExpectedEvidenceGateRefs | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $passedGateRefs -cnotcontains $_ }).Count) {
    Stop-Gate 'runtime.evidence-gate-missing'
}
$probeContracts = [ordered]@{ install='candidate'; startup='candidate'; restartRecovery='candidate'; rollback='last-known-good' }
if ($null -eq $runtime.probes) { Stop-Gate 'runtime.probes-missing' }
$probeNames = @($runtime.probes.PSObject.Properties.Name)
if ($probeNames.Count -ne $probeContracts.Count -or @($probeNames | Where-Object { -not $probeContracts.Contains($_) }).Count) { Stop-Gate 'runtime.probes-invalid' }
foreach ($probeName in $probeContracts.Keys) {
    $probe = $runtime.probes.$probeName
    if ($null -eq $probe) { Stop-Gate "runtime.probe-$probeName-missing" }
    $probeFields = @($probe.PSObject.Properties.Name)
    if ($probeFields.Count -ne 3 -or @($probeFields | Where-Object { $_ -notin @('status','target','evidenceSha256') }).Count) { Stop-Gate "runtime.probe-$probeName-invalid" }
    if ([string]$probe.status -cne 'passed' -or
        [string]$probe.target -cne [string]$probeContracts[$probeName] -or
        [string]$probe.evidenceSha256 -notmatch '^[a-f0-9]{64}

$suite = Get-AnchoredJson $FullReleaseEvidencePath $ExpectedFullReleaseEvidenceHash 'full-release'
if ($suite.status -cne 'passed' -or $suite.configuration -cne 'Release' -or [int]$suite.failed -ne 0 -or [int]$suite.total -le 0) { Stop-Gate 'full-release.not-passed' }

$accepted = @{}
for ($i = 0; $i -lt $ProducerVerdictPaths.Count; $i++) {
    $verdict = Get-AnchoredJson $ProducerVerdictPaths[$i] $ExpectedProducerVerdictHashes[$i] 'producer.verdict'
    if ($verdict.verdict -cne 'ACCEPT' -or [string]::IsNullOrWhiteSpace([string]$verdict.taskId)) { Stop-Gate 'producer.not-accepted' }
    $accepted[[string]$verdict.taskId] = $true
}
foreach ($taskId in $requiredProducers) { if (-not $accepted.ContainsKey($taskId)) { Stop-Gate 'producer.required-verdict-missing' } }

$resolvedProviderRoot = Resolve-LocalFixedPath $AuthorizedProviderFixtureRoot 'provider.fixture-root'
$resolvedProviderFixture = Resolve-LocalFixedPath $ProviderFixturePath 'provider.fixture-path'
$rootWithSeparator = $resolvedProviderRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedProviderFixture.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) { Stop-Gate 'provider.fixture-outside-authorized-root' }
if (-not (Test-Path -LiteralPath $resolvedProviderRoot -PathType Container)) { Stop-Gate 'provider.fixture-root-missing' }
Assert-NoReparsePoint $resolvedProviderRoot $resolvedProviderFixture
if (-not (Test-Path -LiteralPath $resolvedProviderFixture -PathType Leaf)) { Stop-Gate 'provider.fixture-missing' }
$providerFixtureHash = (Get-FileHash -LiteralPath $resolvedProviderFixture -Algorithm SHA256).Hash.ToLowerInvariant()
if ($providerFixtureHash -cne $ExpectedProviderFixtureHash.ToLowerInvariant()) { Stop-Gate 'provider.fixture-hash-mismatch' }

$mutationEvidenceRef = $null
if ($TestnetMutationSmoke) {
    if ([string]::IsNullOrWhiteSpace($VerifiedMutationGateEvidencePath) -or $ExpectedVerifiedMutationGateEvidenceHash -notmatch '^[a-fA-F0-9]{64}$') { Stop-Gate 'mutation.gate-evidence-required' }
    $mutation = Get-AnchoredJson $VerifiedMutationGateEvidencePath $ExpectedVerifiedMutationGateEvidenceHash 'mutation.gate'
    if ($mutation.status -cne 'passed' -or $mutation.environment -cne 'Testnet' -or $mutation.authorized -ne $true) { Stop-Gate 'mutation.gate-not-authorized' }
    $mutationEvidenceRef = $mutation.evidenceId
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$commands = [Collections.Generic.List[object]]::new()
$commands.Add((New-Command 'web-lint' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'lint') 'validation'))
$commands.Add((New-Command 'web-typecheck' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'typecheck') 'validation'))
$commands.Add((New-Command 'web-build' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'build') 'validation'))
$commands.Add((New-Command 'full-dotnet-release-tests' (Join-Path $repo 'eng\test.ps1') @('-Configuration', 'Release') 'validation'))
$commands.Add((New-Command 'release-readiness' (Join-Path $repo 'eng\release-readiness.ps1') @('-Runtime', 'win-x64') 'candidate'))
$commands.Add((New-Command 'package-beta' (Join-Path $repo 'eng\package-beta.ps1') @() 'candidate'))
$dogfoodArguments = @('-CandidateRoot',$CandidateRoot,'-LastKnownGoodRoot',$LastKnownGoodRoot,'-ProviderReadOnly','-RequireTrustedRuntimeFresh','-VerifyInstall','-VerifyStartup','-VerifyRestartRecovery','-VerifyRollback','-RejectMainnet','-ExpectedCandidateManifestHash',$ExpectedCandidateManifestHash,'-ExpectedLastKnownGoodManifestHash',$ExpectedLastKnownGoodManifestHash,'-TrustedRuntimeProofPath',$TrustedRuntimeProofPath,'-ExpectedTrustedRuntimeProofHash',$ExpectedTrustedRuntimeProofHash,'-ExpectedRuntimeProofId',$ExpectedRuntimeProofId,'-ExpectedRuntimeSourceIdentity',$ExpectedRuntimeSourceIdentity,'-ExpectedEvidenceGateRefs') + @($ExpectedEvidenceGateRefs) + @('-SoakEvidencePath',$SoakEvidencePath,'-ExpectedSoakEvidenceHash',$ExpectedSoakEvidenceHash,'-MinimumSoakDurationMinutes',[string]$MinimumSoakDurationMinutes)
$commands.Add((New-Command 'dogfood-install-start-restart-rollback' (Join-Path $repo 'eng\dogfood\Test-DogfoodRelease.ps1') $dogfoodArguments 'dogfood'))
$commands.Add((New-Command 'playwright-customer-routes' 'node' @('--test',(Join-Path $repo 'WebUi\tests\v1-visible-surface.test.mjs')) 'customer-surface'))
$commands.Add((New-Command 'provider-read-only' (Join-Path $repo 'eng\dogfood\Test-ProviderReadOnly.ps1') @('-FixturePath',$resolvedProviderFixture) 'provider-read-only'))
if ($TestnetMutationSmoke) { $commands.Add((New-Command 'testnet-mutation-smoke' (Join-Path $repo 'eng\smoke-review-testnet.ps1') @('-VerifiedGateEvidenceRef',[string]$mutationEvidenceRef) 'authorized-testnet-mutation')) }

[pscustomobject][ordered]@{
    schemaVersion = 'wpe.v1-candidate-gate-plan/1.0'
    valid = $true
    dryRun = $true
    environment = 'Testnet'
    mainnetRejected = $true
    mutationEnabled = [bool]$TestnetMutationSmoke
    fullReleaseEvidenceHash = $ExpectedFullReleaseEvidenceHash.ToLowerInvariant()
    commands = @($commands)
}
)][string]$ExpectedSoakEvidenceHash,
    [ValidateRange(1,2880)][int]$MinimumSoakDurationMinutes = 1440,
    [Parameter(Mandatory)][string]$FullReleaseEvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedFullReleaseEvidenceHash,
    [Parameter(Mandatory)][string[]]$ProducerVerdictPaths,
    [Parameter(Mandatory)][string[]]$ExpectedProducerVerdictHashes,
    [Parameter(Mandatory)][string]$AuthorizedProviderFixtureRoot,
    [Parameter(Mandatory)][string]$ProviderFixturePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedProviderFixtureHash,
    [Parameter(Mandatory)][switch]$DryRun,
    [Parameter(Mandatory)][switch]$RejectMainnet,
    [switch]$TestnetMutationSmoke,
    [string]$VerifiedMutationGateEvidencePath,
    [string]$ExpectedVerifiedMutationGateEvidenceHash,
    [switch]$Mainnet
)

$ErrorActionPreference = 'Stop'
$requiredProducers = @('SEC-0027', 'DOR-0005', 'CLI-0009')

function Stop-Gate([string]$Code) { throw $Code }
function Get-AnchoredJson([string]$Path, [string]$ExpectedHash, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Stop-Gate "$Label.missing" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $ExpectedHash.ToLowerInvariant()) { Stop-Gate "$Label.hash-mismatch" }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) }
    catch { Stop-Gate "$Label.malformed" }
}
function Assert-Manifest([string]$Root, [string]$ExpectedHash, [string]$Label) {
    if (-not [IO.Path]::IsPathRooted($Root) -or -not (Test-Path -LiteralPath $Root -PathType Container)) { Stop-Gate "$Label.root-invalid" }
    $manifest = Join-Path $Root 'manifest.json'
    $null = Get-AnchoredJson $manifest $ExpectedHash "$Label.manifest"
}
function New-Command([string]$Id, [string]$File, [string[]]$Arguments, [string]$Phase) {
    [pscustomobject][ordered]@{ id = $Id; phase = $Phase; file = $File; arguments = @($Arguments) }
}
function Resolve-LocalFixedPath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('\\')) { Stop-Gate "$Label.network-path-forbidden" }
    if (-not [IO.Path]::IsPathRooted($Path)) { Stop-Gate "$Label.not-absolute" }
    $resolved = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($resolved)
    if ($pathRoot -notmatch '^[A-Za-z]:\\$') { Stop-Gate "$Label.network-path-forbidden" }
    try { $drive = [IO.DriveInfo]::new($pathRoot) } catch { Stop-Gate "$Label.drive-invalid" }
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { Stop-Gate "$Label.network-path-forbidden" }
    $resolved
}
function Assert-NoReparsePoint([string]$Root, [string]$Path) {
    $current = $Root
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-Gate 'provider.fixture-reparse-forbidden' }
        }
        if ($current -ceq $Path) { break }
        $relative = $Path.Substring($Root.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
        $nextPart = $relative.Split([IO.Path]::DirectorySeparatorChar)[0]
        $current = Join-Path $current $nextPart
        $Root = $current
    }
}

if (-not $DryRun) { Stop-Gate 'mode.dry-run-required' }
if ($Mainnet -or -not $RejectMainnet) { Stop-Gate 'environment.mainnet-forbidden' }
if ($ProducerVerdictPaths.Count -ne $ExpectedProducerVerdictHashes.Count) { Stop-Gate 'producer.anchor-count-mismatch' }

Assert-Manifest $CandidateRoot $ExpectedCandidateManifestHash 'candidate'
Assert-Manifest $LastKnownGoodRoot $ExpectedLastKnownGoodManifestHash 'lkg'

$runtime = Get-AnchoredJson $TrustedRuntimeProofPath $ExpectedTrustedRuntimeProofHash 'runtime.proof'
if ($runtime.proofId -cne $ExpectedRuntimeProofId -or $runtime.sourceIdentity -cne $ExpectedRuntimeSourceIdentity) { Stop-Gate 'runtime.proof-identity-mismatch' }
if ($runtime.status -cne 'passed' -or $runtime.fresh -ne $true -or $runtime.environment -cne 'Testnet') { Stop-Gate 'runtime.proof-not-trusted-fresh' }
$actualGateRefs = @($runtime.gateEvidenceRefs)
foreach ($gateRef in $ExpectedEvidenceGateRefs) { if ($actualGateRefs -cnotcontains $gateRef) { Stop-Gate 'runtime.gate-ref-missing' } }

$suite = Get-AnchoredJson $FullReleaseEvidencePath $ExpectedFullReleaseEvidenceHash 'full-release'
if ($suite.status -cne 'passed' -or $suite.configuration -cne 'Release' -or [int]$suite.failed -ne 0 -or [int]$suite.total -le 0) { Stop-Gate 'full-release.not-passed' }

$accepted = @{}
for ($i = 0; $i -lt $ProducerVerdictPaths.Count; $i++) {
    $verdict = Get-AnchoredJson $ProducerVerdictPaths[$i] $ExpectedProducerVerdictHashes[$i] 'producer.verdict'
    if ($verdict.verdict -cne 'ACCEPT' -or [string]::IsNullOrWhiteSpace([string]$verdict.taskId)) { Stop-Gate 'producer.not-accepted' }
    $accepted[[string]$verdict.taskId] = $true
}
foreach ($taskId in $requiredProducers) { if (-not $accepted.ContainsKey($taskId)) { Stop-Gate 'producer.required-verdict-missing' } }

$resolvedProviderRoot = Resolve-LocalFixedPath $AuthorizedProviderFixtureRoot 'provider.fixture-root'
$resolvedProviderFixture = Resolve-LocalFixedPath $ProviderFixturePath 'provider.fixture-path'
$rootWithSeparator = $resolvedProviderRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedProviderFixture.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) { Stop-Gate 'provider.fixture-outside-authorized-root' }
if (-not (Test-Path -LiteralPath $resolvedProviderRoot -PathType Container)) { Stop-Gate 'provider.fixture-root-missing' }
Assert-NoReparsePoint $resolvedProviderRoot $resolvedProviderFixture
if (-not (Test-Path -LiteralPath $resolvedProviderFixture -PathType Leaf)) { Stop-Gate 'provider.fixture-missing' }
$providerFixtureHash = (Get-FileHash -LiteralPath $resolvedProviderFixture -Algorithm SHA256).Hash.ToLowerInvariant()
if ($providerFixtureHash -cne $ExpectedProviderFixtureHash.ToLowerInvariant()) { Stop-Gate 'provider.fixture-hash-mismatch' }

$mutationEvidenceRef = $null
if ($TestnetMutationSmoke) {
    if ([string]::IsNullOrWhiteSpace($VerifiedMutationGateEvidencePath) -or $ExpectedVerifiedMutationGateEvidenceHash -notmatch '^[a-fA-F0-9]{64}$') { Stop-Gate 'mutation.gate-evidence-required' }
    $mutation = Get-AnchoredJson $VerifiedMutationGateEvidencePath $ExpectedVerifiedMutationGateEvidenceHash 'mutation.gate'
    if ($mutation.status -cne 'passed' -or $mutation.environment -cne 'Testnet' -or $mutation.authorized -ne $true) { Stop-Gate 'mutation.gate-not-authorized' }
    $mutationEvidenceRef = $mutation.evidenceId
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$commands = [Collections.Generic.List[object]]::new()
$commands.Add((New-Command 'web-lint' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'lint') 'validation'))
$commands.Add((New-Command 'web-typecheck' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'typecheck') 'validation'))
$commands.Add((New-Command 'web-build' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'build') 'validation'))
$commands.Add((New-Command 'full-dotnet-release-tests' (Join-Path $repo 'eng\test.ps1') @('-Configuration', 'Release') 'validation'))
$commands.Add((New-Command 'release-readiness' (Join-Path $repo 'eng\release-readiness.ps1') @('-Configuration', 'Release') 'candidate'))
$commands.Add((New-Command 'package-beta' (Join-Path $repo 'eng\package-beta.ps1') @() 'candidate'))
$commands.Add((New-Command 'dogfood-install-start-restart-rollback' (Join-Path $repo 'eng\dogfood\Test-DogfoodRelease.ps1') @('-CandidateRoot',$CandidateRoot,'-LastKnownGoodRoot',$LastKnownGoodRoot,'-ProviderReadOnly','-RequireTrustedRuntimeFresh','-VerifyInstall','-VerifyStartup','-VerifyRestartRecovery','-VerifyRollback','-RejectMainnet','-ExpectedCandidateManifestHash',$ExpectedCandidateManifestHash,'-ExpectedLastKnownGoodManifestHash',$ExpectedLastKnownGoodManifestHash,'-TrustedRuntimeProofPath',$TrustedRuntimeProofPath,'-ExpectedTrustedRuntimeProofHash',$ExpectedTrustedRuntimeProofHash,'-ExpectedRuntimeProofId',$ExpectedRuntimeProofId,'-ExpectedRuntimeSourceIdentity',$ExpectedRuntimeSourceIdentity) 'dogfood'))
$commands.Add((New-Command 'playwright-customer-routes' 'node' @('--test',(Join-Path $repo 'WebUi\tests\v1-visible-surface.test.mjs')) 'customer-surface'))
$commands.Add((New-Command 'provider-read-only' (Join-Path $repo 'eng\dogfood\Test-ProviderReadOnly.ps1') @('-FixturePath',$resolvedProviderFixture) 'provider-read-only'))
if ($TestnetMutationSmoke) { $commands.Add((New-Command 'testnet-mutation-smoke' (Join-Path $repo 'eng\smoke-review-testnet.ps1') @('-VerifiedGateEvidenceRef',[string]$mutationEvidenceRef) 'authorized-testnet-mutation')) }

[pscustomobject][ordered]@{
    schemaVersion = 'wpe.v1-candidate-gate-plan/1.0'
    valid = $true
    dryRun = $true
    environment = 'Testnet'
    mainnetRejected = $true
    mutationEnabled = [bool]$TestnetMutationSmoke
    fullReleaseEvidenceHash = $ExpectedFullReleaseEvidenceHash.ToLowerInvariant()
    commands = @($commands)
}
) {
        Stop-Gate "runtime.probe-$probeName-not-passed"
    }
}

$suite = Get-AnchoredJson $FullReleaseEvidencePath $ExpectedFullReleaseEvidenceHash 'full-release'
if ($suite.status -cne 'passed' -or $suite.configuration -cne 'Release' -or [int]$suite.failed -ne 0 -or [int]$suite.total -le 0) { Stop-Gate 'full-release.not-passed' }

$accepted = @{}
for ($i = 0; $i -lt $ProducerVerdictPaths.Count; $i++) {
    $verdict = Get-AnchoredJson $ProducerVerdictPaths[$i] $ExpectedProducerVerdictHashes[$i] 'producer.verdict'
    if ($verdict.verdict -cne 'ACCEPT' -or [string]::IsNullOrWhiteSpace([string]$verdict.taskId)) { Stop-Gate 'producer.not-accepted' }
    $accepted[[string]$verdict.taskId] = $true
}
foreach ($taskId in $requiredProducers) { if (-not $accepted.ContainsKey($taskId)) { Stop-Gate 'producer.required-verdict-missing' } }

$resolvedProviderRoot = Resolve-LocalFixedPath $AuthorizedProviderFixtureRoot 'provider.fixture-root'
$resolvedProviderFixture = Resolve-LocalFixedPath $ProviderFixturePath 'provider.fixture-path'
$rootWithSeparator = $resolvedProviderRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedProviderFixture.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) { Stop-Gate 'provider.fixture-outside-authorized-root' }
if (-not (Test-Path -LiteralPath $resolvedProviderRoot -PathType Container)) { Stop-Gate 'provider.fixture-root-missing' }
Assert-NoReparsePoint $resolvedProviderRoot $resolvedProviderFixture
if (-not (Test-Path -LiteralPath $resolvedProviderFixture -PathType Leaf)) { Stop-Gate 'provider.fixture-missing' }
$providerFixtureHash = (Get-FileHash -LiteralPath $resolvedProviderFixture -Algorithm SHA256).Hash.ToLowerInvariant()
if ($providerFixtureHash -cne $ExpectedProviderFixtureHash.ToLowerInvariant()) { Stop-Gate 'provider.fixture-hash-mismatch' }

$mutationEvidenceRef = $null
if ($TestnetMutationSmoke) {
    if ([string]::IsNullOrWhiteSpace($VerifiedMutationGateEvidencePath) -or $ExpectedVerifiedMutationGateEvidenceHash -notmatch '^[a-fA-F0-9]{64}$') { Stop-Gate 'mutation.gate-evidence-required' }
    $mutation = Get-AnchoredJson $VerifiedMutationGateEvidencePath $ExpectedVerifiedMutationGateEvidenceHash 'mutation.gate'
    if ($mutation.status -cne 'passed' -or $mutation.environment -cne 'Testnet' -or $mutation.authorized -ne $true) { Stop-Gate 'mutation.gate-not-authorized' }
    $mutationEvidenceRef = $mutation.evidenceId
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$commands = [Collections.Generic.List[object]]::new()
$commands.Add((New-Command 'web-lint' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'lint') 'validation'))
$commands.Add((New-Command 'web-typecheck' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'typecheck') 'validation'))
$commands.Add((New-Command 'web-build' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'build') 'validation'))
$commands.Add((New-Command 'full-dotnet-release-tests' (Join-Path $repo 'eng\test.ps1') @('-Configuration', 'Release') 'validation'))
$commands.Add((New-Command 'release-readiness' (Join-Path $repo 'eng\release-readiness.ps1') @('-Configuration', 'Release') 'candidate'))
$commands.Add((New-Command 'package-beta' (Join-Path $repo 'eng\package-beta.ps1') @() 'candidate'))
$commands.Add((New-Command 'dogfood-install-start-restart-rollback' (Join-Path $repo 'eng\dogfood\Test-DogfoodRelease.ps1') @('-CandidateRoot',$CandidateRoot,'-LastKnownGoodRoot',$LastKnownGoodRoot,'-ProviderReadOnly','-RequireTrustedRuntimeFresh','-VerifyInstall','-VerifyStartup','-VerifyRestartRecovery','-VerifyRollback','-RejectMainnet','-ExpectedCandidateManifestHash',$ExpectedCandidateManifestHash,'-ExpectedLastKnownGoodManifestHash',$ExpectedLastKnownGoodManifestHash,'-TrustedRuntimeProofPath',$TrustedRuntimeProofPath,'-ExpectedTrustedRuntimeProofHash',$ExpectedTrustedRuntimeProofHash,'-ExpectedRuntimeProofId',$ExpectedRuntimeProofId,'-ExpectedRuntimeSourceIdentity',$ExpectedRuntimeSourceIdentity) 'dogfood'))
$commands.Add((New-Command 'playwright-customer-routes' 'node' @('--test',(Join-Path $repo 'WebUi\tests\v1-visible-surface.test.mjs')) 'customer-surface'))
$commands.Add((New-Command 'provider-read-only' (Join-Path $repo 'eng\dogfood\Test-ProviderReadOnly.ps1') @('-FixturePath',$resolvedProviderFixture) 'provider-read-only'))
if ($TestnetMutationSmoke) { $commands.Add((New-Command 'testnet-mutation-smoke' (Join-Path $repo 'eng\smoke-review-testnet.ps1') @('-VerifiedGateEvidenceRef',[string]$mutationEvidenceRef) 'authorized-testnet-mutation')) }

[pscustomobject][ordered]@{
    schemaVersion = 'wpe.v1-candidate-gate-plan/1.0'
    valid = $true
    dryRun = $true
    environment = 'Testnet'
    mainnetRejected = $true
    mutationEnabled = [bool]$TestnetMutationSmoke
    fullReleaseEvidenceHash = $ExpectedFullReleaseEvidenceHash.ToLowerInvariant()
    commands = @($commands)
}
)][string]$ExpectedSoakEvidenceHash,
    [ValidateRange(1,2880)][int]$MinimumSoakDurationMinutes = 1440,
    [Parameter(Mandatory)][string]$FullReleaseEvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedFullReleaseEvidenceHash,
    [Parameter(Mandatory)][string[]]$ProducerVerdictPaths,
    [Parameter(Mandatory)][string[]]$ExpectedProducerVerdictHashes,
    [Parameter(Mandatory)][string]$AuthorizedProviderFixtureRoot,
    [Parameter(Mandatory)][string]$ProviderFixturePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedProviderFixtureHash,
    [Parameter(Mandatory)][switch]$DryRun,
    [Parameter(Mandatory)][switch]$RejectMainnet,
    [switch]$TestnetMutationSmoke,
    [string]$VerifiedMutationGateEvidencePath,
    [string]$ExpectedVerifiedMutationGateEvidenceHash,
    [switch]$Mainnet
)

$ErrorActionPreference = 'Stop'
$requiredProducers = @('SEC-0027', 'DOR-0005', 'CLI-0009')

function Stop-Gate([string]$Code) { throw $Code }
function Get-AnchoredJson([string]$Path, [string]$ExpectedHash, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Stop-Gate "$Label.missing" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $ExpectedHash.ToLowerInvariant()) { Stop-Gate "$Label.hash-mismatch" }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) }
    catch { Stop-Gate "$Label.malformed" }
}
function Assert-Manifest([string]$Root, [string]$ExpectedHash, [string]$Label) {
    if (-not [IO.Path]::IsPathRooted($Root) -or -not (Test-Path -LiteralPath $Root -PathType Container)) { Stop-Gate "$Label.root-invalid" }
    $manifest = Join-Path $Root 'manifest.json'
    $null = Get-AnchoredJson $manifest $ExpectedHash "$Label.manifest"
}
function New-Command([string]$Id, [string]$File, [string[]]$Arguments, [string]$Phase) {
    [pscustomobject][ordered]@{ id = $Id; phase = $Phase; file = $File; arguments = @($Arguments) }
}
function Resolve-LocalFixedPath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('\\')) { Stop-Gate "$Label.network-path-forbidden" }
    if (-not [IO.Path]::IsPathRooted($Path)) { Stop-Gate "$Label.not-absolute" }
    $resolved = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($resolved)
    if ($pathRoot -notmatch '^[A-Za-z]:\\$') { Stop-Gate "$Label.network-path-forbidden" }
    try { $drive = [IO.DriveInfo]::new($pathRoot) } catch { Stop-Gate "$Label.drive-invalid" }
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { Stop-Gate "$Label.network-path-forbidden" }
    $resolved
}
function Assert-NoReparsePoint([string]$Root, [string]$Path) {
    $current = $Root
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-Gate 'provider.fixture-reparse-forbidden' }
        }
        if ($current -ceq $Path) { break }
        $relative = $Path.Substring($Root.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
        $nextPart = $relative.Split([IO.Path]::DirectorySeparatorChar)[0]
        $current = Join-Path $current $nextPart
        $Root = $current
    }
}

if (-not $DryRun) { Stop-Gate 'mode.dry-run-required' }
if ($Mainnet -or -not $RejectMainnet) { Stop-Gate 'environment.mainnet-forbidden' }
if ($ProducerVerdictPaths.Count -ne $ExpectedProducerVerdictHashes.Count) { Stop-Gate 'producer.anchor-count-mismatch' }

Assert-Manifest $CandidateRoot $ExpectedCandidateManifestHash 'candidate'
Assert-Manifest $LastKnownGoodRoot $ExpectedLastKnownGoodManifestHash 'lkg'

$runtime = Get-AnchoredJson $TrustedRuntimeProofPath $ExpectedTrustedRuntimeProofHash 'runtime.proof'
if ($runtime.proofId -cne $ExpectedRuntimeProofId -or $runtime.sourceIdentity -cne $ExpectedRuntimeSourceIdentity) { Stop-Gate 'runtime.proof-identity-mismatch' }
if ($runtime.status -cne 'passed' -or $runtime.fresh -ne $true -or $runtime.environment -cne 'Testnet') { Stop-Gate 'runtime.proof-not-trusted-fresh' }
$actualGateRefs = @($runtime.gateEvidenceRefs)
foreach ($gateRef in $ExpectedEvidenceGateRefs) { if ($actualGateRefs -cnotcontains $gateRef) { Stop-Gate 'runtime.gate-ref-missing' } }

$suite = Get-AnchoredJson $FullReleaseEvidencePath $ExpectedFullReleaseEvidenceHash 'full-release'
if ($suite.status -cne 'passed' -or $suite.configuration -cne 'Release' -or [int]$suite.failed -ne 0 -or [int]$suite.total -le 0) { Stop-Gate 'full-release.not-passed' }

$accepted = @{}
for ($i = 0; $i -lt $ProducerVerdictPaths.Count; $i++) {
    $verdict = Get-AnchoredJson $ProducerVerdictPaths[$i] $ExpectedProducerVerdictHashes[$i] 'producer.verdict'
    if ($verdict.verdict -cne 'ACCEPT' -or [string]::IsNullOrWhiteSpace([string]$verdict.taskId)) { Stop-Gate 'producer.not-accepted' }
    $accepted[[string]$verdict.taskId] = $true
}
foreach ($taskId in $requiredProducers) { if (-not $accepted.ContainsKey($taskId)) { Stop-Gate 'producer.required-verdict-missing' } }

$resolvedProviderRoot = Resolve-LocalFixedPath $AuthorizedProviderFixtureRoot 'provider.fixture-root'
$resolvedProviderFixture = Resolve-LocalFixedPath $ProviderFixturePath 'provider.fixture-path'
$rootWithSeparator = $resolvedProviderRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedProviderFixture.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) { Stop-Gate 'provider.fixture-outside-authorized-root' }
if (-not (Test-Path -LiteralPath $resolvedProviderRoot -PathType Container)) { Stop-Gate 'provider.fixture-root-missing' }
Assert-NoReparsePoint $resolvedProviderRoot $resolvedProviderFixture
if (-not (Test-Path -LiteralPath $resolvedProviderFixture -PathType Leaf)) { Stop-Gate 'provider.fixture-missing' }
$providerFixtureHash = (Get-FileHash -LiteralPath $resolvedProviderFixture -Algorithm SHA256).Hash.ToLowerInvariant()
if ($providerFixtureHash -cne $ExpectedProviderFixtureHash.ToLowerInvariant()) { Stop-Gate 'provider.fixture-hash-mismatch' }

$mutationEvidenceRef = $null
if ($TestnetMutationSmoke) {
    if ([string]::IsNullOrWhiteSpace($VerifiedMutationGateEvidencePath) -or $ExpectedVerifiedMutationGateEvidenceHash -notmatch '^[a-fA-F0-9]{64}$') { Stop-Gate 'mutation.gate-evidence-required' }
    $mutation = Get-AnchoredJson $VerifiedMutationGateEvidencePath $ExpectedVerifiedMutationGateEvidenceHash 'mutation.gate'
    if ($mutation.status -cne 'passed' -or $mutation.environment -cne 'Testnet' -or $mutation.authorized -ne $true) { Stop-Gate 'mutation.gate-not-authorized' }
    $mutationEvidenceRef = $mutation.evidenceId
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$commands = [Collections.Generic.List[object]]::new()
$commands.Add((New-Command 'web-lint' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'lint') 'validation'))
$commands.Add((New-Command 'web-typecheck' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'typecheck') 'validation'))
$commands.Add((New-Command 'web-build' 'pnpm' @('--dir', (Join-Path $repo 'WebUi'), 'build') 'validation'))
$commands.Add((New-Command 'full-dotnet-release-tests' (Join-Path $repo 'eng\test.ps1') @('-Configuration', 'Release') 'validation'))
$commands.Add((New-Command 'release-readiness' (Join-Path $repo 'eng\release-readiness.ps1') @('-Configuration', 'Release') 'candidate'))
$commands.Add((New-Command 'package-beta' (Join-Path $repo 'eng\package-beta.ps1') @() 'candidate'))
$commands.Add((New-Command 'dogfood-install-start-restart-rollback' (Join-Path $repo 'eng\dogfood\Test-DogfoodRelease.ps1') @('-CandidateRoot',$CandidateRoot,'-LastKnownGoodRoot',$LastKnownGoodRoot,'-ProviderReadOnly','-RequireTrustedRuntimeFresh','-VerifyInstall','-VerifyStartup','-VerifyRestartRecovery','-VerifyRollback','-RejectMainnet','-ExpectedCandidateManifestHash',$ExpectedCandidateManifestHash,'-ExpectedLastKnownGoodManifestHash',$ExpectedLastKnownGoodManifestHash,'-TrustedRuntimeProofPath',$TrustedRuntimeProofPath,'-ExpectedTrustedRuntimeProofHash',$ExpectedTrustedRuntimeProofHash,'-ExpectedRuntimeProofId',$ExpectedRuntimeProofId,'-ExpectedRuntimeSourceIdentity',$ExpectedRuntimeSourceIdentity) 'dogfood'))
$commands.Add((New-Command 'playwright-customer-routes' 'node' @('--test',(Join-Path $repo 'WebUi\tests\v1-visible-surface.test.mjs')) 'customer-surface'))
$commands.Add((New-Command 'provider-read-only' (Join-Path $repo 'eng\dogfood\Test-ProviderReadOnly.ps1') @('-FixturePath',$resolvedProviderFixture) 'provider-read-only'))
if ($TestnetMutationSmoke) { $commands.Add((New-Command 'testnet-mutation-smoke' (Join-Path $repo 'eng\smoke-review-testnet.ps1') @('-VerifiedGateEvidenceRef',[string]$mutationEvidenceRef) 'authorized-testnet-mutation')) }

[pscustomobject][ordered]@{
    schemaVersion = 'wpe.v1-candidate-gate-plan/1.0'
    valid = $true
    dryRun = $true
    environment = 'Testnet'
    mainnetRejected = $true
    mutationEnabled = [bool]$TestnetMutationSmoke
    fullReleaseEvidenceHash = $ExpectedFullReleaseEvidenceHash.ToLowerInvariant()
    commands = @($commands)
}

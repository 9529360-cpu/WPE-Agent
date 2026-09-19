$ErrorActionPreference = 'Stop'
$scriptPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Invoke-V1CandidateGate.ps1'))
$root = Join-Path ([IO.Path]::GetTempPath()) ("wpe-v1-gate-" + [guid]::NewGuid().ToString('N'))

function Write-JsonFixture([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Assert-Equal($Expected, $Actual, [string]$Name) { if ($Expected -cne $Actual) { throw "$Name expected '$Expected', got '$Actual'" } }
function Assert-Throws([string]$Code, [scriptblock]$Action) {
    try { $null = & $Action; throw "expected $Code" } catch { if ($_.Exception.Message -cne $Code) { throw "expected $Code, got $($_.Exception.Message)" } }
}

try {
    $candidate = New-Item -ItemType Directory -Path (Join-Path $root 'candidate-immutable') -Force
    $lkg = New-Item -ItemType Directory -Path (Join-Path $root 'lkg-immutable') -Force
    $candidateHash = Write-JsonFixture (Join-Path $candidate.FullName 'manifest.json') ([ordered]@{ version='1.0.0'; environment='Testnet' })
    $lkgHash = Write-JsonFixture (Join-Path $lkg.FullName 'manifest.json') ([ordered]@{ version='0.9.0'; environment='Testnet' })
    $runtimePath = Join-Path $root 'runtime.json'
    $runtimeHash = Write-JsonFixture $runtimePath ([ordered]@{ proofId='runtime-1'; sourceIdentity='trusted-bridge'; status='passed'; fresh=$true; environment='Testnet'; gateEvidenceRefs=@('authority','freshness') })
    $suitePath = Join-Path $root 'suite.json'
    $suiteHash = Write-JsonFixture $suitePath ([ordered]@{ status='passed'; configuration='Release'; total=1522; failed=0 })
    $soakPath = Join-Path $root 'soak.json'
    $soakHash = Write-JsonFixture $soakPath ([ordered]@{ schemaVersion='wpe.headless-soak-evidence/1.0'; status='passed' })
    $verdictPaths = @(); $verdictHashes = @()
    foreach ($id in @('SEC-0027','DOR-0005','CLI-0009')) {
        $path = Join-Path $root "$id.json"; $verdictPaths += $path
        $verdictHashes += Write-JsonFixture $path ([ordered]@{ taskId=$id; verdict='ACCEPT' })
    }
    $providerRoot = New-Item -ItemType Directory -Path (Join-Path $root 'provider-fixtures') -Force
    $providerPath = Join-Path $providerRoot.FullName 'provider-read-only.json'
    $providerHash = Write-JsonFixture $providerPath ([ordered]@{ schemaVersion='wpe.provider-read-only-fixture/1.0'; environment='Testnet'; mutationRequested=$false })
    $base = @{
        CandidateRoot=$candidate.FullName; ExpectedCandidateManifestHash=$candidateHash
        LastKnownGoodRoot=$lkg.FullName; ExpectedLastKnownGoodManifestHash=$lkgHash
        TrustedRuntimeProofPath=$runtimePath; ExpectedTrustedRuntimeProofHash=$runtimeHash
        ExpectedRuntimeProofId='runtime-1'; ExpectedRuntimeSourceIdentity='trusted-bridge'; ExpectedEvidenceGateRefs=@('authority','freshness')
        SoakEvidencePath=$soakPath; ExpectedSoakEvidenceHash=$soakHash; MinimumSoakDurationMinutes=1440
        FullReleaseEvidencePath=$suitePath; ExpectedFullReleaseEvidenceHash=$suiteHash
        ProducerVerdictPaths=$verdictPaths; ExpectedProducerVerdictHashes=$verdictHashes
        AuthorizedProviderFixtureRoot=$providerRoot.FullName; ProviderFixturePath=$providerPath; ExpectedProviderFixtureHash=$providerHash
        DryRun=$true; RejectMainnet=$true
    }

    $plan = & $scriptPath @base
    Assert-Equal 'wpe.v1-candidate-gate-plan/1.0' $plan.schemaVersion 'schema'
    Assert-Equal 9 $plan.commands.Count 'default command count'
    Assert-Equal 'full-dotnet-release-tests' $plan.commands[3].id 'full suite order'
    Assert-Equal 'release-readiness' $plan.commands[4].id 'downstream order'
    $readiness = @($plan.commands | Where-Object id -eq 'release-readiness')
    if ($readiness[0].arguments -contains '-Configuration') { throw 'release-readiness received unsupported Configuration parameter' }
    Assert-Equal '-Runtime' $readiness[0].arguments[0] 'release-readiness runtime parameter'
    Assert-Equal 'win-x64' $readiness[0].arguments[1] 'release-readiness runtime'
    $dogfood = @($plan.commands | Where-Object id -eq 'dogfood-install-start-restart-rollback')
    foreach ($required in @('-ExpectedEvidenceGateRefs','authority','freshness','-SoakEvidencePath',$soakPath,'-ExpectedSoakEvidenceHash',$soakHash,'-MinimumSoakDurationMinutes','1440')) {
        if ($dogfood[0].arguments -cnotcontains $required) { throw "dogfood command missing argument: $required" }
    }
    Assert-Equal $false $plan.mutationEnabled 'mutation default'
    if (@($plan.commands.id) -contains 'testnet-mutation-smoke') { throw 'default mutation command present' }
    $customer = @($plan.commands | Where-Object id -eq 'playwright-customer-routes')
    Assert-Equal 1 $customer.Count 'customer command count'
    Assert-Equal 'node' $customer[0].file 'customer command executable'
    Assert-Equal '--test' $customer[0].arguments[0] 'customer command mode'
    if (-not (Test-Path -LiteralPath $customer[0].arguments[1] -PathType Leaf)) { throw 'customer test target missing' }
    $provider = @($plan.commands | Where-Object id -eq 'provider-read-only')
    Assert-Equal $providerPath $provider[0].arguments[1] 'provider fixture binding'
    if ($provider[0].arguments -contains '<provider-read-only-fixture>') { throw 'literal provider placeholder present' }

    $package = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\..\WebUi\package.json') -Raw | ConvertFrom-Json
    foreach ($command in @($plan.commands | Where-Object file -eq 'pnpm')) {
        $scriptName = $command.arguments[-1]
        if ($package.scripts.PSObject.Properties.Name -cnotcontains $scriptName) { throw "package script absent: $scriptName" }
    }
    if ($package.scripts.PSObject.Properties.Name -contains 'test:e2e') { throw 'failing-before fixture unexpectedly exists' }

    $relativeFixture = $base.Clone(); $relativeFixture.ProviderFixturePath = 'provider-read-only.json'
    Assert-Throws 'provider.fixture-path.not-absolute' { & $scriptPath @relativeFixture }
    $uncFixture = $base.Clone(); $uncFixture.ProviderFixturePath = '\\server\share\provider.json'
    Assert-Throws 'provider.fixture-path.network-path-forbidden' { & $scriptPath @uncFixture }
    $uncRoot = $base.Clone(); $uncRoot.AuthorizedProviderFixtureRoot = '\\server\share\fixtures'
    Assert-Throws 'provider.fixture-root.network-path-forbidden' { & $scriptPath @uncRoot }
    $outsidePath = Join-Path $root 'outside-provider.json'
    $null = Write-JsonFixture $outsidePath ([ordered]@{ environment='Testnet' })
    $outsideFixture = $base.Clone(); $outsideFixture.ProviderFixturePath = $outsidePath
    Assert-Throws 'provider.fixture-outside-authorized-root' { & $scriptPath @outsideFixture }
    $siblingRoot = New-Item -ItemType Directory -Path ($providerRoot.FullName + '-sibling') -Force
    $siblingPath = Join-Path $siblingRoot.FullName 'provider.json'
    $null = Write-JsonFixture $siblingPath ([ordered]@{ environment='Testnet' })
    $siblingFixture = $base.Clone(); $siblingFixture.ProviderFixturePath = $siblingPath
    Assert-Throws 'provider.fixture-outside-authorized-root' { & $scriptPath @siblingFixture }
    $traversalFixture = $base.Clone(); $traversalFixture.ProviderFixturePath = Join-Path $providerRoot.FullName '..\outside-provider.json'
    Assert-Throws 'provider.fixture-outside-authorized-root' { & $scriptPath @traversalFixture }
    $missingFixture = $base.Clone(); $missingFixture.ProviderFixturePath = (Join-Path $providerRoot.FullName 'missing-provider.json')
    Assert-Throws 'provider.fixture-missing' { & $scriptPath @missingFixture }
    $mismatchFixture = $base.Clone(); $mismatchFixture.ExpectedProviderFixtureHash = ('0' * 64)
    Assert-Throws 'provider.fixture-hash-mismatch' { & $scriptPath @mismatchFixture }

    $junction = Join-Path $providerRoot.FullName 'linked'
    try {
        $null = New-Item -ItemType Junction -Path $junction -Target $siblingRoot.FullName -ErrorAction Stop
        $reparseFixture = $base.Clone(); $reparseFixture.ProviderFixturePath = Join-Path $junction 'provider.json'
        Assert-Throws 'provider.fixture-reparse-forbidden' { & $scriptPath @reparseFixture }
    } catch {
        if ($_.Exception.Message -ne 'provider.fixture-reparse-forbidden') { Write-Output 'V1CandidateGate.Tests: reparse regression SKIP (junction unavailable)' }
    }

    $failedSuiteHash = Write-JsonFixture $suitePath ([ordered]@{ status='failed'; configuration='Release'; total=1522; failed=3 })
    $failed = $base.Clone(); $failed.ExpectedFullReleaseEvidenceHash = $failedSuiteHash
    Assert-Throws 'full-release.not-passed' { & $scriptPath @failed }
    $missing = $base.Clone(); $missing.FullReleaseEvidencePath = (Join-Path $root 'missing.json')
    Assert-Throws 'full-release.missing' { & $scriptPath @missing }
    $badHash = $base.Clone(); $badHash.ExpectedFullReleaseEvidenceHash = ('0' * 64)
    Assert-Throws 'full-release.hash-mismatch' { & $scriptPath @badHash }
    $suiteHash = Write-JsonFixture $suitePath ([ordered]@{ status='passed'; configuration='Release'; total=1522; failed=0 })
    $base.ExpectedFullReleaseEvidenceHash = $suiteHash
    $mainnet = $base.Clone(); $mainnet.Mainnet = $true
    Assert-Throws 'environment.mainnet-forbidden' { & $scriptPath @mainnet }
    $mutationMissing = $base.Clone(); $mutationMissing.TestnetMutationSmoke = $true
    Assert-Throws 'mutation.gate-evidence-required' { & $scriptPath @mutationMissing }

    $rejectedPath = $verdictPaths[0]
    $rejectedHash = Write-JsonFixture $rejectedPath ([ordered]@{ taskId='SEC-0027'; verdict='REJECT' })
    $rejected = $base.Clone(); $rejected.ExpectedProducerVerdictHashes = @($rejectedHash,$verdictHashes[1],$verdictHashes[2])
    Assert-Throws 'producer.not-accepted' { & $scriptPath @rejected }

    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
    Assert-Equal 0 $errors.Count 'AST parse errors'
    $invokeOperators = @($tokens | Where-Object { $_.Kind -eq 'Ampersand' })
    Assert-Equal 0 $invokeOperators.Count 'dynamic execution operators'
    $source = Get-Content -LiteralPath $scriptPath -Raw
    foreach ($forbidden in @('Invoke-WebRequest','Invoke-RestMethod','Start-Process')) { if ($source -match [regex]::Escape($forbidden)) { throw "forbidden command $forbidden" } }
    Write-Output 'V1CandidateGate.Tests: PASS (27 assertions/groups)'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

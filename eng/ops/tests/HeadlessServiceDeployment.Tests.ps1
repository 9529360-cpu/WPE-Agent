$ErrorActionPreference='Stop'

function Assert($condition,[string]$message){if(-not $condition){throw "ASSERT: $message"}}
function Throws([scriptblock]$action,[string]$contains){
    try{& $action|Out-Null;throw "expected:$contains"}
    catch{if($_.Exception.Message -notlike "*$contains*"){throw}}
}

$tool=Split-Path $PSScriptRoot -Parent
$preflight=Join-Path $tool 'Test-HeadlessServiceDeployment.ps1'
$root=Join-Path ([IO.Path]::GetTempPath()) ('wpe-service-preflight-'+[Guid]::NewGuid().ToString('N'))
$candidate=Join-Path $root 'candidate'
$data=Join-Path $root 'data'
New-Item -ItemType Directory -Path (Join-Path $candidate 'headless') -Force|Out-Null
New-Item -ItemType Directory -Path (Join-Path $candidate 'maintenance') -Force|Out-Null
New-Item -ItemType Directory -Path $data -Force|Out-Null
Set-Content -LiteralPath (Join-Path $candidate 'headless\WPE-Headless.exe') -Value 'headless' -Encoding ascii
Set-Content -LiteralPath (Join-Path $candidate 'maintenance\WPE.Maintenance.exe') -Value 'maintenance' -Encoding ascii

try{
    $result=& $preflight -CandidateRoot $candidate -DataRoot $data -ServiceAccount 'MACHINE\wpe' -MaintenanceAccount 'machine\WPE'
    Assert $result.valid 'valid deployment rejected'
    Assert ($result.schemaVersion -eq 'wpe.headless-service-deployment-preflight/1.0') 'schema mismatch'
    Assert (-not $result.scmMutationPerformed) 'preflight mutated SCM'
    Assert (-not $result.credentialsAccepted) 'preflight accepted credentials'
    Assert ($result.imagePath.Contains('--data-root')) 'image path omitted data-root'
    Assert ($result.headlessExecutable.EndsWith('headless\WPE-Headless.exe')) 'headless executable binding missing'
    Assert ($result.maintenanceExecutable.EndsWith('maintenance\WPE.Maintenance.exe')) 'maintenance executable binding missing'

    Throws {& $preflight -CandidateRoot 'relative-candidate' -DataRoot $data -ServiceAccount 'a' -MaintenanceAccount 'a'} 'candidate.root.not-absolute'
    Throws {& $preflight -CandidateRoot $candidate -DataRoot '\\server\share\wpe' -ServiceAccount 'a' -MaintenanceAccount 'a'} 'data.root.network-path-forbidden'
    Throws {& $preflight -CandidateRoot $candidate -DataRoot (Join-Path $candidate 'state') -ServiceAccount 'a' -MaintenanceAccount 'a'} 'data.root-candidate-overlap'
    Throws {& $preflight -CandidateRoot $candidate -DataRoot $data -ServiceAccount 'svc-a' -MaintenanceAccount 'svc-b'} 'account.dpapi-identity-mismatch'

    Remove-Item -LiteralPath (Join-Path $candidate 'headless\WPE-Headless.exe') -Force
    Throws {& $preflight -CandidateRoot $candidate -DataRoot $data -ServiceAccount 'a' -MaintenanceAccount 'a'} 'candidate.headless-exe-missing'
    Set-Content -LiteralPath (Join-Path $candidate 'headless\WPE-Headless.exe') -Value 'headless' -Encoding ascii

    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($preflight,[ref]$tokens,[ref]$errors)
    Assert ($errors.Count -eq 0) 'preflight AST parse errors'
    $source=Get-Content -LiteralPath $preflight -Raw
    foreach($forbidden in @('New-Service','Set-Service','Start-Service','Stop-Service','Restart-Service','sc.exe','Invoke-WebRequest','Invoke-RestMethod')){
        if($source -match [regex]::Escape($forbidden)){throw "forbidden mutation/network command: $forbidden"}
    }

    'PASS headless service deployment preflight'
}finally{
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

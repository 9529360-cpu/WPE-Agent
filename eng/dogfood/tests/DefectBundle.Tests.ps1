$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot '..\New-DefectBundle.ps1'
$schema=Join-Path $PSScriptRoot '..\defect-bundle.schema.json'
$root=Join-Path ([IO.Path]::GetTempPath()) ('wpe-defect-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
function Assert([bool]$value,[string]$message){if(-not $value){throw $message}}
function Throws([scriptblock]$action,[string]$code){try{&$action;throw "expected $code"}catch{if($_.Exception.Message -notmatch [regex]::Escape($code)){throw "expected $code got $($_.Exception.Message)"}}}
function Write-Input([string]$name,[hashtable]$changes=@{}){
    $value=[ordered]@{version='3.6.0';sourceIdentity='commit/abc123';manifestSha256=('a'*64);environment='Testnet';diagnosticCode='runtime.bridge-lost';failureClass='runtime-bridge';severity='P1';observedAtUtc='2026-07-23T13:30:00Z';runtimeHealth='bridge unavailable';minimalInput='open reference ui';expected='trusted snapshot visible';observed='runtime unavailable';logs=@('bridge disconnected');evidenceReferences=@('evidence/runtime.json');screenshotReferences=@('screenshots/runtime.png')}
    foreach($key in $changes.Keys){$value[$key]=$changes[$key]}
    $path=Join-Path $root ($name+'.json');$value|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding utf8;return $path
}
try{
    $schemaObject=Get-Content -Raw $schema|ConvertFrom-Json
    Assert ($schemaObject.properties.schemaVersion.const -eq 'wpe.dogfood-defect-bundle/1.0') 'schema version missing'
    Assert ($schemaObject.additionalProperties -eq $false) 'schema permits unknown output fields'

    $input=Write-Input 'valid';$output=Join-Path $root 'out';$first=&$script -InputPath $input -OutputRoot $output
    $firstBytes=[IO.File]::ReadAllBytes($first.Path);$second=&$script -InputPath $input -OutputRoot $output;$secondBytes=[IO.File]::ReadAllBytes($second.Path)
    Assert ($first.DefectId -eq $second.DefectId) 'defect id is not stable'
    Assert ($first.BundleHash -eq $second.BundleHash) 'bundle hash is not stable'
    Assert ([Convert]::ToBase64String($firstBytes) -eq [Convert]::ToBase64String($secondBytes)) 'bundle bytes are not deterministic'
    $bundle=Get-Content -Raw $first.Path|ConvertFrom-Json
    Assert ($bundle.routing -eq 'immediate-rollback') 'P1 runtime bridge did not roll back'
    Assert ($bundle.permanentRegression.required -and $bundle.permanentRegression.regressionId -eq ('REG-'+$bundle.defectId)) 'permanent regression link missing'

    foreach($failureClass in @('safety','authority','runtime-bridge','startup','permission')){
        $result=&$script -InputPath (Write-Input ('route-'+$failureClass) @{failureClass=$failureClass;severity='P3'}) -OutputRoot $output
        Assert ($result.Routing -eq 'immediate-rollback') "$failureClass did not roll back"
    }
    foreach($severity in @('P0','P1')){Assert ((&$script -InputPath (Write-Input ('severity-'+$severity) @{failureClass='ui';severity=$severity}) -OutputRoot $output).Routing -eq 'immediate-rollback') "$severity did not roll back"}
    foreach($severity in @('P2','P3')){Assert ((&$script -InputPath (Write-Input ('next-'+$severity) @{failureClass='ui';severity=$severity}) -OutputRoot $output).Routing -eq 'next-batch') "$severity did not route next batch"}

    $adversarial=Write-Input 'adversarial' @{runtimeHealth='user@example.com +1 (415) 555-1212 10.0.0.8';minimalInput='apiKey=AKIA_TEST secret=hunter2 destination=https://private.invalid/hook';observed='accountId=acct-7 destinationId=chat-9 account=broker-42';logs=@('Bearer abc.def.ghi','signature=deadbeef','password: pw-leak-77')}
    $safe=&$script -InputPath $adversarial -OutputRoot $output;$safeText=Get-Content -Raw $safe.Path
    foreach($forbidden in @('user@example.com','555-1212','10.0.0.8','AKIA_TEST','hunter2','private.invalid','acct-7','chat-9','broker-42','abc.def.ghi','deadbeef','pw-leak-77')){Assert ($safeText -notmatch [regex]::Escape($forbidden)) "sensitive value leaked: $forbidden"}

    Throws {&$script -InputPath (Write-Input 'traversal' @{evidenceReferences=@('../secret.log')}) -OutputRoot $output} 'invalid-reference'
    $malformed=Join-Path $root 'malformed.json';'{'|Set-Content $malformed;Throws {&$script -InputPath $malformed -OutputRoot $output} 'malformed-json'
    Throws {&$script -InputPath (Write-Input 'untraceable' @{sourceIdentity=''}) -OutputRoot $output} 'sourceIdentity-required'
    $oversized=Join-Path $root 'oversized.json';[IO.File]::WriteAllText($oversized,('x'*65537));Throws {&$script -InputPath $oversized -OutputRoot $output} 'size-invalid'
    Throws {&$script -InputPath (Write-Input 'mainnet' @{environment='Mainnet'}) -OutputRoot $output} 'contract-invalid'

    $source=Get-Content -Raw $script
    Assert ($source -notmatch '(?i)Invoke-WebRequest|Invoke-RestMethod|HttpClient|WebClient|Start-Process|curl|wget') 'network or external process API present'
    Assert ($source -notmatch '(?i)PlaceOrder|SubmitOrder|CancelOrder|TradingExecutionGateway|ReliableOrderExecutor') 'product mutation API present'
    'PASS DefectBundle schema/redaction/adversarial/size/traversal/routing/determinism/no-network/no-mutation'
}finally{Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}

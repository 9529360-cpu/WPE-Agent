param([string]$DotNetPath='dotnet')

$ErrorActionPreference='Stop'

$root=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appProjects=@(Get-ChildItem -LiteralPath $root -File -Filter '*.csproj')
if($appProjects.Count -ne 1){throw "dependency-audit.application-project-count:$($appProjects.Count)"}
$applicationProject=$appProjects[0].FullName

$checks=@(
    @{ Project=$applicationProject; Label='application'; Framework='net8.0' },
    @{ Project=$applicationProject; Label='application'; Framework='net8.0-windows' },
    @{ Project=(Join-Path $root 'WPE.Tests/WPE.Tests.csproj'); Label='tests'; Framework='net8.0-windows' },
    @{ Project=(Join-Path $root 'WPE.Headless/WPE.Headless.csproj'); Label='headless'; Framework='net8.0' },
    @{ Project=(Join-Path $root 'WPE.Maintenance/WPE.Maintenance.csproj'); Label='maintenance'; Framework='net8.0' },
    @{ Project=(Join-Path $root 'WPE.ExchangeMcp/WPE.ExchangeMcp.csproj'); Label='exchange-mcp'; Framework='net8.0' },
    @{ Project=(Join-Path $root 'WPE.BinanceMcp/WPE.BinanceMcp.csproj'); Label='binance-mcp'; Framework='net8.0' }
)

foreach($check in $checks){
    $project=[System.IO.Path]::GetFullPath($check.Project)
    if(-not (Test-Path -LiteralPath $project -PathType Leaf)){
        throw "dependency-audit.project-missing:$($check.Label)"
    }

    $arguments=@(
        'list',$project,'package',
        '--framework',$check.Framework,
        '--vulnerable',
        '--include-transitive',
        '--format','json',
        '--output-version','1'
    )
    $jsonLines=@(& $DotNetPath @arguments)
    if($LASTEXITCODE -ne 0){
        throw "dependency-audit.command-failed:$($check.Label):$($check.Framework):exit=$LASTEXITCODE"
    }
    $jsonText=$jsonLines -join [Environment]::NewLine
    if([string]::IsNullOrWhiteSpace($jsonText)){
        throw "dependency-audit.empty-result:$($check.Label):$($check.Framework)"
    }

    try{$report=$jsonText|ConvertFrom-Json}
    catch{throw "dependency-audit.invalid-json:$($check.Label):$($check.Framework)"}

    $errors=@($report.problems|Where-Object {$_.level -eq 'error'})
    if($errors.Count -gt 0){
        $details=($errors|ForEach-Object {$_.text}) -join ' | '
        throw "dependency-audit.report-error:$($check.Label):$($check.Framework):$details"
    }

    if($jsonText -match '"vulnerabilities"\s*:\s*\[\s*\{'){
        Write-Host $jsonText
        throw "dependency-audit.vulnerability-found:$($check.Label):$($check.Framework)"
    }

    Write-Host "dependency-audit passed: $($check.Label) [$($check.Framework)]"
}

Write-Output 'NuGet transitive vulnerability audit passed.'

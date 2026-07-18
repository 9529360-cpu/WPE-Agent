param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "publish"
)

Write-Host "==> Publishing 币安量化机器人 ($Configuration, $Runtime) 到 $Output ..." -ForegroundColor Cyan

dotnet publish "币安量化机器人.csproj" -c $Configuration -r $Runtime -p:PublishSingleFile=true --self-contained false -o $Output

if ($LASTEXITCODE -ne 0) {
    Write-Host "构建失败，退出码 $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "==> 复制配置与数据目录 ..." -ForegroundColor Cyan

$dataSource = Join-Path -Path (Get-Location) -ChildPath "Data"
$dataTarget = Join-Path -Path (Get-Location) -ChildPath $Output

if (Test-Path $dataSource) {
    Copy-Item -Path $dataSource -Destination $dataTarget -Recurse -Force
}

if (-not (Test-Path (Join-Path $dataTarget "appsettings.json"))) {
    "@{`"TradingMode`"=`"Testnet`";`"DefaultSymbol`"=`"BTCUSDT`";`"DefaultTimeframe`"=`"1m`"}" | Out-Null
}

Write-Host "发布完成，输出目录：$Output" -ForegroundColor Green



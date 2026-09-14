$ErrorActionPreference = 'Stop'

[System.Text.Encoding]::RegisterProvider([System.Text.CodePagesEncodingProvider]::Instance)
$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
$strictGbk = [System.Text.Encoding]::GetEncoding(
    936,
    [System.Text.EncoderExceptionFallback]::new(),
    [System.Text.DecoderExceptionFallback]::new())
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

$legacyPaths = @(
    'Application/Services/LeverageController.cs',
    'Application/Services/PositionManager.cs',
    'Application/Services/StrategyEvaluationService.cs',
    'Application/Services/StrategyFilterService.cs',
    'Core/Data/IDataCollectionService.cs',
    'Core/Persistence/ITradingRecorder.cs',
    'Core/Risk/ILeverageController.cs',
    'Core/Risk/IPositionManager.cs',
    'Core/Strategy/IStrategyEvaluationService.cs',
    'Core/Strategy/IStrategyFilterService.cs',
    'Infrastructure/Data/BinanceDataCollectionService.cs',
    'Infrastructure/Persistence/SqliteTradingRecorder.cs',
    'Infrastructure/Persistence/TradeRepository.cs',
    'Services/IRawStreamRecorder.cs',
    'Services/RawStreamRecorder.cs',
    'Tests/Integration/ClosedLoopIntegrationTests.cs',
    'Tests/Unit/PositionManagerTests.cs'
)

$converted = 0
foreach ($path in $legacyPaths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected legacy source file is missing: $path"
    }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    try {
        [void]$strictUtf8.GetString($bytes)
        continue
    }
    catch [System.Text.DecoderFallbackException] {
        # Expected only for the legacy CP936 files listed above.
    }

    $text = $strictGbk.GetString($bytes)
    $roundTrip = $strictGbk.GetBytes($text)
    if ($roundTrip.Length -ne $bytes.Length) {
        throw "CP936 round-trip length mismatch for $path"
    }
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -ne $roundTrip[$i]) {
            throw "CP936 round-trip mismatch for $path at byte $i"
        }
    }

    [System.IO.File]::WriteAllText($path, $text, $utf8NoBom)
    $converted++
}

Write-Host "Normalized $converted legacy C# files from CP936 to UTF-8."

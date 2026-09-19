namespace WPE.Tests;

public sealed class HeadlessSoakEvidenceBoundaryTests
{
    [Fact]
    public void SoakWatcherIsReadOnlyWithRespectToRuntimeAndTradingAuthority()
    {
        var root=Root();
        var watcher=File.ReadAllText(Path.Combine(root,"eng","ops","Watch-HeadlessSoak.ps1"));
        var verifier=File.ReadAllText(Path.Combine(root,"eng","ops","Test-HeadlessSoakEvidence.ps1"));

        Assert.Contains("Runtime/headless-health-v1.json",watcher,StringComparison.Ordinal);
        Assert.Contains("wpe.headless-soak-evidence/1.0",watcher,StringComparison.Ordinal);
        Assert.Contains("wpe.headless-soak-sample/1.0",watcher,StringComparison.Ordinal);
        Assert.Contains("soak.output-inside-authoritative-data-forbidden",watcher,StringComparison.Ordinal);
        Assert.Contains("Get-FileHash",watcher,StringComparison.Ordinal);
        Assert.Contains("sourceIdentity = $SourceIdentity",watcher,StringComparison.Ordinal);
        Assert.Contains("candidateManifestSha256 = $CandidateManifestSha256.ToLowerInvariant()",watcher,StringComparison.Ordinal);
        Assert.Contains("soak.source-identity-mismatch",verifier,StringComparison.Ordinal);
        Assert.Contains("soak.candidate-manifest-mismatch",verifier,StringComparison.Ordinal);
        Assert.Contains("samples-hash-mismatch",verifier,StringComparison.Ordinal);
        Assert.Contains("duration-insufficient",verifier,StringComparison.Ordinal);

        foreach(var source in new[]{watcher,verifier})
        foreach(var forbidden in new[]{
            "Start-Service","Stop-Service","Restart-Service","sc.exe","NamedPipeClientStream",
            "HttpClient","Invoke-WebRequest","Invoke-RestMethod","TcpClient","TradingRuntimeHost",
            "AutoTradingAgent","IExchangeAdapter","PlaceOrder","EmergencyCloseAll","Mainnet"
        })
            Assert.DoesNotContain(forbidden,source,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SoakEvidenceProjectionDoesNotPersistPrivateRuntimeIdentifiers()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"eng","ops","Watch-HeadlessSoak.ps1"));
        var sampleStart=source.IndexOf("$sample = [ordered]@{",StringComparison.Ordinal);
        var sampleEnd=source.IndexOf("$writer.WriteLine",sampleStart,StringComparison.Ordinal);
        var projection=source[sampleStart..sampleEnd];

        foreach(var forbiddenField in new[]{"ProcessId =","RunId =","EventSequence =","UserName =","Symbol =","Position =","Order =","Provider ="})
            Assert.DoesNotContain(forbiddenField,projection,StringComparison.OrdinalIgnoreCase);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

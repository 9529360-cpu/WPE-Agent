namespace WPE.Tests;

public sealed class ReleaseSoakGateBoundaryTests
{
    [Fact]
    public void SlotPromotionRequiresCandidateBoundSoakEvidence()
    {
        var root=Root();
        var release=File.ReadAllText(Path.Combine(root,"eng","dogfood","Test-DogfoodRelease.ps1"));
        var slot=File.ReadAllText(Path.Combine(root,"eng","dogfood","Switch-DogfoodSlot.ps1"));
        var workflow=File.ReadAllText(Path.Combine(root,".github","workflows","dotnet.yml"));

        Assert.Contains("SoakEvidencePath",release,StringComparison.Ordinal);
        Assert.Contains("ExpectedSoakEvidenceHash",release,StringComparison.Ordinal);
        Assert.Contains("MinimumSoakDurationMinutes = 1440",release,StringComparison.Ordinal);
        Assert.Contains("Test-HeadlessSoakEvidence.ps1",release,StringComparison.Ordinal);
        Assert.Contains("candidate.Manifest.sourceIdentity",release,StringComparison.Ordinal);
        Assert.Contains("candidate.ManifestHash",release,StringComparison.Ordinal);
        Assert.Contains("SoakEvidenceSha256",release,StringComparison.Ordinal);

        Assert.Contains("ExpectedSoakEvidenceHash",slot,StringComparison.Ordinal);
        Assert.Contains("soakEvidenceSha256=$result.SoakEvidenceSha256",slot,StringComparison.Ordinal);
        Assert.Contains("Verify dogfood release slots",workflow,StringComparison.Ordinal);
        Assert.Contains("./eng/dogfood/tests/ReleaseSlots.Tests.ps1",workflow,StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseSoakGateDoesNotCreateTradingOrMainnetAuthority()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"eng","dogfood","Test-DogfoodRelease.ps1"));

        Assert.Contains("mainnet.always-forbidden",source,StringComparison.Ordinal);
        Assert.Contains("provider.read-only-required",source,StringComparison.Ordinal);
        foreach(var forbidden in new[]{
            "PlaceOrder","SubmitOrder","EmergencyCloseAll","ChangeAuthorizationModeAsync",
            "Start-Service","Stop-Service","Restart-Service","HttpClient","TcpClient"
        })
            Assert.DoesNotContain(forbidden,source,StringComparison.OrdinalIgnoreCase);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,"..","..","..",".."));
}

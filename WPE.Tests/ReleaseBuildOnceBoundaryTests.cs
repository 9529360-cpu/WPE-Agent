namespace WPE.Tests;

public sealed class ReleaseBuildOnceBoundaryTests
{
    [Fact]
    public void SigningAndPackagingConsumeReadinessArtifactsWithoutRebuildingThem()
    {
        var root=Root();
        var signing=File.ReadAllText(Path.Combine(root,"eng","sign-beta.ps1"));
        var packaging=File.ReadAllText(Path.Combine(root,"eng","package-beta.ps1"));
        var verification=File.ReadAllText(Path.Combine(root,"eng","verify-beta-package.ps1"));

        foreach(var source in new[]{signing,packaging,verification})
        {
            Assert.DoesNotContain("dotnet publish",source,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dotnet build",source,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pnpm build",source,StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("inputTreeSha256",signing,StringComparison.Ordinal);
        Assert.Contains("outputTreeSha256",signing,StringComparison.Ordinal);
        Assert.Contains("Signing staging does not match release-readiness artifact",signing,StringComparison.Ordinal);

        Assert.Contains("Runtime bundle copy changed artifact bytes",packaging,StringComparison.Ordinal);
        Assert.Contains("readinessReportSha256",packaging,StringComparison.Ordinal);
        Assert.Contains("Signing transition artifact facts do not match package bytes",verification,StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseReadinessRemainsTheOnlyRuntimeArtifactBuilderInTheReleaseChain()
    {
        var readiness=File.ReadAllText(Path.Combine(Root(),"eng","release-readiness.ps1"));

        Assert.Contains("Headless publish",readiness,StringComparison.Ordinal);
        Assert.Contains("Maintenance publish",readiness,StringComparison.Ordinal);
        Assert.Contains("Desktop release publish",readiness,StringComparison.Ordinal);
        Assert.Contains("treeSha256",readiness,StringComparison.Ordinal);
        Assert.Contains("Release readiness requires a clean source tree",readiness,StringComparison.Ordinal);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,"..","..","..",".."));
}

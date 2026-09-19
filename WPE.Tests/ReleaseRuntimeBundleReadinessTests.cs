namespace WPE.Tests;

public sealed class ReleaseRuntimeBundleReadinessTests
{
    [Fact]
    public void ReleaseReadinessBindsDesktopHeadlessAndMaintenanceToOneSourceIdentity()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"eng","release-readiness.ps1"));

        Assert.Contains("[string]$HeadlessOutput",source,StringComparison.Ordinal);
        Assert.Contains("[string]$MaintenanceOutput",source,StringComparison.Ordinal);
        Assert.Contains("$sourceCommit = (& git -C $root rev-parse HEAD).Trim()",source,StringComparison.Ordinal);
        Assert.Contains("Release readiness requires a clean source tree",source,StringComparison.Ordinal);
        Assert.Contains("source = [ordered]@{ commit = $sourceCommit; dirty = $sourceDirty }",source,StringComparison.Ordinal);
        Assert.Contains("artifacts = [ordered]@{",source,StringComparison.Ordinal);
        Assert.Contains("desktop = $desktopArtifact",source,StringComparison.Ordinal);
        Assert.Contains("headless = $headlessArtifact",source,StringComparison.Ordinal);
        Assert.Contains("maintenance = $maintenanceArtifact",source,StringComparison.Ordinal);
        Assert.Contains("treeSha256 = Get-TextSha256",source,StringComparison.Ordinal);

        Assert.Contains("Invoke-Step \"Headless publish\"",source,StringComparison.Ordinal);
        Assert.Contains("Invoke-Step \"Maintenance publish\"",source,StringComparison.Ordinal);
        Assert.Contains("Invoke-Step \"Desktop release publish\"",source,StringComparison.Ordinal);
        Assert.Contains("-p:Version=$productVersion",source,StringComparison.Ordinal);
        Assert.Contains("-p:FileVersion=$assemblyVersion",source,StringComparison.Ordinal);
        Assert.Contains("Headless binary version does not match product Version",source,StringComparison.Ordinal);
        Assert.Contains("Maintenance binary version does not match product Version",source,StringComparison.Ordinal);
        Assert.Contains("Published binary version",source,StringComparison.Ordinal);
    }

    [Fact]
    public void NonDesktopArtifactsStayPresentationFreeAndStateFree()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"eng","release-readiness.ps1"));

        Assert.Contains("Assert-HeadlessArtifact",source,StringComparison.Ordinal);
        Assert.Contains("Assert-MaintenanceArtifact",source,StringComparison.Ordinal);
        Assert.Contains("Assert-NoRuntimeStateOrSecrets",source,StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsDesktop.App",source,StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2",source,StringComparison.Ordinal);
        Assert.Contains("ScottPlot.WPF",source,StringComparison.Ordinal);
        Assert.Contains("PresentationFramework",source,StringComparison.Ordinal);
        Assert.Contains("agent-settings|appsettings|order-state",source,StringComparison.Ordinal);
        Assert.Contains("@(\".db\", \".sqlite\", \".sqlite3\", \".pem\", \".key\", \".p12\", \".pfx\", \".env\", \".pdb\", \".cs\", \".csproj\", \".sln\", \".ps1\")",source,StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessDoesNotClaimUploadDeploymentOrMainnet()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"eng","release-readiness.ps1"));

        Assert.Contains("mainnetEnabled = $false",source,StringComparison.Ordinal);
        Assert.Contains("deploymentPerformed = $false",source,StringComparison.Ordinal);
        Assert.Contains("uploadPerformed = $false",source,StringComparison.Ordinal);
        Assert.DoesNotContain("Mainnet = $true",source,StringComparison.Ordinal);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,"..","..","..",".."));
}

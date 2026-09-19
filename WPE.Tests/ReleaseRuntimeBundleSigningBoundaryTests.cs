namespace WPE.Tests;

public sealed class ReleaseRuntimeBundleSigningBoundaryTests
{
    [Fact]
    public void SigningUsesOneApprovedIdentityAcrossAllRuntimeExecutables()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"eng","sign-beta.ps1"));

        Assert.Contains("[string[]]$AdditionalPublishPaths = @()",source,StringComparison.Ordinal);
        Assert.Contains("$signingRoots = @($PublishPath) + @($AdditionalPublishPaths)",source,StringComparison.Ordinal);
        Assert.Contains("Expected exactly one top-level executable in signing staging",source,StringComparison.Ordinal);
        Assert.Contains("/sha1 $certificate.Thumbprint",source,StringComparison.Ordinal);
        Assert.Contains("foreach ($executable in $executables)",source,StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $executable.FullName",source,StringComparison.Ordinal);
        Assert.Contains("Signing and verification passed for $($executables.Count) runtime executable(s).",source,StringComparison.Ordinal);
    }

    [Fact]
    public void SigningStillRejectsUntrustedCertificateAndSecretFileFlows()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"eng","sign-beta.ps1"));

        Assert.Contains("Self-signed certificates are prohibited",source,StringComparison.Ordinal);
        Assert.Contains("The certificate subject does not match the approved legal publisher.",source,StringComparison.Ordinal);
        Assert.Contains("The certificate does not include the Code Signing EKU.",source,StringComparison.Ordinal);
        Assert.DoesNotContain(".pfx",source,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password",source,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-WebRequest",source,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upload",source,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllSigningRootsArePreflightedBeforeFirstSignatureMutation()
    {
        var source=File.ReadAllText(Path.Combine(Root(),"eng","sign-beta.ps1"));
        var collect=source.IndexOf("$executables.Add($rootExecutables[0])",StringComparison.Ordinal);
        var duplicate=source.IndexOf("Signing staging resolved the same executable more than once.",StringComparison.Ordinal);
        var firstSign=source.IndexOf("& $signTool sign",StringComparison.Ordinal);

        Assert.True(collect >= 0 && duplicate > collect && firstSign > duplicate);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,"..","..","..",".."));
}

namespace WPE.Tests;

public sealed class ReleaseRuntimeBundlePackageBoundaryTests
{
    [Fact]
    public void BetaPackageContainsExactDesktopHeadlessAndMaintenanceRoots()
    {
        var source=Read("eng","package-beta.ps1");

        Assert.Contains("[string]$HeadlessPublishPath",source,StringComparison.Ordinal);
        Assert.Contains("[string]$MaintenancePublishPath",source,StringComparison.Ordinal);
        Assert.Contains("[string]$SigningResultPath",source,StringComparison.Ordinal);
        Assert.Contains("[string]$ExpectedSignerSubject",source,StringComparison.Ordinal);
        Assert.Contains("[string]$ExpectedSignerThumbprint",source,StringComparison.Ordinal);
        Assert.Contains("Executable = \"WPE-Agent.exe\"",source,StringComparison.Ordinal);
        Assert.Contains("Executable = \"WPE-Headless.exe\"",source,StringComparison.Ordinal);
        Assert.Contains("Executable = \"WPE.Maintenance.exe\"",source,StringComparison.Ordinal);
        Assert.Contains("Runtime bundle signature states must be all Valid or all NotSigned.",source,StringComparison.Ordinal);
        Assert.Contains("Runtime bundle signer identity does not match the approved publisher.",source,StringComparison.Ordinal);
        Assert.Contains("Runtime bundle executable timestamp is missing",source,StringComparison.Ordinal);

        Assert.Contains("$payloadRoot = Join-Path $packageRoot \"app\"",source,StringComparison.Ordinal);
        Assert.Contains("$headlessPayloadRoot = Join-Path $packageRoot \"headless\"",source,StringComparison.Ordinal);
        Assert.Contains("$maintenancePayloadRoot = Join-Path $packageRoot \"maintenance\"",source,StringComparison.Ordinal);
        Assert.Contains("Runtime bundle copy changed artifact bytes",source,StringComparison.Ordinal);
        Assert.Contains("SIGNING-RESULT.json",source,StringComparison.Ordinal);
        Assert.Contains("SIGNING-RESULT.p7s",source,StringComparison.Ordinal);
        Assert.Contains("Assert-DetachedSigningAttestation",source,StringComparison.Ordinal);
        Assert.Contains("transitionSignatureSha256",source,StringComparison.Ordinal);
    }

    [Fact]
    public void PackageManifestAndSbomCoverTheCompleteRuntimeBundle()
    {
        var source=Read("eng","package-beta.ps1");

        Assert.Contains("path = $packageRelative",source,StringComparison.Ordinal);
        Assert.Contains("\"app/$relative\"",source,StringComparison.Ordinal);
        Assert.Contains("\"$($copy.Label)/$relative\"",source,StringComparison.Ordinal);
        Assert.Contains("WPE.Headless/obj/project.assets.json",source,StringComparison.Ordinal);
        Assert.Contains("WPE.Maintenance/obj/project.assets.json",source,StringComparison.Ordinal);
        Assert.Contains("foreach ($dependencyProject in @($projectFile, $headlessProject, $maintenanceProject))",source,StringComparison.Ordinal);
        Assert.Contains("runtimeArtifacts = @($artifactStates",source,StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentVerifierReplaysManifestReadinessSigningAndSignerIdentity()
    {
        var source=Read("eng","verify-beta-package.ps1");

        Assert.Contains("[string]$ExpectedSignerSubject",source,StringComparison.Ordinal);
        Assert.Contains("[string]$ExpectedSignerThumbprint",source,StringComparison.Ordinal);
        Assert.Contains("Payload manifest does not exactly cover the runtime bundle files.",source,StringComparison.Ordinal);
        Assert.Contains("Assert-ZipEntriesSafe",source,StringComparison.Ordinal);
        Assert.Contains("Archive entry escapes verification root.",source,StringComparison.Ordinal);
        Assert.Contains("Archive contains duplicate normalized entry paths.",source,StringComparison.Ordinal);
        Assert.Contains("Archive must contain exactly one top-level package directory.",source,StringComparison.Ordinal);
        Assert.Contains("Resolve-InPackageRoot",source,StringComparison.Ordinal);
        Assert.Contains("Package metadata path escapes package root.",source,StringComparison.Ordinal);
        Assert.Contains("PAYLOAD-SHA256SUMS does not match FILE-MANIFEST.json.",source,StringComparison.Ordinal);
        Assert.Contains("$expectedSumLine = \"$zipHash  $([System.IO.Path]::GetFileName($zip))\"",source,StringComparison.Ordinal);
        Assert.Contains("(Get-Item -LiteralPath $path).Length -ne [long]$entry.size",source,StringComparison.Ordinal);
        Assert.Contains("Runtime package signature states are mixed.",source,StringComparison.Ordinal);
        Assert.Contains("Runtime package executable timestamp is missing",source,StringComparison.Ordinal);
        Assert.Contains("TimeStamperCertificate",source,StringComparison.Ordinal);
        Assert.Contains("wpe.runtime-bundle-signing/1.1",source,StringComparison.Ordinal);
        Assert.Contains("Signing transition artifact facts do not match package bytes",source,StringComparison.Ordinal);
        Assert.Contains("Signed runtime bundle verification requires the approved signer subject and thumbprint.",source,StringComparison.Ordinal);
        Assert.Contains("Unsigned runtime bundle must not contain a signing transition result.",source,StringComparison.Ordinal);
        Assert.Contains("Runtime bundle signing attestation signature is invalid.",source,StringComparison.Ordinal);
        Assert.Contains("Runtime bundle signing attestation identity does not match the approved publisher.",source,StringComparison.Ordinal);
        Assert.Contains("Signing transition signature hash mismatch.",source,StringComparison.Ordinal);
        Assert.Contains("Unsigned runtime bundle must not contain a signing transition signature.",source,StringComparison.Ordinal);
        Assert.Contains("runtimeArtifacts = $artifactStates.Count",source,StringComparison.Ordinal);
    }

    [Fact]
    public void BundlePackagingDoesNotAddUploadDeploymentOrMainnetAuthority()
    {
        foreach(var source in new[]{Read("eng","package-beta.ps1"),Read("eng","verify-beta-package.ps1")})
        {
            Assert.DoesNotContain("Mainnet = $true",source,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Invoke-WebRequest",source,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Start-Service",source,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PlaceOrder",source,StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Read(params string[] parts)=>
        File.ReadAllText(Path.Combine(new[]{Root()}.Concat(parts).ToArray()));

    private static string Root()=>Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,"..","..","..",".."));
}

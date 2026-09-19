using WpeAgent.Headless;

namespace WPE.Tests;

public sealed class HeadlessDataRootBootstrapTests
{
    [Fact]
    public void ExplicitAbsoluteRootIsAccepted()
    {
        var root = Path.Combine(Path.GetTempPath(), "wpe-service-root");

        var result = HeadlessDataRootBootstrap.Resolve(
            new[] { "--data-root", root },
            null);

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), result.Root);
        Assert.Equal("headless.data-root-explicit", result.Code);
    }

    [Fact]
    public void MatchingEnvironmentAndArgumentResolveToOneAuthority()
    {
        var root = Path.Combine(Path.GetTempPath(), "wpe-service-root");

        var result = HeadlessDataRootBootstrap.Resolve(
            new[] { "--data-root", root + Path.DirectorySeparatorChar },
            root);

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), result.Root);
    }

    [Fact]
    public void DriveRootRemainsFullyQualified()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrWhiteSpace(root));

        var result = HeadlessDataRootBootstrap.Resolve(
            new[] { "--data-root", root! },
            null);

        Assert.True(result.Success);
        Assert.True(Path.IsPathFullyQualified(result.Root));
        Assert.Equal(root, result.Root, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(DriveType.Fixed, new DriveInfo(result.Root!).DriveType);
    }

    [Fact]
    public void ConflictingRootsFailClosed()
    {
        var argumentRoot = Path.Combine(Path.GetTempPath(), "wpe-service-a");
        var environmentRoot = Path.Combine(Path.GetTempPath(), "wpe-service-b");

        var result = HeadlessDataRootBootstrap.Resolve(
            new[] { "--data-root", argumentRoot },
            environmentRoot);

        Assert.False(result.Success);
        Assert.Equal("headless.data-root-conflict", result.Code);
    }

    [Theory]
    [InlineData("--data-root")]
    [InlineData("--data-root", "relative-state")]
    [InlineData("--data-root", @"\\server\share\wpe-state")]
    [InlineData("--data-root", "//server/share/wpe-state")]
    [InlineData("--data-root", @"C:\one", "--data-root", @"C:\two")]
    public void InvalidOrDuplicateArgumentsFailClosed(params string[] args)
    {
        var result = HeadlessDataRootBootstrap.Resolve(args, null);

        Assert.False(result.Success);
        Assert.StartsWith("headless.data-root-", result.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingReparseComponentIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "wpe-data-root-link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(testRoot, "target");
        var link = Path.Combine(testRoot, "link");
        Directory.CreateDirectory(target);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or
                IOException or
                PlatformNotSupportedException)
            {
                return;
            }

            var result = HeadlessDataRootBootstrap.Resolve(
                new[] { "--data-root", Path.Combine(link, "state") },
                null);

            Assert.False(result.Success);
            Assert.Equal("headless.data-root-argument-invalid", result.Code);
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public void MissingExplicitRootRemainsAvailableForInteractiveDefaultOnly()
    {
        var result = HeadlessDataRootBootstrap.Resolve(Array.Empty<string>(), null);

        Assert.True(result.Success);
        Assert.Null(result.Root);
        Assert.Equal("headless.data-root-default", result.Code);
    }

    [Fact]
    public void RelativeOrUncEnvironmentRootFailsClosed()
    {
        Assert.False(HeadlessDataRootBootstrap.Resolve(
            Array.Empty<string>(),
            "relative-state").Success);
        Assert.False(HeadlessDataRootBootstrap.Resolve(
            Array.Empty<string>(),
            @"\\server\share\wpe-state").Success);
        Assert.False(HeadlessDataRootBootstrap.Resolve(
            Array.Empty<string>(),
            "//server/share/wpe-state").Success);
    }
}

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
    [InlineData("--data-root", @"C:\one", "--data-root", @"C:\two")]
    public void InvalidOrDuplicateArgumentsFailClosed(params string[] args)
    {
        var result = HeadlessDataRootBootstrap.Resolve(args, null);

        Assert.False(result.Success);
        Assert.StartsWith("headless.data-root-", result.Code, StringComparison.Ordinal);
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
    }
}

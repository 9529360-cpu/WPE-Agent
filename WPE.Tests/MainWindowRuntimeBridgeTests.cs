namespace WPE.Tests;

public sealed class DesktopRuntimeHostTests
{
    [Fact]
    public void BuildRuntimeJson_RefreshesAuthorizationStateBeforeSnapshotCreation()
    {
        var method = BuildRuntimeJsonMethod();

        var refresh = "ServiceLocator.RuntimeAuthorization.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();";
        var create = "var snapshot = RuntimeSnapshotFactory.Create(";
        var refreshIndex = method.IndexOf(refresh, StringComparison.Ordinal);
        var createIndex = method.IndexOf(create, StringComparison.Ordinal);

        Assert.True(refreshIndex >= 0, "BuildRuntimeJson must refresh RuntimeAuthorization before reading it.");
        Assert.True(createIndex > refreshIndex, "RuntimeSnapshotFactory.Create must happen after RuntimeAuthorization.RefreshAsync.");
    }

    [Fact]
    public void BuildRuntimeJson_PassesRuntimeAuthorizationReadAsAuthorizationState()
    {
        var method = BuildRuntimeJsonMethod();
        var createStart = method.IndexOf("var snapshot = RuntimeSnapshotFactory.Create(", StringComparison.Ordinal);
        var createEnd = method.IndexOf(");", createStart, StringComparison.Ordinal);

        Assert.True(createStart >= 0 && createEnd > createStart, "Could not locate the RuntimeSnapshotFactory.Create call.");

        var createCall = method[createStart..(createEnd + 2)];
        var normalizedCreateCall = string.Join(
            " ",
            createCall.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(
            "ServiceLocator.RuntimeNotifications.Read(), ServiceLocator.RuntimeAuthorization.Read(), ServiceLocator.RuntimeEquityMarkets.Read(),",
            normalizedCreateCall,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRuntimeJson_PassesTheSingleCrossAssetResearchStoreProjection()
    {
        var method = BuildRuntimeJsonMethod();
        Assert.Equal(1, Count(method, "ServiceLocator.RuntimeCrossAssetResearch.Read()"));
        var normalized = string.Join(" ", method.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("ServiceLocator.RuntimeHistoricalCollections.Read(), ServiceLocator.RuntimeCrossAssetResearch.Read(),", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRuntimeJson_PassesTheSingleDistributionStoreProjection()
    {
        var method = BuildRuntimeJsonMethod();
        Assert.Equal(1, Count(method, "ServiceLocator.RuntimeDistribution.Read()"));
        var normalized = string.Join(" ", method.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("ServiceLocator.RuntimeCrossAssetResearch.Read(), ServiceLocator.RuntimeDistribution.Read(), ServiceLocator.PublicMarket.Read(), ServiceLocator.SecurityStorage.Read());", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRuntimeJson_PassesTheSinglePublicMarketProjection()
    {
        var method = BuildRuntimeJsonMethod();

        Assert.Equal(1, Count(method, "ServiceLocator.PublicMarket.Read()"));
    }

    [Fact]
    public void BuildRuntimeJson_PassesOnlyTheReadOnlySecurityStorageStatus()
    {
        var method=BuildRuntimeJsonMethod();Assert.Equal(1,Count(method,"ServiceLocator.SecurityStorage.Read()"));Assert.DoesNotContain("MigrateBatchAsync",method,StringComparison.Ordinal);Assert.DoesNotContain("CommitRotationAndRetireOldKeyAsync",method,StringComparison.Ordinal);Assert.DoesNotContain("RestoreAsync",method,StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRuntimeJson_RefreshesAndProjectsTeacherReadOnlyState()
    {
        var method=BuildRuntimeJsonMethod();
        Assert.Contains("ServiceLocator.RuntimeTeacher.RefreshAsync",method,StringComparison.Ordinal);
        Assert.Contains("ServiceLocator.RuntimeTeacher.Read()",method,StringComparison.Ordinal);
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private static string BuildRuntimeJsonMethod()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Services", "DesktopRuntimeHost.cs"));
        var start = source.IndexOf("public string BuildRuntimeJson()", StringComparison.Ordinal);
        var end = source.IndexOf("private static void PublishAccess(", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "Could not locate DesktopRuntimeHost.BuildRuntimeJson in source.");
        return source[start..end];
    }
}

namespace WPE.Tests;

public sealed class TradingRuntimeHostTests
{
    [Fact]
    public void BuildRuntimeJson_IsReadOnlyMemoryProjection()
    {
        var method = Method("public string BuildRuntimeJson()", "private async Task RunRuntimeSnapshotPumpAsync(");

        Assert.Contains("Volatile.Read(ref _runtimeJson)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeSnapshotFactory.Create", method, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPump_RunsOffTheCallerThreadAndRefreshesSerially()
    {
        var source = Source();
        var pump = Method("private async Task RunRuntimeSnapshotPumpAsync(", "private async Task RefreshRuntimeSnapshotAsync(");

        Assert.Contains("Task.Run(() => RunRuntimeSnapshotPumpAsync", source, StringComparison.Ordinal);
        Assert.Contains("await RefreshRuntimeSnapshotAsync(ct)", pump, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(SnapshotRefreshInterval, ct)", pump, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAll", pump, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPump_IsCancelledAndAwaitedDuringApplicationExit()
    {
        var source = Source();
        var dispose = Method("public async ValueTask DisposeAsync()", "private async Task RunRuntimeSnapshotPumpAsync(");
        var app = File.ReadAllText(Path.Combine(Root(), "App.xaml.cs"));

        Assert.Contains("IAsyncDisposable", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _disposed, 1)", dispose, StringComparison.Ordinal);
        Assert.Contains("_snapshotPumpCancellation.Cancel();", dispose, StringComparison.Ordinal);
        Assert.Contains("await _snapshotPumpTask.ConfigureAwait(false);", dispose, StringComparison.Ordinal);
        Assert.Contains("await _runtimeHost.DisposeAsync();", app, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPump_RefreshesAuthorizationStateBeforeSnapshotCreation()
    {
        var method = RefreshRuntimeSnapshotMethod();

        var refresh = "await ServiceLocator.RuntimeAuthorization.RefreshAsync(ct);";
        var create = "var snapshot = RuntimeSnapshotFactory.Create(";
        var refreshIndex = method.IndexOf(refresh, StringComparison.Ordinal);
        var createIndex = method.IndexOf(create, StringComparison.Ordinal);

        Assert.True(refreshIndex >= 0, "The background snapshot pump must refresh RuntimeAuthorization before reading it.");
        Assert.True(createIndex > refreshIndex, "RuntimeSnapshotFactory.Create must happen after RuntimeAuthorization.RefreshAsync.");
        Assert.DoesNotContain("GetAwaiter().GetResult()", method, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPump_PassesRuntimeAuthorizationReadAsAuthorizationState()
    {
        var method = RefreshRuntimeSnapshotMethod();
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
    public void SnapshotPump_PassesTheSingleCrossAssetResearchStoreProjection()
    {
        var method = RefreshRuntimeSnapshotMethod();
        Assert.Equal(1, Count(method, "ServiceLocator.RuntimeCrossAssetResearch.Read()"));
        var normalized = string.Join(" ", method.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("ServiceLocator.RuntimeHistoricalCollections.Read(), ServiceLocator.RuntimeCrossAssetResearch.Read(),", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPump_PassesTheSingleDistributionStoreProjection()
    {
        var method = RefreshRuntimeSnapshotMethod();
        Assert.Equal(1, Count(method, "ServiceLocator.RuntimeDistribution.Read()"));
        var normalized = string.Join(" ", method.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("ServiceLocator.RuntimeCrossAssetResearch.Read(), ServiceLocator.RuntimeDistribution.Read(), ServiceLocator.PublicMarket.Read(), ServiceLocator.SecurityStorage.Read());", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPump_PassesTheSinglePublicMarketProjection()
    {
        var method = RefreshRuntimeSnapshotMethod();
        Assert.Equal(1, Count(method, "ServiceLocator.PublicMarket.Read()"));
    }

    [Fact]
    public void SnapshotPump_PassesOnlyTheReadOnlySecurityStorageStatus()
    {
        var method = RefreshRuntimeSnapshotMethod();
        Assert.Equal(1, Count(method, "ServiceLocator.SecurityStorage.Read()"));
        Assert.DoesNotContain("MigrateBatchAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("CommitRotationAndRetireOldKeyAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreAsync", method, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPump_RefreshesAndProjectsTeacherReadOnlyState()
    {
        var method = RefreshRuntimeSnapshotMethod();
        Assert.Contains("ServiceLocator.RuntimeTeacher.RefreshAsync(ct)", method, StringComparison.Ordinal);
        Assert.Contains("ServiceLocator.RuntimeTeacher.Read()", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHost_IsUiNeutralAndOwnsNormalKernelLifecycle()
    {
        var source = Source();
        var app = File.ReadAllText(Path.Combine(Root(), "App.xaml.cs"));

        foreach (var forbidden in new[] { "System.Windows", "ReferenceUiWindow", "SetupWindow", "ActivationWindow" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);

        Assert.Contains("ServiceLocator.PublicMarket.StartAsync()", source, StringComparison.Ordinal);
        Assert.Contains("AutoTradingAgent.StartDefault()", source, StringComparison.Ordinal);
        Assert.Contains("await AutoTradingAgent.StopAsync().ConfigureAwait(false);", source, StringComparison.Ordinal);
        Assert.Contains("await ServiceLocator.DisposeAsync().ConfigureAwait(false);", source, StringComparison.Ordinal);

        Assert.Contains("new TradingRuntimeHost(localIdentity)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new DesktopRuntimeHost", app, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoTradingAgent.StartDefault()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoTradingAgent.StopAsync()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceLocator.PublicMarket.StartAsync()", app, StringComparison.Ordinal);
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private static string RefreshRuntimeSnapshotMethod() =>
        Method("private async Task RefreshRuntimeSnapshotAsync(", "private static string SerializeSnapshot(");

    private static string Method(string startToken, string endToken)
    {
        var source = Source();
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        var end = source.IndexOf(endToken, start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, $"Could not locate source section starting with {startToken}.");
        return source[start..end];
    }

    private static string Source()
    {
        return File.ReadAllText(Path.Combine(Root(), "Services", "TradingRuntimeHost.cs"));
    }

    private static string Root() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}

namespace WPE.Tests;

public sealed class HeadlessLocalControlBoundaryTests
{
    [Fact]
    public void ControlPlaneIsCurrentUserNamedPipeOnly()
    {
        var root = Root();
        var source = File.ReadAllText(Path.Combine(
            root, "WPE.Headless", "HeadlessLocalControl.cs"));
        var program = File.ReadAllText(Path.Combine(
            root, "WPE.Headless", "Program.cs"));

        Assert.Contains("NamedPipeServerStream", source, StringComparison.Ordinal);
        Assert.Contains("PipeOptions.CurrentUserOnly", source, StringComparison.Ordinal);
        Assert.Contains("PipeOptions.Asynchronous", source, StringComparison.Ordinal);
        Assert.Contains("MaximumRequestBytes = 4096", source, StringComparison.Ordinal);
        Assert.Contains("RequestTimeout = TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
        Assert.Contains("MaximumHealthAge = TimeSpan.FromSeconds(15)", source, StringComparison.Ordinal);
        Assert.Contains("requestTimeout.CancelAfter(RequestTimeout)", source, StringComparison.Ordinal);
        Assert.Contains("value.ObservedAtUtc > now", source, StringComparison.Ordinal);
        Assert.Contains("now - value.ObservedAtUtc > MaximumHealthAge", source, StringComparison.Ordinal);
        Assert.Contains("headless-health-v1.json", source, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<HeadlessLocalControlWorker>()", program, StringComparison.Ordinal);

        foreach (var forbidden in new[]
        {
            "TcpListener",
            "TcpClient",
            "HttpListener",
            "HttpClient",
            "Kestrel",
            "Socket",
            "IPAddress",
            "WebSocket",
            "ListenAnyIP",
            "ListenLocalhost"
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlProtocolCannotBecomeTradingOrConfigurationAuthority()
    {
        var source = File.ReadAllText(Path.Combine(
            Root(), "WPE.Headless", "HeadlessLocalControl.cs"));

        Assert.Contains("\"health\"", source, StringComparison.Ordinal);
        Assert.Contains("\"shutdown\"", source, StringComparison.Ordinal);
        Assert.Contains("ShutdownConfirmation = \"shutdown-wpe-headless\"", source, StringComparison.Ordinal);
        Assert.Contains("control.command-unsupported", source, StringComparison.Ordinal);

        foreach (var forbidden in new[]
        {
            "agent-start",
            "AutoTradingAgent",
            "TradingRuntimeHost",
            "IExchangeAdapter",
            "ExchangeProviderCatalog",
            "submitOrder",
            "PlaceOrder",
            "EmergencyCloseAllAsync",
            "ChangeAuthorizationModeAsync",
            "Mainnet",
            "credential",
            "apiKey",
            "secret",
            "appsettings"
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShutdownAcknowledgementIsFlushedBeforeHostCancellation()
    {
        var source = File.ReadAllText(Path.Combine(
            Root(), "WPE.Headless", "HeadlessLocalControl.cs"));

        var requested = source.IndexOf(
            "control.shutdown-requested",
            StringComparison.Ordinal);
        var flush = source.IndexOf(
            "await pipe.FlushAsync(requestTimeout.Token)",
            requested,
            StringComparison.Ordinal);
        var stop = source.IndexOf(
            "applicationLifetime.StopApplication();",
            flush,
            StringComparison.Ordinal);

        Assert.True(requested >= 0 && flush > requested && stop > flush);
    }

    [Fact]
    public void HealthProjectionDoesNotExposeRuntimeIdentifiersOrPayloads()
    {
        var source = File.ReadAllText(Path.Combine(
            Root(), "WPE.Headless", "HeadlessLocalControl.cs"));

        var projectionStart = source.IndexOf(
            "public sealed record HeadlessControlHealthV1",
            StringComparison.Ordinal);
        var projectionEnd = source.IndexOf(
            "public sealed record HeadlessControlResponseV1",
            projectionStart,
            StringComparison.Ordinal);
        var projection = source[projectionStart..projectionEnd];

        foreach (var forbidden in new[]
        {
            "RunId",
            "EventSequence",
            "ProcessId",
            "UserName",
            "Symbol",
            "Position",
            "Order",
            "Credential",
            "Provider"
        })
            Assert.DoesNotContain(forbidden, projection, StringComparison.OrdinalIgnoreCase);
    }

    private static string Root() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", ".."));
}

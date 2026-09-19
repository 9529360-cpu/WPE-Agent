namespace WPE.Tests;

public sealed class HeadlessLocalControlClientBoundaryTests
{
    [Fact]
    public void SameHeadlessExecutableProvidesOnlyHealthAndConfirmedShutdownClientModes()
    {
        var root = Root();
        var client = File.ReadAllText(Path.Combine(
            root, "WPE.Headless", "HeadlessLocalControlClient.cs"));
        var program = File.ReadAllText(Path.Combine(
            root, "WPE.Headless", "Program.cs"));

        Assert.Contains("HeadlessLocalControlClient.RunAsync(args[1..])", program, StringComparison.Ordinal);
        var clientDispatch = program.IndexOf(
            "HeadlessLocalControlClient.RunAsync(args[1..])",
            StringComparison.Ordinal);
        var hostBuilder = program.IndexOf(
            "Host.CreateApplicationBuilder(args)",
            StringComparison.Ordinal);
        Assert.True(clientDispatch >= 0 && hostBuilder > clientDispatch);

        Assert.Contains("NamedPipeClientStream", client, StringComparison.Ordinal);
        Assert.Contains("HeadlessLocalControlWorker.PipeName", client, StringComparison.Ordinal);
        Assert.Contains("\".\"", client, StringComparison.Ordinal);
        Assert.Contains("\"health\"", client, StringComparison.Ordinal);
        Assert.Contains("\"shutdown\"", client, StringComparison.Ordinal);
        Assert.Contains("\"--confirm\"", client, StringComparison.Ordinal);
        Assert.Contains("HeadlessLocalControlProtocol.ShutdownConfirmation", client, StringComparison.Ordinal);
        Assert.Contains("MaximumResponseBytes = 4096", client, StringComparison.Ordinal);
        Assert.Contains("ConnectTimeout = TimeSpan.FromSeconds(5)", client, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientCannotBecomeRemoteServiceTradingOrConfigurationAuthority()
    {
        var client = File.ReadAllText(Path.Combine(
            Root(), "WPE.Headless", "HeadlessLocalControlClient.cs"));

        foreach (var forbidden in new[]
        {
            "TcpClient",
            "HttpClient",
            "Socket",
            "IPAddress",
            "WebSocket",
            "Start-Service",
            "Stop-Service",
            "Restart-Service",
            "ServiceController",
            "AutoTradingAgent",
            "TradingRuntimeHost",
            "IExchangeAdapter",
            "PlaceOrder",
            "EmergencyCloseAll",
            "ChangeAuthorizationMode",
            "Mainnet",
            "credential",
            "apiKey",
            "secret",
            "appsettings"
        })
            Assert.DoesNotContain(forbidden, client, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientReturnsMachineReadableBoundedFailureCodes()
    {
        var client = File.ReadAllText(Path.Combine(
            Root(), "WPE.Headless", "HeadlessLocalControlClient.cs"));

        Assert.Contains("InvalidArgumentsExitCode = 2", client, StringComparison.Ordinal);
        Assert.Contains("ControlUnavailableExitCode = 3", client, StringComparison.Ordinal);
        Assert.Contains("ControlRejectedExitCode = 4", client, StringComparison.Ordinal);
        Assert.Contains("control.arguments-invalid", client, StringComparison.Ordinal);
        Assert.Contains("control.unavailable", client, StringComparison.Ordinal);
        Assert.Contains("control.response-invalid", client, StringComparison.Ordinal);
        Assert.Contains("JsonSerializer.Serialize", client, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Error.Write", client, StringComparison.Ordinal);
    }

    private static string Root() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", ".."));
}

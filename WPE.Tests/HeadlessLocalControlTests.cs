using System.Text.Json;
using WpeAgent.Headless;
using 币安量化机器人.Services;

namespace WPE.Tests;

public sealed class HeadlessLocalControlTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 19, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HealthReturnsOnlyWhitelistedOperationalState()
    {
        var runtime = new TradingRuntimeHealthV1(
            TradingRuntimeHealthV1.CurrentSchema,
            Now,
            true,
            Now.AddMinutes(-1),
            true,
            "Running",
            "private-run-id",
            Now.AddSeconds(-2),
            "CLEAN_START",
            42,
            false);
        var process = new HeadlessProcessHealthV1(
            HeadlessProcessHealthV1.CurrentSchema,
            1234,
            Now,
            "ready",
            "headless.ready",
            runtime);

        var response = HeadlessLocalControlProtocol.Handle(
            """{"schema":"wpe.headless-local-control/1.0","command":"health"}""",
            () => process);

        Assert.True(response.Success);
        Assert.Equal("control.health", response.Code);
        var health = Assert.IsType<HeadlessControlHealthV1>(response.Health);
        Assert.True(health.RuntimeReady);
        Assert.True(health.AgentRunning);
        Assert.True(health.AccessFresh);
        Assert.True(health.HeartbeatFresh);
        Assert.False(health.LeaseLost);

        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("private-run-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EventSequence", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessId", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"schema":"wpe.headless-local-control/1.0","command":"shutdown"}""", "control.shutdown-confirmation-required")]
    [InlineData("""{"schema":"wpe.headless-local-control/1.0","command":"shutdown","confirmation":"wrong"}""", "control.shutdown-confirmation-required")]
    [InlineData("""{"schema":"wpe.headless-local-control/1.0","command":"restart"}""", "control.command-unsupported")]
    [InlineData("""{"schema":"wrong","command":"health"}""", "control.schema-unsupported")]
    [InlineData("""{"schema":"wpe.headless-local-control/1.0","command":"health","extra":"x"}""", "control.request-field-unsupported")]
    public void UnsupportedOrUnconfirmedCommandsFailClosed(string request, string code)
    {
        var response = HeadlessLocalControlProtocol.Handle(request, () => null);

        Assert.False(response.Success);
        Assert.Equal(code, response.Code);
        Assert.Null(response.Health);
    }

    [Fact]
    public void ShutdownRequiresExactConfirmationAndDoesNotCarryMutationPayload()
    {
        var response = HeadlessLocalControlProtocol.Handle(
            """{"schema":"wpe.headless-local-control/1.0","command":"shutdown","confirmation":"shutdown-wpe-headless"}""",
            () => null);

        Assert.True(response.Success);
        Assert.Equal("control.shutdown-requested", response.Code);
        Assert.Null(response.Health);
    }

    [Fact]
    public void HealthFailsClosedWhenPersistedHealthIsUnavailable()
    {
        var response = HeadlessLocalControlProtocol.Handle(
            """{"schema":"wpe.headless-local-control/1.0","command":"health"}""",
            () => null);

        Assert.False(response.Success);
        Assert.Equal("control.health-unavailable", response.Code);
    }
}

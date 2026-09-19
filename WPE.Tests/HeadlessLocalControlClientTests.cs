using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WpeAgent.Headless;

namespace WPE.Tests;

public sealed class HeadlessLocalControlClientTests
{
    [Fact]
    public async Task HealthClientUsesFixedLocalProtocolAndAcceptsValidResponse()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(
            HeadlessLocalControlWorker.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            4096,
            4096);

        string? requestJson = null;
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            requestJson = await ReadLineAsync(server, timeout.Token);

            var response = JsonSerializer.Serialize(new
            {
                schema = HeadlessControlResponseV1.CurrentSchema,
                success = true,
                code = "control.health",
                health = new
                {
                    processState = "ready",
                    processCode = "headless.ready",
                    observedAtUtc = DateTimeOffset.UtcNow,
                    runtimeReady = true,
                    agentRunning = true,
                    accessFresh = true,
                    heartbeatFresh = true,
                    leaseLost = false
                }
            });
            var bytes = Encoding.UTF8.GetBytes(response + "\n");
            await server.WriteAsync(bytes, timeout.Token);
            await server.FlushAsync(timeout.Token);
        }, timeout.Token);

        var exitCode = await HeadlessLocalControlClient.RunAsync(
            new[] { "health" },
            timeout.Token);
        await serverTask;

        Assert.Equal(0, exitCode);
        using var request = JsonDocument.Parse(requestJson!);
        Assert.Equal(
            HeadlessLocalControlProtocol.RequestSchema,
            request.RootElement.GetProperty("schema").GetString());
        Assert.Equal(
            "health",
            request.RootElement.GetProperty("command").GetString());
        Assert.False(request.RootElement.TryGetProperty("confirmation", out var confirmation) &&
                     confirmation.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task ShutdownRequiresExactConfirmationBeforePipeConnection()
    {
        var exitCode = await HeadlessLocalControlClient.RunAsync(
            new[] { "shutdown", "--confirm", "wrong" });

        Assert.Equal(
            HeadlessLocalControlClient.InvalidArgumentsExitCode,
            exitCode);
    }

    private static async Task<string> ReadLineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        using var memory = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken);
            if (read == 0) break;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;
            memory.Write(buffer, 0, count);
            if (newline >= 0) break;
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}

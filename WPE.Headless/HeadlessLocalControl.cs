using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using 币安量化机器人.Services;

namespace WpeAgent.Headless;

public sealed record HeadlessControlHealthV1(
    string ProcessState,
    string ProcessCode,
    DateTimeOffset ObservedAtUtc,
    bool RuntimeReady,
    bool AgentRunning,
    bool AccessFresh,
    bool HeartbeatFresh,
    bool LeaseLost);

public sealed record HeadlessControlResponseV1(
    string Schema,
    bool Success,
    string Code,
    HeadlessControlHealthV1? Health = null)
{
    public const string CurrentSchema = "wpe.headless-local-control-response/1.0";
}

public static class HeadlessLocalControlProtocol
{
    public const string RequestSchema = "wpe.headless-local-control/1.0";
    public const string ShutdownConfirmation = "shutdown-wpe-headless";

    public static HeadlessControlResponseV1 Handle(
        string requestJson,
        Func<HeadlessProcessHealthV1?> readHealth)
    {
        if (string.IsNullOrWhiteSpace(requestJson) || requestJson.Length > HeadlessLocalControlWorker.MaximumRequestBytes)
            return Reject("control.request-invalid");

        JsonDocument document;
        try { document = JsonDocument.Parse(requestJson); }
        catch (JsonException) { return Reject("control.request-invalid"); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Reject("control.request-invalid");

            var allowed = new HashSet<string>(StringComparer.Ordinal) { "schema", "command", "confirmation" };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                    return Reject("control.request-field-unsupported");
                if (!seen.Add(property.Name))
                    return Reject("control.request-field-duplicate");
            }

            var schema = ReadString(document.RootElement, "schema");
            var command = ReadString(document.RootElement, "command");
            var confirmation = ReadString(document.RootElement, "confirmation");
            if (!string.Equals(schema, RequestSchema, StringComparison.Ordinal))
                return Reject("control.schema-unsupported");

            if (string.Equals(command, "health", StringComparison.Ordinal))
            {
                if (confirmation is not null)
                    return Reject("control.health-confirmation-forbidden");
                var process = readHealth();
                if (process is null)
                    return Reject("control.health-unavailable");
                var runtime = process.Runtime;
                return new(
                    HeadlessControlResponseV1.CurrentSchema,
                    true,
                    "control.health",
                    new(
                        process.State,
                        process.Code,
                        process.ObservedAtUtc,
                        runtime?.Ready ?? false,
                        runtime?.AgentRunning ?? false,
                        runtime?.AccessFresh ?? false,
                        runtime?.HeartbeatFresh ?? false,
                        runtime?.LeaseLost ?? false));
            }

            if (string.Equals(command, "shutdown", StringComparison.Ordinal))
            {
                if (!string.Equals(confirmation, ShutdownConfirmation, StringComparison.Ordinal))
                    return Reject("control.shutdown-confirmation-required");
                return new(HeadlessControlResponseV1.CurrentSchema, true, "control.shutdown-requested");
            }

            return Reject("control.command-unsupported");
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static HeadlessControlResponseV1 Reject(string code)
        => new(HeadlessControlResponseV1.CurrentSchema, false, code);
}

public sealed class HeadlessLocalControlWorker(IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    public const string PipeName = "wpe-agent-headless-control-v1";
    public const int MaximumRequestBytes = 4096;
    private const int MaximumHealthBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumHealthAge = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                MaximumRequestBytes,
                MaximumRequestBytes);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                requestTimeout.CancelAfter(RequestTimeout);
                var request = await ReadBoundedRequestAsync(pipe, requestTimeout.Token).ConfigureAwait(false);
                var response = HeadlessLocalControlProtocol.Handle(request, ReadHealth);
                var shutdownRequested = response.Success &&
                    string.Equals(response.Code, "control.shutdown-requested", StringComparison.Ordinal);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response, Json);
                await pipe.WriteAsync(bytes, requestTimeout.Token).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { (byte)'\n' }, requestTimeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(requestTimeout.Token).ConfigureAwait(false);
                if (shutdownRequested)
                    applicationLifetime.StopApplication();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidDataException)
            {
                if (pipe.IsConnected)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(
                        new HeadlessControlResponseV1(
                            HeadlessControlResponseV1.CurrentSchema,
                            false,
                            "control.request-too-large"),
                        Json);
                    try
                    {
                        await pipe.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                        await pipe.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            catch
            {
                // Keep the local control loop available. Detailed errors stay out of the wire response.
            }
        }
    }

    private static HeadlessProcessHealthV1? ReadHealth()
    {
        var path = AppDataPaths.RuntimeFile("headless-health-v1.json");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumHealthBytes) return null;
        try
        {
            var value = JsonSerializer.Deserialize<HeadlessProcessHealthV1>(
                File.ReadAllBytes(path),
                Json);
            if (value is null ||
                !string.Equals(value.Schema, HeadlessProcessHealthV1.CurrentSchema, StringComparison.Ordinal))
                return null;
            var now = DateTimeOffset.UtcNow;
            if (value.ObservedAtUtc > now || now - value.ObservedAtUtc > MaximumHealthAge)
                return null;
            return value;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadBoundedRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        using var memory = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;
            if (memory.Length + count > MaximumRequestBytes)
                throw new InvalidDataException("Headless control request is too large.");
            memory.Write(buffer, 0, count);
            if (newline >= 0) break;
        }

        if (memory.Length == 0)
            throw new InvalidDataException("Headless control request is empty.");
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}

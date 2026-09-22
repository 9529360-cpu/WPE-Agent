using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WpeAgent.Headless;

public static class HeadlessLocalControlClient
{
    public const int InvalidArgumentsExitCode = 2;
    public const int ControlUnavailableExitCode = 3;
    public const int ControlRejectedExitCode = 4;

    private const int MaximumResponseBytes = 4096;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default) =>
        RunAsync(args, HeadlessLocalControlWorker.PipeName, cancellationToken);

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        string pipeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var request = CreateRequest(args);
        if (request is null)
            return WriteFailure("control.arguments-invalid", InvalidArgumentsExitCode);

        if (!OperatingSystem.IsWindows())
            return WriteFailure("control.platform-unsupported", ControlUnavailableExitCode);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            await pipe.WriteAsync(requestBytes, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { (byte)'\n' }, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

            var responseJson = await ReadBoundedResponseAsync(pipe, timeout.Token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<HeadlessControlResponseV1>(responseJson, Json);
            if (response is null ||
                !string.Equals(response.Schema, HeadlessControlResponseV1.CurrentSchema, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(response.Code))
                return WriteFailure("control.response-invalid", ControlUnavailableExitCode);

            Console.WriteLine(JsonSerializer.Serialize(response, Json));
            return response.Success ? 0 : ControlRejectedExitCode;
        }
        catch (OperationCanceledException)
        {
            return WriteFailure("control.unavailable", ControlUnavailableExitCode);
        }
        catch (IOException)
        {
            return WriteFailure("control.unavailable", ControlUnavailableExitCode);
        }
        catch (UnauthorizedAccessException)
        {
            return WriteFailure("control.unavailable", ControlUnavailableExitCode);
        }
        catch (InvalidDataException)
        {
            return WriteFailure("control.response-invalid", ControlUnavailableExitCode);
        }
        catch (JsonException)
        {
            return WriteFailure("control.response-invalid", ControlUnavailableExitCode);
        }
    }

    private static HeadlessControlRequestV1? CreateRequest(IReadOnlyList<string> args)
    {
        if (args.Count == 1 &&
            string.Equals(args[0], "health", StringComparison.Ordinal))
            return new(HeadlessLocalControlProtocol.RequestSchema, "health", null);

        if (args.Count == 3 &&
            string.Equals(args[0], "shutdown", StringComparison.Ordinal) &&
            string.Equals(args[1], "--confirm", StringComparison.Ordinal) &&
            string.Equals(args[2], HeadlessLocalControlProtocol.ShutdownConfirmation, StringComparison.Ordinal))
            return new(
                HeadlessLocalControlProtocol.RequestSchema,
                "shutdown",
                HeadlessLocalControlProtocol.ShutdownConfirmation);

        return null;
    }

    private static async Task<string> ReadBoundedResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        using var memory = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;
            if (memory.Length + count > MaximumResponseBytes)
                throw new InvalidDataException("Headless control response is too large.");
            memory.Write(buffer, 0, count);
            if (newline >= 0) break;
        }

        if (memory.Length == 0)
            throw new InvalidDataException("Headless control response is empty.");
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static int WriteFailure(string code, int exitCode)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new HeadlessControlResponseV1(
                HeadlessControlResponseV1.CurrentSchema,
                false,
                code),
            Json));
        return exitCode;
    }

    private sealed record HeadlessControlRequestV1(
        string Schema,
        string Command,
        string? Confirmation);
}

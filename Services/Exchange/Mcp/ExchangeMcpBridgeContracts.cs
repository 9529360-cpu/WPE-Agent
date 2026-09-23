using System.Text.Json;

namespace 币安量化机器人.Services.Exchange.Mcp;

public sealed record ExchangeMcpToolDescriptor(string Name,string Description,JsonElement InputSchema);

public sealed record ExchangeMcpToolResult(
    bool IsError,
    string Text,
    JsonElement? StructuredContent);

public interface IExchangeMcpToolClient : IAsyncDisposable
{
    string ProviderId { get; }
    bool ReadOnly { get; }

    Task<IReadOnlyList<ExchangeMcpToolDescriptor>> ListToolsAsync(CancellationToken ct);

    Task<ExchangeMcpToolResult> CallToolAsync(
        string tool,
        IReadOnlyDictionary<string,object?> arguments,
        CancellationToken ct);
}

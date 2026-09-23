using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WpeAgent.ExchangeMcp;

public sealed record ExchangeMcpToolInfo(string Name,string Description,JsonElement InputSchema);

public sealed record ExchangeMcpCallResult(
    bool IsError,
    string Text,
    JsonElement? StructuredContent);

public sealed class McpExchangeSession : IAsyncDisposable
{
    private readonly OfficialExchangeMcpProfile _profile;
    private readonly bool _authorize;
    private IClientTransport? _transport;
    private McpClient? _client;

    public McpExchangeSession(OfficialExchangeMcpProfile profile,bool authorize=false)
    {
        _profile=profile??throw new ArgumentNullException(nameof(profile));
        _authorize=authorize;
    }

    public OfficialExchangeMcpProfile Profile=>_profile;

    public async Task ConnectAsync(CancellationToken ct)
    {
        if(_client is not null)return;

        _transport=_profile.Transport switch
        {
            ExchangeMcpTransportKind.Stdio=>CreateStdioTransport(_profile),
            ExchangeMcpTransportKind.StreamableHttp=>new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name=_profile.DisplayName,
                    Endpoint=_profile.Endpoint??throw new InvalidOperationException("HTTP endpoint is missing"),
                    TransportMode=HttpTransportMode.StreamableHttp,
                    OAuth=_profile.RequiresAuthorization
                        ?BrowserOAuthAuthorization.Create(_authorize,_profile.ProviderId)
                        :null
                }),
            _=>throw new ArgumentOutOfRangeException()
        };

        _client=await McpClient.CreateAsync(_transport,cancellationToken:ct);
    }

    public async Task<IReadOnlyList<ExchangeMcpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        await ConnectAsync(ct);
        var tools=await _client!.ListToolsAsync(cancellationToken:ct);
        return tools
            .Select(tool=>new ExchangeMcpToolInfo(
                tool.Name,
                tool.Description??string.Empty,
                tool.ProtocolTool.InputSchema.Clone()))
            .OrderBy(tool=>tool.Name,StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ExchangeMcpCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string,object?> arguments,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Tool name is required.",nameof(toolName));

        await ConnectAsync(ct);
        var tools=await _client!.ListToolsAsync(cancellationToken:ct);
        var tool=tools.FirstOrDefault(value=>string.Equals(value.Name,toolName,StringComparison.Ordinal))
            ??throw new InvalidOperationException($"MCP tool '{toolName}' is not exposed by {_profile.DisplayName}.");

        var result=await tool.CallAsync(arguments,cancellationToken:ct);
        var text=string.Join(
            Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(content=>content.Text));

        return new(
            result.IsError==true,
            text,
            result.StructuredContent?.Clone());
    }

    private static StdioClientTransport CreateStdioTransport(OfficialExchangeMcpProfile profile)
    {
        var environment=StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        Forward(environment,"WPE_OKX_API_KEY","OKX_API_KEY");
        Forward(environment,"WPE_OKX_SECRET_KEY","OKX_SECRET_KEY");
        Forward(environment,"WPE_OKX_PASSPHRASE","OKX_PASSPHRASE");
        Forward(environment,"WPE_OKX_API_BASE_URL","OKX_API_BASE_URL");
        Forward(environment,"WPE_BINANCE_MCP_ALLOW_WRITE","WPE_BINANCE_MCP_ALLOW_WRITE");
        Forward(environment,"HTTP_PROXY","HTTP_PROXY");
        Forward(environment,"HTTPS_PROXY","HTTPS_PROXY");
        Forward(environment,"NO_PROXY","NO_PROXY");

        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name=profile.DisplayName,
                Command=profile.Command??throw new InvalidOperationException("stdio command is missing"),
                Arguments=[..profile.Arguments],
                InheritEnvironmentVariables=false,
                EnvironmentVariables=environment
            });
    }

    private static void Forward(
        IDictionary<string,string?> environment,
        string source,
        string target)
    {
        var value=Environment.GetEnvironmentVariable(source);
        if(!string.IsNullOrWhiteSpace(value))
            environment[target]=value;
    }

    public async ValueTask DisposeAsync()
    {
        if(_client is not null)
            await _client.DisposeAsync();
        else if(_transport is IAsyncDisposable asyncTransport)
            await asyncTransport.DisposeAsync();

        _client=null;
        _transport=null;
    }
}

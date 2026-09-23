using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WpeAgent.BinanceMcp;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

var allowWriteArg=args.Any(value=>
    value.Equals("--allow-write",StringComparison.OrdinalIgnoreCase));
var allowWriteEnv=string.Equals(
    Environment.GetEnvironmentVariable("WPE_BINANCE_MCP_ALLOW_WRITE"),
    "1",
    StringComparison.Ordinal);
var allowWrite=allowWriteArg&&allowWriteEnv;
var requestedProfileId=Arg(args,"--profile-id");

var store=new AgentSettingsStore();
var settings=store.Load();
var profile=SelectProfile(settings,requestedProfileId);

if(!profile.IsTestnet)
    throw new InvalidOperationException("WPE Binance MCP only supports Binance Futures Testnet.");
if(!profile.ProviderId.Equals("binance-futures",StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("WPE Binance MCP requires a binance-futures profile.");
if(allowWriteArg&&!allowWriteEnv)
    throw new InvalidOperationException(
        "Write mode requires WPE_BINANCE_MCP_ALLOW_WRITE=1 in addition to --allow-write.");

AgentSettingsStore.ValidateExchangeProfile(profile);
var credentials=store.GetExchangeCredentials(profile);
var provider=new ExchangeProviderCatalog().Create(profile,credentials);
var runtime=new BinanceNativeMcpRuntime(provider,profile,allowWrite);

var builder=Host.CreateApplicationBuilder(Array.Empty<string>());
builder.Logging.ClearProviders();
builder.Services.AddSingleton(runtime);
var mcp=builder.Services
    .AddMcpServer(options=>
    {
        options.ServerInfo=new()
        {
            Name="wpe-binance-testnet",
            Version=typeof(Program).Assembly.GetName().Version?.ToString()??"dev",
            Title="WPE Binance Futures Testnet MCP"
        };
    })
    .WithStdioServerTransport()
    .WithTools<BinanceNativeReadMcpTools>();

if(allowWrite)
    mcp.WithTools<BinanceNativeWriteMcpTools>();

using var host=builder.Build();
try
{
    await host.RunAsync();
}
finally
{
    await runtime.DisposeAsync();
}

static ExchangeConnectionProfile SelectProfile(
    AgentSettings settings,
    string? requestedProfileId)
{
    ArgumentNullException.ThrowIfNull(settings);
    var candidates=settings.Exchanges
        .Where(profile=>
            profile.Enabled&&
            profile.IsTestnet&&
            profile.ProviderId.Equals("binance-futures",StringComparison.OrdinalIgnoreCase))
        .ToArray();

    if(!string.IsNullOrWhiteSpace(requestedProfileId))
        return candidates.FirstOrDefault(profile=>
                   profile.Id.Equals(requestedProfileId,StringComparison.OrdinalIgnoreCase))
               ??throw new InvalidOperationException(
                   $"Binance Testnet profile '{requestedProfileId}' was not found or is disabled.");

    var active=candidates.FirstOrDefault(profile=>
        profile.Id.Equals(settings.ActiveExecutionConnectionId,StringComparison.OrdinalIgnoreCase));
    return active
           ??candidates.FirstOrDefault()
           ??throw new InvalidOperationException("No enabled Binance Futures Testnet profile is configured.");
}

static string? Arg(string[] values,string name)
{
    for(var index=0;index<values.Length-1;index++)
        if(values[index].Equals(name,StringComparison.OrdinalIgnoreCase))
            return values[index+1];
    return null;
}

namespace WpeAgent.ExchangeMcp;

public enum ExchangeMcpTransportKind
{
    Stdio,
    StreamableHttp
}

public sealed record OfficialExchangeMcpProfile(
    string ProviderId,
    string DisplayName,
    ExchangeMcpTransportKind Transport,
    string? Command,
    IReadOnlyList<string> Arguments,
    Uri? Endpoint,
    bool ReadOnly,
    bool RequiresAuthorization);

public static class OfficialExchangeMcpProfiles
{
    public const string BinanceEndpoint = "https://agent.binance.com/mcp/agentic";

    public static OfficialExchangeMcpProfile Resolve(string provider,string mode,bool allowWrite)
    {
        provider=(provider??string.Empty).Trim().ToLowerInvariant();
        mode=(mode??string.Empty).Trim().ToLowerInvariant();

        return provider switch
        {
            "okx" => mode switch
            {
                "demo" => OkxDemo(allowWrite),
                "public" or "" => OkxPublic(),
                _ => throw new ArgumentOutOfRangeException(nameof(mode),"OKX mode must be public or demo.")
            },
            "binance" => BinanceAgentic(allowWrite),
            "binance-local" => BinanceLocalTestnet(allowWrite),
            _ => throw new ArgumentOutOfRangeException(nameof(provider),"Provider must be okx, binance, or binance-local.")
        };
    }

    public static OfficialExchangeMcpProfile OkxPublic()=>new(
        "okx-mcp",
        "OKX Agent Trade Kit",
        ExchangeMcpTransportKind.Stdio,
        "npx.cmd",
        ["-y","@okx_ai/okx-trade-mcp","--modules","market","--read-only"],
        null,
        true,
        false);

    public static OfficialExchangeMcpProfile OkxDemo(bool allowWrite)
    {
        var args=new List<string>
        {
            "-y","@okx_ai/okx-trade-mcp",
            "--demo",
            "--modules","market,swap,account"
        };
        if(!allowWrite)args.Add("--read-only");

        return new(
            "okx-mcp",
            "OKX Agent Trade Kit",
            ExchangeMcpTransportKind.Stdio,
            "npx.cmd",
            args,
            null,
            !allowWrite,
            true);
    }

    public static OfficialExchangeMcpProfile BinanceAgentic(bool allowWrite)=>new(
        "binance-mcp",
        "Binance Agent OS MCP",
        ExchangeMcpTransportKind.StreamableHttp,
        null,
        [],
        new Uri(BinanceEndpoint,UriKind.Absolute),
        !allowWrite,
        true);

    public static OfficialExchangeMcpProfile BinanceLocalTestnet(bool allowWrite)
    {
        var server=BinanceLocalMcpLocator.Resolve();
        var isDll=server.EndsWith(".dll",StringComparison.OrdinalIgnoreCase);
        var arguments=new List<string>();
        if(isDll)arguments.Add(server);
        if(allowWrite)arguments.Add("--allow-write");

        return new(
            "binance-local-mcp",
            "WPE Binance Futures Testnet MCP",
            ExchangeMcpTransportKind.Stdio,
            isDll?"dotnet":server,
            arguments,
            null,
            !allowWrite,
            false);
    }
}

internal static class BinanceLocalMcpLocator
{
    public const string EnvironmentVariable="WPE_BINANCE_NATIVE_MCP_PATH";

    public static string Resolve()
    {
        var configured=Environment.GetEnvironmentVariable(EnvironmentVariable);
        if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(configured))
            return Path.GetFullPath(configured);

        foreach(var candidate in AdjacentCandidates())
            if(File.Exists(candidate))return candidate;

        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        for(var depth=0;directory is not null&&depth<8;depth++,directory=directory.Parent)
        {
            var output=Path.Combine(
                directory.FullName,
                "WPE.BinanceMcp",
                "bin",
                "Release",
                "net8.0");
            var appHost=Path.Combine(output,"WPE.BinanceMcp.exe");
            if(File.Exists(appHost))return appHost;
            var assembly=Path.Combine(output,"WPE.BinanceMcp.dll");
            if(File.Exists(assembly))return assembly;
        }

        throw new FileNotFoundException(
            $"WPE Binance MCP server is unavailable. Set {EnvironmentVariable} or publish it beside the exchange MCP client.");
    }

    private static IEnumerable<string> AdjacentCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory,"WPE.BinanceMcp.exe");
        yield return Path.Combine(AppContext.BaseDirectory,"WPE.BinanceMcp.dll");
        yield return Path.Combine(AppContext.BaseDirectory,"binance-mcp","WPE.BinanceMcp.exe");
        yield return Path.Combine(AppContext.BaseDirectory,"binance-mcp","WPE.BinanceMcp.dll");
    }
}

namespace 币安量化机器人.Services.Exchange.Mcp;

public static class OfficialExchangeMcpClientFactory
{
    public static ExchangeMcpBridgeClient CreateOkxDemo(
        ExchangeConnectionProfile profile,
        IReadOnlyDictionary<string,string> credentials,
        bool allowWrite,
        string? sidecarPath=null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);

        if(!profile.IsTestnet)
            throw new InvalidOperationException("OKX MCP live trading is not enabled by WPE.");
        if(!ProviderEndpointPolicy.IsOfficialHttpsOrigin(profile.Endpoint,"www.okx.com"))
            throw new InvalidOperationException("OKX MCP demo requires the official https://www.okx.com endpoint.");

        var environment=new Dictionary<string,string?>(StringComparer.Ordinal)
        {
            ["OKX_API_KEY"]=Required(credentials,"apiKey"),
            ["OKX_SECRET_KEY"]=Required(credentials,"secret"),
            ["OKX_PASSPHRASE"]=Required(credentials,"passphrase"),
            ["OKX_API_BASE_URL"]=profile.Endpoint
        };

        if(profile.UseProxy&&!string.IsNullOrWhiteSpace(profile.ProxyUrl))
        {
            environment["HTTPS_PROXY"]=profile.ProxyUrl;
            environment["HTTP_PROXY"]=profile.ProxyUrl;
        }

        return new ExchangeMcpBridgeClient(
            "okx",
            "demo",
            allowWrite&&profile.ExecutionEnabled,
            authorize:false,
            sidecarPath,
            environment);
    }

    public static ExchangeMcpBridgeClient CreateOkxPublic(string? sidecarPath=null)=>
        new("okx","public",allowWrite:false,authorize:false,sidecarPath);

    public static ExchangeMcpBridgeClient CreateBinanceAgentic(
        bool allowWrite,
        bool authorize,
        string? sidecarPath=null)=>
        new("binance","agentic",allowWrite,authorize,sidecarPath);

    public static ExchangeMcpBridgeClient CreateBinanceLocalTestnet(
        bool allowWrite=false,
        string? sidecarPath=null)=>
        new("binance-local","testnet",allowWrite,authorize:false,sidecarPath);

    private static string Required(IReadOnlyDictionary<string,string> values,string key)=>
        values.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)
            ?value
            :throw new InvalidOperationException($"Exchange credential '{key}' is incomplete.");
}

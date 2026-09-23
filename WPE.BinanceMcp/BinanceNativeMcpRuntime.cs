using 币安量化机器人.Services.Exchange;

namespace WpeAgent.BinanceMcp;

public sealed class BinanceNativeMcpRuntime : IAsyncDisposable
{
    public BinanceNativeMcpRuntime(
        IExchangeProvider provider,
        ExchangeConnectionProfile profile,
        bool allowWrite)
    {
        Provider=provider??throw new ArgumentNullException(nameof(provider));
        Profile=profile??throw new ArgumentNullException(nameof(profile));
        AllowWrite=allowWrite;
    }

    public IExchangeProvider Provider { get; }
    public ExchangeConnectionProfile Profile { get; }
    public bool AllowWrite { get; }

    public void RequireWrite(string operation)
    {
        if(!AllowWrite)
            throw new InvalidOperationException(
                $"Binance local MCP write tool '{operation}' is disabled. " +
                "Start the server with --allow-write and WPE_BINANCE_MCP_ALLOW_WRITE=1.");
        if(!Profile.IsTestnet)
            throw new InvalidOperationException("Binance local MCP refuses Mainnet write operations.");
        if(!Profile.ExecutionEnabled)
            throw new InvalidOperationException("The selected Binance Testnet profile has execution disabled.");
    }

    public ValueTask DisposeAsync()=>Provider.DisposeAsync();
}

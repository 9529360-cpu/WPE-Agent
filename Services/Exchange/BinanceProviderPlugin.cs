using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange;

public sealed class BinanceProviderPlugin:IExchangeProviderPlugin
{
    public static ExchangeProviderDescriptor ProviderDescriptor { get; }=new(
        "binance-futures","Binance Futures",ExchangeAssetClass.CryptoCex,true,false,
        [new("apiKey","API Key",ExchangeCredentialKind.ApiKey),new("secret","API Secret",ExchangeCredentialKind.Secret)],
        new HashSet<string>(["account","balance","positions","margin","candles","orderbook","funding","open-interest","index-price","mark-price","place-order","cancel-order","query-order","protection-orders","realtime","health"]));
    public ExchangeProviderDescriptor Descriptor=>ProviderDescriptor;
    public IExchangeProvider Create(ExchangeConnectionProfile profile,IReadOnlyDictionary<string,string> credentials)
    {
        if(!credentials.TryGetValue("apiKey",out var key)||string.IsNullOrWhiteSpace(key)||!credentials.TryGetValue("secret",out var secret)||string.IsNullOrWhiteSpace(secret))throw new InvalidOperationException("Binance credentials are incomplete.");
        return new BinanceFuturesAdapter(profile,key,secret);
    }
}

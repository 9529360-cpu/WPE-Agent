using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WpeAgent.BinanceMcp;

[McpServerToolType]
public sealed class BinanceNativeReadMcpTools
{
    private readonly BinanceNativeMcpRuntime _runtime;

    public BinanceNativeReadMcpTools(BinanceNativeMcpRuntime runtime)
    {
        _runtime=runtime;
    }

    [McpServerTool(Name="exchange_health",ReadOnly=true,Destructive=false)]
    [Description("Read Binance Futures Testnet provider health, latency, clock skew, and execution-gate state.")]
    public async Task<string> HealthAsync(CancellationToken cancellationToken)
    {
        var health=await _runtime.Provider.HealthCheckAsync(cancellationToken);
        return BinanceNativeMcpJson.Serialize(new
        {
            provider=_runtime.Provider.ProviderId,
            environment=_runtime.Provider.Environment.ToString(),
            profileId=_runtime.Profile.Id,
            readOnly=!_runtime.AllowWrite,
            health
        });
    }

    [McpServerTool(Name="exchange_get_market",ReadOnly=true,Destructive=false)]
    [Description("Read canonical WPE market evidence for a Binance Futures Testnet symbol.")]
    public async Task<string> GetMarketAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.GetMarketAsync(symbol,cancellationToken));

    [McpServerTool(Name="exchange_get_candles",ReadOnly=true,Destructive=false)]
    [Description("Read confirmed Binance Futures Testnet candles through the existing WPE adapter.")]
    public async Task<string> GetCandlesAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        [Description("Interval such as 1m, 15m, 1h, or 4h.")] string interval,
        [Description("Maximum candle count.")] int limit,
        CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.GetCandlesAsync(
                symbol,interval,Math.Clamp(limit,1,1500),cancellationToken));

    [McpServerTool(Name="exchange_get_rules",ReadOnly=true,Destructive=false)]
    [Description("Read Binance Futures Testnet trading rules from the existing provider.")]
    public async Task<string> GetRulesAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.GetRulesAsync(symbol,cancellationToken));

    [McpServerTool(Name="exchange_get_account",ReadOnly=true,Destructive=false)]
    [Description("Read the Binance Futures Testnet account snapshot.")]
    public async Task<string> GetAccountAsync(CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.GetAccountAsync(cancellationToken));

    [McpServerTool(Name="exchange_get_positions",ReadOnly=true,Destructive=false)]
    [Description("Read current Binance Futures Testnet positions.")]
    public async Task<string> GetPositionsAsync(CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.GetPositionsAsync(cancellationToken));

    [McpServerTool(Name="exchange_get_open_orders",ReadOnly=true,Destructive=false)]
    [Description("Read current open Binance Futures Testnet orders. Omit symbol to read all.")]
    public async Task<string> GetOpenOrdersAsync(
        [Description("Optional canonical symbol such as BTCUSDT.")] string symbol="",
        CancellationToken cancellationToken=default)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.GetOpenOrdersAsync(
                string.IsNullOrWhiteSpace(symbol)?null:symbol,
                cancellationToken));

    [McpServerTool(Name="exchange_find_order",ReadOnly=true,Destructive=false)]
    [Description("Find a Binance Futures Testnet order by WPE client order id.")]
    public async Task<string> FindOrderAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        [Description("Durable WPE client order id.")] string clientOrderId,
        CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.FindOrderAsync(symbol,clientOrderId,cancellationToken));

    [McpServerTool(Name="exchange_get_recent_orders",ReadOnly=true,Destructive=false)]
    [Description("Read recent Binance Futures Testnet orders for recovery and reconciliation.")]
    public async Task<string> GetRecentOrdersAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        [Description("Maximum order count.")] int limit=100,
        CancellationToken cancellationToken=default)
    {
        if(_runtime.Provider is not IRecentOrderProvider recent)
            throw new NotSupportedException("The selected Binance provider does not expose recent orders.");
        return BinanceNativeMcpJson.Serialize(
            await recent.GetRecentOrdersAsync(
                symbol,Math.Clamp(limit,1,500),cancellationToken));
    }

    [McpServerTool(Name="exchange_get_derivative_history",ReadOnly=true,Destructive=false)]
    [Description("Read Binance Futures Testnet derivatives history through the existing WPE adapter.")]
    public async Task<string> GetDerivativeHistoryAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.GetDerivativeHistoryAsync(symbol,cancellationToken));

    [McpServerTool(Name="exchange_get_candles_range",ReadOnly=true,Destructive=false)]
    [Description("Read confirmed Binance Futures Testnet candles for an explicit UTC range.")]
    public async Task<string> GetCandlesRangeAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        [Description("Interval such as 1m, 15m, 1h, or 4h.")] string interval,
        [Description("UTC range start in ISO-8601 format.")] string startUtc,
        [Description("UTC range end in ISO-8601 format.")] string endUtc,
        [Description("Maximum candle count.")] int limit,
        CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.GetCandlesRangeAsync(
                symbol,
                interval,
                BinanceNativeMcpJson.ParseUtc(startUtc,nameof(startUtc)),
                BinanceNativeMcpJson.ParseUtc(endUtc,nameof(endUtc)),
                Math.Clamp(limit,1,1500),
                cancellationToken));

    [McpServerTool(Name="exchange_get_margin",ReadOnly=true,Destructive=false)]
    [Description("Read the Binance Futures Testnet margin snapshot.")]
    public async Task<string> GetMarginAsync(CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.Broker.GetMarginAsync(cancellationToken));

    [McpServerTool(Name="exchange_get_server_time",ReadOnly=true,Destructive=false)]
    [Description("Read Binance Futures Testnet server time in UTC.")]
    public async Task<string> GetServerTimeAsync(CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.GetServerTimeAsync(cancellationToken));

    [McpServerTool(Name="exchange_get_permissions",ReadOnly=true,Destructive=false)]
    [Description("Read Binance Futures Testnet account permissions.")]
    public async Task<string> GetPermissionsAsync(CancellationToken cancellationToken)
        =>BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.CheckPermissionsAsync(cancellationToken));

    [McpServerTool(Name="exchange_read_order_fee_evidence",ReadOnly=true,Destructive=false)]
    [Description("Read canonical fill and fee evidence for an existing Binance Futures Testnet order.")]
    public async Task<string> ReadOrderFeeEvidenceAsync(
        string symbol,
        string orderId,
        string clientOrderId,
        string status,
        decimal executedQuantity,
        decimal avgPrice,
        string type,
        string positionSide="",
        bool isProtection=false,
        string updatedAtUtc="",
        CancellationToken cancellationToken=default)
    {
        if(_runtime.Provider is not IExchangeOrderFeeEvidenceReader reader)
            throw new NotSupportedException("The selected Binance provider does not expose fee evidence.");

        PositionSide? parsedSide=string.IsNullOrWhiteSpace(positionSide)
            ?null
            :BinanceNativeMcpJson.ParseSide(positionSide);
        var updated=string.IsNullOrWhiteSpace(updatedAtUtc)
            ?DateTime.UtcNow
            :BinanceNativeMcpJson.ParseUtc(updatedAtUtc,nameof(updatedAtUtc));
        var order=new ExchangeOrder(
            symbol,orderId,clientOrderId,status,executedQuantity,avgPrice,type,parsedSide,isProtection,updated);
        return BinanceNativeMcpJson.Serialize(
            await reader.ReadOrderFeeEvidenceAsync(order,cancellationToken));
    }

    [McpServerTool(Name="exchange_read_funding_income",ReadOnly=true,Destructive=false)]
    [Description("Read canonical Binance Futures Testnet funding-income evidence for a UTC range.")]
    public async Task<string> ReadFundingIncomeAsync(
        string symbol,
        string startUtc,
        string endUtc,
        CancellationToken cancellationToken)
    {
        if(_runtime.Provider is not IExchangeFundingIncomeReader reader)
            throw new NotSupportedException("The selected Binance provider does not expose funding income.");
        return BinanceNativeMcpJson.Serialize(
            await reader.ReadFundingIncomeAsync(
                symbol,
                new DateTimeOffset(BinanceNativeMcpJson.ParseUtc(startUtc,nameof(startUtc)),TimeSpan.Zero),
                new DateTimeOffset(BinanceNativeMcpJson.ParseUtc(endUtc,nameof(endUtc)),TimeSpan.Zero),
                cancellationToken));
    }

}

[McpServerToolType]
public sealed class BinanceNativeWriteMcpTools
{
    private readonly BinanceNativeMcpRuntime _runtime;

    public BinanceNativeWriteMcpTools(BinanceNativeMcpRuntime runtime)
    {
        _runtime=runtime;
    }

    [McpServerTool(Name="exchange_place_market",ReadOnly=false,Destructive=true,Idempotent=true)]
    [Description("Place an idempotent Binance Futures Testnet market order through the existing WPE adapter.")]
    public async Task<string> PlaceMarketAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        [Description("Long or Short.")] string side,
        [Description("Canonical asset quantity.")] decimal quantity,
        [Description("Durable WPE client order id.")] string clientOrderId,
        [Description("True only for position-reducing orders.")] bool reduceOnly=false,
        CancellationToken cancellationToken=default)
    {
        _runtime.RequireWrite("exchange_place_market");
        return BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.PlaceMarketAsync(
                symbol,BinanceNativeMcpJson.ParseSide(side),quantity,clientOrderId,reduceOnly,cancellationToken));
    }

    [McpServerTool(Name="exchange_place_limit",ReadOnly=false,Destructive=true,Idempotent=true)]
    [Description("Place an idempotent Binance Futures Testnet limit order through the existing WPE adapter.")]
    public async Task<string> PlaceLimitAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        [Description("Long or Short.")] string side,
        [Description("Canonical asset quantity.")] decimal quantity,
        [Description("Limit price.")] decimal price,
        [Description("Durable WPE client order id.")] string clientOrderId,
        [Description("True only for position-reducing orders.")] bool reduceOnly=false,
        CancellationToken cancellationToken=default)
    {
        _runtime.RequireWrite("exchange_place_limit");
        return BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.PlaceLimitAsync(
                symbol,BinanceNativeMcpJson.ParseSide(side),quantity,price,clientOrderId,reduceOnly,cancellationToken));
    }

    [McpServerTool(Name="exchange_cancel_order",ReadOnly=false,Destructive=true,Idempotent=true)]
    [Description("Cancel a Binance Futures Testnet order through the existing WPE adapter.")]
    public async Task<string> CancelOrderAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        [Description("Exchange order id.")] string orderId,
        CancellationToken cancellationToken)
    {
        _runtime.RequireWrite("exchange_cancel_order");
        await _runtime.Provider.CancelOrderAsync(symbol,orderId,cancellationToken);
        return BinanceNativeMcpJson.Serialize(new{ok=true,symbol,orderId});
    }

    [McpServerTool(Name="exchange_place_protection",ReadOnly=false,Destructive=true,Idempotent=false)]
    [Description("Place stop-loss and take-profit protection for a Binance Futures Testnet position.")]
    public async Task<string> PlaceProtectionAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        [Description("Long or Short side of the position being protected.")] string side,
        [Description("Stop-loss trigger price.")] decimal stopLoss,
        [Description("Take-profit trigger price.")] decimal takeProfit,
        [Description("Durable WPE protection group id.")] string groupId,
        CancellationToken cancellationToken)
    {
        _runtime.RequireWrite("exchange_place_protection");
        return BinanceNativeMcpJson.Serialize(
            await _runtime.Provider.PlaceProtectionAsync(
                symbol,BinanceNativeMcpJson.ParseSide(side),stopLoss,takeProfit,groupId,cancellationToken));
    }

    [McpServerTool(Name="exchange_set_leverage",ReadOnly=false,Destructive=true,Idempotent=true)]
    [Description("Set Binance Futures Testnet leverage for a symbol.")]
    public async Task<string> SetLeverageAsync(
        [Description("Canonical symbol such as BTCUSDT.")] string symbol,
        [Description("Requested leverage.")] int leverage,
        CancellationToken cancellationToken)
    {
        _runtime.RequireWrite("exchange_set_leverage");
        await _runtime.Provider.SetLeverageAsync(symbol,leverage,cancellationToken);
        return BinanceNativeMcpJson.Serialize(new{ok=true,symbol,leverage});
    }

    [McpServerTool(Name="exchange_set_margin_mode",ReadOnly=false,Destructive=true,Idempotent=true)]
    [Description("Set isolated or cross margin mode for a Binance Futures Testnet symbol.")]
    public async Task<string> SetMarginModeAsync(
        string symbol,
        bool isolated,
        CancellationToken cancellationToken)
    {
        _runtime.RequireWrite("exchange_set_margin_mode");
        await _runtime.Provider.SetMarginModeAsync(symbol,isolated,cancellationToken);
        return BinanceNativeMcpJson.Serialize(new{ok=true,symbol,isolated});
    }

    [McpServerTool(Name="exchange_set_hedge_mode",ReadOnly=false,Destructive=true,Idempotent=true)]
    [Description("Enable or disable Binance Futures Testnet hedge mode.")]
    public async Task<string> SetHedgeModeAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        _runtime.RequireWrite("exchange_set_hedge_mode");
        await _runtime.Provider.SetHedgeModeAsync(enabled,cancellationToken);
        return BinanceNativeMcpJson.Serialize(new{ok=true,enabled});
    }
}

internal static class BinanceNativeMcpJson
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull,
        Converters={new JsonStringEnumConverter()}
    };

    public static PositionSide ParseSide(string value)=>
        Enum.TryParse<PositionSide>(value,true,out var side)
            ?side
            :throw new ArgumentException("side must be Long or Short.",nameof(value));

    public static DateTime ParseUtc(string value,string parameterName)
    {
        if(!DateTimeOffset.TryParse(
               value,
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AssumeUniversal|
               System.Globalization.DateTimeStyles.AdjustToUniversal,
               out var parsed))
            throw new ArgumentException("Value must be a valid ISO-8601 UTC timestamp.",parameterName);
        return parsed.UtcDateTime;
    }

    public static string Serialize<T>(T value)=>JsonSerializer.Serialize(value,JsonOptions);
}

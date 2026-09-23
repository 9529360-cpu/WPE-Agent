using System.Globalization;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange.Mcp;

public sealed partial class OkxOfficialMcpProvider
{
    private static JsonElement Root(ExchangeMcpToolResult result)
    {
        if(result.IsError)
            throw new InvalidOperationException($"OKX MCP tool failed: {Safe(result.Text)}");
        if(result.StructuredContent is not { } structured||
           structured.ValueKind!=JsonValueKind.Object)
            throw new InvalidOperationException("OKX MCP tool did not return structured content.");
        if(structured.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.False)
            throw new InvalidOperationException($"OKX MCP tool failed: {Safe(result.Text)}");
        return structured;
    }

    private static JsonElement DataArray(ExchangeMcpToolResult result)
    {
        var root=Root(result);
        if(!root.TryGetProperty("data",out var envelope)||
           envelope.ValueKind!=JsonValueKind.Object||
           !envelope.TryGetProperty("data",out var data)||
           data.ValueKind!=JsonValueKind.Array)
            throw new InvalidOperationException("OKX MCP tool payload does not contain a data array.");
        return data;
    }

    private static decimal Dec(JsonElement element,string name)=>
        element.TryGetProperty(name,out var value)?Decimal(Value(value)):0;

    private static decimal Decimal(string value)=>
        decimal.TryParse(value,NumberStyles.Any,CultureInfo.InvariantCulture,out var result)
            ?result
            :0;

    private static string Str(JsonElement element,string name)=>
        element.TryGetProperty(name,out var value)
            ?Value(value)
            :string.Empty;

    private static string Value(JsonElement element,int index)=>Value(element[index]);

    private static string Value(JsonElement value)=>value.ValueKind==JsonValueKind.String
        ?value.GetString()??string.Empty
        :value.GetRawText();

    private static DateTime Millis(string value)=>
        long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var milliseconds)
            ?DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime
            :DateTime.UnixEpoch;

    private static DateTime OrderTimestamp(string value)=>Millis(value);

    private static string Status(string value)=>value switch
    {
        "live"=>"NEW",
        "partially_filled"=>"PARTIALLY_FILLED",
        "filled" or "effective"=>"FILLED",
        "canceled" or "mmp_canceled"=>"CANCELED",
        "order_failed"=>"REJECTED",
        _=>"UNKNOWN"
    };

    private static IReadOnlyList<ExchangeOrder> NormalizeOrderEvents(IEnumerable<ExchangeOrder> orders)=>
        orders
            .GroupBy(
                order=>string.IsNullOrWhiteSpace(order.ClientOrderId)
                    ?$"order:{order.OrderId}"
                    :$"client:{order.ClientOrderId}",
                StringComparer.Ordinal)
            .Select(group=>group
                .OrderByDescending(order=>order.Status switch
                {
                    "FILLED" or "CANCELED" or "REJECTED"=>3,
                    "UNKNOWN"=>2,
                    "PARTIALLY_FILLED"=>1,
                    _=>0
                })
                .ThenByDescending(order=>order.UpdatedAt)
                .First())
            .OrderBy(order=>order.OrderId,StringComparer.Ordinal)
            .ToArray();

    private static string ClientId(string value)
    {
        var normalized=new string(value.Where(char.IsLetterOrDigit).ToArray());
        if(normalized.Length==0)normalized=Guid.NewGuid().ToString("N");
        return normalized[..Math.Min(32,normalized.Length)];
    }

    private static string ProtectionClientId(string parentClientOrderId,string suffix)
    {
        var parent=ClientId(parentClientOrderId);
        var kind=new string((suffix??string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if(kind.Length==0)throw new ArgumentException("Protection suffix is required.",nameof(suffix));
        var stemLength=Math.Max(1,32-kind.Length);
        var stem=parent[..Math.Min(stemLength,parent.Length)];
        return stem+kind;
    }

    private static bool TryProtectionClientId(
        string parentClientOrderId,
        string childClientOrderId,
        out ProtectionFillKind kind)
    {
        kind=default;
        if(string.IsNullOrWhiteSpace(parentClientOrderId)||string.IsNullOrWhiteSpace(childClientOrderId))
            return false;

        var child=ClientId(childClientOrderId);
        string suffix;
        if(child.EndsWith("SL",StringComparison.OrdinalIgnoreCase))
        {
            suffix="SL";
            kind=ProtectionFillKind.StopLoss;
        }
        else if(child.EndsWith("TP",StringComparison.OrdinalIgnoreCase))
        {
            suffix="TP";
            kind=ProtectionFillKind.TakeProfit;
        }
        else return false;

        var expected=ProtectionClientId(parentClientOrderId,suffix);
        return string.Equals(expected,child,StringComparison.OrdinalIgnoreCase);
    }

    private static string Bar(string interval)=>interval switch
    {
        "1m"=>"1m",
        "3m"=>"3m",
        "5m"=>"5m",
        "15m"=>"15m",
        "30m"=>"30m",
        "1h"=>"1H",
        "2h"=>"2H",
        "4h"=>"4H",
        "6h"=>"6H",
        "12h"=>"12H",
        "1d"=>"1D",
        _=>throw new ArgumentException($"Unsupported OKX candle interval '{interval}'.",nameof(interval))
    };

    private static decimal NonZero(decimal value,decimal fallback)=>value!=0?value:fallback;
    private static string F(decimal value)=>value.ToString(CultureInfo.InvariantCulture);
    private static string Safe(string value)=>
        global::币安量化机器人.Services.SensitiveDataRedactor.ForLog(value,240);
}

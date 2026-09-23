using System.Globalization;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange.Mcp;

public sealed partial class OkxOfficialMcpProvider
{
    public async Task<TradingRule> GetRulesAsync(string canonicalSymbol,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(canonicalSymbol);
        var native=Symbols.ToNative(canonical);
        var row=await InstrumentAsync(native,ct);
        var contractValue=ContractValue(row);
        _contractValues[native]=contractValue;

        var ticker=DataArray(await _mcp.CallToolAsync(
            "market_get_ticker",
            new Dictionary<string,object?> { ["instId"]=native, ["demo"]=true },
            ct));
        var price=ticker.GetArrayLength()>0?Dec(ticker[0],"last"):0;
        var step=Dec(row,"lotSz")*contractValue;
        var minimum=Dec(row,"minSz")*contractValue;
        var minNotional=minimum>0&&price>0?minimum*price:0;

        return new(
            canonical,
            step,
            Dec(row,"tickSz"),
            minimum,
            minNotional,
            (int)Math.Max(1,Dec(row,"lever")));
    }

    public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(
        string canonicalSymbol,
        string interval,
        int limit,
        CancellationToken ct)=>
        GetCandlePageAsync(canonicalSymbol,interval,Math.Clamp(limit,1,300),null,null,ct);

    public async Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(
        string canonicalSymbol,
        string interval,
        DateTime start,
        DateTime end,
        int limit,
        CancellationToken ct)
    {
        var startUtc=start.Kind==DateTimeKind.Utc?start:start.ToUniversalTime();
        var endUtc=end.Kind==DateTimeKind.Utc?end:end.ToUniversalTime();
        var remaining=Math.Clamp(limit,1,5000);
        var cursor=new DateTimeOffset(endUtc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var collected=new Dictionary<DateTime,CandleEvidence>();

        while(remaining>0)
        {
            var page=await GetCandlePageAsync(
                canonicalSymbol,
                interval,
                Math.Min(300,remaining),
                cursor,
                null,
                ct);
            if(page.Count==0)break;

            foreach(var candle in page)
                if(candle.OpenTime>=startUtc&&candle.OpenTime<=endUtc)
                    collected[candle.OpenTime]=candle;

            var oldest=page.Min(candle=>candle.OpenTime);
            if(oldest<=startUtc)break;
            cursor=new DateTimeOffset(oldest).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            remaining-=page.Count;
        }

        return collected.Values.OrderBy(value=>value.OpenTime).TakeLast(limit).ToArray();
    }

    public async Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(
        string canonicalSymbol,
        CancellationToken ct)=>[await GetDerivativesAsync(canonicalSymbol,ct)];

    public Task<MarketEvidence> GetMarketAsync(string canonicalSymbol,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(canonicalSymbol);
        return CanonicalMarketEvidenceBuilder.BuildAsync(
            canonical,
            (interval,limit,token)=>GetCandlesAsync(canonical,interval,limit,token),
            token=>GetDerivativesAsync(canonical,token),
            ct);
    }

    private async Task<IReadOnlyList<CandleEvidence>> GetCandlePageAsync(
        string canonicalSymbol,
        string interval,
        int limit,
        string? after,
        string? before,
        CancellationToken ct)
    {
        var args=new Dictionary<string,object?>
        {
            ["instId"]=Symbols.ToNative(canonicalSymbol),
            ["bar"]=Bar(interval),
            ["limit"]=limit,
            ["demo"]=true
        };
        if(after is not null)args["after"]=after;
        if(before is not null)args["before"]=before;

        var rows=DataArray(await _mcp.CallToolAsync("market_get_candles",args,ct));
        return rows.EnumerateArray()
            .Where(row=>row.ValueKind==JsonValueKind.Array&&row.GetArrayLength()>=6)
            .Select(row=>new CandleEvidence(
                Millis(Value(row,0)),
                Decimal(Value(row,1)),
                Decimal(Value(row,2)),
                Decimal(Value(row,3)),
                Decimal(Value(row,4)),
                Decimal(Value(row,5)),
                row.GetArrayLength()>7?Decimal(Value(row,7)):0,
                0,
                0))
            .OrderBy(candle=>candle.OpenTime)
            .ToArray();
    }

    private async Task<DerivativesSnapshot> GetDerivativesAsync(
        string canonicalSymbol,
        CancellationToken ct)
    {
        var native=Symbols.ToNative(canonicalSymbol);
        decimal funding=0,openInterest=0,mark=0,index=0;

        try
        {
            var rows=DataArray(await _mcp.CallToolAsync(
                "market_get_funding_rate",
                new Dictionary<string,object?>
                {
                    ["instId"]=native,
                    ["history"]=false,
                    ["demo"]=true
                },
                ct));
            if(rows.GetArrayLength()>0)funding=Dec(rows[0],"fundingRate");
        }
        catch{}

        try
        {
            var rows=DataArray(await _mcp.CallToolAsync(
                "market_get_open_interest",
                new Dictionary<string,object?>
                {
                    ["instType"]="SWAP",
                    ["instId"]=native,
                    ["demo"]=true
                },
                ct));
            if(rows.GetArrayLength()>0)openInterest=Dec(rows[0],"oi");
        }
        catch{}

        try
        {
            var rows=DataArray(await _mcp.CallToolAsync(
                "market_get_mark_price",
                new Dictionary<string,object?>
                {
                    ["instType"]="SWAP",
                    ["instId"]=native,
                    ["demo"]=true
                },
                ct));
            if(rows.GetArrayLength()>0)mark=Dec(rows[0],"markPx");
        }
        catch{}

        try
        {
            var rows=DataArray(await _mcp.CallToolAsync(
                "market_get_index_ticker",
                new Dictionary<string,object?>
                {
                    ["instId"]=native.Replace("-SWAP","",StringComparison.OrdinalIgnoreCase)
                },
                ct));
            if(rows.GetArrayLength()>0)index=Dec(rows[0],"idxPx");
        }
        catch{}

        return new(
            funding,
            openInterest,
            0,0,0,0,
            index>0?(mark-index)/index:0);
    }

    private async Task<decimal> ContractValueAsync(string native,CancellationToken ct)
    {
        if(_contractValues.TryGetValue(native,out var cached)&&cached>0)
            return cached;
        var row=await InstrumentAsync(native,ct);
        var value=ContractValue(row);
        _contractValues[native]=value;
        return value;
    }

    private async Task<JsonElement> InstrumentAsync(string native,CancellationToken ct)
    {
        var rows=DataArray(await _mcp.CallToolAsync(
            "market_get_instruments",
            new Dictionary<string,object?>
            {
                ["instType"]="SWAP",
                ["instId"]=native,
                ["demo"]=true
            },
            ct));
        if(rows.GetArrayLength()==0)
            throw new InvalidOperationException($"OKX MCP instrument '{native}' was not found.");
        return rows[0].Clone();
    }

    private static decimal ContractValue(JsonElement row)=>
        Math.Max(Dec(row,"ctVal"),0.00000001m)*Math.Max(Dec(row,"ctMult"),1);

}

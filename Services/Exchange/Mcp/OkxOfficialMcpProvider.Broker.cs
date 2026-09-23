using System.Globalization;
using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace 币安量化机器人.Services.Exchange.Mcp;

public sealed partial class OkxOfficialMcpProvider
{
    public async Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)
    {
        var rows=DataArray(await _mcp.CallToolAsync(
            "account_get_balance",
            new Dictionary<string,object?> { ["ccy"]="USDT" },
            ct));
        if(rows.GetArrayLength()==0)
            throw new InvalidOperationException("OKX MCP balance response is empty.");

        var account=rows[0];
        var wallet=Dec(account,"totalEq");
        var available=Dec(account,"adjEq");
        var equity=Dec(account,"totalEq");

        if(account.TryGetProperty("details",out var details)&&details.ValueKind==JsonValueKind.Array)
        {
            var usdt=details.EnumerateArray().FirstOrDefault(row=>Str(row,"ccy")=="USDT");
            if(usdt.ValueKind!=JsonValueKind.Undefined)
            {
                wallet=NonZero(Dec(usdt,"cashBal"),wallet);
                available=NonZero(Dec(usdt,"availBal"),available);
                equity=NonZero(Dec(usdt,"eq"),equity);
            }
        }

        return new(wallet,available,equity,DateTime.UtcNow);
    }

    public async Task<MarginSnapshot> GetMarginAsync(CancellationToken ct)
    {
        var account=await GetAccountAsync(ct);
        var used=Math.Max(0,account.Equity-account.AvailableBalance);
        return new(
            account.WalletBalance,
            account.AvailableBalance,
            used,
            0,
            account.Equity>0?used/account.Equity:0);
    }

    public async Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct)
    {
        var rows=DataArray(await _mcp.CallToolAsync(
            "swap_get_positions",
            new Dictionary<string,object?> { ["instType"]="SWAP" },
            ct));
        var result=new List<ManagedPosition>();
        foreach(var row in rows.EnumerateArray())
        {
            var contracts=Math.Abs(Dec(row,"pos"));
            if(contracts<=0)continue;
            var native=Str(row,"instId");
            var contractValue=await ContractValueAsync(native,ct);
            result.Add(new(
                Symbols.ToCanonical(native),
                Str(row,"posSide")=="short"||Dec(row,"pos")<0
                    ?PositionSide.Short
                    :PositionSide.Long,
                contracts*contractValue,
                Dec(row,"avgPx"),
                Dec(row,"markPx"),
                Dec(row,"upl"),
                Dec(row,"lever"),
                Str(row,"mgnMode")=="isolated",
                Dec(row,"liqPx")));
        }
        return result;
    }

    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(
        string? canonicalSymbol,
        CancellationToken ct)
    {
        var native=string.IsNullOrWhiteSpace(canonicalSymbol)
            ?null
            :Symbols.ToNative(canonicalSymbol);
        var args=new Dictionary<string,object?>
        {
            ["status"]="open",
            ["instType"]="SWAP"
        };
        if(native is not null)args["instId"]=native;

        var result=new List<ExchangeOrder>();
        var regular=DataArray(await _mcp.CallToolAsync("swap_get_orders",args,ct));
        foreach(var row in regular.EnumerateArray())
            result.Add(await MapOrderAsync(row,false,ct));

        var algoArgs=new Dictionary<string,object?>
        {
            ["status"]="pending",
            ["instType"]="SWAP"
        };
        if(native is not null)algoArgs["instId"]=native;
        var algo=DataArray(await _mcp.CallToolAsync("swap_get_algo_orders",algoArgs,ct));
        foreach(var row in algo.EnumerateArray())
            result.Add(await MapOrderAsync(row,true,ct));

        return NormalizeOrderEvents(result);
    }

    public async Task SetLeverageAsync(string canonicalSymbol,int leverage,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(canonicalSymbol);
        var native=Symbols.ToNative(canonical);
        var marginMode=_isolated.GetValueOrDefault(canonical,true)?"isolated":"cross";
        var sides=_hedgeMode?new[]{"long","short"}:new string?[]{null};

        foreach(var side in sides)
        {
            var args=new Dictionary<string,object?>
            {
                ["instId"]=native,
                ["lever"]=leverage.ToString(CultureInfo.InvariantCulture),
                ["mgnMode"]=marginMode
            };
            if(side is not null)args["posSide"]=side;
            _=DataArray(await _mcp.CallToolAsync("swap_set_leverage",args,ct));
        }

        _leverage[canonical]=leverage;
    }

    public async Task SetMarginModeAsync(string canonicalSymbol,bool isolated,CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(canonicalSymbol);
        _isolated[canonical]=isolated;
        if(_leverage.TryGetValue(canonical,out var leverage))
            await SetLeverageAsync(canonical,leverage,ct);
    }

    public async Task SetHedgeModeAsync(bool enabled,CancellationToken ct)
    {
        var args=new Dictionary<string,object?>
        {
            ["posMode"]=enabled?"long_short_mode":"net_mode"
        };
        try
        {
            _=DataArray(await _mcp.CallToolAsync("account_set_position_mode",args,ct));
        }
        catch(InvalidOperationException ex) when(
            ex.Message.Contains("already",StringComparison.OrdinalIgnoreCase)||
            ex.Message.Contains("51000",StringComparison.OrdinalIgnoreCase))
        {
        }
        _hedgeMode=enabled;
    }

    public Task<ExchangeOrder> PlaceMarketAsync(
        string canonicalSymbol,
        PositionSide side,
        decimal quantity,
        string clientOrderId,
        bool reduceOnly,
        CancellationToken ct)=>
        PlaceAsync(canonicalSymbol,side,quantity,0,"market",clientOrderId,reduceOnly,ct);

    public Task<ExchangeOrder> PlaceLimitAsync(
        string canonicalSymbol,
        PositionSide side,
        decimal quantity,
        decimal price,
        string clientOrderId,
        bool reduceOnly,
        CancellationToken ct)=>
        PlaceAsync(canonicalSymbol,side,quantity,price,"limit",clientOrderId,reduceOnly,ct);

    public async Task<ExchangeOrder> PlaceProtectionAsync(
        string canonicalSymbol,
        PositionSide sideToClose,
        decimal stopLoss,
        decimal takeProfit,
        string groupId,
        CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(canonicalSymbol);
        var position=(await GetPositionsAsync(ct))
            .FirstOrDefault(value=>value.Symbol==canonical&&value.Side==sideToClose);
        if(position is null||position.Quantity<=0)
            throw new InvalidOperationException(
                $"OKX MCP {canonical} position was not found for protection.");

        var native=Symbols.ToNative(canonical);
        var contracts=position.Quantity/await ContractValueAsync(native,ct);
        var stop=await PlaceProtectionLegAsync(
            canonical,native,position,sideToClose,contracts,stopLoss,groupId,"SL",ct);

        try
        {
            _=await PlaceProtectionLegAsync(
                canonical,native,position,sideToClose,contracts,takeProfit,groupId,"TP",ct);
        }
        catch
        {
            try{await CancelOrderAsync(canonical,stop.OrderId,ct);}catch{}
            throw;
        }

        return stop;
    }

    private async Task<ExchangeOrder> PlaceProtectionLegAsync(
        string canonical,
        string native,
        ManagedPosition position,
        PositionSide sideToClose,
        decimal contracts,
        decimal triggerPrice,
        string groupId,
        string kind,
        CancellationToken ct)
    {
        var clientId=ProtectionClientId(groupId,kind);
        var args=new Dictionary<string,object?>
        {
            ["instId"]=native,
            ["tdMode"]=position.Isolated?"isolated":"cross",
            ["side"]=sideToClose==PositionSide.Long?"sell":"buy",
            ["posSide"]=_hedgeMode
                ?sideToClose==PositionSide.Long?"long":"short"
                :"net",
            ["ordType"]="conditional",
            ["sz"]=F(contracts),
            ["algoClOrdId"]=clientId
        };

        if(kind=="SL")
        {
            args["slTriggerPx"]=F(triggerPrice);
            args["slOrdPx"]="-1";
            args["slTriggerPxType"]="mark";
        }
        else
        {
            args["tpTriggerPx"]=F(triggerPrice);
            args["tpOrdPx"]="-1";
            args["tpTriggerPxType"]="mark";
        }
        if(!_hedgeMode)args["reduceOnly"]=true;

        var rows=DataArray(await _mcp.CallToolAsync("swap_place_algo_order",args,ct));
        if(rows.GetArrayLength()==0)
            throw new InvalidOperationException($"OKX MCP {kind} protection response is empty.");
        var id=Str(rows[0],"algoId");
        if(string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"OKX MCP {kind} protection id is missing.");

        _algoIds[id]=0;
        return new(
            canonical,
            id,
            clientId,
            "NEW",
            0,
            0,
            kind=="SL"?"STOP_MARKET":"TAKE_PROFIT_MARKET",
            sideToClose,
            true,
            DateTime.UtcNow);
    }

    public async Task<ExchangeOrder?> FindOrderAsync(
        string canonicalSymbol,
        string clientOrderId,
        CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(canonicalSymbol);
        var rows=DataArray(await _mcp.CallToolAsync(
            "swap_get_order",
            new Dictionary<string,object?>
            {
                ["instId"]=Symbols.ToNative(canonical),
                ["clOrdId"]=ClientId(clientOrderId)
            },
            ct));
        if(rows.GetArrayLength()==0)return null;
        var mapped=await MapOrderAsync(rows[0],false,ct);
        return mapped with { ClientOrderId=clientOrderId };
    }

    public async Task CancelOrderAsync(
        string canonicalSymbol,
        string orderId,
        CancellationToken ct)
    {
        var native=Symbols.ToNative(canonicalSymbol);
        if(_algoIds.TryRemove(orderId,out _))
        {
            _=DataArray(await _mcp.CallToolAsync(
                "swap_cancel_algo_orders",
                new Dictionary<string,object?>
                {
                    ["orders"]=new[]
                    {
                        new Dictionary<string,object?>
                        {
                            ["instId"]=native,
                            ["algoId"]=orderId
                        }
                    }
                },
                ct));
            return;
        }

        _=DataArray(await _mcp.CallToolAsync(
            "swap_cancel_order",
            new Dictionary<string,object?>
            {
                ["instId"]=native,
                ["ordId"]=orderId
            },
            ct));
    }

    private async Task<ExchangeOrder> PlaceAsync(
        string canonicalSymbol,
        PositionSide side,
        decimal quantity,
        decimal price,
        string type,
        string clientOrderId,
        bool reduceOnly,
        CancellationToken ct)
    {
        var canonical=Symbols.ToCanonical(canonicalSymbol);
        var native=Symbols.ToNative(canonical);
        var contracts=quantity/await ContractValueAsync(native,ct);
        var orderSide=side==PositionSide.Long?"buy":"sell";
        if(reduceOnly)orderSide=orderSide=="buy"?"sell":"buy";
        var args=new Dictionary<string,object?>
        {
            ["instId"]=native,
            ["tdMode"]=_isolated.GetValueOrDefault(canonical,true)?"isolated":"cross",
            ["clOrdId"]=ClientId(clientOrderId),
            ["side"]=orderSide,
            ["posSide"]=_hedgeMode
                ?side==PositionSide.Long?"long":"short"
                :"net",
            ["ordType"]=type,
            ["sz"]=F(contracts)
        };
        if(type=="limit")args["px"]=F(price);
        if(!_hedgeMode&&reduceOnly)args["reduceOnly"]=true;

        var rows=DataArray(await _mcp.CallToolAsync("swap_place_order",args,ct));
        if(rows.GetArrayLength()==0)
            throw new InvalidOperationException("OKX MCP order response is empty.");
        var id=Str(rows[0],"ordId");
        if(string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("OKX MCP order id is missing.");

        return new(
            canonical,
            id,
            clientOrderId,
            "NEW",
            0,
            0,
            type.ToUpperInvariant(),
            side,
            false,
            DateTime.UtcNow);
    }

    private async Task<ExchangeOrder> MapOrderAsync(
        JsonElement row,
        bool algo,
        CancellationToken ct)
    {
        var id=algo?Str(row,"algoId"):Str(row,"ordId");
        if(algo&&!string.IsNullOrWhiteSpace(id))_algoIds[id]=0;
        var native=Str(row,"instId");
        var contractValue=await ContractValueAsync(native,ct);
        var side=Str(row,"posSide")=="short"?PositionSide.Short:PositionSide.Long;
        var executed=algo
            ?NonZero(Dec(row,"actualSz"),Dec(row,"accFillSz"))
            :Dec(row,"accFillSz");
        var averagePrice=algo
            ?NonZero(Dec(row,"actualPx"),Dec(row,"avgPx"))
            :Dec(row,"avgPx");

        return new(
            Symbols.ToCanonical(native),
            id,
            algo?Str(row,"algoClOrdId"):Str(row,"clOrdId"),
            Status(Str(row,"state")),
            executed*contractValue,
            averagePrice,
            Str(row,"ordType").ToUpperInvariant(),
            side,
            algo,
            OrderTimestamp(Str(row,"uTime") is {Length:>0} value?value:Str(row,"cTime")));
    }

}

using System.Runtime.CompilerServices;
using System.Text.Json;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;
using 币安量化机器人.Services.Exchange.Mcp;

namespace WPE.Tests;

public sealed class OfficialExchangeMcpProviderTests
{
    [Fact]
    public void CatalogExposesOkxOfficialMcpProvider()
    {
        var descriptor=Assert.Single(
            new ExchangeProviderCatalog().Installed,
            value=>value.Id=="okx-mcp");

        Assert.Equal("OKX Official MCP",descriptor.DisplayName);
        Assert.True(descriptor.SupportsTestnet);
        Assert.False(descriptor.SupportsMainnet);
        Assert.Contains("mcp",descriptor.Capabilities);
    }

    [Fact]
    public async Task OkxRulesComeFromOfficialMcpInstrumentAndTicker()
    {
        await using var client=new FakeMcpClient(
            readOnly:true,
            (tool,args)=>tool switch
            {
                "market_get_instruments"=>Success(new[]
                {
                    new Dictionary<string,object?>
                    {
                        ["instId"]="BTC-USDT-SWAP",
                        ["ctVal"]="0.01",
                        ["ctMult"]="1",
                        ["lotSz"]="0.01",
                        ["tickSz"]="0.1",
                        ["minSz"]="0.01",
                        ["lever"]="100"
                    }
                }),
                "market_get_ticker"=>Success(new[]
                {
                    new Dictionary<string,object?> { ["last"]="86000" }
                }),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=Provider(client);

        var rule=await provider.GetRulesAsync("BTCUSDT",CancellationToken.None);

        Assert.Equal("BTCUSDT",rule.Symbol);
        Assert.Equal(0.0001m,rule.StepSize);
        Assert.Equal(0.1m,rule.TickSize);
        Assert.Equal(0.0001m,rule.MinQuantity);
        Assert.Equal(8.6m,rule.MinNotional);
        Assert.Equal(100,rule.MaxLeverage);
        Assert.Equal(
            ["market_get_instruments","market_get_ticker"],
            client.Calls.Select(call=>call.Tool).ToArray());
    }

    [Fact]
    public async Task OkxMarketOrderUsesOfficialSwapToolAndDurableClientOrderId()
    {
        await using var client=new FakeMcpClient(
            readOnly:false,
            (tool,args)=>tool switch
            {
                "market_get_instruments"=>Success(new[]
                {
                    new Dictionary<string,object?>
                    {
                        ["instId"]="BTC-USDT-SWAP",
                        ["ctVal"]="0.01",
                        ["ctMult"]="1"
                    }
                }),
                "swap_place_order"=>Success(new[]
                {
                    new Dictionary<string,object?> { ["ordId"]="okx-order-42" }
                }),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=Provider(client);

        var order=await provider.PlaceMarketAsync(
            "BTCUSDT",
            PositionSide.Long,
            .01m,
            "WPE-DIRECT-0001",
            reduceOnly:false,
            CancellationToken.None);

        Assert.Equal("okx-order-42",order.OrderId);
        Assert.Equal("WPE-DIRECT-0001",order.ClientOrderId);
        var call=Assert.Single(client.Calls,value=>value.Tool=="swap_place_order");
        Assert.Equal("BTC-USDT-SWAP",call.Arguments["instId"]);
        Assert.Equal("buy",call.Arguments["side"]);
        Assert.Equal("long",call.Arguments["posSide"]);
        Assert.Equal("market",call.Arguments["ordType"]);
        Assert.Equal("1",call.Arguments["sz"]);
        Assert.Equal("WPEDIRECT0001",call.Arguments["clOrdId"]);
    }

    [Fact]
    public async Task OkxCancelUsesOfficialMcpTool()
    {
        await using var client=new FakeMcpClient(
            readOnly:false,
            (tool,args)=>tool switch
            {
                "swap_cancel_order"=>Success(new[]
                {
                    new Dictionary<string,object?> { ["ordId"]="42" }
                }),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=Provider(client);

        await provider.CancelOrderAsync("BTCUSDT","42",CancellationToken.None);

        var call=Assert.Single(client.Calls);
        Assert.Equal("swap_cancel_order",call.Tool);
        Assert.Equal("BTC-USDT-SWAP",call.Arguments["instId"]);
        Assert.Equal("42",call.Arguments["ordId"]);
    }

    [Fact]
    public async Task OkxProtectionUsesSeparateRecoverableOfficialAlgoTools()
    {
        var nextAlgo=0;
        await using var client=new FakeMcpClient(
            readOnly:false,
            (tool,args)=>tool switch
            {
                "swap_get_positions"=>Success(new[]
                {
                    new Dictionary<string,object?>
                    {
                        ["instId"]="BTC-USDT-SWAP",
                        ["pos"]="1",
                        ["posSide"]="long",
                        ["avgPx"]="86000",
                        ["markPx"]="86100",
                        ["upl"]="1",
                        ["lever"]="10",
                        ["mgnMode"]="isolated",
                        ["liqPx"]="78000"
                    }
                }),
                "market_get_instruments"=>Success(new[]
                {
                    new Dictionary<string,object?>
                    {
                        ["instId"]="BTC-USDT-SWAP",
                        ["ctVal"]="0.01",
                        ["ctMult"]="1"
                    }
                }),
                "swap_place_algo_order"=>Success(new[]
                {
                    new Dictionary<string,object?>
                    {
                        ["algoId"]=(++nextAlgo).ToString()
                    }
                }),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=Provider(client);

        var stop=await provider.PlaceProtectionAsync(
            "BTCUSDT",PositionSide.Long,85000m,88000m,"WPE-DIRECT-0001",CancellationToken.None);

        Assert.Equal("STOP_MARKET",stop.Type);
        var writes=client.Calls.Where(call=>call.Tool=="swap_place_algo_order").ToArray();
        Assert.Equal(2,writes.Length);
        Assert.All(writes,call=>Assert.Equal("conditional",call.Arguments["ordType"]));
        Assert.EndsWith("SL",writes[0].Arguments["algoClOrdId"]?.ToString(),StringComparison.Ordinal);
        Assert.EndsWith("TP",writes[1].Arguments["algoClOrdId"]?.ToString(),StringComparison.Ordinal);
        Assert.Contains("slTriggerPx",writes[0].Arguments.Keys);
        Assert.DoesNotContain("tpTriggerPx",writes[0].Arguments.Keys);
        Assert.Contains("tpTriggerPx",writes[1].Arguments.Keys);
        Assert.DoesNotContain("slTriggerPx",writes[1].Arguments.Keys);
    }

    [Fact]
    public async Task OkxRecoveryIdentifiesTriggeredProtectionLegFromOfficialHistory()
    {
        await using var client=new FakeMcpClient(
            readOnly:true,
            (tool,args)=>tool switch
            {
                "swap_get_orders"=>Success(Array.Empty<object>()),
                "swap_get_algo_orders"=>Success(new[]
                {
                    new Dictionary<string,object?>
                    {
                        ["instId"]="BTC-USDT-SWAP",
                        ["algoId"]="algo-sl",
                        ["algoClOrdId"]="WPEDIRECT0001SL",
                        ["state"]="effective",
                        ["actualSz"]="1",
                        ["actualPx"]="85000",
                        ["ordType"]="conditional",
                        ["posSide"]="long",
                        ["uTime"]="1790121600000"
                    }
                }),
                "market_get_instruments"=>Success(new[]
                {
                    new Dictionary<string,object?>
                    {
                        ["instId"]="BTC-USDT-SWAP",
                        ["ctVal"]="0.01",
                        ["ctMult"]="1"
                    }
                }),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=Provider(client);

        var recent=await provider.GetRecentOrdersAsync("BTCUSDT",100,CancellationToken.None);
        var order=Assert.Single(recent);

        Assert.Equal("FILLED",order.Status);
        Assert.Equal(.01m,order.ExecutedQuantity);
        Assert.True(order.IsProtection);
        Assert.True(provider.TryMatchProtectionFill(
            "WPE-DIRECT-0001",order,out var kind));
        Assert.Equal(ProtectionFillKind.StopLoss,kind);
    }

    [Fact]
    public async Task OkxFeeEvidenceUsesOfficialFillCommission()
    {
        await using var client=new FakeMcpClient(
            readOnly:true,
            (tool,args)=>tool switch
            {
                "swap_get_fills"=>Success(new[]
                {
                    new Dictionary<string,object?>
                    {
                        ["tradeId"]="trade-1",
                        ["ordId"]="order-42",
                        ["fillSz"]="1",
                        ["fee"]="-0.05",
                        ["feeCcy"]="USDT"
                    }
                }),
                "market_get_instruments"=>Success(new[]
                {
                    new Dictionary<string,object?>
                    {
                        ["instId"]="BTC-USDT-SWAP",
                        ["ctVal"]="0.01",
                        ["ctMult"]="1"
                    }
                }),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=Provider(client);
        var order=new ExchangeOrder(
            "BTCUSDT","order-42","WPE-DIRECT-0001","FILLED",.01m,86000m,
            "MARKET",PositionSide.Long,false,DateTime.UtcNow);

        var evidence=await provider.ReadOrderFeeEvidenceAsync(order,CancellationToken.None);

        Assert.Equal(ExchangeOrderFeeEvidenceStateV1.Confirmed,evidence.State);
        Assert.Equal(1,evidence.FillCount);
        Assert.Equal(.01m,evidence.ExecutedQuantity);
        Assert.Equal(.05m,evidence.FeeAmount);
        Assert.Equal("USDT",evidence.FeeAsset);
        Assert.True(ExchangeOrderFeeEvidenceCanonicalizerV1.IsCanonical(evidence));
    }

    [Fact]
    public void OkxMcpProviderContainsNoOkxRestProtocolImplementation()
    {
        var source=File.ReadAllText(Path.Combine(
            ProjectRoot(),
            "Services",
            "Exchange",
            "Mcp",
            "OkxOfficialMcpProvider.cs"));

        Assert.DoesNotContain("/api/v5/",source,StringComparison.Ordinal);
        Assert.DoesNotContain("HMACSHA256",source,StringComparison.Ordinal);
        Assert.DoesNotContain("OK-ACCESS-SIGN",source,StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestMessage",source,StringComparison.Ordinal);
    }

    [Fact]
    public void SidecarPinsOfficialExchangeMcpOriginsAndOAuthBoundary()
    {
        var root=ProjectRoot();
        var profiles=File.ReadAllText(Path.Combine(
            root,"WPE.ExchangeMcp","OfficialExchangeMcpProfiles.cs"));
        var oauth=File.ReadAllText(Path.Combine(
            root,"WPE.ExchangeMcp","BrowserOAuthAuthorization.cs"));
        var cache=File.ReadAllText(Path.Combine(
            root,"WPE.ExchangeMcp","ProtectedTokenCache.cs"));

        Assert.Contains("@okx_ai/okx-trade-mcp",profiles,StringComparison.Ordinal);
        Assert.Contains("\"--demo\"",profiles,StringComparison.Ordinal);
        Assert.Contains("\"--read-only\"",profiles,StringComparison.Ordinal);
        var factory=File.ReadAllText(Path.Combine(
            root,"Services","Exchange","Mcp","OfficialExchangeMcpClientFactory.cs"));
        Assert.Contains("\"OKX_API_KEY\"",factory,StringComparison.Ordinal);
        Assert.Contains("\"OKX_SECRET_KEY\"",factory,StringComparison.Ordinal);
        Assert.Contains("\"OKX_PASSPHRASE\"",factory,StringComparison.Ordinal);
        Assert.Contains("\"OKX_API_BASE_URL\"",factory,StringComparison.Ordinal);
        Assert.DoesNotContain("\"WPE_OKX_API_KEY\"",factory,StringComparison.Ordinal);
        Assert.Contains(
            "https://agent.binance.com/mcp/agentic",
            profiles,
            StringComparison.Ordinal);
        Assert.Contains("ClientOAuthOptions",oauth,StringComparison.Ordinal);
        Assert.Contains("ClientMetadataDocumentUri",oauth,StringComparison.Ordinal);
        Assert.Contains("WPE_BINANCE_MCP_CLIENT_METADATA_URI",oauth,StringComparison.Ordinal);
        Assert.Contains("ProtectedTokenCache",oauth,StringComparison.Ordinal);
        Assert.DoesNotContain("DynamicClientRegistration",oauth,StringComparison.Ordinal);
        Assert.DoesNotContain("WPE_BINANCE_MCP_CLIENT_SECRET",oauth,StringComparison.Ordinal);
        Assert.Contains("DataProtectionScope.CurrentUser",cache,StringComparison.Ordinal);
        Assert.DoesNotContain("BINANCE_API_KEY",profiles,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BINANCE_SECRET",profiles,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BinanceLocalMcpWrapsExistingProviderWithoutReimplementingBinanceProtocol()
    {
        var root=ProjectRoot();
        var directory=Path.Combine(root,"WPE.BinanceMcp");
        var source=string.Join(
            Environment.NewLine,
            Directory.GetFiles(directory,"*.cs",SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));

        Assert.Contains("IExchangeProvider",source,StringComparison.Ordinal);
        Assert.Contains("ExchangeProviderCatalog",source,StringComparison.Ordinal);
        Assert.Contains("WPE_BINANCE_MCP_ALLOW_WRITE",source,StringComparison.Ordinal);
        Assert.Contains("WithTools<BinanceNativeWriteMcpTools>",source,StringComparison.Ordinal);
        Assert.Contains("--allow-write",source,StringComparison.Ordinal);
        Assert.DoesNotContain("/fapi/",source,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HMACSHA256",source,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-MBX-APIKEY",source,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpRequestMessage",source,StringComparison.Ordinal);
        Assert.DoesNotContain("Binance.Net.Clients",source,StringComparison.Ordinal);
    }

    [Fact]
    public void BinanceLocalMcpPluginRequiresDistinctUpstreamConnection()
    {
        var plugin=new BinanceLocalMcpProviderPlugin();
        var profile=new ExchangeConnectionProfile
        {
            Id="binance-mcp-test",
            ProviderId="binance-mcp-local",
            Endpoint="https://testnet.binancefuture.com",
            IsTestnet=true,
            ExecutionEnabled=false
        };

        var missing=Assert.Throws<InvalidOperationException>(()=>
            plugin.Create(profile,new Dictionary<string,string>()));
        Assert.Contains("UpstreamConnectionId",missing.Message,StringComparison.Ordinal);

        profile.UpstreamConnectionId=profile.Id;
        var recursive=Assert.Throws<InvalidOperationException>(()=>
            plugin.Create(profile,new Dictionary<string,string>()));
        Assert.Contains("cannot delegate to itself",recursive.Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BinanceLocalMcpProviderReportsReadOnlyExecutionGate()
    {
        var profile=new ExchangeConnectionProfile
        {
            Id="binance-mcp-test",
            ProviderId="binance-mcp-local",
            Endpoint="https://testnet.binancefuture.com",
            IsTestnet=true,
            ExecutionEnabled=true,
            UpstreamConnectionId="binance-testnet-default"
        };
        var client=new FakeMcpClient(
            readOnly:true,
            (_,_)=>new ExchangeMcpToolResult(false,"{}",null));

        await using var provider=new BinanceLocalMcpProvider(profile,client);
        var validation=provider.ValidateEnvironment(requireTestnet:true);

        Assert.True(validation.CanRead);
        Assert.False(validation.CanTrade);
        Assert.True(validation.TestnetAvailable);
        Assert.Contains("write gate",validation.Failure??string.Empty,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogExposesBinanceLocalMcpProviderAsTestnetOnly()
    {
        var descriptor=Assert.Single(
            new ExchangeProviderCatalog().Installed,
            value=>value.Id=="binance-mcp-local");

        Assert.Equal("Binance Futures Testnet MCP",descriptor.DisplayName);
        Assert.True(descriptor.SupportsTestnet);
        Assert.False(descriptor.SupportsMainnet);
        Assert.Empty(descriptor.CredentialFields);
        Assert.Contains("mcp",descriptor.Capabilities);
    }

    [Fact]
    public async Task BinanceLocalMcpProviderMapsReadContractToMcpTools()
    {
        await using var client=new FakeMcpClient(
            readOnly:true,
            (tool,args)=>tool switch
            {
                "exchange_get_rules"=>LocalSuccess(
                    new TradingRule("BTCUSDT",.001m,.1m,.001m,5m,20)),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=BinanceLocalProvider(client,executionEnabled:true);

        var environment=provider.ValidateEnvironment(requireTestnet:true);
        var rules=await provider.GetRulesAsync("BTCUSDT",CancellationToken.None);

        Assert.True(environment.CanRead);
        Assert.False(environment.CanTrade);
        Assert.Equal("BTCUSDT",rules.Symbol);
        var call=Assert.Single(client.Calls);
        Assert.Equal("exchange_get_rules",call.Tool);
        Assert.Equal("BTCUSDT",call.Arguments["symbol"]);
    }

    [Fact]
    public async Task BinanceLocalMcpProviderForwardsCanonicalMarketCatalog()
    {
        var checkedAt=DateTimeOffset.UtcNow;
        var expected=new ProviderMarketCatalog(
            ProviderCatalogState.Available,
            [
                new(
                    new Instrument("BTCUSDT","BTCUSDT","binance-futures","binance-futures",MarketType.Perpetual),
                    CapabilityStatus.Available,
                    true,
                    true,
                    true,
                    checkedAt)
            ],
            checkedAt);
        await using var client=new FakeMcpClient(
            readOnly:false,
            (tool,args)=>tool switch
            {
                "exchange_get_market_catalog"=>LocalSuccess(expected),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=BinanceLocalProvider(client,executionEnabled:true);

        var catalog=await provider.DiscoverMarketCatalogAsync(CancellationToken.None);

        Assert.Equal(ProviderCatalogState.Available,catalog.State);
        var market=Assert.Single(catalog.Markets);
        Assert.Equal("BTCUSDT",market.Instrument.CanonicalSymbol);
        Assert.True(market.CanTrade);
        Assert.Equal("exchange_get_market_catalog",Assert.Single(client.Calls).Tool);
    }

    [Fact]
    public async Task BinanceLocalMcpProviderForwardsCanonicalInstrumentFundamentals()
    {
        var now=DateTimeOffset.UtcNow;
        var expected=CryptoInstrumentFundamentalCanonicalizerV1.Create(
            "binance-futures","Testnet","BTCUSDT","BTCUSDT","BTC","USDT","USDT","PERPETUAL","TRADING",
            now.AddYears(-2),now,new string('a',64));
        await using var client=new FakeMcpClient(
            readOnly:true,
            (tool,args)=>tool switch
            {
                "exchange_get_instrument_fundamentals"=>LocalSuccess(new[]{expected}),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=BinanceLocalProvider(client,executionEnabled:false);

        var facts=await provider.GetInstrumentFundamentalsAsync(["BTCUSDT"],CancellationToken.None);

        var fact=Assert.Single(facts);
        Assert.True(CryptoInstrumentFundamentalCanonicalizerV1.IsCanonical(fact,DateTimeOffset.UtcNow));
        var call=Assert.Single(client.Calls);
        Assert.Equal("exchange_get_instrument_fundamentals",call.Tool);
        var symbols=Assert.IsAssignableFrom<IReadOnlyList<string>>(call.Arguments["symbols"]);
        Assert.Equal("BTCUSDT",Assert.Single(symbols));
    }

    [Fact]
    public async Task BinanceLocalMcpProviderMapsApprovedWriteToMcpTool()
    {
        var expected=new ExchangeOrder(
            "BTCUSDT",
            "order-1",
            "WPE-DIRECT-0002",
            "NEW",
            .01m,
            86000m,
            "MARKET",
            PositionSide.Long,
            false,
            DateTime.UtcNow);
        await using var client=new FakeMcpClient(
            readOnly:false,
            (tool,args)=>tool switch
            {
                "exchange_place_market"=>LocalSuccess(expected),
                _=>throw new InvalidOperationException($"Unexpected MCP tool {tool}")
            });
        await using var provider=BinanceLocalProvider(client,executionEnabled:true);

        var environment=provider.ValidateEnvironment(requireTestnet:true);
        var order=await provider.PlaceMarketAsync(
            "BTCUSDT",
            PositionSide.Long,
            .01m,
            "WPE-DIRECT-0002",
            reduceOnly:false,
            CancellationToken.None);

        Assert.True(environment.CanTrade);
        Assert.Equal(expected.OrderId,order.OrderId);
        var call=Assert.Single(client.Calls);
        Assert.Equal("exchange_place_market",call.Tool);
        Assert.Equal("BTCUSDT",call.Arguments["symbol"]);
        Assert.Equal("Long",call.Arguments["side"]);
        Assert.Equal(.01m,call.Arguments["quantity"]);
        Assert.Equal("WPE-DIRECT-0002",call.Arguments["clientOrderId"]);
        Assert.Equal(false,call.Arguments["reduceOnly"]);
    }

    [Fact]
    public void BinanceLocalMcpProviderContainsNoBinanceRestProtocolImplementation()
    {
        var source=File.ReadAllText(Path.Combine(
            ProjectRoot(),
            "Services",
            "Exchange",
            "Mcp",
            "BinanceLocalMcpProvider.cs"));

        Assert.DoesNotContain("/fapi/",source,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HMACSHA256",source,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-MBX-APIKEY",source,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpRequestMessage",source,StringComparison.Ordinal);
        Assert.DoesNotContain("Binance.Net.Clients",source,StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentSidecarLocatorPrefersExecutableBeforeDllFallback()
    {
        var source=File.ReadAllText(Path.Combine(
            ProjectRoot(),"Services","Exchange","Mcp","ExchangeMcpBridgeClient.cs"));
        var executable=source.IndexOf("WPE.ExchangeMcp.exe",StringComparison.Ordinal);
        var library=source.IndexOf("WPE.ExchangeMcp.dll",StringComparison.Ordinal);
        Assert.True(executable>=0);
        Assert.True(library>executable);
        Assert.Contains("if(File.Exists(executable))return executable",source,StringComparison.Ordinal);
    }

    [Fact]
    public void MainProductDoesNotReferenceMcpSdkPackage()
    {
        var project=File.ReadAllText(Path.Combine(ProjectRoot(),"币安量化机器人.csproj"));

        Assert.DoesNotContain(
            @"<PackageReference Include=""ModelContextProtocol""",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            @"<Compile Remove=""WPE.ExchangeMcp\**\*.cs"" />",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            @"<Compile Remove=""WPE.BinanceMcp\**\*.cs"" />",
            project,
            StringComparison.Ordinal);
    }

    private static OkxOfficialMcpProvider Provider(FakeMcpClient client)=>new(
        new ExchangeConnectionProfile
        {
            ProviderId="okx-mcp",
            DisplayName="OKX Official MCP Demo",
            Endpoint="https://www.okx.com",
            IsTestnet=true,
            ExecutionEnabled=!client.ReadOnly
        },
        client);

    private static ExchangeMcpToolResult Success(object data)=>
        new(
            false,
            string.Empty,
            JsonSerializer.SerializeToElement(new
            {
                tool="fake",
                ok=true,
                data=new
                {
                    endpoint="fake",
                    requestTime=DateTime.UtcNow.ToString("O"),
                    data
                },
                timestamp=DateTime.UtcNow.ToString("O")
            }));

    private static ExchangeMcpToolResult LocalSuccess(object value)=>
        new(false,JsonSerializer.Serialize(value),null);

    private static BinanceLocalMcpProvider BinanceLocalProvider(
        FakeMcpClient client,
        bool executionEnabled)=>new(
            new ExchangeConnectionProfile
            {
                Id="binance-mcp-test",
                ProviderId="binance-mcp-local",
                DisplayName="Binance Futures Testnet MCP",
                Endpoint="https://testnet.binancefuture.com",
                IsTestnet=true,
                ExecutionEnabled=executionEnabled,
                UpstreamConnectionId="binance-testnet-default"
            },
            client);

    private static string ProjectRoot([CallerFilePath] string sourceFile="")=>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!,".."));

    private sealed record FakeCall(
        string Tool,
        IReadOnlyDictionary<string,object?> Arguments);

    private sealed class FakeMcpClient : IExchangeMcpToolClient
    {
        private readonly Func<string,IReadOnlyDictionary<string,object?>,ExchangeMcpToolResult> _handler;

        public FakeMcpClient(
            bool readOnly,
            Func<string,IReadOnlyDictionary<string,object?>,ExchangeMcpToolResult> handler)
        {
            ReadOnly=readOnly;
            _handler=handler;
        }

        public string ProviderId=>"fake-okx-mcp";
        public bool ReadOnly { get; }
        public List<FakeCall> Calls { get; }=[];

        public Task<IReadOnlyList<ExchangeMcpToolDescriptor>> ListToolsAsync(CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<ExchangeMcpToolDescriptor>>([]);

        public Task<ExchangeMcpToolResult> CallToolAsync(
            string tool,
            IReadOnlyDictionary<string,object?> arguments,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(new(tool,new Dictionary<string,object?>(arguments)));
            return Task.FromResult(_handler(tool,arguments));
        }

        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}

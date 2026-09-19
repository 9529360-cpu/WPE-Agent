using System.Net;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class OkxBybitCertificationContractTests
{
    public static TheoryData<ProviderContract> Providers => new()
    {
        new ProviderContract(
            "okx", "https://www.okx.com", typeof(OkxExchangeProvider),
            ["apiKey", "secret", "passphrase"]),
        new ProviderContract(
            "bybit", "https://api-testnet.bybit.com", typeof(BybitExchangeProvider),
            ["apiKey", "secret"])
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TestnetContract_FreezesEnvironmentCredentialsAndCapabilities(ProviderContract contract)
    {
        var descriptor = Assert.Single(
            new ExchangeProviderCatalog().Installed,
            provider => provider.Id == contract.ProviderId);

        Assert.True(descriptor.SupportsTestnet);
        Assert.False(descriptor.SupportsMainnet);
        Assert.Equal(contract.CredentialKeys, descriptor.CredentialFields.Select(field => field.Key));
        Assert.All(
            new[] { "account", "positions", "place-order", "cancel-order", "query-order", "protection-orders" },
            capability => Assert.Contains(capability, descriptor.Capabilities));

        await using var provider = Create(contract, contract.OfficialTestnetEndpoint);
        var environment = Assert.IsAssignableFrom<IProviderEnvironmentGuard>(provider)
            .ValidateEnvironment(requireTestnet: true);

        Assert.True(environment.CanRead, environment.Failure);
        Assert.True(environment.CanTrade, environment.Failure);
        Assert.True(environment.TestnetAvailable, environment.Failure);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EnvironmentGuard_RejectsMainnetAndUntrustedOriginsBeforeNetwork(ProviderContract contract)
    {
        await using var mainnet = Create(contract, contract.OfficialTestnetEndpoint, isTestnet: false);
        await using var untrusted = Create(contract, "https://example.invalid");

        foreach (var provider in new[] { mainnet, untrusted })
        {
            var result = Assert.IsAssignableFrom<IProviderEnvironmentGuard>(provider)
                .ValidateEnvironment(requireTestnet: true);
            Assert.False(result.CanRead);
            Assert.False(result.CanTrade);
            Assert.False(result.TestnetAvailable);
            Assert.False(string.IsNullOrWhiteSpace(result.Failure));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task InstrumentOrderQueryAndReconcileSurface_IsOfflineConformanceOnly(ProviderContract contract)
    {
        await using var provider = Create(contract, contract.OfficialTestnetEndpoint);

        Assert.IsAssignableFrom<IProviderMarketCatalog>(provider);
        Assert.IsAssignableFrom<IBrokerProvider>(provider);
        Assert.NotNull(contract.ImplementationType.GetMethod(nameof(IBrokerProvider.PlaceMarketAsync)));
        Assert.NotNull(contract.ImplementationType.GetMethod(nameof(IBrokerProvider.PlaceLimitAsync)));
        Assert.NotNull(contract.ImplementationType.GetMethod(nameof(IBrokerProvider.FindOrderAsync)));
        Assert.NotNull(contract.ImplementationType.GetMethod(nameof(IBrokerProvider.CancelOrderAsync)));

        // Neither adapter currently exposes bounded recent-order history. Certification must remain unsupported.
        Assert.False(provider is IRecentOrderProvider);
        Assert.Contains(typeof(IDurableReviewExecutionReconciler), typeof(ReliableOrderExecutor).GetInterfaces());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void CertificationState_RemainsUnknownWithoutExternalSandboxEvidence(ProviderContract contract)
    {
        Assert.Equal(CertificationState.Unknown, contract.PermissionCertification);
        Assert.Equal(CertificationState.Unknown, contract.OrderLifecycleCertification);
        Assert.Equal(CertificationState.Unsupported, contract.RecentOrderHistoryCertification);
        Assert.False(contract.RealSandboxCertified);
    }

    [Theory]
    [InlineData("live", "NEW")]
    [InlineData("partially_filled", "PARTIALLY_FILLED")]
    [InlineData("filled", "FILLED")]
    [InlineData("canceled", "CANCELED")]
    [InlineData("mmp_canceled", "CANCELED")]
    [InlineData("order_failed", "REJECTED")]
    [InlineData("mystery", "UNKNOWN")]
    [InlineData("", "UNKNOWN")]
    public void Okx_OrderTerminalAndUnknownStatesFailClosed(string providerState, string expected)
    {
        Assert.Equal(expected, OkxExchangeProvider.Status(providerState));
    }

    [Theory]
    [InlineData("Created", "NEW")]
    [InlineData("New", "NEW")]
    [InlineData("Untriggered", "NEW")]
    [InlineData("PartiallyFilled", "PARTIALLY_FILLED")]
    [InlineData("Filled", "FILLED")]
    [InlineData("Cancelled", "CANCELED")]
    [InlineData("Deactivated", "CANCELED")]
    [InlineData("Rejected", "REJECTED")]
    [InlineData("Mystery", "UNKNOWN")]
    [InlineData("", "UNKNOWN")]
    public void Bybit_OrderTerminalAndUnknownStatesFailClosed(string providerState, string expected)
    {
        Assert.Equal(expected, BybitExchangeProvider.Status(providerState));
    }

    [Fact]
    public void Okx_InvalidServerTimeFailsAndInvalidOrderTimeIsStale()
    {
        Assert.Throws<InvalidOperationException>(() => OkxExchangeProvider.ParseServerTime("invalid"));
        Assert.Equal(DateTime.UnixEpoch, OkxExchangeProvider.OrderTimestamp("invalid"));
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).UtcDateTime,
            OkxExchangeProvider.ParseServerTime("1700000000000"));
    }

    [Fact]
    public void Bybit_InvalidServerTimeFailsAndInvalidOrderTimeIsStale()
    {
        Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.ParseServerTime("invalid"));
        Assert.Equal(DateTime.UnixEpoch, BybitExchangeProvider.OrderTimestamp("invalid"));
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).UtcDateTime,
            BybitExchangeProvider.ParseServerTime("1700000000000"));
    }

    [Theory]
    [InlineData("trade", true, false)]
    [InlineData("read_only,trade", true, false)]
    [InlineData("read_only", false, false)]
    [InlineData("notrade", false, false)]
    [InlineData("trade,withdraw", true, true)]
    [InlineData("", false, false)]
    public void Okx_PermissionTokensRequireExplicitTradeAndExposeWithdrawal(
        string permissions, bool canTrade, bool canWithdraw)
    {
        using var json = JsonDocument.Parse($$"""{"perm":"{{permissions}}","uid":"okx-test"}""");

        var result = OkxExchangeProvider.ParsePermissionSnapshot(json.RootElement);

        Assert.Equal(canTrade, result.CanTrade);
        Assert.Equal(canWithdraw, result.CanWithdraw);
        if (!canTrade) Assert.Contains(result.Warnings, warning => warning.Contains("not positively verified", StringComparison.OrdinalIgnoreCase));
        if (canWithdraw) Assert.Contains(result.Warnings, warning => warning.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("false", true)]
    [InlineData("False", true)]
    [InlineData("1", false)]
    [InlineData("true", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void Bybit_PermissionRequiresExplicitWritableValue(string readOnly, bool canTrade)
    {
        using var json = JsonDocument.Parse($$"""{"readOnly":"{{readOnly}}","userID":"bybit-test"}""");

        var result = BybitExchangeProvider.ParsePermissionSnapshot(json.RootElement);

        Assert.Equal(canTrade, result.CanTrade);
        Assert.False(result.CanWithdraw);
        if (!canTrade) Assert.Contains(result.Warnings, warning => warning.Contains("not positively verified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Okx_ItemLevelCancelFailureIsNotTreatedAsSuccess()
    {
        using var success = JsonDocument.Parse("""{"code":"0","data":[{"sCode":"0","sMsg":""}]}""");
        using var failed = JsonDocument.Parse("""{"code":"0","data":[{"sCode":"51400","sMsg":"Order cancellation failed"}]}""");
        using var malformed = JsonDocument.Parse("""{"data":[]}""");

        Assert.Null(OkxExchangeProvider.Error(success.RootElement));
        Assert.Contains("51400", OkxExchangeProvider.Error(failed.RootElement));
        Assert.NotNull(OkxExchangeProvider.Error(malformed.RootElement));
    }

    [Fact]
    public void Bybit_CancelAndMalformedResponseErrorsFailClosed()
    {
        using var success = JsonDocument.Parse("""{"retCode":0,"retMsg":"OK"}""");
        using var failed = JsonDocument.Parse("""{"retCode":110001,"retMsg":"Order does not exist"}""");
        using var malformed = JsonDocument.Parse("""{}""");

        Assert.Null(BybitExchangeProvider.Error(success.RootElement));
        Assert.Contains("110001", BybitExchangeProvider.Error(failed.RootElement));
        Assert.NotNull(BybitExchangeProvider.Error(malformed.RootElement));
    }

    [Fact]
    public async Task Bybit_PositionProtectionCancelDoesNotSilentlySucceedWithoutMutation()
    {
        var contract = Contract("bybit");
        await using var provider = Assert.IsType<BybitExchangeProvider>(Create(contract, contract.OfficialTestnetEndpoint));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.CancelOrderAsync("BTCUSDT", "position-tpsl:offline", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.CancelOrderAsync("BTCUSDT", "position-protection:BTCUSDT:long:position_tpsl", CancellationToken.None));
    }

    [Fact]
    public void Bybit_PositionLevelProtectionIsProjectedAsReadOnlyEvidence()
    {
        using var json=JsonDocument.Parse("""[{"symbol":"BTCUSDT","side":"Buy","positionIdx":"1","size":"1","stopLoss":"49000","takeProfit":"52000"},{"symbol":"ETHUSDT","side":"Sell","positionIdx":"2","size":"2","stopLoss":"3100","takeProfit":"0"},{"symbol":"SOLUSDT","side":"","positionIdx":"0","size":"3","stopLoss":"100","takeProfit":"0"},{"symbol":"XRPUSDT","side":"Buy","positionIdx":"1","size":"0","stopLoss":"1","takeProfit":"2"}]""");var observed=new DateTime(2026,7,27,9,0,0,DateTimeKind.Utc);
        var rows=BybitExchangeProvider.ParsePositionProtectionOrders(json.RootElement,observed,value=>value);
        var btc=Assert.Single(rows,x=>x.Symbol=="BTCUSDT");Assert.Equal("POSITION_TPSL",btc.Type);Assert.Equal(PositionSide.Long,btc.PositionSide);Assert.True(btc.IsProtection);Assert.Equal(ProtectionCoverageKind.PositionWide,btc.ProtectionCoverage);Assert.Equal(0m,btc.ProtectionQuantity);Assert.Equal(observed,btc.UpdatedAt);
        var eth=Assert.Single(rows,x=>x.Symbol=="ETHUSDT");Assert.Equal("STOP_POSITION",eth.Type);Assert.Equal(PositionSide.Short,eth.PositionSide);Assert.Equal(ProtectionCoverageKind.PositionWide,eth.ProtectionCoverage);
        Assert.Null(Assert.Single(rows,x=>x.Symbol=="SOLUSDT").PositionSide);Assert.DoesNotContain(rows,x=>x.Symbol=="XRPUSDT");
    }

    [Fact]
    public async Task Okx_AlgoProtectionProjectsFixedQuantityCoverage()
    {
        var profile=new ExchangeConnectionProfile{ProviderId="okx",IsTestnet=true,ExecutionEnabled=false,Endpoint="https://www.okx.com"};
        await using var provider=new OkxExchangeProvider(profile,"key","secret","passphrase");
        var contractValues=Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<string,decimal>>(
            typeof(OkxExchangeProvider).GetField("_contractValues",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(provider));
        contractValues["BTC-USDT-SWAP"]=0.01m;
        using var json=JsonDocument.Parse("""{"algoId":"42","algoClOrdId":"wpe-protection","instId":"BTC-USDT-SWAP","posSide":"long","ordType":"oco","state":"live","sz":"25","accFillSz":"0","avgPx":"0","cTime":"1700000000000"}""");
        var method=typeof(OkxExchangeProvider).GetMethod("MapOrder",BindingFlags.NonPublic|BindingFlags.Instance)!;

        var order=await Assert.IsType<Task<ExchangeOrder>>(method.Invoke(provider,[json.RootElement,true,CancellationToken.None]));

        Assert.True(order.IsProtection);
        Assert.Equal(ProtectionCoverageKind.FixedQuantity,order.ProtectionCoverage);
        Assert.Equal(0.25m,order.ProtectionQuantity);
    }

    [Fact]
    public void Bybit_MalformedPositionProtectionPayloadFailsClosed()
    {
        using var json=JsonDocument.Parse("""{"list":{}}""");Assert.Throws<InvalidOperationException>(()=>BybitExchangeProvider.ParsePositionProtectionOrders(json.RootElement.GetProperty("list"),DateTime.UtcNow,value=>value));
    }

    [Theory]
    [InlineData("okx", "http://www.okx.com")]
    [InlineData("okx", "https://www.okx.com:444")]
    [InlineData("okx", "https://www.okx.com/api/v5")]
    [InlineData("bybit", "http://api-testnet.bybit.com")]
    [InlineData("bybit", "https://api-testnet.bybit.com:444")]
    [InlineData("bybit", "https://api-testnet.bybit.com/v5")]
    public async Task SandboxEndpointRejectsSchemePortAndPathVariants(string providerId, string endpoint)
    {
        var contract = Contract(providerId);
        await using var provider = Create(contract, endpoint);

        var result = Assert.IsAssignableFrom<IProviderEnvironmentGuard>(provider).ValidateEnvironment(true);

        Assert.False(result.CanRead);
        Assert.False(result.CanTrade);
        Assert.False(result.TestnetAvailable);
    }

    [Theory]
    [InlineData("okx", InjectedFault.TooManyRequests, "UNAVAILABLE", 3)]
    [InlineData("okx", InjectedFault.Timeout, "UNAVAILABLE", 3)]
    [InlineData("okx", InjectedFault.MalformedJson, "UNKNOWN", 1)]
    [InlineData("okx", InjectedFault.PartialPayload, "UNKNOWN", 1)]
    [InlineData("bybit", InjectedFault.TooManyRequests, "UNAVAILABLE", 3)]
    [InlineData("bybit", InjectedFault.Timeout, "UNAVAILABLE", 3)]
    [InlineData("bybit", InjectedFault.MalformedJson, "UNKNOWN", 1)]
    [InlineData("bybit", InjectedFault.PartialPayload, "UNKNOWN", 1)]
    public async Task ProviderFaultInjectionFailsClosedWithoutNetwork(
        string providerId, InjectedFault fault, string expectedState, int expectedCalls)
    {
        var contract = Contract(providerId);
        await using var provider = Create(contract, contract.OfficialTestnetEndpoint);
        var handler = new OfflineFaultHandler(providerId, fault);
        InjectHttp(provider, handler, contract.OfficialTestnetEndpoint);

        var result = await Assert.IsAssignableFrom<IProviderMarketCatalog>(provider)
            .DiscoverMarketCatalogAsync(CancellationToken.None);

        Assert.Equal(ProviderCatalogState.Error, result.State);
        Assert.StartsWith(expectedState + ":", result.Failure, StringComparison.Ordinal);
        Assert.Equal(expectedCalls, handler.CallCount);
        Assert.DoesNotContain(OfflineFaultHandler.Secret, result.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("okx")]
    [InlineData("bybit")]
    public async Task ProviderErrorMessageIsRedactedBeforeCatalogDiagnostic(string providerId)
    {
        var contract = Contract(providerId);
        await using var provider = Create(contract, contract.OfficialTestnetEndpoint);
        var handler = new OfflineFaultHandler(providerId, InjectedFault.SecretApiError);
        InjectHttp(provider, handler, contract.OfficialTestnetEndpoint);

        var result = await Assert.IsAssignableFrom<IProviderMarketCatalog>(provider)
            .DiscoverMarketCatalogAsync(CancellationToken.None);

        Assert.Equal(ProviderCatalogState.Error, result.State);
        Assert.StartsWith("UNKNOWN:", result.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain(OfflineFaultHandler.Secret, result.Failure, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Okx_MixedItemLevelSuccessFailsTheWholeResponse()
    {
        using var json = JsonDocument.Parse("""{"code":"0","data":[{"sCode":"0"},{"sCode":"51000","sMsg":"second item failed"}]}""");

        Assert.Contains("51000", OkxExchangeProvider.Error(json.RootElement));
    }

    [Fact]
    public void Bybit_MixedItemLevelSuccessFailsTheWholeResponse()
    {
        using var json = JsonDocument.Parse("""{"retCode":0,"result":{"list":[{"code":"0"},{"code":"110001","msg":"second item failed"}]}}""");

        Assert.Contains("110001", BybitExchangeProvider.Error(json.RootElement));
    }

    [Fact]
    public void Okx_DuplicateAndOutOfOrderEventsCannotReopenTerminalOrder()
    {
        var normalized = OkxExchangeProvider.NormalizeOrderEvents(OutOfOrderEvents("okx"));

        Assert.Equal("FILLED", Assert.Single(normalized).Status);
    }

    [Fact]
    public void Bybit_DuplicateAndOutOfOrderEventsCannotReopenTerminalOrder()
    {
        var normalized = BybitExchangeProvider.NormalizeOrderEvents(OutOfOrderEvents("bybit"));

        Assert.Equal("FILLED", Assert.Single(normalized).Status);
    }

    [Fact]
    public void Okx_PublicSigningVectorFreezesCanonicalMethodTargetBodyHashAndUtcTimestamp()
    {
        var secret = Encoding.UTF8.GetBytes("test-secret");
        var target = OkxExchangeProvider.CanonicalTarget("GET", "/api/v5/account/config",
        [
            new("symbol", "BTC USDT"),
            new("instType", "SWAP")
        ]);
        var body = OkxExchangeProvider.CanonicalBody(new Dictionary<string, object?>
        {
            ["sz"] = "0.25", ["instId"] = "BTC-USDT-SWAP", ["px"] = "1234.50"
        });

        Assert.Equal("/api/v5/account/config?instType=SWAP&symbol=BTC%20USDT", target);
        Assert.Equal("{\"instId\":\"BTC-USDT-SWAP\",\"px\":\"1234.50\",\"sz\":\"0.25\"}", body);
        Assert.Equal("d6e364c6221efcc4c82a93601bfa9dc07101053d90df8fbd79fbd70daaea2468", OkxExchangeProvider.Sha256Hex(body));
        Assert.Equal("2026-07-19T15:30:00.000Z", OkxExchangeProvider.FormatTimestamp(new DateTimeOffset(2026, 7, 20, 0, 30, 0, TimeSpan.FromHours(9))));
        Assert.Equal("lWrfHGBeGs6r85WSUF27vHYZsGr7CKryWUYLN7U4YR0=", OkxExchangeProvider.ComputeSignature(secret, "2026-07-19T15:00:00.000Z", "GET", "/api/v5/account/config", ""));
    }

    [Fact]
    public void Bybit_PublicSigningVectorFreezesCanonicalTargetBodyHashAndUnixTimestamp()
    {
        var secret = Encoding.UTF8.GetBytes("test-secret");
        var target = BybitExchangeProvider.CanonicalTarget("GET", "/v5/order/realtime",
        [
            new("symbol", "BTCUSDT"),
            new("category", "linear")
        ]);
        var body = BybitExchangeProvider.CanonicalBody(new Dictionary<string, object?>
        {
            ["symbol"] = "BTCUSDT", ["qty"] = "1.25"
        });

        Assert.Equal("/v5/order/realtime?category=linear&symbol=BTCUSDT", target);
        Assert.Equal("{\"qty\":\"1.25\",\"symbol\":\"BTCUSDT\"}", body);
        Assert.Equal("e889ecfc6020b0420242161aa12b2ddd4cf3919c7ea4f59d4b235237985e3579", BybitExchangeProvider.Sha256Hex(body));
        Assert.Equal("1752939000000", BybitExchangeProvider.FormatTimestamp(new DateTimeOffset(2025, 7, 20, 0, 30, 0, TimeSpan.FromHours(9))));
        Assert.Equal("9a7c8cfd6ba1a7c498aa4dd5a7f9cfbba01fcb6eebae734ffe0d775870a1a3fb", BybitExchangeProvider.ComputeSignature(secret, "1700000000000", "test-key", 5000, "category=linear&symbol=BTCUSDT"));
    }

    [Fact]
    public void Okx_DuplicateParametersBodyKeysAndHeadersFailClosedWithoutCredentialEcho()
    {
        var duplicate = new[] { new KeyValuePair<string, string?>("symbol", "BTC"), new("SYMBOL", "ETH") };
        var body = new[] { new KeyValuePair<string, object?>("px", "1"), new("PX", "2") };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.okx.com/");
        OkxExchangeProvider.AddHeader(request, "OK-ACCESS-KEY", "public-test-key");

        Assert.Throws<InvalidOperationException>(() => OkxExchangeProvider.CanonicalQuery(duplicate));
        Assert.Throws<InvalidOperationException>(() => OkxExchangeProvider.CanonicalBody(body));
        var error = Assert.Throws<InvalidOperationException>(() => OkxExchangeProvider.AddHeader(request, "OK-ACCESS-KEY", "secret-second-value"));
        Assert.DoesNotContain("secret-second-value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bybit_DuplicateParametersBodyKeysAndHeadersFailClosedWithoutCredentialEcho()
    {
        var duplicate = new[] { new KeyValuePair<string, string?>("symbol", "BTC"), new("SYMBOL", "ETH") };
        var body = new[] { new KeyValuePair<string, object?>("qty", "1"), new("QTY", "2") };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api-testnet.bybit.com/");
        BybitExchangeProvider.AddHeader(request, "X-BAPI-API-KEY", "public-test-key");

        Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.CanonicalQuery(duplicate));
        Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.CanonicalBody(body));
        var error = Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.AddHeader(request, "X-BAPI-API-KEY", "secret-second-value"));
        Assert.DoesNotContain("secret-second-value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Okx_NonCanonicalSigningInputsFailClosed()
    {
        var secret = Encoding.UTF8.GetBytes("test-secret");

        Assert.Throws<InvalidOperationException>(() => OkxExchangeProvider.CanonicalTarget("get", "/api/v5/time", null));
        Assert.Throws<InvalidOperationException>(() => OkxExchangeProvider.CanonicalTarget("GET", "https://www.okx.com/api/v5/time", null));
        Assert.Throws<InvalidOperationException>(() => OkxExchangeProvider.CanonicalTarget("GET", "/api/v5/time?x=1", null));
        Assert.Throws<InvalidOperationException>(() => OkxExchangeProvider.ComputeSignature(secret, "2026-07-19T15:00:00+09:00", "GET", "/api/v5/time", ""));
        var error = Assert.Throws<InvalidOperationException>(() => OkxExchangeProvider.ComputeSignature([], "2026-07-19T15:00:00.000Z", "GET", "/api/v5/time", ""));
        Assert.DoesNotContain("test-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bybit_NonCanonicalSigningInputsFailClosed()
    {
        var secret = Encoding.UTF8.GetBytes("test-secret");

        Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.CanonicalTarget("DELETE", "/v5/order/realtime", null));
        Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.CanonicalTarget("GET", "https://api-testnet.bybit.com/v5/order/realtime", null));
        Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.CanonicalTarget("GET", "/v5/order/realtime?x=1", null));
        Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.ComputeSignature(secret, "not-a-timestamp", "test-key", 5000, ""));
        Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.ComputeSignature(secret, "1700000000000", "test-key", 999, ""));
        var error = Assert.Throws<InvalidOperationException>(() => BybitExchangeProvider.ComputeSignature([], "1700000000000", "secret-key", 5000, ""));
        Assert.DoesNotContain("secret-key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Okx_CanonicalNumbersRemainInvariantUnderNonEnglishCulture()
    {
        using var culture = new CultureScope("fr-FR");
        var body = OkxExchangeProvider.CanonicalBody(new Dictionary<string, object?> { ["price"] = 1234.5m });

        Assert.Equal("{\"price\":1234.5}", body);
    }

    [Fact]
    public void Bybit_CanonicalNumbersRemainInvariantUnderNonEnglishCulture()
    {
        using var culture = new CultureScope("fr-FR");
        var body = BybitExchangeProvider.CanonicalBody(new Dictionary<string, object?> { ["price"] = 1234.5m });

        Assert.Equal("{\"price\":1234.5}", body);
    }

    [Fact]
    public async Task Okx_FakeHandlerCapturesCanonicalSignedReadRequestWithoutNetwork()
    {
        var contract = Contract("okx");
        await using var provider = Create(contract, contract.OfficialTestnetEndpoint);
        var handler = new SigningCaptureHandler("okx");
        InjectHttp(provider, handler, contract.OfficialTestnetEndpoint);

        Assert.Empty(await provider.Broker.GetOpenOrdersAsync("BTCUSDT", CancellationToken.None));
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Bybit_FakeHandlerCapturesCanonicalSignedReadRequestWithoutNetwork()
    {
        var contract = Contract("bybit");
        await using var provider = Create(contract, contract.OfficialTestnetEndpoint);
        var handler = new SigningCaptureHandler("bybit");
        InjectHttp(provider, handler, contract.OfficialTestnetEndpoint);

        Assert.Empty(await provider.Broker.GetOpenOrdersAsync("BTCUSDT", CancellationToken.None));
        Assert.Equal(2, handler.CallCount);
    }

    private static ProviderContract Contract(string providerId) => providerId switch
    {
        "okx" => new("okx", "https://www.okx.com", typeof(OkxExchangeProvider), ["apiKey", "secret", "passphrase"]),
        "bybit" => new("bybit", "https://api-testnet.bybit.com", typeof(BybitExchangeProvider), ["apiKey", "secret"]),
        _ => throw new ArgumentOutOfRangeException(nameof(providerId))
    };

    private static IReadOnlyList<ExchangeOrder> OutOfOrderEvents(string providerId)
    {
        var now = DateTime.UtcNow;
        return
        [
            new("BTCUSDT", providerId + "-order", "wpe-client", "FILLED", 1, 100, "LIMIT", PositionSide.Long, false, now),
            new("BTCUSDT", providerId + "-order", "wpe-client", "NEW", 0, 0, "LIMIT", PositionSide.Long, false, now.AddSeconds(2)),
            new("BTCUSDT", providerId + "-order", "wpe-client", "UNKNOWN", 0, 0, "LIMIT", PositionSide.Long, false, now.AddSeconds(1)),
            new("BTCUSDT", providerId + "-order", "wpe-client", "FILLED", 1, 100, "LIMIT", PositionSide.Long, false, now)
        ];
    }

    private static void InjectHttp(IExchangeProvider provider, HttpMessageHandler handler, string endpoint)
    {
        var field = typeof(RestExchangeProviderBase).GetField("Http", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(field);
        Assert.IsType<HttpClient>(field.GetValue(provider)).Dispose();
        field.SetValue(provider, new HttpClient(handler) { BaseAddress = new Uri(endpoint + "/"), Timeout = Timeout.InfiniteTimeSpan });
    }

    public enum InjectedFault { TooManyRequests, Timeout, MalformedJson, PartialPayload, SecretApiError }

    private sealed class OfflineFaultHandler(string providerId, InjectedFault fault) : HttpMessageHandler
    {
        public const string Secret = "sk-offline-provider-secret-123456";
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            Assert.NotNull(request.RequestUri);
            Assert.True(request.RequestUri.Host is "www.okx.com" or "api-testnet.bybit.com");
            return fault switch
            {
                InjectedFault.TooManyRequests => Task.FromResult(Response(HttpStatusCode.TooManyRequests,
                    $"Authorization: Bearer {Secret} signature={Secret}")),
                InjectedFault.Timeout => Task.FromException<HttpResponseMessage>(
                    new TaskCanceledException($"timeout Authorization: Bearer {Secret}")),
                InjectedFault.MalformedJson => Task.FromResult(Response(HttpStatusCode.OK, "{")),
                InjectedFault.PartialPayload => Task.FromResult(Response(HttpStatusCode.OK,
                    providerId == "okx" ? "{\"code\":\"0\",\"data\":[]}" : "{\"retCode\":0,\"result\":{\"list\":[]}}")),
                InjectedFault.SecretApiError => Task.FromResult(Response(HttpStatusCode.OK,
                    providerId == "okx"
                        ? $$"""{"code":"51000","msg":"Authorization: Bearer {{Secret}} signature={{Secret}}"}"""
                        : $$"""{"retCode":110001,"retMsg":"Authorization: Bearer {{Secret}} signature={{Secret}}"}""")),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SigningCaptureHandler(string providerId) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            var headers = request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            if (providerId == "okx")
            {
                Assert.StartsWith("/api/v5/trade/", request.RequestUri!.PathAndQuery, StringComparison.Ordinal);
                Assert.Single(headers["OK-ACCESS-KEY"]);
                Assert.Single(headers["OK-ACCESS-SIGN"]);
                Assert.EndsWith("Z", Assert.Single(headers["OK-ACCESS-TIMESTAMP"]), StringComparison.Ordinal);
                Assert.Single(headers["OK-ACCESS-PASSPHRASE"]);
                Assert.Equal("1", Assert.Single(headers["x-simulated-trading"]));
                return Task.FromResult(JsonResponse("{\"code\":\"0\",\"data\":[]}"));
            }

            Assert.Contains(request.RequestUri!.PathAndQuery,
            new string[]
            {
                "/v5/order/realtime?category=linear&settleCoin=USDT&symbol=BTCUSDT",
                "/v5/position/list?category=linear&settleCoin=USDT&symbol=BTCUSDT"
            });
            Assert.Single(headers["X-BAPI-API-KEY"]);
            Assert.Single(headers["X-BAPI-SIGN"]);
            Assert.True(long.TryParse(Assert.Single(headers["X-BAPI-TIMESTAMP"]), NumberStyles.None, CultureInfo.InvariantCulture, out _));
            Assert.Equal("5000", Assert.Single(headers["X-BAPI-RECV-WINDOW"]));
            return Task.FromResult(JsonResponse("{\"retCode\":0,\"result\":{\"list\":[]}}"));
        }

        private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        public void Dispose() { CultureInfo.CurrentCulture = _culture; CultureInfo.CurrentUICulture = _uiCulture; }
    }

    private static IExchangeProvider Create(
        ProviderContract contract,
        string endpoint,
        bool isTestnet = true)
    {
        var credentials = contract.CredentialKeys.ToDictionary(
            key => key,
            key => $"offline-{key}",
            StringComparer.OrdinalIgnoreCase);
        return new ExchangeProviderCatalog().Create(
            new ExchangeConnectionProfile
            {
                ProviderId = contract.ProviderId,
                IsTestnet = isTestnet,
                ExecutionEnabled = true,
                Endpoint = endpoint
            },
            credentials);
    }

    public enum CertificationState { Unknown, Unsupported }

    public sealed record ProviderContract(
        string ProviderId,
        string OfficialTestnetEndpoint,
        Type ImplementationType,
        IReadOnlyList<string> CredentialKeys,
        CertificationState PermissionCertification = CertificationState.Unknown,
        CertificationState OrderLifecycleCertification = CertificationState.Unknown,
        CertificationState RecentOrderHistoryCertification = CertificationState.Unsupported,
        bool RealSandboxCertified = false);
}

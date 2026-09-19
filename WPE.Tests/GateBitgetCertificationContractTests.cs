using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class GateBitgetCertificationContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-gate-bitget-cert-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<string> Providers => new() { "gate", "bitget" };

    [Theory]
    [MemberData(nameof(Providers))]
    public void PermissionInformationThatCannotProveTradeAccessRemainsReadOnly(string providerId)
    {
        var permission = providerId == "gate"
            ? GateExchangeProvider.ConservativePermissionSnapshot()
            : BitgetExchangeProvider.ConservativePermissionSnapshot();

        Assert.True(permission.CanRead);
        Assert.False(permission.CanTrade);
        Assert.False(permission.CanWithdraw);
        Assert.Contains(permission.Warnings, warning => warning.Contains("trading remains disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("gate", "https://api-testnet.gateapi.io", true)]
    [InlineData("gate", "https://api-testnet.gateapi.io.evil.example", false)]
    [InlineData("gate", "http://api-testnet.gateapi.io", false)]
    [InlineData("bitget", "https://api.bitget.com", true)]
    [InlineData("bitget", "https://api.bitget.com.evil.example", false)]
    [InlineData("bitget", "http://api.bitget.com", false)]
    public async Task OnlyOfficialSandboxOriginsPassTheEnvironmentGuard(string providerId, string endpoint, bool expected)
    {
        var profile = new ExchangeConnectionProfile { ProviderId = providerId, IsTestnet = true, ExecutionEnabled = true, Endpoint = endpoint };
        await using var provider = new ExchangeProviderCatalog().Create(profile, Credentials(providerId));

        var result = Assert.IsAssignableFrom<IProviderEnvironmentGuard>(provider).ValidateEnvironment(requireTestnet: true);

        Assert.Equal(expected, result.CanRead);
        Assert.Equal(expected, result.CanTrade);
        Assert.Equal(expected, result.TestnetAvailable);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void UnknownAndStaleCapabilitiesNeverAuthorizeExecution(string providerId)
    {
        var instrument = new Instrument("BTCUSDT", providerId == "gate" ? "BTC_USDT" : "BTCUSDT", providerId, providerId, MarketType.Perpetual);
        var stale = new ExchangeCapability(providerId, providerId, instrument.CanonicalSymbol, instrument.NativeSymbol,
            MarketType.Perpetual, CapabilityStatus.Available, true, true, true,
            DateTimeOffset.UtcNow - ProviderCapabilityPrecondition.MaximumAge - TimeSpan.FromSeconds(1));
        var gate = new ProviderCapabilityPrecondition();

        Assert.False(gate.Check(instrument, null, testnet: true).Allowed);
        Assert.False(gate.Check(instrument, stale, testnet: true).Allowed);
        Assert.False(gate.Check(instrument, stale with { Status = CapabilityStatus.Unsupported, CheckedAt = DateTimeOffset.UtcNow }, testnet: true).Allowed);
        Assert.False(gate.Check(instrument, stale with { Status = CapabilityStatus.Error, CheckedAt = DateTimeOffset.UtcNow }, testnet: true).Allowed);
    }

    [Theory]
    [InlineData("live", "NEW")]
    [InlineData("partial-fill", "PARTIALLY_FILLED")]
    [InlineData("full-fill", "FILLED")]
    [InlineData("cancelled", "CANCELED")]
    [InlineData("rejected", "REJECTED")]
    [InlineData("expired", "EXPIRED")]
    [InlineData("mystery", "UNKNOWN")]
    public void BitgetOrderStatesMapToTheReliableExecutorVocabulary(string providerState, string expected)
    {
        var method = typeof(BitgetExchangeProvider).GetMethod("Status", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [providerState]));
    }

    [Theory]
    [InlineData("unexpected", "", "UNKNOWN")]
    [InlineData("finished", "liquidated", "UNKNOWN")]
    [InlineData("finished", "position_closed", "UNKNOWN")]
    public void GateUnknownOrderAndFillTerminalsAlwaysNormalizeToUnknown(string providerState, string finishAs, string expected)
        => Assert.Equal(expected, GateExchangeProvider.NormalizeOrderStatus(providerState, finishAs, 1, 0));

    [Theory]
    [InlineData("open", "NEW")]
    [InlineData("cancelled", "CANCELED")]
    [InlineData("expired", "EXPIRED")]
    [InlineData("rejected", "REJECTED")]
    [InlineData("finished", "UNKNOWN")]
    [InlineData("mystery", "UNKNOWN")]
    public void GateProtectionTerminalsFailClosed(string providerState, string expected)
        => Assert.Equal(expected, GateExchangeProvider.NormalizeProtectionStatus(providerState));

    [Fact]
    public async Task GateProtectionSizeProjectsPositionWideOrFixedCoverage()
    {
        var profile=new ExchangeConnectionProfile{ProviderId="gate",IsTestnet=true,ExecutionEnabled=false,Endpoint="https://api-testnet.gateapi.io"};
        await using var provider=new GateExchangeProvider(profile,"key","secret");
        var multipliers=Assert.IsType<ConcurrentDictionary<string,decimal>>(
            typeof(GateExchangeProvider).GetField("_multipliers",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(provider));
        multipliers["BTC_USDT"]=0.001m;
        var method=typeof(GateExchangeProvider).GetMethod("MapProtection",BindingFlags.NonPublic|BindingFlags.Instance)!;

        using var fullJson=JsonDocument.Parse("""{"id":"p1","status":"open","order_type":"close-long-order","create_time":"1700000000","initial":{"contract":"BTC_USDT","size":"0","text":"full"},"trigger":{"rule":2}}""");
        var full=await Assert.IsType<Task<ExchangeOrder>>(method.Invoke(provider,[fullJson.RootElement,CancellationToken.None]));
        Assert.Equal(ProtectionCoverageKind.PositionWide,full.ProtectionCoverage);
        Assert.Equal(0m,full.ProtectionQuantity);

        using var fixedJson=JsonDocument.Parse("""{"id":"p2","status":"open","order_type":"close-long-order","create_time":"1700000000","initial":{"contract":"BTC_USDT","size":"3","text":"fixed"},"trigger":{"rule":2}}""");
        var fixedOrder=await Assert.IsType<Task<ExchangeOrder>>(method.Invoke(provider,[fixedJson.RootElement,CancellationToken.None]));
        Assert.Equal(ProtectionCoverageKind.FixedQuantity,fixedOrder.ProtectionCoverage);
        Assert.Equal(0.003m,fixedOrder.ProtectionQuantity);
    }

    [Fact]
    public void BitgetOnlyPositionLevelPlansClaimPositionWideCoverage()
    {
        Assert.Equal(ProtectionCoverageKind.PositionWide,BitgetExchangeProvider.ProtectionCoverage("pos_profit"));
        Assert.Equal(ProtectionCoverageKind.PositionWide,BitgetExchangeProvider.ProtectionCoverage("pos_loss"));
        Assert.Equal(ProtectionCoverageKind.Unknown,BitgetExchangeProvider.ProtectionCoverage("profit_plan"));
        Assert.Equal(ProtectionCoverageKind.Unknown,BitgetExchangeProvider.ProtectionCoverage("loss_plan"));
        Assert.Equal(ProtectionCoverageKind.Unknown,BitgetExchangeProvider.ProtectionCoverage(""));
    }

    [Theory]
    [InlineData("open", "", "NEW")]
    [InlineData("finished", "filled", "FILLED")]
    [InlineData("finished", "cancelled", "CANCELED")]
    [InlineData("finished", "rejected", "REJECTED")]
    [InlineData("finished", "mystery", "UNKNOWN")]
    [InlineData("unexpected", "", "UNKNOWN")]
    public async Task GateOrderStatesMapWithoutTreatingUnknownAsFilled(string providerState, string finishAs, string expected)
    {
        var profile = new ExchangeConnectionProfile { ProviderId = "gate", IsTestnet = true, ExecutionEnabled = false, Endpoint = "https://api-testnet.gateapi.io" };
        await using var provider = new GateExchangeProvider(profile, "key", "secret");
        var multipliers = Assert.IsType<ConcurrentDictionary<string, decimal>>(
            typeof(GateExchangeProvider).GetField("_multipliers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(provider));
        multipliers["BTC_USDT"] = 1m;
        using var json = JsonDocument.Parse($$"""{"contract":"BTC_USDT","id":"1","text":"t","status":"{{providerState}}","finish_as":"{{finishAs}}","size":"1","left":"1","price":"1","create_time":"1700000000"}""");
        var method = typeof(GateExchangeProvider).GetMethod("MapOrder", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var order = await Assert.IsType<Task<ExchangeOrder>>(method.Invoke(provider, [json.RootElement, false, CancellationToken.None]));

        Assert.Equal(expected, order.Status);
    }

    [Fact]
    public async Task UnknownReconcileResultDoesNotSubmitAReplacementOrder()
    {
        Directory.CreateDirectory(_directory);
        var exchange = new UnknownExchange();
        var store = new AgentSqliteStore(Path.Combine(_directory, "agent.db"));
        var intent = TestIntentFactory.Opening("gate-bitget-unknown");
        await store.SaveIntentAsync("cycle-cert", intent, "UNKNOWN", null, CancellationToken.None);
        var executor = new ReliableOrderExecutor(exchange, store);

        var result = await executor.RecoverPendingAsync(CancellationToken.None);

        Assert.False(result.SafeToIncreaseRisk);
        Assert.Equal(1, exchange.FindCalls);
        Assert.Equal(0, exchange.MutationCalls);
    }

    [Fact]
    public async Task ExplicitUnknownProviderStateDoesNotTriggerCancelProtectionOrReplacement()
    {
        Directory.CreateDirectory(_directory);
        var exchange = new UnknownExchange(returnUnknownOrder: true);
        var store = new AgentSqliteStore(Path.Combine(_directory, "explicit-unknown.db"));
        var intent = TestIntentFactory.Opening("gate-finished-unknown");
        await store.SaveIntentAsync("cycle-explicit-unknown", intent, "UNKNOWN", null, CancellationToken.None);
        var executor = new ReliableOrderExecutor(exchange, store);

        var result = await executor.RecoverPendingAsync(CancellationToken.None);

        Assert.False(result.SafeToIncreaseRisk);
        Assert.Equal(1, exchange.FindCalls);
        Assert.Equal(0, exchange.MutationCalls);
        Assert.Equal("UNKNOWN", await store.GetOrderIntentStatusAsync(intent.ClientOrderId, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void PaginatedOrderEventsAreDeduplicatedWithLatestStateWinning(string providerId)
    {
        var first = Order("order-1", "client-1", "NEW", DateTime.UtcNow.AddSeconds(-2));
        var duplicate = first with { Status = "UNKNOWN", UpdatedAt = first.UpdatedAt.AddSeconds(1) };
        var second = Order("order-2", "client-2", "CANCELED", DateTime.UtcNow);

        var merged = providerId == "gate"
            ? GateExchangeProvider.MergeOrderPages([[first], [duplicate, second]])
            : BitgetExchangeProvider.MergeOrderPages([[first], [duplicate, second]]);

        Assert.Equal(2, merged.Count);
        Assert.Equal("UNKNOWN", Assert.Single(merged, x => x.OrderId == "order-1").Status);
        Assert.Equal("CANCELED", Assert.Single(merged, x => x.OrderId == "order-2").Status);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void AnonymousPaginatedEventsAreNotIncorrectlyCollapsed(string providerId)
    {
        var first = Order("", "", "UNKNOWN", DateTime.UtcNow.AddSeconds(-1));
        var second = first with { UpdatedAt = DateTime.UtcNow };

        var merged = providerId == "gate"
            ? GateExchangeProvider.MergeOrderPages([[first], [second]])
            : BitgetExchangeProvider.MergeOrderPages([[first], [second]]);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, x => Assert.Equal("UNKNOWN", x.Status));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void ServerTimeOutsideReceiveWindowFailsClosed(string providerId)
    {
        var now = DateTime.UtcNow;
        var accepted = providerId == "gate"
            ? GateExchangeProvider.IsServerTimeWithinReceiveWindow(now.AddSeconds(-5), now)
            : BitgetExchangeProvider.IsServerTimeWithinReceiveWindow(now.AddSeconds(-5), now);
        var stale = providerId == "gate"
            ? GateExchangeProvider.IsServerTimeWithinReceiveWindow(now.AddSeconds(-6), now)
            : BitgetExchangeProvider.IsServerTimeWithinReceiveWindow(now.AddSeconds(-6), now);
        var future = providerId == "gate"
            ? GateExchangeProvider.IsServerTimeWithinReceiveWindow(now.AddSeconds(6), now)
            : BitgetExchangeProvider.IsServerTimeWithinReceiveWindow(now.AddSeconds(6), now);

        Assert.True(accepted);
        Assert.False(stale);
        Assert.False(future);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void MissingCredentialsFailBeforeProviderCreation(string providerId)
    {
        var profile = new ExchangeConnectionProfile { ProviderId = providerId, IsTestnet = true, Endpoint = providerId == "gate" ? "https://api-testnet.gateapi.io" : "https://api.bitget.com" };

        var error = Assert.Throws<InvalidOperationException>(() => new ExchangeProviderCatalog().Create(profile, new Dictionary<string, string>()));

        Assert.Contains("credential", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void FutureOrReadOnlyCapabilityCannotAuthorizeExecution(string providerId)
    {
        var instrument = new Instrument("BTCUSDT", providerId == "gate" ? "BTC_USDT" : "BTCUSDT", providerId, providerId, MarketType.Perpetual);
        var capability = new ExchangeCapability(providerId, providerId, instrument.CanonicalSymbol, instrument.NativeSymbol,
            MarketType.Perpetual, CapabilityStatus.Available, true, true, true,
            DateTimeOffset.UtcNow + ProviderCapabilityPrecondition.MaximumAge + TimeSpan.FromSeconds(1));
        var gate = new ProviderCapabilityPrecondition();

        Assert.False(gate.Check(instrument, capability, testnet: true).Allowed);
        Assert.False(gate.Check(instrument, capability with { CheckedAt = DateTimeOffset.UtcNow, CanTrade = false }, testnet: true).Allowed);
    }

    [Theory]
    [InlineData("gate", "rate-limit")]
    [InlineData("gate", "timeout")]
    [InlineData("gate", "malformed")]
    [InlineData("gate", "partial")]
    [InlineData("bitget", "rate-limit")]
    [InlineData("bitget", "timeout")]
    [InlineData("bitget", "malformed")]
    [InlineData("bitget", "partial")]
    public async Task TransportAndPayloadFaultsRemainUnavailableWithoutMutation(string providerId, string fault)
    {
        await using var provider = CreateProvider(providerId);
        var handler = new FaultHandler((_, request, _) => FaultResponse(providerId, fault, request));
        InjectHttp(provider, handler);

        var health = await provider.HealthCheckAsync(CancellationToken.None);

        Assert.False(health.Healthy);
        Assert.NotEmpty(health.Message);
        Assert.NotEmpty(handler.Methods);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CredentialFailureDiagnosticsAreRedactedAndDoNotMutate(string providerId)
    {
        const string secret = "sk-gate-bitget-secret-123456";
        const string signature = "signature-secret-value-123456";
        const string passphrase = "passphrase-secret-value-123456";
        await using var provider = CreateProvider(providerId);
        var handler = new FaultHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.Unauthorized,
            JsonSerializer.Serialize(new { error = "Authorization: Bearer " + secret, apiKey = secret, signature, passphrase }))));
        InjectHttp(provider, handler);

        var health = await provider.HealthCheckAsync(CancellationToken.None);

        Assert.False(health.Healthy);
        Assert.DoesNotContain(secret, health.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, health.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(passphrase, health.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", health.Message, StringComparison.Ordinal);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void OutOfOrderDuplicateFillCannotReplaceNewerUnknownState(string providerId)
    {
        var now = DateTime.UtcNow;
        var newerUnknown = Order("order-race", "client-race", "UNKNOWN", now);
        var lateOldFill = newerUnknown with { Status = "FILLED", ExecutedQuantity = 1, UpdatedAt = now.AddSeconds(-10) };

        var merged = providerId == "gate"
            ? GateExchangeProvider.MergeOrderPages([[newerUnknown], [lateOldFill]])
            : BitgetExchangeProvider.MergeOrderPages([[newerUnknown], [lateOldFill]]);

        var order = Assert.Single(merged);
        Assert.Equal("UNKNOWN", order.Status);
        Assert.Equal(0, order.ExecutedQuantity);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"total\":\"1\"}")]
    [InlineData("{\"total\":\"bad\",\"available\":\"1\"}")]
    [InlineData("{\"total\":\"-1\",\"available\":\"0\"}")]
    public void GateMalformedOrPartialAccountPayloadFailsClosed(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() => GateExchangeProvider.ParseAccountSnapshot(document.RootElement, DateTime.UtcNow));
    }

    [Fact]
    public void GateSignatureMatchesFixedCanonicalVector()
    {
        const string expected = "984c2ee2899d31c387a1d154d6428e2a8f1b7e4b7c60dfd05426c4340144d1f95de99c0f0272624552cc35e6e994e7d801cbee28068f183552a18fe464443790";
        var query = GateExchangeProvider.CanonicalQuery([
            new("status", "open"), new("contract", "BTC_USDT")]);

        var actual = GateExchangeProvider.ComputeSignature(Encoding.UTF8.GetBytes("secret"), "get",
            "/api/v4/futures/usdt/orders", query, "", "1700000000");

        Assert.Equal("contract=BTC_USDT&status=open", query);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BitgetSignatureMatchesFixedCanonicalVector()
    {
        const string expected = "W43BlqpsNhPHhfOcRrjpY3peBdp738P1Dd9fkz2AxTo=";
        var query = BitgetExchangeProvider.CanonicalQuery([
            new("symbol", "BTCUSDT"), new("clientOid", "abc 1"), new("productType", "USDT-FUTURES")]);
        var target = "/api/v2/mix/order/detail?" + query;

        var actual = BitgetExchangeProvider.ComputeSignature(Encoding.UTF8.GetBytes("secret"),
            "1700000000000", "get", target, "");

        Assert.Equal("clientOid=abc%201&productType=USDT-FUTURES&symbol=BTCUSDT", query);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void CanonicalQueryIsInvariantAndRejectsDuplicateParameters(string providerId)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var values = new KeyValuePair<string, string?>[] { new("price", 12.5m.ToString(CultureInfo.InvariantCulture)), new("note", "a/b + c") };
            var query = providerId == "gate" ? GateExchangeProvider.CanonicalQuery(values) : BitgetExchangeProvider.CanonicalQuery(values);
            Assert.Equal("note=a%2Fb%20%2B%20c&price=12.5", query);

            var duplicate = new KeyValuePair<string, string?>[] { new("symbol", "BTCUSDT"), new("symbol", "ETHUSDT") };
            Assert.Throws<InvalidOperationException>(() => providerId == "gate"
                ? GateExchangeProvider.CanonicalQuery(duplicate)
                : BitgetExchangeProvider.CanonicalQuery(duplicate));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void TimestampReceiveWindowFailsClosedOnMalformedStaleOrFutureValues(string providerId)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        Func<string, bool> check = providerId == "gate"
            ? value => GateExchangeProvider.IsTimestampWithinReceiveWindow(value, now)
            : value => BitgetExchangeProvider.IsTimestampWithinReceiveWindow(value, now);
        var current = providerId == "gate" ? now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var stale = providerId == "gate" ? (now.ToUnixTimeSeconds() - 6).ToString(CultureInfo.InvariantCulture) : (now.ToUnixTimeMilliseconds() - 6000).ToString(CultureInfo.InvariantCulture);
        var future = providerId == "gate" ? (now.ToUnixTimeSeconds() + 6).ToString(CultureInfo.InvariantCulture) : (now.ToUnixTimeMilliseconds() + 6000).ToString(CultureInfo.InvariantCulture);

        Assert.True(check(current));
        Assert.False(check(stale));
        Assert.False(check(future));
        Assert.False(check("not-a-timestamp"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void InvalidMethodPathAndDuplicateHeadersAreRejected(string providerId)
    {
        var secret = Encoding.UTF8.GetBytes("secret");
        if (providerId == "gate")
        {
            Assert.Throws<InvalidOperationException>(() => GateExchangeProvider.ComputeSignature(secret, "PATCH", "/api/v4/orders", "", "", "1700000000"));
            Assert.Throws<InvalidOperationException>(() => GateExchangeProvider.ComputeSignature(secret, "GET", "/api/v4/orders?x=1", "", "", "1700000000"));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => BitgetExchangeProvider.ComputeSignature(secret, "1700000000000", "PATCH", "/api/v2/orders", ""));
            Assert.Throws<InvalidOperationException>(() => BitgetExchangeProvider.ComputeSignature(secret, "1700000000000", "GET", "https://api.bitget.com/api/v2/orders", ""));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        if (providerId == "gate")
        {
            GateExchangeProvider.AddUniqueHeader(request, "KEY", "first");
            Assert.Throws<InvalidOperationException>(() => GateExchangeProvider.AddUniqueHeader(request, "KEY", "second"));
        }
        else
        {
            BitgetExchangeProvider.AddUniqueHeader(request, "ACCESS-KEY", "first");
            Assert.Throws<InvalidOperationException>(() => BitgetExchangeProvider.AddUniqueHeader(request, "ACCESS-KEY", "second"));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SignedReadUsesCanonicalQueryAndUniqueHeaders(string providerId)
    {
        await using var provider = CreateProvider(providerId);
        var handler = new FaultHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK,
            providerId == "gate" ? "[]" : "{\"code\":\"00000\",\"data\":{\"entrustedList\":[]}}")));
        InjectHttp(provider, handler);

        _ = await provider.GetOpenOrdersAsync("BTCUSDT", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(handler.Requests, request => Assert.DoesNotContain(" ", request.Uri.Query, StringComparison.Ordinal));
        Assert.All(handler.Requests, request => Assert.Equal(request.HeaderNames.Count, request.HeaderNames.Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static IReadOnlyDictionary<string, string> Credentials(string providerId) => providerId == "gate"
        ? new Dictionary<string, string> { ["apiKey"] = "key", ["secret"] = "secret" }
        : new Dictionary<string, string> { ["apiKey"] = "key", ["secret"] = "secret", ["passphrase"] = "passphrase" };

    private static ExchangeOrder Order(string orderId, string clientOrderId, string status, DateTime updatedAt)
        => new("BTCUSDT", orderId, clientOrderId, status, 0, 0, "LIMIT", PositionSide.Long, false, updatedAt);

    private static IExchangeProvider CreateProvider(string providerId)
    {
        var profile = new ExchangeConnectionProfile
        {
            ProviderId = providerId,
            IsTestnet = true,
            ExecutionEnabled = true,
            Endpoint = providerId == "gate" ? "https://api-testnet.gateapi.io" : "https://api.bitget.com"
        };
        return new ExchangeProviderCatalog().Create(profile, Credentials(providerId));
    }

    private static void InjectHttp(IExchangeProvider provider, HttpMessageHandler handler)
    {
        var field = typeof(RestExchangeProviderBase).GetField("Http", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(provider, new HttpClient(handler) { BaseAddress = new Uri(provider.ProviderId == "gate" ? "https://api-testnet.gateapi.io/" : "https://api.bitget.com/") });
    }

    private static Task<HttpResponseMessage> FaultResponse(string providerId, string fault, HttpRequestMessage request)
    {
        if (fault == "timeout") return Task.FromException<HttpResponseMessage>(new TimeoutException("provider timeout"));
        if (fault == "rate-limit") return Task.FromResult(Response(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"));
        if (fault == "malformed") return Task.FromResult(Response(HttpStatusCode.OK, "{"));
        var isTime = request.RequestUri!.AbsolutePath.Contains("time", StringComparison.OrdinalIgnoreCase);
        if (isTime)
        {
            var millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Task.FromResult(Response(HttpStatusCode.OK, providerId == "gate"
                ? JsonSerializer.Serialize(new { server_time = millis.ToString() })
                : JsonSerializer.Serialize(new { code = "00000", data = new { serverTime = millis.ToString() } })));
        }
        return Task.FromResult(Response(HttpStatusCode.OK, providerId == "gate" ? "{}" : "{\"code\":\"00000\",\"data\":[]}"));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string payload)
        => new(status) { Content = new StringContent(payload) };

    private sealed class FaultHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _calls;
        public List<HttpMethod> Methods { get; } = [];
        public List<RequestCapture> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Select(x => x.Key).ToArray()));
            return response(Interlocked.Increment(ref _calls), request, cancellationToken);
        }
    }

    private sealed record RequestCapture(HttpMethod Method, Uri Uri, IReadOnlyList<string> HeaderNames);

    private sealed class UnknownExchange : IExchangeAdapter
    {
        private readonly bool _returnUnknownOrder;
        public UnknownExchange(bool returnUnknownOrder = false) => _returnUnknownOrder = returnUnknownOrder;
        public int FindCalls { get; private set; }
        public int MutationCalls { get; private set; }
        public ExchangeEnvironment Environment => ExchangeEnvironment.Testnet;
        public Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct)
        {
            FindCalls++;
            ExchangeOrder? order = _returnUnknownOrder
                ? new(symbol, "gate-order", clientOrderId, "UNKNOWN", 0, 0, "LIMIT", PositionSide.Long, false, DateTime.UtcNow)
                : null;
            return Task.FromResult(order);
        }
        public Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct) { MutationCalls++; throw new InvalidOperationException(); }
        public Task<ExchangeOrder> PlaceLimitAsync(string symbol, PositionSide side, decimal quantity, decimal price, string clientOrderId, bool reduceOnly, CancellationToken ct) { MutationCalls++; throw new InvalidOperationException(); }
        public Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide side, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct) { MutationCalls++; throw new InvalidOperationException(); }
        public Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct) { MutationCalls++; return Task.CompletedTask; }
        public Task SetLeverageAsync(string symbol, int leverage, CancellationToken ct) { MutationCalls++; return Task.CompletedTask; }
        public Task SetMarginModeAsync(string symbol, bool isolated, CancellationToken ct) { MutationCalls++; return Task.CompletedTask; }
        public Task SetHedgeModeAsync(bool enabled, CancellationToken ct) { MutationCalls++; return Task.CompletedTask; }
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<TradingRule> GetRulesAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<MarketEvidence> GetMarketAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol, string interval, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol, string interval, DateTime start, DateTime end, int limit, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

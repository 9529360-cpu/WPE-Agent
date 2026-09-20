using WpeAgent.RuntimeContracts;
using System.Net;
using System.Globalization;
using 币安量化机器人.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

/// <summary>
/// Offline release-contract tests. Passing these tests is only eligibility for a credentialed
/// target-machine smoke; it is never evidence that Binance Testnet was contacted or certified.
/// </summary>
public sealed class BinanceTestnetCertificationContractTests
{
    private static readonly Instrument Instrument = new(
        "BTC-USDT-PERP", "BTCUSDT", "binance", "binance-futures", MarketType.Perpetual);

    [Fact]
    public void OfficialFuturesTestnetOrigin_IsTheOnlyAcceptedRestOrigin()
    {
        AgentSettingsStore.ValidateExchangeProfile(Profile("https://testnet.binancefuture.com"));

        foreach (var endpoint in new[]
        {
            "http://testnet.binancefuture.com",
            "https://fapi.binance.com",
            "https://testnet.binancefuture.com.evil.example",
            "https://testnet.binancefuture.com:444",
            "https://testnet.binancefuture.com/fapi"
        })
        {
            Assert.Throws<InvalidOperationException>(() =>
                AgentSettingsStore.ValidateExchangeProfile(Profile(endpoint)));
        }
    }

    [Fact]
    public void OfficialFuturesTestnetWebSocketOrigin_IsFrozen()
    {
        var client = new BinanceStreamClient();
        var endpoint = typeof(BinanceStreamClient)
            .GetField("_streamEndpoint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client);

        Assert.Equal("wss://stream.binancefuture.com/stream", endpoint);
    }

    [Fact]
    public void MainnetInput_FailsClosedBeforeExternalSmoke()
    {
        AssertBlocked(Evaluate(ValidInput() with { IsTestnet = false }), "binance-cert.testnet-required");
    }

    [Theory]
    [InlineData("NEW", "NEW")]
    [InlineData("partially_filled", "PARTIALLY_FILLED")]
    [InlineData("FILLED", "FILLED")]
    [InlineData("CANCELED", "CANCELED")]
    [InlineData("REJECTED", "REJECTED")]
    [InlineData("EXPIRED_IN_MATCH", "EXPIRED")]
    [InlineData("provider-new-state", "UNKNOWN")]
    [InlineData(null, "UNKNOWN")]
    public void ProductionOrderStatusMapping_IsCanonicalAndUnknownFailsClosed(string? providerStatus, string expected)
    {
        Assert.Equal(expected, BinanceFuturesAdapter.NormalizeStandardOrderStatus(providerStatus));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ProductionPermissionParser_DoesNotInferTradeOrWithdraw(
        bool includeCanTrade, bool canTrade, bool expectedCanTrade)
    {
        var trade = includeCanTrade ? $",\"canTrade\":{canTrade.ToString().ToLowerInvariant()}" : string.Empty;
        using var json = System.Text.Json.JsonDocument.Parse($"{{\"accountAlias\":\"test\",\"canWithdraw\":false{trade}}}");

        var result = BinanceFuturesAdapter.ParsePermissionSnapshot(json.RootElement);

        Assert.True(result.CanRead);
        Assert.Equal(expectedCanTrade, result.CanTrade);
        Assert.False(result.CanWithdraw);
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    [InlineData(-1001, false)]
    public void ProductionPermissionBoundary_DisablesTradeWhenClockSkewExceedsThreshold(
        int skewMilliseconds, bool expectedCanTrade)
    {
        var observed = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        var permission = new ExchangePermissionSnapshot(true, true, false, "test", []);

        var result = BinanceFuturesAdapter.ApplyClockSkew(
            permission, observed.AddMilliseconds(-skewMilliseconds), observed);

        Assert.Equal(expectedCanTrade, result.CanTrade);
        Assert.Equal(expectedCanTrade ? 0 : 1, result.Warnings.Count);
    }

    [Theory]
    [InlineData("https://testnet.binancefuture.com", true, true, true)]
    [InlineData("https://testnet.binancefuture.com", false, false, false)]
    [InlineData("https://fapi.binance.com", true, false, false)]
    [InlineData("https://example.invalid", true, false, false)]
    public async Task ProductionEnvironmentGuard_FailsClosedOutsideEnabledOfficialTestnet(
        string endpoint, bool isTestnet, bool expectedRead, bool expectedTrade)
    {
        var profile = Profile(endpoint, isTestnet);
        await using var adapter = new BinanceFuturesAdapter(profile, "offline-key", "offline-secret");

        var result = adapter.ValidateEnvironment(requireTestnet: true);

        Assert.Equal(expectedRead, result.CanRead);
        Assert.Equal(expectedTrade, result.CanTrade);
        Assert.Equal(expectedRead, result.TestnetAvailable);
    }

    [Fact]
    public void UnknownCapability_FailsClosedBeforeExternalSmoke()
    {
        var result = Evaluate(ValidInput() with { Capability = null });

        AssertBlocked(result, "binance-cert.capability-unknown");
    }

    [Fact]
    public void ExpiredCapability_FailsClosedBeforeExternalSmoke()
    {
        var input = ValidInput();
        var result = Evaluate(input with
        {
            Capability = input.Capability! with
            {
                CheckedAt = DateTimeOffset.UtcNow - ProviderCapabilityPrecondition.MaximumAge - TimeSpan.FromSeconds(1)
            }
        });

        AssertBlocked(result, "binance-cert.capability-stale");
    }

    [Theory]
    [InlineData(null, true, false, "binance-cert.permissions-unknown")]
    [InlineData(false, true, false, "binance-cert.permission-read-required")]
    [InlineData(true, false, false, "binance-cert.permission-trade-required")]
    [InlineData(true, true, true, "binance-cert.permission-withdraw-forbidden")]
    public void UnknownOrInsufficientPermissions_FailClosed(
        bool? canRead, bool canTrade, bool canWithdraw, string expectedCode)
    {
        var permissions = canRead is null ? null : new PermissionEvidence(canRead.Value, canTrade, canWithdraw);

        AssertBlocked(Evaluate(ValidInput() with { Permissions = permissions }), expectedCode);
    }

    [Theory]
    [InlineData(null, "binance-cert.clock-unknown")]
    [InlineData(1001L, "binance-cert.clock-skew")]
    [InlineData(-1001L, "binance-cert.clock-skew")]
    public void UnknownOrExcessiveClockSkew_FailsClosed(long? offsetMilliseconds, string expectedCode)
    {
        AssertBlocked(Evaluate(ValidInput() with { ServerOffsetMilliseconds = offsetMilliseconds }), expectedCode);
    }

    [Theory]
    [InlineData("submitted", "binance-cert.lifecycle-incomplete")]
    [InlineData("partially-filled", "binance-cert.lifecycle-incomplete")]
    [InlineData("filled", "binance-cert.lifecycle-incomplete")]
    [InlineData("canceled", "binance-cert.lifecycle-incomplete")]
    public void MissingOrderLifecycleObservation_FailsClosed(string missing, string expectedCode)
    {
        var observed = ValidInput().ObservedOrderStates.Where(value => value != missing).ToArray();

        AssertBlocked(Evaluate(ValidInput() with { ObservedOrderStates = observed }), expectedCode);
    }

    [Theory]
    [InlineData(ReconcileEvidence.Unknown, "binance-cert.reconcile-unknown")]
    [InlineData(ReconcileEvidence.Resubmitted, "binance-cert.reconcile-resubmitted")]
    public void UnknownOrDuplicateReconcile_FailsClosed(ReconcileEvidence evidence, string expectedCode)
    {
        AssertBlocked(Evaluate(ValidInput() with { Reconcile = evidence }), expectedCode);
    }

    [Theory]
    [InlineData(418)]
    [InlineData(429)]
    public async Task RateLimitResponses_AreRetriedThenRemainUnavailableWithoutMutation(int statusCode)
    {
        var handler = new FaultHandler(_ => new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent("{\"apiKey\":\"phase3-secret\",\"signature\":\"signed-secret\"}")
        });
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<BinanceHttpException>(() =>
            client.GetPublicRawAsync("/fapi/v1/time", null, CancellationToken.None));
        var failure = BinanceFuturesAdapter.ClassifyFailure(error, "phase3-secret", "signed-secret");

        Assert.Equal("Unavailable", failure.State);
        Assert.Equal(BinanceHttpOutcome.Uncertain,error.Outcome);
        Assert.Equal(statusCode,(int?)error.StatusCode);
        Assert.Equal(4, handler.CallCount);
        Assert.Equal(0, handler.MutationCount);
        Assert.DoesNotContain("phase3-secret", failure.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-secret", failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_IsUnavailableAndNeverFallsBackToMutation()
    {
        var handler = new FaultHandler(_ => throw new TaskCanceledException("apiKey=timeout-secret"));
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<BinanceHttpException>(() =>
            client.GetPublicRawAsync("/fapi/v1/time", null, CancellationToken.None));
        var failure = BinanceFuturesAdapter.ClassifyFailure(error, "timeout-secret");

        Assert.Equal("Unavailable", failure.State);
        Assert.Equal(BinanceHttpOutcome.Uncertain,error.Outcome);
        Assert.Null(error.StatusCode);
        Assert.Equal(4, handler.CallCount);
        Assert.Equal(0, handler.MutationCount);
        Assert.DoesNotContain("timeout-secret", failure.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"symbol\":\"BTCUSDT\"}")]
    public async Task MalformedOrPartialPayload_IsUnavailableWithoutMutation(string payload)
    {
        var handler = new FaultHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });
        using var client = Client(handler);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.GetMiniTickersAsync(cancellationToken: CancellationToken.None));
        var failure = BinanceFuturesAdapter.ClassifyFailure(error);

        Assert.Equal("Unavailable", failure.State);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(0, handler.MutationCount);
    }

    [Fact]
    public async Task HedgeModeAlreadyEnabled_IsReadOnly()
    {
        var handler = new FaultHandler(request =>
        {
            if(request.RequestUri?.AbsolutePath=="/fapi/v1/time")
                return Json(HttpStatusCode.OK,System.Text.Json.JsonSerializer.Serialize(new{serverTime=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}));
            if(request.RequestUri?.AbsolutePath=="/fapi/v1/positionSide/dual")
                return Json(HttpStatusCode.OK,"{\"dualSidePosition\":true}");
            return Json(HttpStatusCode.NotFound,"{}");
        });
        using var client=Client(handler);client.SetApiCredentials("test-key","test-secret");

        await BinanceFuturesAdapter.SetHedgeModeIfNeededAsync(client,true,CancellationToken.None);

        Assert.Equal(0,handler.MutationCount);
    }

    [Fact]
    public async Task HedgeModeMismatch_PerformsSingleMutation()
    {
        var handler = new FaultHandler(request =>
        {
            if(request.RequestUri?.AbsolutePath=="/fapi/v1/time")
                return Json(HttpStatusCode.OK,System.Text.Json.JsonSerializer.Serialize(new{serverTime=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}));
            if(request.Method==HttpMethod.Get&&request.RequestUri?.AbsolutePath=="/fapi/v1/positionSide/dual")
                return Json(HttpStatusCode.OK,"{\"dualSidePosition\":false}");
            if(request.Method==HttpMethod.Post&&request.RequestUri?.AbsolutePath=="/fapi/v1/positionSide/dual")
                return Json(HttpStatusCode.OK,"{}");
            return Json(HttpStatusCode.NotFound,"{}");
        });
        using var client=Client(handler);client.SetApiCredentials("test-key","test-secret");

        await BinanceFuturesAdapter.SetHedgeModeIfNeededAsync(client,true,CancellationToken.None);

        Assert.Equal(1,handler.MutationCount);
    }

    [Fact]
    public void DuplicateOrderEvents_AreCollapsedWithoutInventingATransition()
    {
        var observed = new DateTime(2026, 7, 22, 1, 0, 0, DateTimeKind.Utc);
        var first = new ExchangeOrder("BTCUSDT", "42", "wpe-42", "PARTIALLY_FILLED", .01m, 50_000m,
            "LIMIT", PositionSide.Long, false, observed);

        var result = BinanceFuturesAdapter.DeduplicateOrderEvents([first, first, first with { Status = "FILLED", ExecutedQuantity = .02m }]);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "PARTIALLY_FILLED", "FILLED" }, result.Select(x => x.Status));
    }

    [Fact]
    public void UnknownFailure_IsRedactedAndCannotBecomeAvailable()
    {
        const string apiKey = "phase3-api-key-secret";
        const string signature = "phase3-signature-secret";

        var failure = BinanceFuturesAdapter.ClassifyFailure(
            new InvalidOperationException($"apiKey={apiKey}; signature={signature}; accountId=private-account"),
            apiKey, signature);

        Assert.Equal("UNKNOWN", failure.State);
        Assert.DoesNotContain(apiKey, failure.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, failure.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-account", failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalQuery_UsesOrdinalOrderingEncodingNullOmissionAndEmptyPreservation()
    {
        var query = BinanceApiClient.BuildQueryString(new KeyValuePair<string, string?>[]
        {
            new("symbol", "BTC/USDT + PERP"),
            new("z-null", null),
            new("empty", string.Empty),
            new("alpha", "A&B=C")
        });

        Assert.Equal("alpha=A%26B%3DC&empty=&symbol=BTC%2FUSDT%20%2B%20PERP", query);
    }

    [Fact]
    public void CanonicalQuery_RejectsDuplicateParametersBeforeSigning()
    {
        var duplicate = new KeyValuePair<string, string?>[] { new("symbol", "BTCUSDT"), new("symbol", "ETHUSDT") };

        var error = Assert.Throws<ArgumentException>(() => BinanceApiClient.BuildQueryString(duplicate));

        Assert.Contains("Duplicate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderSerialization_IsInvariantAcrossCurrentCultures()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var payload = BinanceApiClient.BuildOrderPayload(new OrderRequest
            {
                Symbol = "btcusdt", Side = OrderSide.Buy, Type = OrderType.Limit,
                Quantity = 1.25m, Price = 1234.56m, TimeInForce = TimeInForce.Gtc
            });

            Assert.Equal("1.25", payload["quantity"]);
            Assert.Equal("1234.56", payload["price"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task SignedRequest_MatchesFixedPublicVectorAndClampsReceiveWindow()
    {
        const string publicApiKey = "phase4-public-api-key";
        const string publicSecret = "phase4-public-test-secret";
        const string expectedUnsigned = "newClientOrderId=wpe%20phase%2F4&price=1234.56&recvWindow=60000&symbol=BTCUSDT&timestamp=1700000000123";
        const string expectedSignature = "eeafa3046b26b90e448342b82061001a7690c38a45a0980ab8ff82f80d0dc3e3";
        HttpRequestMessage? observed = null;
        var handler = new FaultHandler(request =>
        {
            if(request.RequestUri!.AbsolutePath=="/fapi/v1/time")
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"serverTime\":1700000000123}")};
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        using var client = Client(handler, 999_999, () => DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123));
        client.SetApiCredentials(publicApiKey, publicSecret);

        _ = await client.GetSignedRawAsync("/fapi/v1/order", new Dictionary<string, string?>
        {
            ["symbol"] = "BTCUSDT", ["price"] = "1234.56", ["newClientOrderId"] = "wpe phase/4"
        }, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(publicApiKey, observed!.Headers.GetValues("X-MBX-APIKEY").Single());
        var query = observed.RequestUri!.Query.TrimStart('?');
        Assert.Equal(expectedUnsigned + "&signature=" + expectedSignature, query);
        Assert.Equal(0, handler.MutationCount);
    }

    [Fact]
    public async Task SignedTransportFailure_RedactsSignatureAndApiKeyFromException()
    {
        const string publicApiKey = "phase4-public-api-key";
        var handler = new FaultHandler(request => throw new HttpRequestException(
            $"failed uri={request.RequestUri}; X-MBX-APIKEY={request.Headers.GetValues("X-MBX-APIKEY").Single()}"));
        handler.Response = request => request.RequestUri!.AbsolutePath=="/fapi/v1/time"
            ?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"serverTime\":1700000000123}")}
            :throw new HttpRequestException($"failed uri={request.RequestUri}; X-MBX-APIKEY={request.Headers.GetValues("X-MBX-APIKEY").Single()}");
        using var client = Client(handler, 5000, () => DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123));
        client.SetApiCredentials(publicApiKey, "phase4-public-test-secret");

        var error = await Assert.ThrowsAsync<BinanceHttpException>(() =>
            client.GetSignedRawAsync("/fapi/v1/order", new Dictionary<string, string?> { ["symbol"] = "BTCUSDT" }, CancellationToken.None));

        Assert.DoesNotContain(publicApiKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("signature=", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BinanceHttpOutcome.Uncertain,error.Outcome);
        Assert.Contains("outcome=Uncertain",error.Message,StringComparison.Ordinal);
        Assert.Equal(0, handler.MutationCount);
    }

    [Fact]
    public async Task ClockMeasurement_UsesMedianMidpointOffsetAndAdjustedSignedTimestamp()
    {
        var clock=new MutableClock(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        HttpRequestMessage? signed=null;
        var handler=new FaultHandler(request=>
        {
            if(request.RequestUri!.AbsolutePath=="/fapi/v1/time")
            {
                var sent=clock.Now;
                clock.Advance(TimeSpan.FromMilliseconds(100));
                var server=sent.AddMilliseconds(2050).ToUnixTimeMilliseconds();
                return new(HttpStatusCode.OK){Content=new StringContent($"{{\"serverTime\":{server}}}")};
            }
            signed=request;
            return new(HttpStatusCode.OK){Content=new StringContent("{}")};
        });
        using var client=Client(handler,5000,()=>clock.Now);
        client.SetApiCredentials("offline-key","offline-secret");

        var measurement=await client.SynchronizeClockAsync();
        _=await client.GetSignedRawAsync("/fapi/v2/account",null);

        Assert.True(measurement.Trusted);
        Assert.Equal(3,measurement.SampleCount);
        Assert.Equal(2000,measurement.OffsetMilliseconds);
        Assert.Equal(100,measurement.MaximumRoundTripMilliseconds);
        Assert.Equal(50,measurement.UncertaintyMilliseconds);
        var timestamp=long.Parse(signed!.RequestUri!.Query.TrimStart('?').Split('&').Single(x=>x.StartsWith("timestamp=",StringComparison.Ordinal)).Split('=',2)[1],CultureInfo.InvariantCulture);
        Assert.Equal(clock.Now.AddMilliseconds(2000).ToUnixTimeMilliseconds(),timestamp);
    }

    [Fact]
    public async Task ClockMeasurement_HighRoundTripFailsClosedBeforeSignedRequest()
    {
        var clock=new MutableClock(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var handler=new FaultHandler(request=>
        {
            var sent=clock.Now;clock.Advance(TimeSpan.FromMilliseconds(1600));
            return new(HttpStatusCode.OK){Content=new StringContent($"{{\"serverTime\":{sent.AddMilliseconds(800).ToUnixTimeMilliseconds()}}}")};
        });
        using var client=Client(handler,5000,()=>clock.Now);client.SetApiCredentials("offline-key","offline-secret");

        var measurement=await client.SynchronizeClockAsync();
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>client.GetSignedRawAsync("/fapi/v2/account",null));

        Assert.False(measurement.Trusted);
        Assert.Equal("clock.uncertain",measurement.Code);
        Assert.Contains("clock",error.Message,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6,handler.CallCount);
    }

    [Fact]
    public async Task ClockMeasurement_StaleAndRefreshFailureRejectsSignedRequest()
    {
        var clock=new MutableClock(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var fail=false;
        var handler=new FaultHandler(_=>
        {
            if(fail)throw new HttpRequestException("clock unavailable");
            return new(HttpStatusCode.OK){Content=new StringContent($"{{\"serverTime\":{clock.Now.ToUnixTimeMilliseconds()}}}")};
        });
        using var client=Client(handler,5000,()=>clock.Now);client.SetApiCredentials("offline-key","offline-secret");
        Assert.True((await client.SynchronizeClockAsync()).Trusted);
        clock.Advance(TimeSpan.FromMinutes(6));fail=true;

        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.GetSignedRawAsync("/fapi/v2/account",null));

        Assert.Equal("clock.measurement-failed",client.ClockMeasurement.Code);
    }

    [Fact]
    public async Task ClockMeasurement_UntrustedEndpointFailsWithoutNetwork()
    {
        var handler=new FaultHandler(_=>new(HttpStatusCode.OK){Content=new StringContent("{\"serverTime\":1}")});
        using var http=new HttpClient(handler){BaseAddress=new Uri("https://example.invalid")};
        using var client=new BinanceApiClient(http,useTestnet:true,utcNow:()=>DateTimeOffset.UtcNow);

        var measurement=await client.SynchronizeClockAsync();

        Assert.False(measurement.Trusted);
        Assert.Equal("clock.endpoint-untrusted",measurement.Code);
        Assert.Equal(0,handler.CallCount);
    }

    [Fact]
    public void MissingEvidenceArtifact_FailsClosed()
    {
        var artifacts = ValidInput().ArtifactNames.Where(value => value != "manifest.sha256").ToArray();

        AssertBlocked(Evaluate(ValidInput() with { ArtifactNames = artifacts }), "binance-cert.evidence-incomplete");
    }

    [Fact]
    public void CompleteOfflineFake_IsEligibleForExternalSmokeButNeverCertified()
    {
        var result = Evaluate(ValidInput());

        Assert.Equal(CertificationDisposition.OfflineContractPassed, result.Disposition);
        Assert.Equal("binance-cert.offline-contract-passed", result.Code);
        Assert.True(result.EligibleForCredentialedSmoke);
        Assert.False(result.Certified);
        Assert.Equal("NOT CERTIFIED - credentialed target-machine smoke required", result.ReleaseStatement);
    }

    private static CertificationResult Evaluate(CertificationInput input)
    {
        if (!input.IsTestnet)
            return Blocked("binance-cert.testnet-required");
        try
        {
            AgentSettingsStore.ValidateExchangeProfile(Profile(input.RestOrigin));
        }
        catch (InvalidOperationException)
        {
            return Blocked("binance-cert.endpoint-untrusted");
        }

        if (input.Permissions is null)
            return Blocked("binance-cert.permissions-unknown");
        if (!input.Permissions.CanRead)
            return Blocked("binance-cert.permission-read-required");
        if (!input.Permissions.CanTrade)
            return Blocked("binance-cert.permission-trade-required");
        if (input.Permissions.CanWithdraw)
            return Blocked("binance-cert.permission-withdraw-forbidden");
        if (input.ServerOffsetMilliseconds is null)
            return Blocked("binance-cert.clock-unknown");
        if (Math.Abs(input.ServerOffsetMilliseconds.Value) > 1000)
            return Blocked("binance-cert.clock-skew");

        var capability = new ProviderCapabilityPrecondition().Check(Instrument, input.Capability, testnet: true);
        if (!capability.Allowed)
        {
            var suffix = capability.Reason switch
            {
                "capability unknown" => "capability-unknown",
                "capability stale" => "capability-stale",
                "trading unsupported" => "permission-trade-required",
                "testnet unavailable" => "testnet-unavailable",
                _ => "capability-invalid"
            };
            return Blocked("binance-cert." + suffix);
        }

        if (!RequiredOrderStates.All(required => input.ObservedOrderStates.Contains(required, StringComparer.Ordinal)))
            return Blocked("binance-cert.lifecycle-incomplete");
        if (input.Reconcile == ReconcileEvidence.Unknown)
            return Blocked("binance-cert.reconcile-unknown");
        if (input.Reconcile == ReconcileEvidence.Resubmitted)
            return Blocked("binance-cert.reconcile-resubmitted");
        if (!RequiredArtifacts.All(required => input.ArtifactNames.Contains(required, StringComparer.Ordinal)))
            return Blocked("binance-cert.evidence-incomplete");

        return new(CertificationDisposition.OfflineContractPassed,
            "binance-cert.offline-contract-passed", true, false,
            "NOT CERTIFIED - credentialed target-machine smoke required");
    }

    private static CertificationInput ValidInput() => new(
        true,
        "https://testnet.binancefuture.com",
        new(true, true, false),
        250,
        new("binance", "binance-futures", "BTC-USDT-PERP", "BTCUSDT", MarketType.Perpetual,
            CapabilityStatus.Available, true, true, true, DateTimeOffset.UtcNow, null),
        RequiredOrderStates,
        ReconcileEvidence.FoundByClientOrderIdWithoutResubmission,
        RequiredArtifacts);

    private static ExchangeConnectionProfile Profile(string endpoint, bool isTestnet = true) => new()
    {
        ProviderId = "binance-futures",
        IsTestnet = isTestnet,
        Endpoint = endpoint
    };

    private static BinanceApiClient Client(HttpMessageHandler handler, int receiveWindow = 5000,
        Func<DateTimeOffset>? utcNow = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binancefuture.com") },
        useTestnet: true, receiveWindow: receiveWindow, utcNow: utcNow);

    private static HttpResponseMessage Json(HttpStatusCode status,string payload) =>
        new(status){Content=new StringContent(payload,System.Text.Encoding.UTF8,"application/json")};

    private sealed class FaultHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<HttpRequestMessage,HttpResponseMessage> Response{get;set;}=response;
        public int CallCount { get; private set; }
        public int MutationCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (request.Method != HttpMethod.Get) MutationCount++;
            return Task.FromResult(Response(request));
        }
    }
    private sealed class MutableClock(DateTimeOffset now)
    {
        public DateTimeOffset Now{get;private set;}=now;
        public void Advance(TimeSpan value)=>Now=Now.Add(value);
    }

    private static CertificationResult Blocked(string code) => new(
        CertificationDisposition.Blocked, code, false, false,
        "NOT CERTIFIED - fail-closed precondition blocked");

    private static void AssertBlocked(CertificationResult result, string expectedCode)
    {
        Assert.Equal(CertificationDisposition.Blocked, result.Disposition);
        Assert.Equal(expectedCode, result.Code);
        Assert.False(result.EligibleForCredentialedSmoke);
        Assert.False(result.Certified);
    }

    private static readonly string[] RequiredOrderStates = ["submitted", "partially-filled", "filled", "canceled"];
    private static readonly string[] RequiredArtifacts =
    [
        "access.json", "clock.json", "capability.json", "order-lifecycle.jsonl",
        "reconcile.json", "audit.jsonl", "manifest.sha256"
    ];

    private sealed record PermissionEvidence(bool CanRead, bool CanTrade, bool CanWithdraw);
    private sealed record CertificationInput(
        bool IsTestnet,
        string RestOrigin,
        PermissionEvidence? Permissions,
        long? ServerOffsetMilliseconds,
        ExchangeCapability? Capability,
        IReadOnlyList<string> ObservedOrderStates,
        ReconcileEvidence Reconcile,
        IReadOnlyList<string> ArtifactNames);
    private sealed record CertificationResult(
        CertificationDisposition Disposition,
        string Code,
        bool EligibleForCredentialedSmoke,
        bool Certified,
        string ReleaseStatement);
    private enum CertificationDisposition { Blocked, OfflineContractPassed }
    public enum ReconcileEvidence { Unknown, FoundByClientOrderIdWithoutResubmission, Resubmitted }
}

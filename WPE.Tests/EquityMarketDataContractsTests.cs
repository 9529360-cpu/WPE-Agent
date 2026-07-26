using WpeAgent.Equities;

namespace WPE.Tests;

public sealed class EquityMarketDataContractsTests
{
    [Fact]
    public void StoreWithoutAuthorizedProvider_IsUnsupportedAndContainsNoMarketData()
    {
        var state = new EquityMarketDataStateStore().Read();

        Assert.Equal(EquityMarketDataStatus.Unsupported, state.Status);
        Assert.Equal(EquityLicenseStatus.Unsupported, state.License);
        Assert.Empty(state.Quotes);
        Assert.Empty(state.Sessions);
        Assert.Empty(state.Halts);
        Assert.Empty(state.CorporateActions);
    }

    [Fact]
    public void AuthorizedFreshProjection_PreservesProviderNeutralReadFacts()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new EquityMarketDataStateStore();
        store.Publish(AvailableProjection(now) with
        {
            Halts = [new("US:AAPL", "XNAS", EquityTradingHaltStatus.NotHalted, now)],
            CorporateActions = [new("action-1", "US:AAPL", EquityCorporateActionType.Split,
                DateOnly.FromDateTime(now.AddDays(1).UtcDateTime), now, "licensed-provider", Ratio: 4m)]
        });

        var state = store.Read();
        Assert.Equal(EquityMarketDataStatus.Available, state.Status);
        Assert.Equal(210.25m, Assert.Single(state.Quotes).Last);
        Assert.Equal(EquitySessionPhase.Open, Assert.Single(state.Sessions).Phase);
        Assert.Equal(EquityTradingHaltStatus.NotHalted, Assert.Single(state.Halts).Status);
        Assert.Equal(4m, Assert.Single(state.CorporateActions).Ratio);
    }

    [Theory]
    [InlineData(EquityLicenseStatus.Unknown)]
    [InlineData(EquityLicenseStatus.Restricted)]
    [InlineData(EquityLicenseStatus.Unsupported)]
    public void UnlicensedProjection_FailsClosedAndDoesNotExposeQuote(EquityLicenseStatus license)
    {
        var now = DateTimeOffset.UtcNow;
        var store = new EquityMarketDataStateStore();
        store.Publish(AvailableProjection(now) with { License = license });

        var state = store.Read();
        Assert.Equal(EquityMarketDataStatus.Unsupported, state.Status);
        Assert.Equal(license, state.License);
        Assert.Empty(state.Quotes);
    }

    [Fact]
    public void StaleOrInvalidProjection_WithholdsAllProjectedFacts()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new EquityMarketDataStateStore();

        store.Publish(AvailableProjection(now) with { Freshness = EquityFreshnessStatus.Stale });
        Assert.Equal(EquityMarketDataStatus.Stale, store.Read().Status);
        Assert.Empty(store.Read().Quotes);

        store.Publish(AvailableProjection(now) with { Quality = EquityQualityStatus.Invalid });
        Assert.Equal(EquityMarketDataStatus.Error, store.Read().Status);
        Assert.Empty(store.Read().Quotes);

        store.Publish(AvailableProjection(now) with { Quality = EquityQualityStatus.Degraded });
        Assert.Equal(EquityMarketDataStatus.Stale, store.Read().Status);
        Assert.Empty(store.Read().Quotes);
    }

    [Fact]
    public void AuthorizationRevocation_ClearsPreviouslyAvailableProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var store = AvailableStore(now);

        store.Publish(AvailableProjection(now.AddSeconds(1)) with
        {
            License = EquityLicenseStatus.Restricted,
            Message = "Equity data license was revoked."
        });

        var state = store.Read(now.AddSeconds(1));
        AssertWithheld(state, EquityMarketDataStatus.Unsupported);
        Assert.Equal(EquityLicenseStatus.Restricted, state.License);
    }

    [Fact]
    public void AvailableProjection_ExpiresAndWithholdsFactsAtReadBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        var store = AvailableStore(now);

        var state = store.Read(now + EquityMarketDataStateStore.StaleAfter + TimeSpan.FromTicks(1));

        AssertWithheld(state, EquityMarketDataStatus.Stale);
    }

    [Fact]
    public void OlderProjection_IsRejectedAndClearsCurrentFacts()
    {
        var now = DateTimeOffset.UtcNow;
        var store = AvailableStore(now);

        store.Publish(AvailableProjection(now.AddMinutes(-1)));

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Error);
        Assert.Contains("Out-of-order", store.Read(now).Message);
    }

    [Fact]
    public void ProjectionWithObservationAfterBatchTimestamp_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var projection = AvailableProjection(now) with
        {
            Quotes = [Quote(now.AddSeconds(1))]
        };
        var store = new EquityMarketDataStateStore();

        store.Publish(projection);

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Error);
    }

    [Fact]
    public void EffectiveCorporateActionWithUnadjustedQuote_IsHidden()
    {
        var now = DateTimeOffset.UtcNow;
        var action = new EquityCorporateActionProjection(
            "split-1", "US:AAPL", EquityCorporateActionType.Split,
            DateOnly.FromDateTime(now.UtcDateTime), now, "licensed-provider", Ratio: 4m);
        var projection = AvailableProjection(now) with
        {
            Quotes = [Quote(now) with { AdjustmentStatus = EquityPriceAdjustmentStatus.Unadjusted }],
            CorporateActions = [action]
        };
        var store = new EquityMarketDataStateStore();

        store.Publish(projection);

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Unsupported);
    }

    [Theory]
    [InlineData(EquityTradingHaltStatus.Halted)]
    [InlineData(EquityTradingHaltStatus.Resuming)]
    [InlineData(EquityTradingHaltStatus.Unknown)]
    public void NonTradableHaltState_HidesMarketFacts(EquityTradingHaltStatus haltStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var projection = AvailableProjection(now) with
        {
            Halts = [new("US:AAPL", "XNAS", haltStatus, now, "venue halt")]
        };
        var store = new EquityMarketDataStateStore();

        store.Publish(projection);

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Unsupported);
    }

    [Fact]
    public void ProviderError_ClearsPreviouslyAvailableProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var store = AvailableStore(now);

        store.PublishError("Licensed provider read failed.", now.AddSeconds(1));

        AssertWithheld(store.Read(now.AddSeconds(1)), EquityMarketDataStatus.Error);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("observed")]
    [InlineData("exchange-time")]
    [InlineData("currency")]
    [InlineData("session")]
    [InlineData("adjustment-basis")]
    public void MissingRequiredProvenance_IsUnsupported(string missingField)
    {
        var now = DateTimeOffset.UtcNow;
        var projection = AvailableProjection(now);
        projection = missingField == "provider" ? projection with { ProviderId = null } : projection;
        var quote = Assert.Single(projection.Quotes);
        quote = missingField switch
        {
            "observed" => quote with { ObservedAtUtc = null },
            "exchange-time" => quote with { ExchangeTimestampUtc = null },
            "currency" => quote with { Currency = "" },
            "session" => quote with { Session = EquitySessionPhase.Unknown },
            "adjustment-basis" => quote with { AdjustmentBasis = EquityAdjustmentBasis.Unknown },
            _ => quote
        };
        projection = projection with { Quotes = [quote] };
        var store = new EquityMarketDataStateStore();

        store.Publish(projection);

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Unsupported);
    }

    [Fact]
    public void ProviderConflict_IsErrorAndClearsProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var projection = AvailableProjection(now) with
        {
            Quotes = [Quote(now) with { ProviderId = "different-provider" }]
        };
        var store = new EquityMarketDataStateStore();

        store.Publish(projection);

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Error);
    }

    [Fact]
    public void ExchangeTimestampConflict_IsErrorAndClearsProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var projection = AvailableProjection(now) with
        {
            Quotes = [Quote(now) with { ExchangeTimestampUtc = now.AddSeconds(-1) }]
        };
        var store = new EquityMarketDataStateStore();

        store.Publish(projection);

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Error);
    }

    [Fact]
    public void SameInstrumentAcrossCurrencies_IsErrorAndClearsProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var projection = AvailableProjection(now) with
        {
            Quotes = [Quote(now), Quote(now) with { Currency = "JPY" }]
        };
        var store = new EquityMarketDataStateStore();

        store.Publish(projection);

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Error);
    }

    [Fact]
    public void FutureProjectionTimestamp_IsErrorAndClearsProjection()
    {
        var future = DateTimeOffset.UtcNow.AddMinutes(1);
        var store = new EquityMarketDataStateStore();

        store.Publish(AvailableProjection(future));

        AssertWithheld(store.Read(DateTimeOffset.UtcNow), EquityMarketDataStatus.Error);
    }

    [Fact]
    public void QuoteSessionMismatch_IsErrorAndClearsProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var projection = AvailableProjection(now) with
        {
            Quotes = [Quote(now) with { Session = EquitySessionPhase.PreMarket }]
        };
        var store = new EquityMarketDataStateStore();

        store.Publish(projection);

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Error);
    }

    [Fact]
    public void AdjustedStatusWithRawBasis_IsUnsupportedAndClearsProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var projection = AvailableProjection(now) with
        {
            Quotes = [Quote(now) with { AdjustmentBasis = EquityAdjustmentBasis.Raw }]
        };
        var store = new EquityMarketDataStateStore();

        store.Publish(projection);

        AssertWithheld(store.Read(now), EquityMarketDataStatus.Unsupported);
    }

    [Fact]
    public async Task DefaultProvider_IsUnsupportedAndProducesNoQuote()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new UnsupportedEquityMarketDataProvider();
        var request = new EquityProviderCapabilityRequest("XNAS", "US:AAPL", now);

        var result = await new EquityMarketDataProviderConformanceHarness()
            .EvaluateAsync(provider, request, now);

        Assert.False(result.ReadAttempted);
        Assert.False(result.CanRead);
        Assert.Equal(EquityProviderCapabilityStatus.Unsupported, result.Capability.Status);
        AssertWithheld(result.Projection, EquityMarketDataStatus.Unsupported);
    }

    [Theory]
    [InlineData(EquityProviderCapabilityStatus.Unknown, EquityMarketDataStatus.Unsupported)]
    [InlineData(EquityProviderCapabilityStatus.Stale, EquityMarketDataStatus.Stale)]
    [InlineData(EquityProviderCapabilityStatus.PermissionDenied, EquityMarketDataStatus.Unsupported)]
    [InlineData(EquityProviderCapabilityStatus.RateLimited, EquityMarketDataStatus.Error)]
    [InlineData(EquityProviderCapabilityStatus.Error, EquityMarketDataStatus.Error)]
    public async Task UnavailableCapability_NeverInvokesProviderRead(
        EquityProviderCapabilityStatus capabilityStatus,
        EquityMarketDataStatus expectedStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new OfflineProviderStub(AvailableCapability(now) with { Status = capabilityStatus },
            AvailableProjection(now));

        var result = await EvaluateAsync(provider, now);

        Assert.False(result.ReadAttempted);
        Assert.Equal(0, provider.ReadCount);
        AssertWithheld(result.Projection, expectedStatus);
    }

    [Fact]
    public async Task ExpiredCapability_NeverInvokesProviderRead()
    {
        var now = DateTimeOffset.UtcNow;
        var capability = AvailableCapability(now - EquityMarketDataProviderConformanceHarness.CapabilityMaxAge - TimeSpan.FromTicks(1));
        var provider = new OfflineProviderStub(capability, AvailableProjection(now));

        var result = await EvaluateAsync(provider, now);

        Assert.False(result.ReadAttempted);
        Assert.Equal(0, provider.ReadCount);
        AssertWithheld(result.Projection, EquityMarketDataStatus.Stale);
    }

    [Fact]
    public async Task PermissionOrMarketSupportGap_NeverInvokesProviderRead()
    {
        var now = DateTimeOffset.UtcNow;
        var capability = AvailableCapability(now) with
        {
            License = EquityLicenseStatus.Restricted,
            VenueSupport = EquityProviderCapabilityStatus.Unsupported,
            InstrumentSupport = EquityProviderCapabilityStatus.Unknown
        };
        var provider = new OfflineProviderStub(capability, AvailableProjection(now));

        var result = await EvaluateAsync(provider, now);

        Assert.False(result.ReadAttempted);
        Assert.Equal(0, provider.ReadCount);
        AssertWithheld(result.Projection, EquityMarketDataStatus.Unsupported);
    }

    [Theory]
    [InlineData("calendar")]
    [InlineData("session")]
    [InlineData("quote")]
    [InlineData("adjustment")]
    [InlineData("rate-limit")]
    public async Task RequiredProviderDimensionMustBeAvailable(string dimension)
    {
        var now = DateTimeOffset.UtcNow;
        var capability = AvailableCapability(now);
        capability = dimension switch
        {
            "calendar" => capability with { CalendarSupport = EquityProviderCapabilityStatus.Unknown },
            "session" => capability with { SessionSupport = EquityProviderCapabilityStatus.Unsupported },
            "quote" => capability with { QuoteFreshness = EquityProviderCapabilityStatus.Stale },
            "adjustment" => capability with { CorporateActionAdjustment = EquityProviderCapabilityStatus.PermissionDenied },
            _ => capability with { RateLimit = EquityProviderCapabilityStatus.RateLimited }
        };
        var provider = new OfflineProviderStub(capability, AvailableProjection(now));

        var result = await EvaluateAsync(provider, now);

        Assert.False(result.CanRead);
        Assert.False(result.ReadAttempted);
        Assert.Equal(0, provider.ReadCount);
        Assert.Empty(result.Projection.Quotes);
    }

    [Fact]
    public async Task FullyAvailableOfflineProvider_ReadsOnceAndPassesQualityGate()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new OfflineProviderStub(AvailableCapability(now), AvailableProjection(now));

        var result = await EvaluateAsync(provider, now);

        Assert.True(result.ReadAttempted);
        Assert.True(result.CanRead);
        Assert.Equal(1, provider.ReadCount);
        Assert.Single(result.Projection.Quotes);
    }

    [Fact]
    public async Task ProviderReadError_IsCaughtAndClearsProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new OfflineProviderStub(AvailableCapability(now), new InvalidOperationException("offline read failure"));

        var result = await EvaluateAsync(provider, now);

        Assert.True(result.ReadAttempted);
        Assert.False(result.CanRead);
        Assert.Equal(1, provider.ReadCount);
        AssertWithheld(result.Projection, EquityMarketDataStatus.Error);
    }

    [Fact]
    public async Task EmptyProviderRegistry_IsUnsupported()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new EquityMarketDataProviderRegistry();

        var result = await registry.SelectAsync([], SelectionRequest(now), now);

        Assert.Equal(0, registry.Count);
        Assert.False(result.CanRead);
        Assert.Equal(EquityMarketDataStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task RegisteredButNotExplicitlyConfiguredProvider_IsUnsupported()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = SelectionProvider("provider-a", now);
        var registry = new EquityMarketDataProviderRegistry([provider]);

        var result = await registry.SelectAsync([], SelectionRequest(now), now);

        Assert.False(result.CanRead);
        Assert.Equal(0, provider.ReadCount);
    }

    [Fact]
    public async Task SingleExplicitAvailableProvider_IsSelectedWithoutReadingQuotes()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = SelectionProvider("provider-a", now);
        var registry = new EquityMarketDataProviderRegistry([provider]);

        var result = await registry.SelectAsync([new("provider-a", 10)], SelectionRequest(now), now);

        Assert.True(result.CanRead);
        Assert.Same(provider, result.Provider);
        Assert.Equal(0, provider.ReadCount);
    }

    [Theory]
    [InlineData("venue")]
    [InlineData("instrument")]
    [InlineData("session")]
    [InlineData("license")]
    [InlineData("stale")]
    public async Task IneligibleConfiguredProvider_IsNotSelected(string mismatch)
    {
        var now = DateTimeOffset.UtcNow;
        var capability = SelectionCapability("provider-a", now);
        capability = mismatch switch
        {
            "venue" => capability with { VenueId = "XNYS" },
            "instrument" => capability with { InstrumentId = "US:MSFT" },
            "session" => capability with { SupportedSession = EquitySessionPhase.PreMarket },
            "license" => capability with { License = EquityLicenseStatus.Restricted },
            _ => capability with { DataAsOfUtc = now - EquityMarketDataStateStore.StaleAfter - TimeSpan.FromTicks(1) }
        };
        var provider = new OfflineProviderStub(capability, AvailableProjection(now));
        var registry = new EquityMarketDataProviderRegistry([provider]);

        var result = await registry.SelectAsync([new("provider-a", 1)], SelectionRequest(now), now);

        Assert.False(result.CanRead);
        Assert.Equal(EquityMarketDataStatus.Unsupported, result.Status);
        Assert.Equal(0, provider.ReadCount);
    }

    [Fact]
    public async Task DuplicateProviderIdentity_IsUnsupported()
    {
        var now = DateTimeOffset.UtcNow;
        var first = SelectionProvider("provider-a", now);
        var second = SelectionProvider("provider-a", now);
        var registry = new EquityMarketDataProviderRegistry([first, second]);

        var result = await registry.SelectAsync([new("provider-a", 1)], SelectionRequest(now), now);

        Assert.False(result.CanRead);
        Assert.Equal(0, first.ReadCount);
        Assert.Equal(0, second.ReadCount);
    }

    [Fact]
    public async Task TiedProviderPriority_IsUnsupportedAndDoesNotRead()
    {
        var now = DateTimeOffset.UtcNow;
        var first = SelectionProvider("provider-a", now);
        var second = SelectionProvider("provider-b", now);
        var registry = new EquityMarketDataProviderRegistry([first, second]);

        var result = await registry.SelectAsync(
            [new("provider-a", 1), new("provider-b", 1)], SelectionRequest(now), now);

        Assert.False(result.CanRead);
        Assert.Equal(0, first.ReadCount + second.ReadCount);
    }

    [Fact]
    public async Task InconsistentProviderDataTimestamps_AreUnsupported()
    {
        var now = DateTimeOffset.UtcNow;
        var first = SelectionProvider("provider-a", now);
        var second = new OfflineProviderStub(
            SelectionCapability("provider-b", now) with { DataAsOfUtc = now.AddSeconds(-1) },
            AvailableProjection(now));
        var registry = new EquityMarketDataProviderRegistry([first, second]);

        var result = await registry.SelectAsync(
            [new("provider-a", 1), new("provider-b", 2)], SelectionRequest(now), now);

        Assert.False(result.CanRead);
        Assert.Equal(EquityMarketDataStatus.Unsupported, result.Status);
        Assert.Equal(0, first.ReadCount + second.ReadCount);
    }

    [Fact]
    public async Task UniquePrioritySelectsOneProviderWithoutMergingOrReading()
    {
        var now = DateTimeOffset.UtcNow;
        var first = SelectionProvider("provider-a", now);
        var second = SelectionProvider("provider-b", now);
        var registry = new EquityMarketDataProviderRegistry([first, second]);

        var result = await registry.SelectAsync(
            [new("provider-a", 20), new("provider-b", 10)], SelectionRequest(now), now);

        Assert.True(result.CanRead);
        Assert.Same(second, result.Provider);
        Assert.Equal(0, first.ReadCount + second.ReadCount);
    }

    [Fact]
    public async Task IngestionCoordinator_AcceptsOneSelectedProviderBatchAtomically()
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var provider = SelectionProvider("licensed-provider", now);
        var selection = Selected(provider, SelectionCapability("licensed-provider", now));
        var store = new EquityMarketDataStateStore();

        var result = await new EquityMarketDataIngestionCoordinator().IngestAsync(
            selection, SelectionRequest(now), store, now);

        Assert.True(result.Accepted);
        Assert.Equal(1, provider.ReadCount);
        Assert.Equal(now, store.Read(now).UpdatedAtUtc);
        Assert.Equal(210.25m, Assert.Single(store.Read(now).Quotes).Last);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("authorization")]
    [InlineData("instrument")]
    [InlineData("venue")]
    [InlineData("session")]
    [InlineData("timestamp")]
    [InlineData("currency")]
    [InlineData("adjustment")]
    public async Task IngestionMismatch_RejectsWholeBatchAndPreservesAuthority(string mismatch)
    {
        var authoritativeAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var candidateAt = authoritativeAt.AddMinutes(1);
        var store = new EquityMarketDataStateStore();
        Assert.True(store.TryPublishAuthoritative(AvailableProjection(authoritativeAt), out _));
        var batch = AvailableProjection(candidateAt);
        var quote = Assert.Single(batch.Quotes);
        batch = mismatch switch
        {
            "provider" => batch with { ProviderId = "other-provider" },
            "authorization" => batch with { License = EquityLicenseStatus.Restricted },
            "instrument" => batch with { Quotes = [quote with { InstrumentId = "US:MSFT" }] },
            "venue" => batch with { Quotes = [quote with { VenueId = "XNYS" }] },
            "session" => batch with { Quotes = [quote with { Session = EquitySessionPhase.PreMarket }] },
            "timestamp" => batch with { Quotes = [quote with { ExchangeTimestampUtc = candidateAt.AddSeconds(-1) }] },
            "currency" => batch with { Quotes = [quote with { Currency = "" }] },
            _ => batch with { Quotes = [quote with { AdjustmentBasis = EquityAdjustmentBasis.Raw }] }
        };
        var capability = SelectionCapability("licensed-provider", candidateAt);
        var provider = new OfflineProviderStub(capability, batch);

        var result = await new EquityMarketDataIngestionCoordinator().IngestAsync(
            Selected(provider, capability), SelectionRequest(candidateAt), store, candidateAt);

        Assert.False(result.Accepted);
        var authoritative = store.Read(candidateAt);
        Assert.Equal(authoritativeAt, authoritative.UpdatedAtUtc);
        Assert.Equal(210.25m, Assert.Single(authoritative.Quotes).Last);
    }

    [Fact]
    public async Task ConcurrentLateOlderBatch_CannotOverwriteNewerAuthority()
    {
        var evaluatedAt = DateTimeOffset.UtcNow;
        var olderAt = evaluatedAt.AddMinutes(-2);
        var newerAt = evaluatedAt.AddMinutes(-1);
        var olderCapability = SelectionCapability("licensed-provider", olderAt);
        var olderProvider = new DeferredReadProvider(olderCapability, AvailableProjection(olderAt));
        var newerProvider = SelectionProvider("licensed-provider", newerAt);
        var store = new EquityMarketDataStateStore();
        var coordinator = new EquityMarketDataIngestionCoordinator();

        var olderTask = coordinator.IngestAsync(
            Selected(olderProvider, olderCapability), SelectionRequest(evaluatedAt), store, evaluatedAt);
        await olderProvider.Started;
        var newerResult = await coordinator.IngestAsync(
            Selected(newerProvider, SelectionCapability("licensed-provider", newerAt)),
            SelectionRequest(evaluatedAt), store, evaluatedAt);
        olderProvider.Release();
        var olderResult = await olderTask;

        Assert.True(newerResult.Accepted);
        Assert.False(olderResult.Accepted);
        Assert.Equal(newerAt, store.Read(evaluatedAt).UpdatedAtUtc);
        Assert.Single(store.Read(evaluatedAt).Quotes);
    }

    [Fact]
    public async Task AcceptedIngestion_AppendsPriceFreeAuditReceipt()
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var ledger = new EquityIngestionAuditLedger();
        var coordinator = new EquityMarketDataIngestionCoordinator(
            ledger, new("equity-config", "v7", "corr-accepted"));
        var provider = SelectionProvider("licensed-provider", now);
        var capability = SelectionCapability("licensed-provider", now);

        var result = await coordinator.IngestAsync(
            Selected(provider, capability), SelectionRequest(now),
            new EquityMarketDataStateStore(), now);

        var receipt = Assert.Single(ledger.Read());
        Assert.True(result.Accepted);
        Assert.Same(receipt, result.Receipt);
        Assert.Equal("equity-config", receipt.ConfigurationId);
        Assert.Equal("v7", receipt.ConfigurationVersion);
        Assert.Equal("corr-accepted", receipt.CorrelationId);
        Assert.Equal(EquityIngestionQualityDecision.Accepted, receipt.QualityDecision);
        Assert.Equal(EquityIngestionReasonCodes.Accepted, receipt.ReasonCode);
        Assert.Equal(64, receipt.BatchHash.Length);
        Assert.Equal(64, receipt.AuthorizationCapabilityHash.Length);
        Assert.Equal(64, receipt.PreviousSnapshotHash.Length);
        Assert.Equal(64, receipt.NewSnapshotHash.Length);
        var propertyNames = typeof(EquityIngestionAuditReceipt).GetProperties().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Price", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Bid", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ask", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Quantity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RejectedIngestion_AppendsReasonWithoutChangingSnapshotHash()
    {
        var authoritativeAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var candidateAt = authoritativeAt.AddMinutes(1);
        var store = new EquityMarketDataStateStore();
        Assert.True(store.TryPublishAuthoritative(AvailableProjection(authoritativeAt), out _));
        var capability = SelectionCapability("licensed-provider", candidateAt);
        var invalid = AvailableProjection(candidateAt) with { License = EquityLicenseStatus.Restricted };
        var provider = new OfflineProviderStub(capability, invalid);
        var ledger = new EquityIngestionAuditLedger();
        var coordinator = new EquityMarketDataIngestionCoordinator(
            ledger, new("equity-config", "v7", "corr-rejected"));

        var result = await coordinator.IngestAsync(
            Selected(provider, capability), SelectionRequest(candidateAt), store, candidateAt);

        var receipt = Assert.Single(ledger.Read());
        Assert.False(result.Accepted);
        Assert.Equal(EquityIngestionQualityDecision.Rejected, receipt.QualityDecision);
        Assert.Equal(EquityIngestionReasonCodes.BatchProvenanceMismatch, receipt.ReasonCode);
        Assert.Equal(receipt.PreviousSnapshotHash, receipt.NewSnapshotHash);
        Assert.Equal(authoritativeAt, store.Read(candidateAt).UpdatedAtUtc);
    }

    [Fact]
    public async Task AuditReceiptChain_VerifiesAcceptedAndRejectedSequence()
    {
        var firstAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var secondAt = firstAt.AddMinutes(1);
        var ledger = new EquityIngestionAuditLedger();
        var coordinator = new EquityMarketDataIngestionCoordinator(
            ledger, new("equity-config", "v7", "corr-chain"));
        var store = new EquityMarketDataStateStore();
        var firstCapability = SelectionCapability("licensed-provider", firstAt);
        await coordinator.IngestAsync(
            Selected(new OfflineProviderStub(firstCapability, AvailableProjection(firstAt)), firstCapability),
            SelectionRequest(secondAt), store, secondAt);
        var rejectedCapability = SelectionCapability("licensed-provider", firstAt);
        await coordinator.IngestAsync(
            Selected(new OfflineProviderStub(rejectedCapability, AvailableProjection(firstAt)), rejectedCapability),
            SelectionRequest(secondAt), store, secondAt);

        var receipts = ledger.Read();
        Assert.Equal(2, receipts.Count);
        Assert.True(EquityIngestionAuditLedger.VerifyChain(receipts));
        Assert.Equal(receipts[0].ReceiptHash, receipts[1].PreviousReceiptHash);
    }

    [Fact]
    public void AuditReceiptChain_DetectsTamperingAndBrokenLink()
    {
        var now = DateTimeOffset.UtcNow;
        var ledger = new EquityIngestionAuditLedger();
        var context = new EquityIngestionAuditContext("config", "v1", "corr");
        var draft = new EquityIngestionAuditDraft(
            now, "provider", "US:AAPL", "XNAS", new string('A', 64), new string('B', 64), now,
            EquityIngestionQualityDecision.Rejected, EquityIngestionReasonCodes.SelectionInvalid,
            new string('C', 64), new string('C', 64));
        ledger.Append(context, draft);
        ledger.Append(context, draft with { RecordedAtUtc = now.AddSeconds(1) });
        var receipts = ledger.Read().ToArray();

        var tampered = receipts.ToArray();
        tampered[0] = tampered[0] with { ReasonCode = "TAMPERED" };
        var broken = receipts.ToArray();
        broken[1] = broken[1] with { PreviousReceiptHash = EquityIngestionAuditLedger.GenesisHash };

        Assert.False(EquityIngestionAuditLedger.VerifyChain(tampered));
        Assert.False(EquityIngestionAuditLedger.VerifyChain(broken));
    }

    [Fact]
    public void BatchHash_IsCanonicalAcrossCollectionOrder()
    {
        var now = DateTimeOffset.UtcNow;
        var first = AvailableProjection(now) with
        {
            CorporateActions =
            [
                new("action-b", "US:AAPL", EquityCorporateActionType.Dividend,
                    DateOnly.FromDateTime(now.UtcDateTime), now, "licensed-provider", CashAmount: 1m, Currency: "USD"),
                new("action-a", "US:AAPL", EquityCorporateActionType.Split,
                    DateOnly.FromDateTime(now.UtcDateTime), now, "licensed-provider", Ratio: 2m)
            ]
        };
        var reordered = first with { CorporateActions = first.CorporateActions.Reverse().ToArray() };

        Assert.Equal(
            EquityIngestionCanonicalHash.HashProjection(first),
            EquityIngestionCanonicalHash.HashProjection(reordered));
    }

    private static EquityMarketDataStateStore AvailableStore(DateTimeOffset now)
    {
        var store = new EquityMarketDataStateStore();
        store.Publish(AvailableProjection(now));
        Assert.Equal(EquityMarketDataStatus.Available, store.Read(now).Status);
        return store;
    }

    private static EquityMarketDataProjection AvailableProjection(DateTimeOffset now) => new(
        EquityMarketDataStatus.Available,
        EquityFreshnessStatus.Fresh,
        EquityQualityStatus.Verified,
        EquityLicenseStatus.Authorized,
        [Quote(now)],
        [new("XNAS", "America/New_York", DateOnly.FromDateTime(now.UtcDateTime),
            EquitySessionPhase.Open, now.AddHours(-1), now.AddHours(5), now)],
        [], [], now, ProviderId: "licensed-provider");

    private static EquityQuote Quote(DateTimeOffset now) =>
        new("US:AAPL", "XNAS", "USD", 210.25m, 210.20m, 210.30m, now,
            "licensed-source", EquityPriceAdjustmentStatus.Adjusted, "licensed-provider",
            now, now, EquitySessionPhase.Open, EquityAdjustmentBasis.SplitAdjusted);

    private static EquityProviderCapability AvailableCapability(DateTimeOffset now) => new(
        new("licensed-provider", "Licensed provider"),
        EquityProviderCapabilityStatus.Available,
        EquityLicenseStatus.Authorized,
        "XNAS",
        EquityProviderCapabilityStatus.Available,
        "US:AAPL",
        EquityProviderCapabilityStatus.Available,
        EquityProviderCapabilityStatus.Available,
        EquityProviderCapabilityStatus.Available,
        EquityProviderCapabilityStatus.Available,
        EquityProviderCapabilityStatus.Available,
        EquityProviderCapabilityStatus.Available,
        now);

    private static EquityProviderCapability SelectionCapability(string providerId, DateTimeOffset now) =>
        AvailableCapability(now) with
        {
            Identity = new(providerId, providerId),
            SupportedSession = EquitySessionPhase.Open,
            DataAsOfUtc = now
        };

    private static OfflineProviderStub SelectionProvider(string providerId, DateTimeOffset now) =>
        new(SelectionCapability(providerId, now), AvailableProjection(now));

    private static EquityProviderCapabilityRequest SelectionRequest(DateTimeOffset now) =>
        new("XNAS", "US:AAPL", now, EquitySessionPhase.Open);

    private static EquityProviderSelectionResult Selected(
        IEquityMarketDataProvider provider,
        EquityProviderCapability capability) => new(
            EquityMarketDataStatus.Available, provider, capability, "selected");

    private static Task<EquityProviderConformanceResult> EvaluateAsync(
        IEquityMarketDataProvider provider,
        DateTimeOffset now) => new EquityMarketDataProviderConformanceHarness().EvaluateAsync(
            provider, new("XNAS", "US:AAPL", now), now);

    private sealed class OfflineProviderStub : IEquityMarketDataProvider
    {
        private readonly EquityProviderCapability _capability;
        private readonly EquityMarketDataProjection? _projection;
        private readonly Exception? _readError;

        public OfflineProviderStub(EquityProviderCapability capability, EquityMarketDataProjection projection)
        {
            _capability = capability;
            _projection = projection;
            Identity = capability.Identity;
        }

        public OfflineProviderStub(EquityProviderCapability capability, Exception readError)
        {
            _capability = capability;
            _readError = readError;
            Identity = capability.Identity;
        }

        public EquityProviderIdentity Identity { get; }
        public int ReadCount { get; private set; }

        public Task<EquityProviderCapability> ProbeCapabilityAsync(
            EquityProviderCapabilityRequest request,
            CancellationToken cancellationToken) => Task.FromResult(_capability);

        public Task<EquityMarketDataProjection> ReadMarketDataAsync(
            EquityMarketDataRequest request,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return _readError is null
                ? Task.FromResult(_projection!)
                : Task.FromException<EquityMarketDataProjection>(_readError);
        }
    }

    private sealed class DeferredReadProvider : IEquityMarketDataProvider
    {
        private readonly EquityProviderCapability _capability;
        private readonly EquityMarketDataProjection _projection;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeferredReadProvider(EquityProviderCapability capability, EquityMarketDataProjection projection)
        {
            _capability = capability;
            _projection = projection;
            Identity = capability.Identity;
        }

        public EquityProviderIdentity Identity { get; }
        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();

        public Task<EquityProviderCapability> ProbeCapabilityAsync(
            EquityProviderCapabilityRequest request,
            CancellationToken cancellationToken) => Task.FromResult(_capability);

        public async Task<EquityMarketDataProjection> ReadMarketDataAsync(
            EquityMarketDataRequest request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return _projection;
        }
    }

    private static void AssertWithheld(EquityMarketDataProjection state, EquityMarketDataStatus expectedStatus)
    {
        Assert.Equal(expectedStatus, state.Status);
        Assert.Empty(state.Quotes);
        Assert.Empty(state.Sessions);
        Assert.Empty(state.Halts);
        Assert.Empty(state.CorporateActions);
    }
}

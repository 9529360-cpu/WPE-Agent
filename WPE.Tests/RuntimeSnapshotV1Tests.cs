using System.Text.Json;
using System.Text.Json.Serialization;
using WpeAgent.RuntimeContracts;
using WpeAgent.RuntimeServices;
using WpeAgent.Equities;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.MarketData;

namespace WPE.Tests;

public sealed class RuntimeSnapshotV1Tests
{
    [Fact]
    public void Create_ProjectsPublicTickerReadModel()
    {
        var now = DateTimeOffset.UtcNow;
        var publicMarket = new PublicMarketState(
            [new("BTCUSDT", 118500.25m, 1.75m, 42000m, "binance-futures-public-websocket", now, false, PublicMarketDataState.Available)],
            [new("BTCUSDT", "1m", 118490.5m, 1.5m, null, "binance-futures-public-rest", now, false, PublicMarketDataState.Available)],
            new(PublicMarketHealthState.Healthy, true, true, now, null));

        var snapshot = RuntimeSnapshotFactory.Create(
            new SystemState { LastUpdated = now.UtcDateTime }, now.UtcDateTime, publicMarketState: publicMarket);

        Assert.Equal(RuntimeCollectionState.Available, snapshot.PublicMarkets.State);
        var ticker = Assert.Single(snapshot.PublicMarkets.Items);
        Assert.Equal("BTCUSDT", ticker.Symbol);
        Assert.Equal(118500.25m, ticker.Price);
        Assert.Equal(1.75m, ticker.ChangePercent);
        Assert.Equal(42000m, ticker.Volume);
        Assert.Equal("binance-futures-public-websocket", ticker.Source);
        Assert.Equal(now, ticker.UpdatedAt);
        Assert.False(ticker.Stale);
        Assert.Equal("Available", ticker.State);
        Assert.Equal(RuntimeCollectionState.Available, snapshot.PublicKlines.State);
        var kline = Assert.Single(snapshot.PublicKlines.Items);
        Assert.Equal("BTCUSDT", kline.Symbol);
        Assert.Equal("1m", kline.Interval);
        Assert.Equal(118490.5m, kline.Close);
        Assert.Null(kline.Volume);
        Assert.Equal("binance-futures-public-rest", kline.Source);
        Assert.Equal(now, kline.UpdatedAt);
        Assert.False(kline.Stale);
        Assert.Equal("Available", kline.State);
    }

    [Fact]
    public void Create_PreservesStaleAndUnknownPublicTickers()
    {
        var now = DateTimeOffset.UtcNow;
        var publicMarket = new PublicMarketState(
            [
                new("BTCUSDT", 118500m, -0.5m, 41000m, "binance-futures-public-rest", now.AddMinutes(-3), true, PublicMarketDataState.Stale),
                new("ETHUSDT", null, null, null, "unknown", null, false, PublicMarketDataState.Unknown)
            ],
            [
                new("BTCUSDT", "1m", 118400m, null, null, "binance-futures-public-rest", now.AddMinutes(-3), true, PublicMarketDataState.Stale),
                new("ETHUSDT", "1m", null, null, null, "unknown", null, false, PublicMarketDataState.Unknown)
            ],
            new(PublicMarketHealthState.Degraded, false, false, now.AddMinutes(-3), "public-market.rest-unavailable"));

        var snapshot = RuntimeSnapshotFactory.Create(
            new SystemState { LastUpdated = now.UtcDateTime }, now.UtcDateTime, publicMarketState: publicMarket);

        Assert.Equal(RuntimeCollectionState.Stale, snapshot.PublicMarkets.State);
        Assert.Equal(2, snapshot.PublicMarkets.Items.Count);
        Assert.True(snapshot.PublicMarkets.Items.Single(x => x.Symbol == "BTCUSDT").Stale);
        Assert.Equal("Unknown", snapshot.PublicMarkets.Items.Single(x => x.Symbol == "ETHUSDT").State);
        Assert.Contains("stale", snapshot.PublicMarkets.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RuntimeCollectionState.Stale, snapshot.PublicKlines.State);
        Assert.Equal(2, snapshot.PublicKlines.Items.Count);
        var staleKline = snapshot.PublicKlines.Items.Single(x => x.Symbol == "BTCUSDT");
        Assert.Equal(118400m, staleKline.Close);
        Assert.Null(staleKline.Volume);
        Assert.True(staleKline.Stale);
        var unknownKline = snapshot.PublicKlines.Items.Single(x => x.Symbol == "ETHUSDT");
        Assert.Null(unknownKline.Close);
        Assert.Null(unknownKline.Volume);
        Assert.Null(unknownKline.UpdatedAt);
        Assert.Equal("Unknown", unknownKline.State);
    }

    [Fact]
    public void Create_ProducesVersionedFreshSnapshot()
    {
        var now = DateTime.UtcNow;
        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now.AddSeconds(-5), RuntimeEventSequence = 42, WalletBalance = 1200m }, now);
        Assert.Equal("1.0", snapshot.ContractVersion);
        Assert.True(snapshot.Freshness.Fresh);
        Assert.Equal(42, snapshot.EventSequence);
        Assert.Equal(1200m, snapshot.Account.Value!.WalletBalance);
        Assert.Equal(RuntimeCollectionState.Unsupported, snapshot.Positions.State);
    }

    [Fact]
    public void Create_LabelsStaleDataWithoutInventingCollections()
    {
        var now = DateTime.UtcNow;
        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now.AddMinutes(-1) }, now);
        Assert.False(snapshot.Freshness.Fresh);
        Assert.Equal(RuntimeCollectionState.Stale, snapshot.Risk.State);
        Assert.Empty(snapshot.Orders.Items);
        Assert.Equal(RuntimeCollectionState.Unsupported, snapshot.Backtests.State);
    }

    [Fact]
    public void Serialize_UsesStableContractAndLegacyFields()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(RuntimeSnapshotFactory.Create(new SystemState(), DateTime.UtcNow), options));
        Assert.Equal("1.0", doc.RootElement.GetProperty("contractVersion").GetString());
        Assert.Equal("unsupported", doc.RootElement.GetProperty("positions").GetProperty("state").GetString());
        Assert.True(doc.RootElement.TryGetProperty("walletBalance", out _));
    }

    [Fact]
    public void Create_RoundTripsConfiguredSolCapabilityOnly()
    {
        var now = DateTime.UtcNow;
        var store = new RuntimeMarketStateStore();
        store.Publish(new Dictionary<string, ExchangeCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["SOLUSDT"] = new("binance", "binance-testnet", "SOLUSDT", "SOLUSDT", MarketType.Perpetual, CapabilityStatus.Available, true, true, true, now)
        });
        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, store.Read());
        Assert.Equal(RuntimeCollectionState.Available, snapshot.Markets.State);
        Assert.Single(snapshot.Markets.Items);
        Assert.Equal("SOLUSDT", snapshot.Markets.Items[0].Symbol);
        Assert.Single(snapshot.Capabilities.Items);
    }

    [Fact]
    public void Create_KeepsCapabilitySourceAvailableInsideItsRefreshWindow()
    {
        var now = DateTime.UtcNow;
        var store = new RuntimeMarketStateStore();
        store.Publish(new Dictionary<string, ExchangeCapability>
        {
            ["SOLUSDT"] = new("binance", "binance-testnet", "SOLUSDT", "SOLUSDT", MarketType.Perpetual, CapabilityStatus.Available, true, true, true, now.AddMinutes(-1))
        });
        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, store.Read());
        Assert.Equal(RuntimeCollectionState.Available, snapshot.Markets.State);
        Assert.Single(snapshot.Markets.Items);
        Assert.Single(snapshot.Capabilities.Items);
    }

    [Fact]
    public void Create_LabelsExpiredCapabilitySourceStaleAndDoesNotInventMarkets()
    {
        var now = DateTime.UtcNow;
        var store = new RuntimeMarketStateStore();
        store.Publish(new Dictionary<string, ExchangeCapability>
        {
            ["SOLUSDT"] = new("binance", "binance-testnet", "SOLUSDT", "SOLUSDT", MarketType.Perpetual, CapabilityStatus.Available, true, true, true, now - RuntimeMarketStateStore.StaleAfter - TimeSpan.FromSeconds(1))
        });

        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, store.Read());

        Assert.Equal(RuntimeCollectionState.Stale, snapshot.Markets.State);
        Assert.Empty(snapshot.Markets.Items);
        Assert.Empty(snapshot.Capabilities.Items);
    }

    [Fact]
    public void Create_ExposesUnsupportedCapabilityWithoutMarketEntry()
    {
        var now = DateTime.UtcNow;
        var store = new RuntimeMarketStateStore();
        store.Publish(new Dictionary<string, ExchangeCapability>
        {
            ["SOLUSDT"] = new("binance", "binance-testnet", "SOLUSDT", "SOLUSDT", MarketType.Perpetual, CapabilityStatus.Unsupported, true, false, true, now, "symbol unsupported")
        });
        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, store.Read());
        Assert.Equal(RuntimeCollectionState.Unsupported, snapshot.Markets.State);
        Assert.Empty(snapshot.Markets.Items);
        Assert.Single(snapshot.Capabilities.Items);
        Assert.Equal(CapabilityStatus.Unsupported, snapshot.Capabilities.Items[0].Status);
    }

    [Fact]
    public void Create_RoundTripsProviderNeutralSolPositionAndOrder()
    {
        var now = DateTime.UtcNow;
        var store = new RuntimeTradingStateStore();
        store.Publish(
            [new ManagedPosition("SOLUSDT", PositionSide.Long, 2.5m, 145.25m, 147m, 4.375m, 5m, true, 118m)],
            [new ExchangeOrder("SOLUSDT", "order-42", "client-42", "NEW", 1.25m, 146.5m, "LIMIT", PositionSide.Long, false, now)],
            now);

        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, tradingState: store.Read());

        Assert.Equal(RuntimeCollectionState.Available, snapshot.Positions.State);
        var position = Assert.Single(snapshot.Positions.Items);
        Assert.Equal("SOLUSDT", position.Symbol);
        Assert.Equal("Long", position.Side);
        Assert.Equal(2.5m, position.Quantity);
        Assert.Equal(145.25m, position.EntryPrice);
        Assert.Equal(4.375m, position.UnrealizedPnl);
        var order = Assert.Single(snapshot.Orders.Items);
        Assert.Equal("order-42", order.OrderId);
        Assert.Equal("SOLUSDT", order.Symbol);
        Assert.Equal("Long", order.Side);
        Assert.Equal("LIMIT", order.Type);
        Assert.Equal("NEW", order.Status);
        Assert.Equal(1.25m, order.Quantity);
        Assert.Equal(146.5m, order.Price);
    }

    [Fact]
    public void Create_TradingCollectionsDefaultToUnsupported()
    {
        var now = DateTime.UtcNow;
        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now);

        Assert.Equal(RuntimeCollectionState.Unsupported, snapshot.Positions.State);
        Assert.Equal(RuntimeCollectionState.Unsupported, snapshot.Orders.State);
        Assert.Empty(snapshot.Positions.Items);
        Assert.Empty(snapshot.Orders.Items);
    }

    [Fact]
    public void Create_KeepsTradingObservationAvailableInsideItsRefreshWindow()
    {
        var now = DateTime.UtcNow;
        var store = new RuntimeTradingStateStore();
        store.Publish(
            [new ManagedPosition("SOLUSDT", PositionSide.Short, 1m, 150m, 149m, 1m, 3m, false, 200m)],
            [],
            now.AddMinutes(-1));

        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, tradingState: store.Read());

        Assert.Equal(RuntimeCollectionState.Available, snapshot.Positions.State);
        Assert.Equal(RuntimeCollectionState.Available, snapshot.Orders.State);
        Assert.Single(snapshot.Positions.Items);
        Assert.Empty(snapshot.Orders.Items);
    }

    [Fact]
    public void Create_LabelsExpiredTradingObservationStaleWithoutServingItems()
    {
        var now = DateTime.UtcNow;
        var store = new RuntimeTradingStateStore();
        store.Publish(
            [new ManagedPosition("SOLUSDT", PositionSide.Short, 1m, 150m, 149m, 1m, 3m, false, 200m)],
            [new ExchangeOrder("SOLUSDT", "order-42", "client-42", "NEW", 1m, 149m, "LIMIT", PositionSide.Short, false, now)],
            now - RuntimeTradingStateStore.StaleAfter - TimeSpan.FromSeconds(1));

        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, tradingState: store.Read());

        Assert.Equal(RuntimeCollectionState.Stale, snapshot.Positions.State);
        Assert.Equal(RuntimeCollectionState.Stale, snapshot.Orders.State);
        Assert.Empty(snapshot.Positions.Items);
        Assert.Empty(snapshot.Orders.Items);
    }

    [Fact]
    public void Create_ExposesTradingObservationErrorWithoutItems()
    {
        var now = DateTime.UtcNow;
        var store = new RuntimeTradingStateStore();
        store.PublishError("Provider position/order observation failed.");

        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now }, now, tradingState: store.Read());

        Assert.Equal(RuntimeCollectionState.Error, snapshot.Positions.State);
        Assert.Equal(RuntimeCollectionState.Error, snapshot.Orders.State);
        Assert.Contains("Trading observation failed.", snapshot.Positions.Message);
        Assert.Contains("WPE-", snapshot.Positions.Message);
        Assert.Contains("UTC", snapshot.Positions.Message);
        Assert.DoesNotContain("Provider position/order observation failed.", snapshot.Positions.Message);
        Assert.Empty(snapshot.Positions.Items);
        Assert.Empty(snapshot.Orders.Items);
    }

    [Fact]
    public void Create_ProjectsOnlyAuthorizedAvailableEquityQuotes()
    {
        var now = DateTimeOffset.UtcNow;
        var projection = new EquityMarketDataProjection(
            EquityMarketDataStatus.Available, EquityFreshnessStatus.Fresh, EquityQualityStatus.Verified,
            EquityLicenseStatus.Authorized,
            [new("US:AAPL", "XNAS", "USD", 190.25m, 190.20m, 190.30m, now, "licensed-feed")],
            [new("XNAS", "America/New_York", DateOnly.FromDateTime(now.UtcDateTime), EquitySessionPhase.Open, null, null, now)],
            [], [], now);

        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now.UtcDateTime }, now.UtcDateTime, equityMarkets: projection);

        Assert.Equal(RuntimeCollectionState.Available, snapshot.EquityMarkets.State);
        var item = Assert.Single(snapshot.EquityMarkets.Items);
        Assert.Equal("US:AAPL", item.InstrumentId);
        Assert.Equal("licensed-feed", item.ProviderId);
        Assert.Equal("Open", item.SessionState);
        Assert.Equal(RuntimeCollectionState.Unsupported, snapshot.EquityBroker.State);
        Assert.Null(snapshot.EquityBroker.Value);
    }

    [Theory]
    [InlineData(EquityMarketDataStatus.Unsupported)]
    [InlineData(EquityMarketDataStatus.Stale)]
    [InlineData(EquityMarketDataStatus.Error)]
    public void Create_WithholdsNonAvailableEquityQuotes(EquityMarketDataStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var projection = new EquityMarketDataProjection(
            status, EquityFreshnessStatus.Fresh, EquityQualityStatus.Verified, EquityLicenseStatus.Authorized,
            [new("US:AAPL", "XNAS", "USD", 190.25m, null, null, now, "licensed-feed")], [], [], [], now, "not available");

        var snapshot = RuntimeSnapshotFactory.Create(new SystemState { LastUpdated = now.UtcDateTime }, now.UtcDateTime, equityMarkets: projection);

        Assert.Equal(status.ToString(), snapshot.EquityMarkets.State.ToString());
        Assert.Empty(snapshot.EquityMarkets.Items);
    }
}

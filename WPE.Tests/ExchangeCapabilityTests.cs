using WpeAgent.RuntimeContracts;

namespace WPE.Tests;

public sealed class ExchangeCapabilityTests
{
    private static readonly Instrument Instrument = new("BTC-USD", "BTCUSDT", "binance", "binance-futures", MarketType.Perpetual);
    private static ExchangeCapability Capability(CapabilityStatus status = CapabilityStatus.Available, bool canTrade = true, bool testnet = true)
        => new("binance", "binance-futures", "BTC-USD", "BTCUSDT", MarketType.Perpetual, status, true, canTrade, testnet, DateTimeOffset.UtcNow, status == CapabilityStatus.Error ? "probe failed" : null);

    [Fact]
    public void UnknownCapabilityFailsClosed()
        => Assert.False(new ProviderCapabilityPrecondition().Check(Instrument, null, true).Allowed);

    [Fact]
    public void UnsupportedInstrumentFailsClosed()
        => Assert.False(new ProviderCapabilityPrecondition().Check(Instrument, Capability() with { NativeSymbol = "DOGEUSDT" }, true).Allowed);

    [Fact]
    public void TestnetMismatchRejects()
        => Assert.False(new ProviderCapabilityPrecondition().Check(Instrument, Capability(testnet: false), true).Allowed);

    [Theory]
    [InlineData(CapabilityStatus.Stale)]
    [InlineData(CapabilityStatus.Error)]
    public void StaleOrErrorCapabilityCannotTrade(CapabilityStatus status)
        => Assert.False(new ProviderCapabilityPrecondition().Check(Instrument, Capability(status), true).Allowed);

    [Fact]
    public void ReadOnlyCapabilityIsNotTradeable()
        => Assert.False(new ProviderCapabilityPrecondition().Check(Instrument, Capability(canTrade: false), true).Allowed);

    [Fact]
    public void AvailableMatchingCapabilityAllowsTestnetTrade()
        => Assert.True(new ProviderCapabilityPrecondition().Check(Instrument, Capability(), true).Allowed);

    [Fact]
    public void ExpiredAvailableCapabilityFailsClosed()
        => Assert.False(new ProviderCapabilityPrecondition().Check(
            Instrument,Capability() with { CheckedAt = DateTimeOffset.UtcNow-ProviderCapabilityPrecondition.MaximumAge-TimeSpan.FromSeconds(1) },true).Allowed);
}

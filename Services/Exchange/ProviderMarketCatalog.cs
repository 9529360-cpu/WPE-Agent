using System.Text.Json;

namespace WpeAgent.RuntimeContracts;

public enum ProviderCatalogState { Available, Unsupported, Error, Stale }

public sealed record ProviderMarketCatalogEntry(
    Instrument Instrument,
    CapabilityStatus Status,
    bool CanRead,
    bool CanTrade,
    bool TestnetAvailable,
    DateTimeOffset CheckedAt,
    string? Failure = null);

public sealed record ProviderMarketCatalog(
    ProviderCatalogState State,
    IReadOnlyList<ProviderMarketCatalogEntry> Markets,
    DateTimeOffset CheckedAt,
    string? Failure = null);

public interface IProviderMarketCatalog
{
    Task<ProviderMarketCatalog> DiscoverMarketCatalogAsync(CancellationToken ct);
}

public static class BinanceMarketCatalogParser
{
    public static IReadOnlyList<ProviderMarketCatalogEntry> Parse(JsonElement root, string exchangeId, string providerId, Func<string, string> toCanonical, bool supportsTestnet, bool testnet, DateTimeOffset checkedAt)
    {
        if (!root.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Binance exchangeInfo symbols are missing");
        var result = new List<ProviderMarketCatalogEntry>();
        foreach (var item in symbols.EnumerateArray())
        {
            var native = item.TryGetProperty("symbol", out var symbol) ? symbol.GetString() : null;
            var status = item.TryGetProperty("status", out var state) ? state.GetString() : null;
            var contract = item.TryGetProperty("contractType", out var type) ? type.GetString() : null;
            if (string.IsNullOrWhiteSpace(native) || !string.Equals(status, "TRADING", StringComparison.OrdinalIgnoreCase) || !string.Equals(contract, "PERPETUAL", StringComparison.OrdinalIgnoreCase)) continue;
            string canonical;
            try { canonical = toCanonical(native); } catch (ArgumentException) { continue; }
            if (string.IsNullOrWhiteSpace(canonical)) continue;
            var instrument = new Instrument(canonical, native.ToUpperInvariant(), exchangeId, providerId, MarketType.Perpetual);
            result.Add(new ProviderMarketCatalogEntry(instrument, CapabilityStatus.Available, true, true, supportsTestnet && testnet, checkedAt));
        }
        return result;
    }
}

public static class OkxMarketCatalogParser
{
    public static IReadOnlyList<ProviderMarketCatalogEntry> Parse(JsonElement root, string exchangeId, string providerId, Func<string, string> toCanonical, bool supportsTestnet, bool testnet, DateTimeOffset checkedAt)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OKX public instruments data are missing");

        var result = new List<ProviderMarketCatalogEntry>();
        foreach (var item in data.EnumerateArray())
        {
            var native = item.TryGetProperty("instId", out var instrumentId) ? instrumentId.GetString() : null;
            var instrumentType = item.TryGetProperty("instType", out var type) ? type.GetString() : null;
            var contractType = item.TryGetProperty("ctType", out var contract) ? contract.GetString() : null;
            var settlementCurrency = item.TryGetProperty("settleCcy", out var settlement) ? settlement.GetString() : null;
            var state = item.TryGetProperty("state", out var status) ? status.GetString() : null;
            if (string.IsNullOrWhiteSpace(native) ||
                !string.Equals(instrumentType, "SWAP", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(contractType, "linear", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(settlementCurrency, "USDT", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(state, "live", StringComparison.OrdinalIgnoreCase)) continue;

            string canonical;
            try { canonical = toCanonical(native); } catch (ArgumentException) { continue; }
            if (string.IsNullOrWhiteSpace(canonical)) continue;
            var instrument = new Instrument(canonical, native.ToUpperInvariant(), exchangeId, providerId, MarketType.Perpetual);
            result.Add(new ProviderMarketCatalogEntry(instrument, CapabilityStatus.Available, true, true, supportsTestnet && testnet, checkedAt));
        }
        return result;
    }
}

public static class BybitMarketCatalogParser
{
    public static IReadOnlyList<ProviderMarketCatalogEntry> Parse(JsonElement root, string exchangeId, string providerId, Func<string, string> toCanonical, bool supportsTestnet, bool testnet, DateTimeOffset checkedAt)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("list", out var instruments) || instruments.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Bybit instruments result list is missing");

        var markets = new List<ProviderMarketCatalogEntry>();
        foreach (var item in instruments.EnumerateArray())
        {
            var native = item.TryGetProperty("symbol", out var symbol) ? symbol.GetString() : null;
            var contractType = item.TryGetProperty("contractType", out var contract) ? contract.GetString() : null;
            var settleCoin = item.TryGetProperty("settleCoin", out var settlement) ? settlement.GetString() : null;
            var status = item.TryGetProperty("status", out var state) ? state.GetString() : null;
            if (string.IsNullOrWhiteSpace(native) ||
                !string.Equals(contractType, "LinearPerpetual", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(settleCoin, "USDT", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(status, "Trading", StringComparison.OrdinalIgnoreCase)) continue;

            string canonical;
            try { canonical = toCanonical(native); } catch (ArgumentException) { continue; }
            if (string.IsNullOrWhiteSpace(canonical)) continue;
            var instrument = new Instrument(canonical, native.ToUpperInvariant(), exchangeId, providerId, MarketType.Perpetual);
            markets.Add(new ProviderMarketCatalogEntry(instrument, CapabilityStatus.Available, true, true, supportsTestnet && testnet, checkedAt));
        }
        return markets;
    }
}

public static class GateMarketCatalogParser
{
    public static IReadOnlyList<ProviderMarketCatalogEntry> Parse(JsonElement root, string exchangeId, string providerId, Func<string, string> toCanonical, bool supportsTestnet, bool testnet, DateTimeOffset checkedAt)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Gate futures contracts payload is not an array");

        var markets = new List<ProviderMarketCatalogEntry>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("in_delisting", out var inDelisting) ||
                inDelisting.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("Gate futures contract schema is invalid");

            var native = name.GetString();
            if (string.IsNullOrWhiteSpace(native) ||
                !string.Equals(status.GetString(), "trading", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(type.GetString(), "direct", StringComparison.OrdinalIgnoreCase) ||
                inDelisting.GetBoolean() ||
                !IsUsdtContract(native)) continue;

            string canonical;
            try { canonical = toCanonical(native); } catch (ArgumentException) { continue; }
            var expectedCanonical = native[..^5] + "USDT";
            if (!string.Equals(canonical, expectedCanonical, StringComparison.OrdinalIgnoreCase)) continue;

            var instrument = new Instrument(canonical.ToUpperInvariant(), native.ToUpperInvariant(), exchangeId, providerId, MarketType.Perpetual);
            markets.Add(new ProviderMarketCatalogEntry(instrument, CapabilityStatus.Available, true, true, supportsTestnet && testnet, checkedAt));
        }
        return markets;
    }

    private static bool IsUsdtContract(string native)
    {
        if (!native.EndsWith("_USDT", StringComparison.OrdinalIgnoreCase) || native.Length <= 5) return false;
        return native[..^5].All(char.IsLetterOrDigit);
    }
}

public static class BitgetMarketCatalogParser
{
    public static IReadOnlyList<ProviderMarketCatalogEntry> Parse(JsonElement root, string exchangeId, string providerId, Func<string, string> toCanonical, bool supportsTestnet, bool testnet, DateTimeOffset checkedAt)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Bitget futures contracts data are missing");

        var markets = new List<ProviderMarketCatalogEntry>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryString(item, "symbol", out var native) ||
                !TryString(item, "baseCoin", out var baseCoin) ||
                !TryString(item, "quoteCoin", out var quoteCoin) ||
                !TryString(item, "symbolType", out var symbolType) ||
                !TryString(item, "symbolStatus", out var symbolStatus))
                throw new InvalidOperationException("Bitget futures contract schema is invalid");

            if (!string.Equals(quoteCoin, "USDT", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(symbolType, "perpetual", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(symbolStatus, "normal", StringComparison.OrdinalIgnoreCase) ||
                !IsAsciiAlphaNumeric(baseCoin) ||
                !string.Equals(native, baseCoin + quoteCoin, StringComparison.OrdinalIgnoreCase)) continue;

            string canonical;
            try { canonical = toCanonical(native); } catch (ArgumentException) { continue; }
            if (!string.Equals(canonical, native, StringComparison.OrdinalIgnoreCase)) continue;

            var instrument = new Instrument(canonical.ToUpperInvariant(), native.ToUpperInvariant(), exchangeId, providerId, MarketType.Perpetual);
            markets.Add(new ProviderMarketCatalogEntry(instrument, CapabilityStatus.Available, true, true, supportsTestnet && testnet, checkedAt));
        }
        return markets;
    }

    private static bool TryString(JsonElement item, string name, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsAsciiAlphaNumeric(string value)=>value.Length>0&&value.All(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');
}

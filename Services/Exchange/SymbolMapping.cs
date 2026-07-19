namespace 币安量化机器人.Services.Exchange;

public interface ISymbolMapper
{
    string ToNative(string canonicalSymbol);
    string ToCanonical(string nativeSymbol);
}

public sealed class ConventionSymbolMapper(string providerId,IReadOnlyDictionary<string,string>? overrides=null):ISymbolMapper
{
    private readonly Dictionary<string,string> _toNative=(overrides??new Dictionary<string,string>()).ToDictionary(x=>Canonicalize(x.Key),x=>x.Value.Trim().ToUpperInvariant(),StringComparer.OrdinalIgnoreCase);
    private readonly string _provider=providerId.ToLowerInvariant();
    public string ToNative(string canonicalSymbol)
    {
        var canonical=Canonicalize(canonicalSymbol);if(_toNative.TryGetValue(canonical,out var mapped))return mapped;
        return _provider switch{"okx" or "okx-swap"=>canonical.EndsWith("USDT")?$"{canonical[..^4]}-USDT-SWAP":canonical,"coinbase"=>canonical.EndsWith("USDT")?$"{canonical[..^4]}-USDT":canonical,_=>canonical};
    }
    public string ToCanonical(string nativeSymbol)
    {
        var native=nativeSymbol.Trim().ToUpperInvariant();var explicitPair=_toNative.FirstOrDefault(x=>x.Value.Equals(native,StringComparison.OrdinalIgnoreCase));if(!string.IsNullOrWhiteSpace(explicitPair.Key))return explicitPair.Key;
        return Canonicalize(native.Replace("-SWAP","",StringComparison.OrdinalIgnoreCase).Replace("PERP","",StringComparison.OrdinalIgnoreCase));
    }
    public static string Canonicalize(string value)
    {
        var normalized=new string(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace("XBT","BTC",StringComparison.Ordinal);
        if(normalized.EndsWith("USDTPERP",StringComparison.Ordinal))normalized=normalized[..^4];
        if(normalized.Length<6||normalized.Length>24)throw new ArgumentException("Invalid trading symbol",nameof(value));return normalized;
    }
}

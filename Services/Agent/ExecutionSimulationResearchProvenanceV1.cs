using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionSimulationResearchProvenanceV1(
    string Schema,
    string CorrelationId,
    string ClientOrderId,
    string StrategyId,
    string StrategyVersion,
    string CostModelVersion,
    string SimulationModelVersion,
    string VenueRuleVersion,
    string Symbol,
    string SimulatedFillCanonicalSha256,
    string BacktestValidationSha256,
    string TimelineSha256,
    string ShadowObservationSha256,
    string MarketProvenanceSha256,
    DateTimeOffset TimelineLastTradableAtUtc,
    DateTimeOffset ValidationAtUtc,
    DateTimeOffset ShadowObservedAtUtc,
    DateTimeOffset MarketAsOfUtc,
    DateTimeOffset SimulatedAtUtc,
    DateTimeOffset BoundAtUtc,
    byte[] CanonicalBytes,
    string CanonicalSha256);

public static class ExecutionSimulationResearchProvenanceCanonicalizerV1
{
    public const string Schema = "wpe.execution-simulation-research-provenance/1.0";

    public static ExecutionSimulationResearchProvenanceV1 Create(
        ExecutionSimulationFillV1 simulatedFill,
        string backtestValidationSha256,
        string timelineSha256,
        string shadowObservationSha256,
        string marketProvenanceSha256,
        DateTimeOffset timelineLastTradableAtUtc,
        DateTimeOffset validationAtUtc,
        DateTimeOffset shadowObservedAtUtc,
        DateTimeOffset boundAtUtc)
    {
        ArgumentNullException.ThrowIfNull(simulatedFill);
        if (!ExecutionSimulationFillCanonicalizerV1.IsCanonical(simulatedFill))
            throw new InvalidOperationException("Simulated fill must be canonical before research provenance can be bound.");

        Validate(
            simulatedFill.CorrelationId,
            simulatedFill.ClientOrderId,
            simulatedFill.StrategyId,
            simulatedFill.StrategyVersion,
            simulatedFill.CostModelVersion,
            simulatedFill.SimulationModelVersion,
            simulatedFill.VenueRuleVersion,
            simulatedFill.Symbol,
            simulatedFill.CanonicalSha256,
            backtestValidationSha256,
            timelineSha256,
            shadowObservationSha256,
            marketProvenanceSha256,
            timelineLastTradableAtUtc,
            validationAtUtc,
            shadowObservedAtUtc,
            simulatedFill.MarketAsOfUtc,
            simulatedFill.SimulatedAtUtc,
            boundAtUtc);

        var draft = new ExecutionSimulationResearchProvenanceV1(
            Schema,
            simulatedFill.CorrelationId,
            simulatedFill.ClientOrderId,
            simulatedFill.StrategyId,
            simulatedFill.StrategyVersion,
            simulatedFill.CostModelVersion,
            simulatedFill.SimulationModelVersion,
            simulatedFill.VenueRuleVersion,
            simulatedFill.Symbol,
            simulatedFill.CanonicalSha256,
            backtestValidationSha256,
            timelineSha256,
            shadowObservationSha256,
            marketProvenanceSha256,
            timelineLastTradableAtUtc.ToUniversalTime(),
            validationAtUtc.ToUniversalTime(),
            shadowObservedAtUtc.ToUniversalTime(),
            simulatedFill.MarketAsOfUtc.ToUniversalTime(),
            simulatedFill.SimulatedAtUtc.ToUniversalTime(),
            boundAtUtc.ToUniversalTime(),
            Array.Empty<byte>(),
            string.Empty);

        var bytes = Serialize(draft);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return draft with { CanonicalBytes = bytes, CanonicalSha256 = hash };
    }

    public static bool IsCanonical(ExecutionSimulationResearchProvenanceV1 value)
    {
        if (value is null
            || value.Schema != Schema
            || value.CanonicalBytes.Length == 0
            || !IsLowerSha256(value.CanonicalSha256))
            return false;

        try
        {
            Validate(
                value.CorrelationId,
                value.ClientOrderId,
                value.StrategyId,
                value.StrategyVersion,
                value.CostModelVersion,
                value.SimulationModelVersion,
                value.VenueRuleVersion,
                value.Symbol,
                value.SimulatedFillCanonicalSha256,
                value.BacktestValidationSha256,
                value.TimelineSha256,
                value.ShadowObservationSha256,
                value.MarketProvenanceSha256,
                value.TimelineLastTradableAtUtc,
                value.ValidationAtUtc,
                value.ShadowObservedAtUtc,
                value.MarketAsOfUtc,
                value.SimulatedAtUtc,
                value.BoundAtUtc);
        }
        catch
        {
            return false;
        }

        var bytes = Serialize(value);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(hash, value.CanonicalSha256, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(bytes, value.CanonicalBytes);
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> canonicalBytes,
        string canonicalSha256,
        out ExecutionSimulationResearchProvenanceV1? value)
    {
        value = null;
        if (canonicalBytes.Length == 0 || !IsLowerSha256(canonicalSha256))
            return false;

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
            if (!string.Equals(hash, canonicalSha256, StringComparison.Ordinal))
                return false;

            using var document = JsonDocument.Parse(canonicalBytes.ToArray());
            var root = document.RootElement;
            value = new(
                root.GetProperty("schema").GetString() ?? string.Empty,
                root.GetProperty("correlation_id").GetString() ?? string.Empty,
                root.GetProperty("client_order_id").GetString() ?? string.Empty,
                root.GetProperty("strategy_id").GetString() ?? string.Empty,
                root.GetProperty("strategy_version").GetString() ?? string.Empty,
                root.GetProperty("cost_model_version").GetString() ?? string.Empty,
                root.GetProperty("simulation_model_version").GetString() ?? string.Empty,
                root.GetProperty("venue_rule_version").GetString() ?? string.Empty,
                root.GetProperty("symbol").GetString() ?? string.Empty,
                root.GetProperty("simulated_fill_sha256").GetString() ?? string.Empty,
                root.GetProperty("backtest_validation_sha256").GetString() ?? string.Empty,
                root.GetProperty("timeline_sha256").GetString() ?? string.Empty,
                root.GetProperty("shadow_observation_sha256").GetString() ?? string.Empty,
                root.GetProperty("market_provenance_sha256").GetString() ?? string.Empty,
                ParseUtc(root, "timeline_last_tradable_at_utc"),
                ParseUtc(root, "validation_at_utc"),
                ParseUtc(root, "shadow_observed_at_utc"),
                ParseUtc(root, "market_as_of_utc"),
                ParseUtc(root, "simulated_at_utc"),
                ParseUtc(root, "bound_at_utc"),
                canonicalBytes.ToArray(),
                canonicalSha256);

            if (IsCanonical(value))
                return true;

            value = null;
            return false;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static void Validate(
        string correlationId,
        string clientOrderId,
        string strategyId,
        string strategyVersion,
        string costModelVersion,
        string simulationModelVersion,
        string venueRuleVersion,
        string symbol,
        string simulatedFillSha256,
        string backtestValidationSha256,
        string timelineSha256,
        string shadowObservationSha256,
        string marketProvenanceSha256,
        DateTimeOffset timelineLastTradableAtUtc,
        DateTimeOffset validationAtUtc,
        DateTimeOffset shadowObservedAtUtc,
        DateTimeOffset marketAsOfUtc,
        DateTimeOffset simulatedAtUtc,
        DateTimeOffset boundAtUtc)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("Correlation id is required.");
        if (string.IsNullOrWhiteSpace(clientOrderId)) throw new ArgumentException("Client order id is required.");
        if (string.IsNullOrWhiteSpace(strategyId)) throw new ArgumentException("Strategy id is required.");
        if (string.IsNullOrWhiteSpace(strategyVersion)) throw new ArgumentException("Strategy version is required.");
        if (string.IsNullOrWhiteSpace(costModelVersion)) throw new ArgumentException("Cost-model version is required.");
        if (string.IsNullOrWhiteSpace(simulationModelVersion)) throw new ArgumentException("Simulation-model version is required.");
        if (string.IsNullOrWhiteSpace(venueRuleVersion)) throw new ArgumentException("Venue-rule version is required.");
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol is required.");
        if (!IsLowerSha256(simulatedFillSha256)
            || !IsLowerSha256(backtestValidationSha256)
            || !IsLowerSha256(timelineSha256)
            || !IsLowerSha256(shadowObservationSha256)
            || !IsLowerSha256(marketProvenanceSha256))
            throw new InvalidOperationException("Simulation research provenance hashes must be lowercase SHA-256 values.");

        foreach (var time in new[]
        {
            timelineLastTradableAtUtc,
            validationAtUtc,
            shadowObservedAtUtc,
            marketAsOfUtc,
            simulatedAtUtc,
            boundAtUtc
        })
        {
            if (time == default || time.Offset != TimeSpan.Zero)
                throw new InvalidOperationException("Simulation research provenance timestamps must be UTC.");
        }

        if (timelineLastTradableAtUtc > validationAtUtc)
            throw new InvalidOperationException("Historical timeline cannot extend past validation.");
        if (validationAtUtc > shadowObservedAtUtc)
            throw new InvalidOperationException("Shadow evidence cannot predate validation.");
        if (shadowObservedAtUtc > marketAsOfUtc)
            throw new InvalidOperationException("Simulation market evidence cannot predate the bound Shadow observation.");
        if (marketAsOfUtc > simulatedAtUtc)
            throw new InvalidOperationException("Simulation cannot use future market evidence.");
        if (simulatedAtUtc > boundAtUtc)
            throw new InvalidOperationException("Research provenance cannot be bound before the simulated fill exists.");
    }

    private static DateTimeOffset ParseUtc(JsonElement root, string name) =>
        DateTimeOffset.Parse(
            root.GetProperty(name).GetString() ?? string.Empty,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] Serialize(ExecutionSimulationResearchProvenanceV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("backtest_validation_sha256", value.BacktestValidationSha256);
            writer.WriteString("bound_at_utc", value.BoundAtUtc.ToUniversalTime());
            writer.WriteString("client_order_id", value.ClientOrderId);
            writer.WriteString("correlation_id", value.CorrelationId);
            writer.WriteString("cost_model_version", value.CostModelVersion);
            writer.WriteString("market_as_of_utc", value.MarketAsOfUtc.ToUniversalTime());
            writer.WriteString("market_provenance_sha256", value.MarketProvenanceSha256);
            writer.WriteString("schema", value.Schema);
            writer.WriteString("shadow_observation_sha256", value.ShadowObservationSha256);
            writer.WriteString("shadow_observed_at_utc", value.ShadowObservedAtUtc.ToUniversalTime());
            writer.WriteString("simulated_at_utc", value.SimulatedAtUtc.ToUniversalTime());
            writer.WriteString("simulated_fill_sha256", value.SimulatedFillCanonicalSha256);
            writer.WriteString("simulation_model_version", value.SimulationModelVersion);
            writer.WriteString("strategy_id", value.StrategyId);
            writer.WriteString("strategy_version", value.StrategyVersion);
            writer.WriteString("symbol", value.Symbol);
            writer.WriteString("timeline_last_tradable_at_utc", value.TimelineLastTradableAtUtc.ToUniversalTime());
            writer.WriteString("timeline_sha256", value.TimelineSha256);
            writer.WriteString("validation_at_utc", value.ValidationAtUtc.ToUniversalTime());
            writer.WriteString("venue_rule_version", value.VenueRuleVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

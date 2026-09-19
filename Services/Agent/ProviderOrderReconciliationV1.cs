using 币安量化机器人.Services.Exchange;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace 币安量化机器人.Services.Agent;

public enum ProviderOrderLifecycleStateV1
{
    NotSubmitted,
    Open,
    PartialOpen,
    Filled,
    TerminalNoFill,
    TerminalPartialFill,
    MissingUnknown,
    QueryFailed,
    Unknown,
    Conflicting
}

public enum ProviderOrderReconciliationStateV1
{
    Clear,
    Pending,
    Unknown,
    Conflicting,
    Stale,
    Invalid
}

public sealed record ProviderOrderReconciliationInputV1(
    string CycleId,
    string ClientOrderId,
    string Symbol,
    string LocalStatus,
    decimal IntendedQuantity,
    bool SubmissionJournaled,
    ExchangeOrder? ExchangeOrder,
    bool QueryFailed);

public sealed record ProviderOrderReconciliationLegV1(
    string CycleId,
    string ClientOrderId,
    string Symbol,
    string LocalStatus,
    decimal IntendedQuantity,
    bool SubmissionJournaled,
    ProviderOrderLifecycleStateV1 ProviderState,
    string? ExchangeOrderId,
    string? ExchangeStatus,
    decimal ExecutedQuantity,
    decimal AveragePrice,
    DateTimeOffset? ExchangeUpdatedAtUtc);

public sealed record ProviderOrderReconciliationReportV1(
    string Schema,
    string ReportId,
    string ProviderId,
    string Environment,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset EvaluatedAtUtc,
    ProviderOrderReconciliationStateV1 State,
    bool AllowsRiskIncrease,
    IReadOnlyList<ProviderOrderReconciliationLegV1> Legs,
    IReadOnlyList<string> ReasonCodes,
    string CanonicalSha256,
    byte[] CanonicalBytes);

public interface IProviderOrderObservationSourceV1
{
    string ProviderId { get; }
    ExchangeEnvironment Environment { get; }
    Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct);
}

public sealed class ExchangeProviderOrderObservationSourceV1(IExchangeProvider provider) : IProviderOrderObservationSourceV1
{
    private readonly IExchangeProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    public string ProviderId => _provider.ProviderId;
    public ExchangeEnvironment Environment => _provider.Environment;
    public Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct) =>
        _provider.FindOrderAsync(symbol, clientOrderId, ct);
}

public static class ProviderOrderReconciliationServiceV1
{
    public const string Schema = "wpe.provider-order-reconciliation/1.0";
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> OpenStatuses = new(StringComparer.Ordinal)
    {
        "NEW", "PENDING_NEW", "ACCEPTED", "PENDING", "OPEN"
    };

    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
    {
        "CANCELED", "CANCELLED", "REJECTED", "EXPIRED", "EXPIRED_IN_MATCH"
    };

    public static async Task<ProviderOrderReconciliationReportV1> CaptureAsync(
        IProviderOrderObservationSourceV1 source,
        AgentSqliteStore store,
        IReadOnlyList<PersistedIntent> intents,
        Func<DateTimeOffset>? utcNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(intents);

        if (source.Environment != ExchangeEnvironment.Testnet)
            throw new InvalidOperationException("Provider order reconciliation is Testnet-only.");

        var inputs = new List<ProviderOrderReconciliationInputV1>(intents.Count);
        foreach (var saved in intents.OrderBy(x => x.Intent.ClientOrderId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var queryFailed = false;
            var journaled = false;
            ExchangeOrder? order = null;

            try
            {
                journaled = await store.HasExecutionSubmissionJournalAsync(saved.Intent.ClientOrderId, ct);
                order = await source.FindOrderAsync(saved.Intent.Symbol, saved.Intent.ClientOrderId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                queryFailed = true;
            }

            inputs.Add(new(
                saved.CycleId,
                saved.Intent.ClientOrderId,
                saved.Intent.Symbol,
                saved.Status,
                saved.Intent.Quantity,
                journaled,
                order,
                queryFailed));
        }

        var now = (utcNow ?? (() => DateTimeOffset.UtcNow))().ToUniversalTime();
        return Reconcile(source.ProviderId, source.Environment.ToString(), inputs, now, now);
    }

    public static ProviderOrderReconciliationReportV1 Reconcile(
        string providerId,
        string environment,
        IReadOnlyList<ProviderOrderReconciliationInputV1> inputs,
        DateTimeOffset observedAtUtc,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        observedAtUtc = observedAtUtc.ToUniversalTime();
        evaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();

        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(providerId)) reasons.Add("order.invalid-provider");
        if (!string.Equals(environment, "Testnet", StringComparison.Ordinal)) reasons.Add("order.invalid-environment");

        var seenClients = new HashSet<string>(StringComparer.Ordinal);
        var legs = new List<ProviderOrderReconciliationLegV1>(inputs.Count);

        foreach (var input in inputs.OrderBy(x => x.ClientOrderId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(input.CycleId)
                || string.IsNullOrWhiteSpace(input.ClientOrderId)
                || string.IsNullOrWhiteSpace(input.Symbol)
                || string.IsNullOrWhiteSpace(input.LocalStatus)
                || input.IntendedQuantity <= 0)
            {
                reasons.Add("order.invalid-local-intent");
                continue;
            }

            if (!seenClients.Add(input.ClientOrderId))
            {
                reasons.Add("order.invalid-local-duplicate");
                continue;
            }

            var leg = Classify(input);
            legs.Add(leg);

            switch (leg.ProviderState)
            {
                case ProviderOrderLifecycleStateV1.QueryFailed:
                    reasons.Add("order.query-failed");
                    break;
                case ProviderOrderLifecycleStateV1.MissingUnknown:
                    reasons.Add("order.missing-journaled");
                    break;
                case ProviderOrderLifecycleStateV1.Unknown:
                    reasons.Add("order.unknown-status");
                    break;
                case ProviderOrderLifecycleStateV1.Conflicting:
                    reasons.Add("order.conflicting");
                    break;
                default:
                    reasons.Add("order.local-unresolved");
                    break;
            }
        }

        ProviderOrderReconciliationStateV1 state;
        if (observedAtUtc > evaluatedAtUtc || evaluatedAtUtc - observedAtUtc > MaximumAge)
        {
            reasons.Add("order.observation-stale");
            state = ProviderOrderReconciliationStateV1.Stale;
        }
        else if (reasons.Any(x => x.StartsWith("order.invalid", StringComparison.Ordinal)))
        {
            state = ProviderOrderReconciliationStateV1.Invalid;
        }
        else if (legs.Any(x => x.ProviderState == ProviderOrderLifecycleStateV1.Conflicting))
        {
            state = ProviderOrderReconciliationStateV1.Conflicting;
        }
        else if (legs.Any(x => x.ProviderState is
            ProviderOrderLifecycleStateV1.QueryFailed or
            ProviderOrderLifecycleStateV1.MissingUnknown or
            ProviderOrderLifecycleStateV1.Unknown))
        {
            state = ProviderOrderReconciliationStateV1.Unknown;
        }
        else
        {
            state = legs.Count == 0
                ? ProviderOrderReconciliationStateV1.Clear
                : ProviderOrderReconciliationStateV1.Pending;
        }

        if (state == ProviderOrderReconciliationStateV1.Clear)
            reasons.Remove("order.local-unresolved");

        var rows = legs.OrderBy(x => x.ClientOrderId, StringComparer.Ordinal).ToArray();
        var reasonRows = reasons.ToArray();
        var canonical = Canonical(
            providerId,
            environment,
            observedAtUtc,
            evaluatedAtUtc,
            state,
            rows,
            reasonRows);
        var hash = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();

        return new(
            Schema,
            "provider-order-reconciliation:" + hash,
            providerId,
            environment,
            observedAtUtc,
            evaluatedAtUtc,
            state,
            state == ProviderOrderReconciliationStateV1.Clear,
            rows,
            reasonRows,
            hash,
            canonical);
    }

    public static bool IsCanonical(ProviderOrderReconciliationReportV1 report)
    {
        if (report is null
            || report.Schema != Schema
            || report.CanonicalSha256.Length != 64
            || report.ReportId != "provider-order-reconciliation:" + report.CanonicalSha256
            || report.AllowsRiskIncrease != (report.State == ProviderOrderReconciliationStateV1.Clear))
            return false;

        try
        {
            var expected = Canonical(
                report.ProviderId,
                report.Environment,
                report.ObservedAtUtc.ToUniversalTime(),
                report.EvaluatedAtUtc.ToUniversalTime(),
                report.State,
                report.Legs,
                report.ReasonCodes);
            return CryptographicOperations.FixedTimeEquals(expected, report.CanonicalBytes)
                && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(expected),
                    Convert.FromHexString(report.CanonicalSha256));
        }
        catch
        {
            return false;
        }
    }

    private static ProviderOrderReconciliationLegV1 Classify(ProviderOrderReconciliationInputV1 input)
    {
        if (input.QueryFailed)
            return Leg(input, ProviderOrderLifecycleStateV1.QueryFailed, null);

        var order = input.ExchangeOrder;
        if (order is null)
            return Leg(
                input,
                input.SubmissionJournaled
                    ? ProviderOrderLifecycleStateV1.MissingUnknown
                    : ProviderOrderLifecycleStateV1.NotSubmitted,
                null);

        if (!string.Equals(order.Symbol, input.Symbol, StringComparison.Ordinal)
            || !string.Equals(order.ClientOrderId, input.ClientOrderId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(order.OrderId)
            || string.IsNullOrWhiteSpace(order.Status)
            || order.UpdatedAt == default
            || order.UpdatedAt.Kind != DateTimeKind.Utc
            || order.ExecutedQuantity < 0
            || order.ExecutedQuantity > input.IntendedQuantity)
            return Leg(input, ProviderOrderLifecycleStateV1.Conflicting, order);

        var status = order.Status.Trim().ToUpperInvariant();
        var executed = order.ExecutedQuantity;

        if (executed == 0 && order.AvgPrice != 0)
            return Leg(input, ProviderOrderLifecycleStateV1.Conflicting, order);
        if (executed > 0 && order.AvgPrice <= 0)
            return Leg(input, ProviderOrderLifecycleStateV1.Conflicting, order);

        if (status == "FILLED")
            return Leg(
                input,
                executed == input.IntendedQuantity
                    ? ProviderOrderLifecycleStateV1.Filled
                    : ProviderOrderLifecycleStateV1.Conflicting,
                order);

        if (status == "PARTIALLY_FILLED")
            return Leg(
                input,
                executed > 0 && executed < input.IntendedQuantity
                    ? ProviderOrderLifecycleStateV1.PartialOpen
                    : ProviderOrderLifecycleStateV1.Conflicting,
                order);

        if (TerminalStatuses.Contains(status))
        {
            if (executed == 0)
                return Leg(input, ProviderOrderLifecycleStateV1.TerminalNoFill, order);
            if (executed < input.IntendedQuantity)
                return Leg(input, ProviderOrderLifecycleStateV1.TerminalPartialFill, order);
            return Leg(input, ProviderOrderLifecycleStateV1.Conflicting, order);
        }

        if (OpenStatuses.Contains(status))
        {
            if (executed == 0)
                return Leg(input, ProviderOrderLifecycleStateV1.Open, order);
            if (executed < input.IntendedQuantity)
                return Leg(input, ProviderOrderLifecycleStateV1.PartialOpen, order);
            return Leg(input, ProviderOrderLifecycleStateV1.Conflicting, order);
        }

        return Leg(input, ProviderOrderLifecycleStateV1.Unknown, order);
    }

    private static ProviderOrderReconciliationLegV1 Leg(
        ProviderOrderReconciliationInputV1 input,
        ProviderOrderLifecycleStateV1 state,
        ExchangeOrder? order) =>
        new(
            input.CycleId,
            input.ClientOrderId,
            input.Symbol,
            input.LocalStatus,
            input.IntendedQuantity,
            input.SubmissionJournaled,
            state,
            order?.OrderId,
            order?.Status?.Trim().ToUpperInvariant(),
            order?.ExecutedQuantity ?? 0m,
            order?.AvgPrice ?? 0m,
            order is null
                ? null
                : new DateTimeOffset(order.UpdatedAt));

    private static object Row(ProviderOrderReconciliationLegV1 value) => new
    {
        cycleId = value.CycleId,
        clientOrderId = value.ClientOrderId,
        symbol = value.Symbol,
        localStatus = value.LocalStatus,
        intendedQuantity = value.IntendedQuantity.ToString("G29", CultureInfo.InvariantCulture),
        submissionJournaled = value.SubmissionJournaled,
        providerState = value.ProviderState.ToString().ToLowerInvariant(),
        exchangeOrderId = value.ExchangeOrderId,
        exchangeStatus = value.ExchangeStatus,
        executedQuantity = value.ExecutedQuantity.ToString("G29", CultureInfo.InvariantCulture),
        averagePrice = value.AveragePrice.ToString("G29", CultureInfo.InvariantCulture),
        exchangeUpdatedAtUtc = value.ExchangeUpdatedAtUtc
    };

    private static byte[] Canonical(
        string providerId,
        string environment,
        DateTimeOffset observedAtUtc,
        DateTimeOffset evaluatedAtUtc,
        ProviderOrderReconciliationStateV1 state,
        IReadOnlyList<ProviderOrderReconciliationLegV1> legs,
        IReadOnlyList<string> reasons) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = Schema,
            providerId,
            environment,
            observedAtUtc,
            evaluatedAtUtc,
            state = state.ToString().ToLowerInvariant(),
            allowsRiskIncrease = state == ProviderOrderReconciliationStateV1.Clear,
            legs = legs.OrderBy(x => x.ClientOrderId, StringComparer.Ordinal).Select(Row),
            reasons = reasons.OrderBy(x => x, StringComparer.Ordinal)
        });
}

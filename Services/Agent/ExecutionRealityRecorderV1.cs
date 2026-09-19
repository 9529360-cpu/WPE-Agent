namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionRealityCostAssumptionV1(
    string Version,
    decimal CommissionRate,
    decimal SlippageRate);

public sealed record ExecutionRealityRecordResultV1(
    bool Recorded,
    bool Idempotent,
    bool FeeComparable,
    string Code,
    string? CanonicalSha256,
    bool ComparisonRecorded = false,
    string? ComparisonCanonicalSha256 = null);

public sealed class ExecutionRealityRecorderV1
{
    private readonly AgentSqliteStore _store;
    private readonly Func<DateTimeOffset> _utcNow;

    public ExecutionRealityRecorderV1(AgentSqliteStore store, Func<DateTimeOffset>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ExecutionRealityRecordResultV1> RecordAsync(
        string correlationId,
        ExecutionIntent intent,
        ExchangeOrder order,
        ExecutionRealityCostAssumptionV1 costs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(costs);

        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        if (string.IsNullOrWhiteSpace(costs.Version))
            throw new ArgumentException("Cost model version is required.", nameof(costs));
        if (costs.CommissionRate < 0 || costs.CommissionRate >= 1)
            throw new ArgumentOutOfRangeException(nameof(costs), "Commission rate is invalid.");
        if (costs.SlippageRate < 0 || costs.SlippageRate >= 1)
            throw new ArgumentOutOfRangeException(nameof(costs), "Slippage rate is invalid.");
        if (!string.Equals(intent.ClientOrderId, order.ClientOrderId, StringComparison.Ordinal))
            throw new InvalidOperationException("Execution order identity does not match the intended client order id.");
        if (!string.Equals(intent.Symbol, order.Symbol, StringComparison.Ordinal))
            throw new InvalidOperationException("Execution order symbol does not match the intended symbol.");
        if (intent.ExpectedPrice <= 0)
            return new(false, false, false, "expected-price-unavailable", null);

        var authority = await _store.GetExecutionRealityIntentAuthorityAsync(
            correlationId,
            intent,
            ct);
        if (!authority.Bound
            || string.IsNullOrWhiteSpace(authority.StrategyId)
            || string.IsNullOrWhiteSpace(authority.StrategyVersion)
            || authority.ExecutionStartedAtUtc is null)
            return new(false, false, false, "intent-authority-" + authority.Code, null);

        var fee = await _store.GetExecutionRealityFeeObservationAsync(
            order.ClientOrderId,
            order.ExecutedQuantity,
            ct);
        var now = _utcNow().ToUniversalTime();
        var expectation = new ExecutionRealityExpectationV1(
            correlationId,
            intent.ClientOrderId,
            authority.StrategyId,
            authority.StrategyVersion,
            costs.Version,
            intent.Symbol,
            intent.Side,
            intent.ReduceOnly,
            intent.OrderType,
            intent.Quantity,
            intent.ExpectedPrice,
            costs.CommissionRate,
            costs.SlippageRate,
            authority.ExecutionStartedAtUtc.Value);
        var observation = new ExecutionRealityObservationV1(
            order.ClientOrderId,
            order.Status,
            order.ExecutedQuantity,
            order.ExecutedQuantity > 0 ? order.AvgPrice : 0,
            fee.Available ? fee.FeeAmount : 0,
            fee.Available ? ExecutionRealityDriftV1.ExchangeReportedFeeBasis : ExecutionRealityDriftV1.UnavailableFeeBasis,
            new DateTimeOffset(order.UpdatedAt.ToUniversalTime()),
            now);
        var fact = ExecutionRealityDriftV1.Analyze(expectation, observation);
        var persisted = await _store.SaveExecutionRealityDriftAsync(fact, ct);

        var simulated = await _store.GetExecutionSimulationIntentEvidenceAsync(
            correlationId,
            intent.ClientOrderId,
            ct);
        if (simulated is null)
            return new(true, persisted.Idempotent, fact.FeeComparable, persisted.Code, fact.CanonicalSha256);

        var comparedAt = _utcNow().ToUniversalTime();
        if (comparedAt < fact.ObservedAtUtc)
            comparedAt = fact.ObservedAtUtc;
        if (comparedAt < simulated.Fill.SimulatedAtUtc)
            comparedAt = simulated.Fill.SimulatedAtUtc;

        var comparison = ExecutionSimulationComparisonCanonicalizerV1.Create(
            simulated.Fill,
            fact,
            comparedAt);
        var comparisonPersisted = await _store.SaveExecutionSimulationComparisonAsync(
            comparison,
            ct);

        return new(
            true,
            persisted.Idempotent,
            fact.FeeComparable,
            persisted.Code,
            fact.CanonicalSha256,
            true,
            comparison.CanonicalSha256);
    }
}

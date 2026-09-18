namespace WpeAgent.RuntimeContracts;

public enum HistoricalCollectionKindV1
{
    Orders,
    Equity,
    Backtests,
    SkillCalls,
    AuditEvents,
    ExecutionReality
}

public sealed record HistoricalCollectionRequestV1(int Limit = 50, string? Cursor = null)
{
    public static readonly TimeSpan CursorLifetime = TimeSpan.FromMinutes(10);
}

public sealed record HistoricalCollectionPageV1<T>(
    string ContractVersion,
    HistoricalCollectionKindV1 Kind,
    RuntimeCollectionState State,
    IReadOnlyList<T> Items,
    string? NextCursor,
    DateTimeOffset? SourceUpdatedAtUtc,
    string Source,
    string? Message = null)
{
    public const string CurrentContractVersion = "1.0";
    public const int MaximumPageSize = 100;
}

public sealed record HistoricalOrderV1(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string? CorrelationId,
    string? ClientOrderId,
    string Symbol,
    string Side,
    string Action,
    bool ReduceOnly,
    decimal Quantity,
    decimal? AveragePrice,
    string Status);

public sealed record HistoricalEquityPointV1(
    long Sequence,
    DateTimeOffset ObservedAtUtc,
    decimal Equity,
    decimal AvailableBalance,
    string Environment,
    string ProviderId);

public sealed record HistoricalBacktestV1(
    string BacktestId,
    DateTimeOffset CompletedAtUtc,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    string Status,
    int CoverageDays,
    int Trades,
    double OutOfSampleReturn,
    double MaxDrawdown,
    double Sharpe);

public sealed record HistoricalSkillCallV1(
    string Id,
    DateTimeOffset OccurredAtUtc,
    string Skill,
    string Status,
    long DurationMs,
    string? Mode,
    bool? RemoteLlmUsed,
    int? Tokens,
    decimal? CostUsd);

public sealed record HistoricalAuditEventV1(
    string Id,
    DateTimeOffset OccurredAtUtc,
    string Category,
    string Source,
    string? CorrelationId,
    string Status);

public sealed record HistoricalExecutionRealityV1(
    DateTimeOffset ObservedAtUtc,
    string StrategyId,
    string StrategyVersion,
    string CostModelVersion,
    string Symbol,
    string State,
    bool Terminal,
    bool PriceComparable,
    bool FeeComparable,
    bool TotalComparable,
    decimal FillRatio,
    decimal? SlippageDriftBps,
    decimal? FeeDriftBps,
    decimal? TotalExecutionDriftBps,
    long ObservationLatencyMs,
    string ReasonCode);

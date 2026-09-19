namespace WpeAgent.RuntimeContracts;

public enum HistoricalCollectionKindV1
{
    Orders,
    Equity,
    Backtests,
    SkillCalls,
    AuditEvents,
    PostTradeReviews,
    Reconciliations
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


public sealed record HistoricalPostTradeReviewV1(
    string TraceId,
    string Schema,
    string Symbol,
    string Side,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal Quantity,
    decimal Fees,
    string FeeBasis,
    decimal FeeRate,
    decimal EntrySlippageAmount,
    decimal ExitSlippageAmount,
    decimal TotalSlippageAmount,
    string SlippageBasis,
    decimal FundingAmount,
    string FundingBasis,
    decimal NetPnl,
    decimal ReturnPct,
    string Outcome,
    DateTimeOffset ClosedAtUtc,
    string? StrategyId,
    string StrategyVersion,
    string AttributionBasis,
    string TraceState,
    string RiskDecision,
    string ExecutionStatus,
    string ExecutionCode,
    int? ExecutionAttempts,
    DateTimeOffset? MarketCollectedAtUtc,
    string? MarketDataVersion);

public sealed record HistoricalReconciliationV1(
    string Kind,
    string TraceId,
    string Schema,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset EvaluatedAtUtc,
    string State,
    bool AllowsRiskIncrease,
    string CanonicalSha256);

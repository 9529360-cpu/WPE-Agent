using System.Text.Json.Serialization;

namespace WpeAgent.RuntimeContracts;

public enum RuntimeCollectionState
{
    Available,
    Unsupported,
    Stale,
    Error
}

public sealed record RuntimeFreshnessV1(bool Fresh, double AgeSeconds, double StaleAfterSeconds);

public sealed record RuntimeAccountV1(decimal WalletBalance, decimal AvailableBalance);

public sealed record RuntimePositionV1(
    string Symbol,
    string Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal UnrealizedPnl);

public sealed record RuntimeOrderV1(
    string OrderId,
    string Symbol,
    string Side,
    string Type,
    string Status,
    decimal Quantity,
    decimal? Price);

public sealed record RuntimeRiskV1(
    bool Ready,
    bool CircuitBreakerActive,
    string ApprovalStatus,
    double RiskLoad,
    double DailyPnl,
    double MaxDrawdown,
    double PortfolioVaR99,
    double PortfolioCVaR99,
    double PortfolioConcentration,
    double PortfolioCorrelation,
    string Summary);

public sealed record RuntimeSkillCallV1(string Id,DateTime OccurredAtUtc,string Skill,string Status,long DurationMs,string? Mode,bool? RemoteLlmUsed,int? Tokens,decimal? CostUsd,int? ContextCharacters=null,int? InputTokens=null,int? OutputTokens=null,bool? CacheHit=null,string? LlmOutcome=null,string? TokenSource=null);
public sealed record RuntimeDiagnosticV1(string Code,DateTime TimeUtc,string Summary);
public sealed record RuntimeAgentOperationV1(string RoleId,string Status,DateTime? LastActivityAtUtc,string? Activity,string Mode);
public sealed record RuntimeAgentHandoffV1(string Id,DateTime OccurredAtUtc,string SourceRoleId,string TargetRoleId,string Result);

public sealed record RuntimeBacktestV1(
    string BacktestId,
    string StrategyId,
    string StrategyVersion,
    string Symbol,
    DateTime? CompletedAtUtc,
    string Status,
    int CoverageDays,
    int Trades,
    double OutOfSampleReturn,
    double MaxDrawdown,
    double Sharpe);

public sealed record RuntimeCrossAssetResearchMetricsV1(
    int Observations,
    int Trades,
    double TotalReturn,
    double OutOfSampleReturn,
    double MaximumDrawdown,
    double Sharpe,
    double WalkForwardScore,
    double MonteCarloLossProbability);

public sealed record RuntimeResearchAlignmentSummaryV1(
    bool Required,
    int VenueCount,
    int AlignmentPointCount,
    double? SynchronizationWindowSeconds,
    double? MaximumTimestampDeviationSeconds,
    bool ForwardFillAllowed);

public sealed record RuntimeMultipleTestingSummaryV1(
    int TrialCount,
    string CorrectionMethod,
    double NominalAlpha,
    double CorrectedSignificanceThreshold,
    bool HoldoutUntouched,
    bool HoldoutUsedForSelection,
    int HoldoutEvaluationCount);

public sealed record RuntimeCrossAssetResearchV1(
    string ResearchId,
    string StrategyId,
    string InstrumentId,
    string AssetClass,
    string Status,
    string Lifecycle,
    bool Passed,
    IReadOnlyList<string> ReasonCodes,
    RuntimeCrossAssetResearchMetricsV1? Metrics,
    string ArtifactHash,
    string DatasetHash,
    string ParameterHash,
    string CodeVersion,
    string StrategyVersion,
    int Seed,
    DateTimeOffset? OutOfSampleStartsAtUtc,
    DateTimeOffset? OutOfSampleEndsAtUtc,
    int OosPurgeObservations,
    int OosEmbargoObservations,
    RuntimeResearchAlignmentSummaryV1 Alignment,
    RuntimeMultipleTestingSummaryV1? MultipleTesting,
    DateTimeOffset EvaluatedAtUtc);

public sealed record RuntimeDistributionV1(
    string Status,
    bool Allowed,
    IReadOnlyList<string> ApprovalRoles,
    DateTime? ValidUntilUtc,
    bool Withdrawn,
    string? ReceiptHash,
    string? PolicyHash,
    string? ContentFactsHash,
    string? ConsentScopeHash,
    string? ConsentVersion,
    string? SuitabilityVersion,
    string? AuditCorrelationHash,
    IReadOnlyList<string> ReasonCodes,
    DateTime AsOfUtc);

public sealed record RuntimeEquityPointV1(
    DateTime TimeUtc,
    decimal Equity,
    decimal AvailableBalance,
    string Environment,
    string ProviderId);

public sealed record RuntimeConnectionStatusV1(
    string ConnectionId,
    string DisplayName,
    string ProviderId,
    string Environment,
    string CredentialStatus,
    string AdapterStatus,
    DateTime LastCheckedAtUtc,
    bool Ready,
    bool ExchangeConnected,
    bool? TradePermission,
    bool? WithdrawPermission,
    string? WithdrawalWarning);

public sealed record RuntimeAuditEventV1(
    string Id,
    DateTime TimeUtc,
    string Category,
    string Source,
    string? CorrelationId,
    string Status,
    string Summary);

public sealed record RuntimeTelemetryV1(
    string RunId,
    DateTime? HeartbeatAtUtc,
    string RecoveryStatus,
    string RealtimeStatus,
    string WorkflowNode,
    int ThinkingProgress);

public sealed record RuntimePluginV1(
    string Id,
    string Name,
    string Type,
    string Version,
    string PublisherId,
    string PublisherName,
    string EntryKind,
    IReadOnlyList<string> Permissions,
    bool Enabled,
    bool DefaultEnabled,
    bool Active,
    string RuntimeStatus,
    bool TestnetOnly,
    string CompatibilityStatus,
    string SignatureStatus,
    string RiskLevel,
    string? StatusMessage);

public sealed record RuntimeValueV1<T>(RuntimeCollectionState State, T? Value, string? Message = null);

public sealed record RuntimeCollectionV1<T>(
    RuntimeCollectionState State,
    IReadOnlyList<T> Items,
    string? Message = null);

public sealed record RuntimeHistoricalCollectionV1<T>(
    RuntimeCollectionState State,
    IReadOnlyList<T> Items,
    string? NextCursor,
    DateTimeOffset? SourceUpdatedAtUtc,
    string Source,
    string? Message = null);

public sealed record RuntimeMarketV1(string ExchangeId, string ProviderId, string Symbol, string NativeSymbol, string State);
public sealed record RuntimeCapabilityV1(string ExchangeId, string ProviderId, string CanonicalSymbol, string NativeSymbol, MarketType MarketType, CapabilityStatus Status, bool CanRead, bool CanTrade, bool TestnetAvailable, DateTimeOffset CheckedAt, string? Failure = null);
public sealed record RuntimeNotificationStatusV1(bool Enabled,bool TelegramStored,bool TelegramReady,bool WhatsAppStored,bool WhatsAppReady,bool LegacyMigrationPending,string? LegacyMigrationDiagnosticCode,IReadOnlyList<string> EventKinds,bool QuietHoursEnabled,string QuietHoursStart,string QuietHoursEnd,string QuietHoursTimeZone,int PendingCount,int RetryingCount,int SentCount,int DeadLetterCount);
public sealed record RuntimeNotificationOutboxRowV1(long Id,string Channel,string Kind,string UiState,bool InFlight,int Attempts,int MaxAttempts,DateTime OccurredAtUtc,DateTime NextAttemptAtUtc,DateTime UpdatedAtUtc,string? DiagnosticCode);
public sealed record RuntimeTelegramSubscriberV1(string SubscriberId,string ChatType,string State,IReadOnlyList<string> EventKinds,DateTime FirstSeenAtUtc,DateTime UpdatedAtUtc);
public sealed record RuntimeAuthorizationModeV1(string Mode);
public sealed record RuntimeSecurityStorageV1(string State,string ReasonCode,int EnvelopeVersion,int RecordCount,string? EvidenceSha256);
public sealed record RuntimeAutomaticExecutionSummaryV1(string ExecutionId,string Status,string Code,int AttemptCount,DateTimeOffset UpdatedAtUtc);
public sealed record RuntimeApprovalSummaryV1(
    string ApprovalId,
    string? Symbol,
    string? Side,
    string? OrderType,
    decimal? Quantity,
    decimal? EntryPrice,
    decimal? StopLoss,
    decimal? TakeProfit,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Status,
    string ReasonCode);

public sealed record RuntimeEquityMarketV1(
    string InstrumentId,
    string VenueId,
    string Currency,
    decimal LastPrice,
    decimal? Bid,
    decimal? Ask,
    string SessionState,
    string HaltStatus,
    DateTimeOffset ObservedAtUtc,
    string ProviderId);

public sealed record RuntimePublicMarketTickerV1(
    string Symbol,
    decimal? Price,
    decimal? ChangePercent,
    decimal? Volume,
    string Source,
    DateTimeOffset? UpdatedAt,
    bool Stale,
    string State);

public sealed record RuntimePublicMarketKlineV1(
    string Symbol,
    string Interval,
    decimal? Close,
    decimal? Volume,
    string Source,
    DateTimeOffset? UpdatedAt,
    bool Stale,
    string State);

public sealed record RuntimeBrokerCapabilityV1(
    string ProviderId,
    string Environment,
    bool CanReadAccounts,
    bool CanReadPositions,
    bool CanReadOrders,
    bool CanSubmitOrders,
    bool CanCancelOrders,
    DateTimeOffset CheckedAtUtc,
    string ReasonCode);

public sealed class RuntimeSnapshotV1
{
    public const string CurrentContractVersion = "1.0";

    public string ContractVersion { get; init; } = CurrentContractVersion;
    public DateTime GeneratedAtUtc { get; init; }
    public DateTime SourceUpdatedAtUtc { get; init; }
    public RuntimeFreshnessV1 Freshness { get; init; } = new(false, 0, 15);
    public long EventSequence { get; init; }
    public string Environment { get; init; } = string.Empty;
    public RuntimeValueV1<RuntimeAccountV1> Account { get; init; } = new(RuntimeCollectionState.Unsupported, null);
    public RuntimeCollectionV1<RuntimePositionV1> Positions { get; init; } = new(RuntimeCollectionState.Unsupported, []);
    public RuntimeCollectionV1<RuntimeOrderV1> Orders { get; init; } = new(RuntimeCollectionState.Unsupported, []);
    public RuntimeValueV1<RuntimeRiskV1> Risk { get; init; } = new(RuntimeCollectionState.Unsupported, null);
    public RuntimeCollectionV1<RuntimeSkillCallV1> SkillCalls { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Skill-call persistence is not connected.");
    public RuntimeValueV1<RuntimeDiagnosticV1> Diagnostic { get; init; } = new(RuntimeCollectionState.Available, null);
    public RuntimeCollectionV1<RuntimeAgentOperationV1> AgentOperations { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Agent operations persistence is not connected.");
    public RuntimeCollectionV1<RuntimeAgentHandoffV1> AgentHandoffs { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Agent handoff persistence is not connected.");
    public RuntimeCollectionV1<RuntimeBacktestV1> Backtests { get; init; } = new(RuntimeCollectionState.Unsupported, []);
    public RuntimeCollectionV1<RuntimeCrossAssetResearchV1> CrossAssetResearch { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Cross-asset research has not been published.");
    public RuntimeValueV1<RuntimeDistributionV1> Distribution { get; init; } = new(RuntimeCollectionState.Available,
        new("Denied", false, [], null, false, null, null, null, null, null, null, null, ["LEDGER_EMPTY"], DateTime.MinValue));
    public RuntimeCollectionV1<RuntimeEquityPointV1> EquityHistory { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Equity persistence is not connected.");
    public RuntimeValueV1<RuntimeConnectionStatusV1> ConnectionStatus { get; init; } = new(RuntimeCollectionState.Unsupported, null, "Access readiness has not been checked.");
    public RuntimeCollectionV1<RuntimeAuditEventV1> AuditEvents { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Audit persistence is not connected.");
    public RuntimeValueV1<RuntimeTelemetryV1> Telemetry { get; init; } = new(RuntimeCollectionState.Unsupported, null);
    public RuntimeCollectionV1<RuntimeMarketV1> Markets { get; init; } = new(RuntimeCollectionState.Unsupported, []);
    public RuntimeCollectionV1<RuntimePublicMarketTickerV1> PublicMarkets { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Public market runtime is not connected.");
    public RuntimeCollectionV1<RuntimePublicMarketKlineV1> PublicKlines { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Public market kline runtime is not connected.");
    public RuntimeCollectionV1<RuntimeCapabilityV1> Capabilities { get; init; } = new(RuntimeCollectionState.Unsupported, []);
    public RuntimeCollectionV1<RuntimePluginV1> Plugins { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Plugin registry is not connected.");
    public RuntimeValueV1<RuntimeNotificationStatusV1> NotificationStatus { get; init; } = new(RuntimeCollectionState.Unsupported, null, "Notification state is not connected.");
    public RuntimeCollectionV1<RuntimeNotificationOutboxRowV1> NotificationOutbox { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Notification outbox is not connected.");
    public RuntimeCollectionV1<RuntimeTelegramSubscriberV1> TelegramSubscribers { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Telegram subscriber registry is not connected.");
    public RuntimeValueV1<RuntimeAuthorizationModeV1> AuthorizationMode { get; init; } = new(RuntimeCollectionState.Unsupported, null, "Trading authorization state is not connected.");
    public RuntimeValueV1<RuntimeSecurityStorageV1> SecurityStorage { get; init; } = new(RuntimeCollectionState.Unsupported, null, "Security storage runtime is not connected.");
    public RuntimeCollectionV1<RuntimeAutomaticExecutionSummaryV1> AutomaticExecutions { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Automatic execution persistence is not connected.");
    public RuntimeCollectionV1<RuntimeApprovalSummaryV1> PendingApprovals { get; init; } = new(RuntimeCollectionState.Unsupported, [], "Trading approval persistence is not connected.");
    public RuntimeCollectionV1<RuntimeEquityMarketV1> EquityMarkets { get; init; } = new(RuntimeCollectionState.Unsupported, [], "No authorized equity market-data source is connected.");
    public RuntimeValueV1<RuntimeBrokerCapabilityV1> EquityBroker { get; init; } = new(RuntimeCollectionState.Unsupported, null, "No paper or sandbox equity broker is connected.");
    public RuntimeHistoricalCollectionV1<HistoricalOrderV1> HistoricalOrders { get; init; } = new(RuntimeCollectionState.Unsupported, [], null, null, "local-agent-sqlite", "Historical orders are not connected.");
    public RuntimeHistoricalCollectionV1<HistoricalEquityPointV1> HistoricalEquity { get; init; } = new(RuntimeCollectionState.Unsupported, [], null, null, "local-agent-sqlite", "Historical equity is not connected.");
    public RuntimeHistoricalCollectionV1<HistoricalBacktestV1> HistoricalBacktests { get; init; } = new(RuntimeCollectionState.Unsupported, [], null, null, "local-agent-sqlite", "Historical backtests are not connected.");
    public RuntimeHistoricalCollectionV1<HistoricalSkillCallV1> HistoricalSkillCalls { get; init; } = new(RuntimeCollectionState.Unsupported, [], null, null, "local-agent-sqlite", "Historical skill calls are not connected.");
    public RuntimeHistoricalCollectionV1<HistoricalAuditEventV1> HistoricalAuditEvents { get; init; } = new(RuntimeCollectionState.Unsupported, [], null, null, "local-agent-sqlite", "Historical audit events are not connected.");

    // Temporary compatibility surface for the existing Web UI runtime bridge.
    [JsonExtensionData]
    public Dictionary<string, object?> LegacyFields { get; init; } = new(StringComparer.Ordinal);
}

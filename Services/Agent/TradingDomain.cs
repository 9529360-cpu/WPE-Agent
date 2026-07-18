using System.Text.Json.Serialization;

namespace 币安量化机器人.Services.Agent;

public enum ExchangeEnvironment { Testnet, Mainnet }
public enum PositionSide { Long, Short }
public enum DecisionAction { OpenLong, OpenShort, AddLong, AddShort, ReduceLong, ReduceShort, CloseLong, CloseShort, Lock, Unlock, ReverseToLong, ReverseToShort, Hold }

public sealed record TradingRule(string Symbol, decimal StepSize, decimal TickSize, decimal MinQuantity, decimal MinNotional, int MaxLeverage)
{
    public decimal RoundQuantity(decimal value) => StepSize <= 0 ? value : Math.Floor(value / StepSize) * StepSize;
    public decimal RoundPrice(decimal value) => TickSize <= 0 ? Math.Round(value, 8) : Math.Round(value / TickSize, MidpointRounding.ToZero) * TickSize;
}
public sealed record AccountSnapshot(decimal WalletBalance, decimal AvailableBalance, decimal Equity, DateTime Timestamp);
public sealed record ManagedPosition(string Symbol, PositionSide Side, decimal Quantity, decimal EntryPrice, decimal MarkPrice, decimal UnrealizedPnl, decimal Leverage, bool Isolated, decimal LiquidationPrice);
public sealed record ExchangeOrder(string Symbol, long OrderId, string ClientOrderId, string Status, decimal ExecutedQuantity, decimal AvgPrice, string Type, PositionSide? PositionSide, bool IsProtection, DateTime UpdatedAt);
public sealed record DerivativesSnapshot(decimal FundingRate, decimal OpenInterest, decimal LongShortRatio, decimal TopAccountRatio, decimal TopPositionRatio, decimal TakerBuySellRatio, decimal Basis);
public sealed record MarketEvidence(string Symbol, decimal Price, decimal Support, decimal Resistance, double Rsi, double Trend15m, double Trend1h, double Trend4h, DerivativesSnapshot Derivatives, DateTime CollectedAt);
public sealed record NewsEvidence(string Source, string Title, string Url, DateTime? PublishedAt, DateTime CollectedAt, string Reliability, string DuplicateGroup, IReadOnlyList<string> AffectedAssets);
public sealed class EvidencePack
{
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
    public AccountSnapshot Account { get; init; } = new(0, 0, 0, DateTime.UtcNow);
    public IReadOnlyList<ManagedPosition> Positions { get; init; } = Array.Empty<ManagedPosition>();
    public IReadOnlyDictionary<string, MarketEvidence> Markets { get; init; } = new Dictionary<string, MarketEvidence>();
    public IReadOnlyList<NewsEvidence> News { get; init; } = Array.Empty<NewsEvidence>();
    public IReadOnlyList<string> MissingSources { get; init; } = Array.Empty<string>();
    public int Completeness { get; init; }
}
public sealed class DecisionPlan
{
    [JsonConverter(typeof(JsonStringEnumConverter))] public DecisionAction Action { get; set; } = DecisionAction.Hold;
    public string Instrument { get; set; } = "BTCUSDT";
    public int TargetTier { get; set; }
    public double Confidence { get; set; }
    public decimal StopLossPrice { get; set; }
    public decimal TakeProfitPrice { get; set; }
    public string Invalidation { get; set; } = string.Empty;
    public string Regime { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<string> EvidenceReferences { get; set; } = new();
    public List<string> MissingConditions { get; set; } = new();
    public string ConflictSummary { get; set; } = string.Empty;
}
public enum MarketRegime { Trending, Ranging, Transition, Unknown }
public sealed record SignalContribution(string Name,string Horizon,double RawValue,double Weight,double WeightedScore,string Direction,string Explanation);
public sealed class MarketDecisionAssessment
{
    public string Symbol { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))] public MarketRegime Regime { get; init; }
    public double NetScore { get; init; }
    public double Confidence { get; init; }
    public double ConflictRatio { get; init; }
    public bool Fresh { get; init; }
    public bool EntryReady { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public DecisionAction RecommendedAction { get; init; } = DecisionAction.Hold;
    public IReadOnlyList<SignalContribution> Signals { get; init; } = Array.Empty<SignalContribution>();
    public IReadOnlyList<string> MissingConditions { get; init; } = Array.Empty<string>();
    public string Summary { get; init; } = string.Empty;
}
public sealed class DecisionReview
{
    public DecisionPlan Decision { get; init; } = new();
    public bool Accepted { get; init; }
    public string Verdict { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
    public string Explanation { get; init; } = string.Empty;
}
public sealed record AgentContext(string BrainName, bool CircuitBreakerActive, string? ActiveSymbol, IReadOnlyList<string> PreviousOutcomes, IReadOnlyList<MarketDecisionAssessment> MarketAssessments, int ConsecutiveHolds);
public sealed record BrainHealth(bool Healthy, string Message);
public sealed record BrainDecisionResult(DecisionPlan Decision, string Request, string Response);
public sealed class BrainCallException : Exception
{
    public BrainCallException(string message,string request,string response,Exception? inner=null):base(message,inner){Request=request;Response=response;}
    public string Request { get; }
    public string Response { get; }
}
public interface IBrainProvider { string Name { get; } Task<BrainHealth> HealthCheckAsync(CancellationToken cancellationToken); Task<BrainDecisionResult> DecideAsync(EvidencePack evidence, AgentContext context, CancellationToken cancellationToken); }
public interface IExchangeAdapter : IAsyncDisposable
{
    ExchangeEnvironment Environment { get; }
    Task<AccountSnapshot> GetAccountAsync(CancellationToken ct);
    Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct);
    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct);
    Task<TradingRule> GetRulesAsync(string symbol, CancellationToken ct);
    Task<MarketEvidence> GetMarketAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol, CancellationToken ct);
    Task SetLeverageAsync(string symbol, int leverage, CancellationToken ct);
    Task SetMarginModeAsync(string symbol, bool isolated, CancellationToken ct);
    Task SetHedgeModeAsync(bool enabled, CancellationToken ct);
    Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct);
    Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct);
    Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct);
    Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct);
}
public sealed class RiskLimits { public decimal[] MarginTiers { get; set; } = [0.20m,0.40m,0.60m]; public decimal MaxMargin { get; set; } = .60m; public int Leverage { get; set; } = 50; public decimal DailyDrawdownLimit { get; set; } = .30m; public bool Isolated { get; set; } = true; }
public sealed class DecisionPolicy
{
    public double MinimumConfidence { get; set; } = .62;
    public double MinimumDirectionalScore { get; set; } = .28;
    public double MaximumConflictRatio { get; set; } = .65;
    public int MinimumEvidenceCompleteness { get; set; } = 70;
    public int MaximumEvidenceAgeMinutes { get; set; } = 5;
}
public sealed record ExecutionIntent(string Symbol, PositionSide Side, decimal Quantity, bool ReduceOnly, decimal StopLoss, decimal TakeProfit, string ClientOrderId, string Reason, DecisionAction Action = DecisionAction.Hold);
public sealed record PersistedIntent(string CycleId, ExecutionIntent Intent, string Status, long? ExchangeOrderId);
public sealed record RecoveryResult(bool SafeToIncreaseRisk, IReadOnlyList<string> Messages);

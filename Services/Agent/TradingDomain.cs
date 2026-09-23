using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 币安量化机器人.Services.Agent;

public enum ExchangeEnvironment { Testnet, Mainnet }
public enum PositionSide { Long, Short }
public enum DecisionAction { OpenLong, OpenShort, AddLong, AddShort, ReduceLong, ReduceShort, CloseLong, CloseShort, Lock, Unlock, ReverseToLong, ReverseToShort, Hold }
public enum ExecutionOrderType { Market, Limit }

public sealed record TradingRule(string Symbol, decimal StepSize, decimal TickSize, decimal MinQuantity, decimal MinNotional, int MaxLeverage)
{
    public decimal RoundQuantity(decimal value) => StepSize <= 0 ? value : Math.Floor(value / StepSize) * StepSize;
    public decimal RoundPrice(decimal value) => TickSize <= 0 ? Math.Round(value, 8) : Math.Round(value / TickSize, MidpointRounding.ToZero) * TickSize;
}
public sealed record AccountSnapshot(decimal WalletBalance, decimal AvailableBalance, decimal Equity, DateTime Timestamp);
public sealed record ManagedPosition(string Symbol, PositionSide Side, decimal Quantity, decimal EntryPrice, decimal MarkPrice, decimal UnrealizedPnl, decimal Leverage, bool Isolated, decimal LiquidationPrice);
public sealed record ExchangeOrder(string Symbol, string OrderId, string ClientOrderId, string Status, decimal ExecutedQuantity, decimal AvgPrice, string Type, PositionSide? PositionSide, bool IsProtection, DateTime UpdatedAt);
public sealed record DerivativesSnapshot(decimal FundingRate, decimal OpenInterest, decimal LongShortRatio, decimal TopAccountRatio, decimal TopPositionRatio, decimal TakerBuySellRatio, decimal Basis);
public sealed record CandleEvidence(DateTime OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal QuoteVolume, long Trades, decimal TakerBuyVolume);
internal static class ConfirmedMarketCandlesV1
{
    internal static IReadOnlyList<CandleEvidence> Select(IReadOnlyList<CandleEvidence> values,string interval,DateTime observedAtUtc)
    {
        var duration=Duration(interval);var observed=observedAtUtc.Kind==DateTimeKind.Utc?observedAtUtc:observedAtUtc.ToUniversalTime();
        return values.Where(x=>Valid(x)&&x.OpenTime.Kind==DateTimeKind.Utc&&x.OpenTime<=observed-duration)
            .OrderBy(x=>x.OpenTime).GroupBy(x=>x.OpenTime).Where(x=>x.Count()==1).Select(x=>x.Single()).ToArray();
    }
    internal static MarketSkillSnapshot Analyze(string symbol,string interval,IReadOnlyList<CandleEvidence> values,DateTime observedAtUtc)
    {
        var candles=Select(values,interval,observedAtUtc);if(candles.Count<31)throw new InvalidOperationException($"{interval} confirmed market history is incomplete");
        var closes=candles.Select(x=>x.Close).ToArray();var price=closes[^1];var changes=closes.Zip(closes.Skip(1),(a,b)=>a==0?0d:(double)((b/a-1)*100)).ToArray();var gains=changes.TakeLast(14).Select(x=>Math.Max(x,0)).Average();var losses=changes.TakeLast(14).Select(x=>Math.Max(-x,0)).Average();var rsi=losses==0?100:100-100/(1+gains/losses);
        return new(symbol,interval,price,candles.TakeLast(20).Min(x=>x.Low),candles.TakeLast(20).Max(x=>x.High),rsi,(double)(price/closes[^11]-1)*100,(double)(price/closes[^31]-1)*100,candles[^1].OpenTime+Duration(interval));
    }
    internal static TimeSpan Duration(string interval)=>interval switch{"1m"=>TimeSpan.FromMinutes(1),"5m"=>TimeSpan.FromMinutes(5),"15m"=>TimeSpan.FromMinutes(15),"1h"=>TimeSpan.FromHours(1),"4h"=>TimeSpan.FromHours(4),"1d"=>TimeSpan.FromDays(1),_=>throw new ArgumentException("Unsupported market interval.",nameof(interval))};
    private static bool Valid(CandleEvidence x)=>x.Open>0&&x.High>0&&x.Low>0&&x.Close>0&&x.Low<=Math.Min(x.Open,x.Close)&&x.High>=Math.Max(x.Open,x.Close)&&x.High>=x.Low&&x.Volume>=0&&x.QuoteVolume>=0&&x.Trades>=0&&x.TakerBuyVolume>=0;
}
public sealed record RealtimeMarketSnapshot(string Symbol,decimal LastPrice,decimal BestBid,decimal BestAsk,decimal BidQuantity,decimal AskQuantity,decimal BuyVolume5m,decimal SellVolume5m,decimal LastMinuteVolume,DateTime UpdatedAt,long Messages,bool Connected)
{
    public IReadOnlyList<CandleEvidence> ClosedMinuteCandles { get; init; } = Array.Empty<CandleEvidence>();
    public double SpreadBps=>BestBid>0&&BestAsk>=BestBid?(double)((BestAsk-BestBid)/((BestAsk+BestBid)/2)*10000):999;
    public bool HasOrderFlow=>BuyVolume5m+SellVolume5m>0;
    public double OrderFlowImbalance=>HasOrderFlow?(double)((BuyVolume5m-SellVolume5m)/(BuyVolume5m+SellVolume5m)):0;
    public bool Fresh=>Connected&&DateTime.UtcNow-UpdatedAt<TimeSpan.FromSeconds(15);
    public bool EligibleForEnrichment=>Fresh&&LastPrice>0&&BestBid>0&&BestAsk>=BestBid&&BidQuantity>=0&&AskQuantity>=0;
}
public sealed record RealtimeAgentEvent(string EventType,string Symbol,string Status,string Summary,DateTime OccurredAt,string PayloadHash);
public sealed class MarketQualityEvidence
{
    public decimal BestBid { get; init; }
    public decimal BestAsk { get; init; }
    public double SpreadBps { get; init; }
    public double OrderBookImbalance { get; init; }
    public double OrderFlowImbalance { get; init; }
    public bool OrderFlowAvailable { get; init; }
    public double AtrPercent { get; init; } = .01;
    public double RealizedVolatility { get; init; } = .01;
    public double RelativeVolume { get; init; } = 1;
    public double LiquidityScore { get; init; } = .80;
    public double LiquidationIntensity { get; init; }
    public long ClockSkewMilliseconds { get; init; }
    public int SourceCount { get; init; } = 5;
    public int QualityScore { get; init; } = 100;
    public IReadOnlyList<string> Anomalies { get; init; } = Array.Empty<string>();
}
public sealed record MarketEvidence(string Symbol, decimal Price, decimal Support, decimal Resistance, double Rsi, double Trend15m, double Trend1h, double Trend4h, DerivativesSnapshot Derivatives, DateTime CollectedAt)
{
    public MarketQualityEvidence Quality { get; init; } = new();
    public IReadOnlyList<CandleEvidence> Candles { get; init; } = Array.Empty<CandleEvidence>();
    public IReadOnlyList<CandleEvidence> Candles1m { get; init; } = Array.Empty<CandleEvidence>();
    public IReadOnlyList<CandleEvidence> Candles1h { get; init; } = Array.Empty<CandleEvidence>();
    public IReadOnlyList<CandleEvidence> Candles4h { get; init; } = Array.Empty<CandleEvidence>();
    public MarketEvidenceProvenanceV1? Provenance { get; init; }
}
public sealed record MarketEvidenceProvenanceV1(string Schema,string ProviderId,string Environment,string Symbol,DateTime CollectedAtUtc,DateTime? FirstCandleOpenUtc,DateTime? LastCandleOpenUtc,int CandleCount,byte[] CanonicalBytes,string CanonicalSha256);
public static class MarketEvidenceProvenanceCanonicalizerV1
{
    public const string Schema="wpe.market-evidence-provenance/1.2";

    public static MarketEvidenceProvenanceV1 Create(MarketEvidence market,string providerId,string environment)
    {
        var candles1m=market.Candles1m.OrderBy(x=>x.OpenTime).ToArray();
        var candles15m=market.Candles.OrderBy(x=>x.OpenTime).ToArray();
        var candles1h=market.Candles1h.OrderBy(x=>x.OpenTime).ToArray();
        var candles4h=market.Candles4h.OrderBy(x=>x.OpenTime).ToArray();
        var bytes=Serialize(market,providerId,environment,candles1m,candles15m,candles1h,candles4h);
        return new(
            Schema,providerId,environment,market.Symbol,market.CollectedAt,
            candles15m.FirstOrDefault()?.OpenTime,candles15m.LastOrDefault()?.OpenTime,candles15m.Length,
            bytes,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public static bool IsCanonical(MarketEvidence market)
    {
        var value=market.Provenance;
        if(value is null||value.Schema!=Schema||value.Environment!="Testnet"||string.IsNullOrWhiteSpace(value.ProviderId)||
           !string.Equals(value.Symbol,market.Symbol,StringComparison.Ordinal)||value.CollectedAtUtc!=market.CollectedAt)
            return false;
        var expected=Create(market,value.ProviderId,value.Environment);
        return value.CandleCount==expected.CandleCount&&
               value.FirstCandleOpenUtc==expected.FirstCandleOpenUtc&&
               value.LastCandleOpenUtc==expected.LastCandleOpenUtc&&
               string.Equals(value.CanonicalSha256,expected.CanonicalSha256,StringComparison.Ordinal)&&
               value.CanonicalBytes.Length>0&&
               CryptographicOperations.FixedTimeEquals(value.CanonicalBytes,expected.CanonicalBytes);
    }

    private static byte[] Serialize(
        MarketEvidence market,
        string providerId,
        string environment,
        IReadOnlyList<CandleEvidence> candles1m,
        IReadOnlyList<CandleEvidence> candles15m,
        IReadOnlyList<CandleEvidence> candles1h,
        IReadOnlyList<CandleEvidence> candles4h)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("candle_count_1m",candles1m.Count);
            writer.WriteNumber("candle_count_15m",candles15m.Count);
            writer.WriteNumber("candle_count_1h",candles1h.Count);
            writer.WriteNumber("candle_count_4h",candles4h.Count);
            writer.WriteString("collected_at_utc",market.CollectedAt.ToUniversalTime());
            writer.WriteString("environment",environment);
            writer.WriteNumber("price",market.Price);
            writer.WriteString("provider_id",providerId);
            writer.WriteNumber("resistance",market.Resistance);
            writer.WriteNumber("rsi",market.Rsi);
            writer.WriteString("schema",Schema);
            writer.WriteNumber("support",market.Support);
            writer.WriteString("symbol",market.Symbol);
            writer.WriteNumber("trend_15m",market.Trend15m);
            writer.WriteNumber("trend_1h",market.Trend1h);
            writer.WriteNumber("trend_4h",market.Trend4h);
            WriteCandles(writer,"candles_1m",candles1m);
            WriteCandles(writer,"candles_15m",candles15m);
            WriteCandles(writer,"candles_1h",candles1h);
            WriteCandles(writer,"candles_4h",candles4h);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteCandles(Utf8JsonWriter writer,string name,IReadOnlyList<CandleEvidence> candles)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach(var x in candles)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(x.OpenTime.ToUniversalTime());
            writer.WriteNumberValue(x.Open);
            writer.WriteNumberValue(x.High);
            writer.WriteNumberValue(x.Low);
            writer.WriteNumberValue(x.Close);
            writer.WriteNumberValue(x.Volume);
            writer.WriteNumberValue(x.QuoteVolume);
            writer.WriteNumberValue(x.Trades);
            writer.WriteNumberValue(x.TakerBuyVolume);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
public sealed record NewsEvidence(string Source,string Title,string Url,DateTime? PublishedAt,DateTime CollectedAt,string Reliability,string DuplicateGroup,IReadOnlyList<string> AffectedAssets,string BodySummary="",double Confidence=.5,int CorroboratingSources=1,string EventType="GENERAL",bool IsBreaking=false,double Sentiment=0);
public sealed class EvidencePack
{
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
    public AccountSnapshot Account { get; init; } = new(0, 0, 0, DateTime.UtcNow);
    public IReadOnlyList<ManagedPosition> Positions { get; init; } = Array.Empty<ManagedPosition>();
    public IReadOnlyDictionary<string, MarketEvidence> Markets { get; init; } = new Dictionary<string, MarketEvidence>();
    public IReadOnlyList<NewsEvidence> News { get; init; } = Array.Empty<NewsEvidence>();
    public IReadOnlyDictionary<string,CryptoInstrumentFundamentalV1> Fundamentals { get; init; } = new Dictionary<string,CryptoInstrumentFundamentalV1>();
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
    public decimal EntryPrice { get; set; }
    public double RiskRewardRatio { get; set; }
    public ExecutionOrderType OrderType { get; set; } = ExecutionOrderType.Market;
    public string StrategyVersion { get; set; } = "wpe-core-v2";
    public string DecisionContextKind { get; set; } = "legacy-signal";
    public string DecisionContextId { get; set; } = string.Empty;
    public double RiskBudgetMultiplier { get; set; } = 1;
}
public enum MarketRegime { Trending, Ranging, Transition, Extreme, Unknown }
public sealed class DecisionReview
{
    public DecisionPlan Decision { get; init; } = new();
    public bool Accepted { get; init; }
    public string Verdict { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
    public string Explanation { get; init; } = string.Empty;
}
public sealed class IndependentRiskReview
{
    public bool Approved { get; init; }
    public string RiskLevel { get; init; } = "UNKNOWN";
    public decimal PlannedQuantity { get; init; }
    public decimal RiskAmount { get; init; }
    public decimal ExposureAfter { get; init; }
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Checks { get; init; } = Array.Empty<string>();
    public string Summary { get; init; } = string.Empty;
}
public sealed class PortfolioRiskAssessment
{
    public decimal GrossExposure { get; init; }
    public decimal NetExposure { get; init; }
    public double LargestPositionShare { get; init; }
    public double VaR95 { get; init; }
    public double VaR99 { get; init; }
    public double CVaR99 { get; init; }
    public double StressLoss { get; init; }
    public double MaximumPairCorrelation { get; init; }
    public IReadOnlyDictionary<string,double> Correlations { get; init; } = new Dictionary<string,double>();
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
    public bool Approved { get; init; }
    public string Summary { get; init; } = string.Empty;
}
public sealed record AgentContext(string BrainName,bool CircuitBreakerActive,string? ActiveSymbol,IReadOnlyList<StructuredOutcomeMemory> OutcomeMemories,int ConsecutiveHolds,IReadOnlyList<PlannerMemoryFact>? RelevantMemories=null);
public sealed record PlannerMemoryFact(string Tier,DateTime OccurredAtUtc,string Result,string Source,string Summary,string? StrategyId=null);
public sealed record StructuredOutcomeMemory(
    DateTime CycleStartedUtc,
    string Mode,
    string Exchange,
    string Symbol,
    string Timeframe,
    string DecisionAction,
    string DecisionSummary,
    string RiskResult,
    string RiskReasonCode,
    bool ExecutionAttempted,
    string ExecutionResult,
    bool StateChanged,
    string RecoveryHint);
public sealed record BrainHealth(bool Healthy, string Message);
public sealed record BrainDecisionResult(DecisionPlan Decision, string Request, string Response);
public sealed class BrainCallException : Exception
{
    public BrainCallException(string message,string request,string response,Exception? inner=null):base(message,inner){Request=request;Response=response;}
    public string Request { get; }
    public string Response { get; }
}
public interface IBrainProvider { string Name { get; } Task<BrainHealth> HealthCheckAsync(CancellationToken cancellationToken); Task<BrainDecisionResult> DecideAsync(EvidencePack evidence, AgentContext context, CancellationToken cancellationToken); }
public interface IAssistantProvider : IBrainProvider { bool IsLocal { get; } }
public interface IInPlaceProtectionUpdateAdapter
{
}

public interface IExchangeAdapter : IAsyncDisposable
{
    ExchangeEnvironment Environment { get; }
    Task<AccountSnapshot> GetAccountAsync(CancellationToken ct);
    Task<IReadOnlyList<ManagedPosition>> GetPositionsAsync(CancellationToken ct);
    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct);
    Task<TradingRule> GetRulesAsync(string symbol, CancellationToken ct);
    Task<MarketEvidence> GetMarketAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<DerivativesSnapshot>> GetDerivativeHistoryAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<CandleEvidence>> GetCandlesAsync(string symbol, string interval, int limit, CancellationToken ct);
    Task<IReadOnlyList<CandleEvidence>> GetCandlesRangeAsync(string symbol,string interval,DateTime start,DateTime end,int limit,CancellationToken ct);
    Task SetLeverageAsync(string symbol, int leverage, CancellationToken ct);
    Task SetMarginModeAsync(string symbol, bool isolated, CancellationToken ct);
    Task SetHedgeModeAsync(bool enabled, CancellationToken ct);
    Task<ExchangeOrder> PlaceMarketAsync(string symbol, PositionSide side, decimal quantity, string clientOrderId, bool reduceOnly, CancellationToken ct);
    Task<ExchangeOrder> PlaceLimitAsync(string symbol, PositionSide side, decimal quantity, decimal price, string clientOrderId, bool reduceOnly, CancellationToken ct);
    Task<ExchangeOrder> PlaceProtectionAsync(string symbol, PositionSide sideToClose, decimal stopLoss, decimal takeProfit, string groupId, CancellationToken ct);
    Task<ExchangeOrder?> FindOrderAsync(string symbol, string clientOrderId, CancellationToken ct);
    Task CancelOrderAsync(string symbol, string orderId, CancellationToken ct);
}
public sealed class RiskLimits
{
    public decimal[] MarginTiers { get; set; } = [.10m,.20m,.35m];
    public decimal MaxMargin { get; set; } = .50m;
    public int Leverage { get; set; } = 10;
    public decimal DailyDrawdownLimit { get; set; } = .08m;
    public decimal MaxRiskPerTrade { get; set; } = .01m;
    public decimal MaxSymbolExposure { get; set; } = .25m;
    public decimal MaxAccountExposure { get; set; } = .50m;
    public decimal MaxDailyLoss { get; set; } = .05m;
    public double MaxAtrPercent { get; set; } = .045;
    public double MinimumLiquidityScore { get; set; } = .55;
    public double MaximumSpreadBps { get; set; } = 8;
    public double MaximumSlippageBps { get; set; } = 12;
    public double MinimumRiskReward { get; set; } = 1.8;
    public int ApiFailureThreshold { get; set; } = 3;
    public double MaxPortfolioVaR99 { get; set; } = .03;
    public double MaxPortfolioCVaR99 { get; set; } = .045;
    public double MaxLargestPositionShare { get; set; } = .65;
    public double MaxCorrelatedExposure { get; set; } = .40;
    public int MinimumHistoricalDays { get; set; } = 365;
    public int MinimumBacktestTrades { get; set; } = 30;
    public bool Isolated { get; set; } = true;
}
public sealed class DecisionPolicy
{
    public int MinimumEvidenceCompleteness { get; set; } = 70;
    public int MaximumEvidenceAgeMinutes { get; set; } = 5;
    public int MinimumMarketQuality { get; set; } = 65;
}
public static class PositionExitReasonCodes
{
    public const string StructureInvalidated = "position.structure-invalidated";
    public const string LiquidationBuffer = "position.liquidation-buffer";
    public const string PartialTakeProfit2R = "position.partial-take-profit-2r";
    public const string ProtectionReplaceFailed = "position.protection-replace-failed";
    public const string ManualEmergencyClose = "manual.emergency-close";
    public const string ProtectionStopLoss = "protection.fill-reconciled.stop-loss";
    public const string ProtectionTakeProfit = "protection.fill-reconciled.take-profit";
    public const string ProtectionFillReconciled = "protection.fill-reconciled";
}

public static class ExecutionReasonCode
{
    public static bool TryNormalize(string? value, out string code)
    {
        code=(value??string.Empty).Trim();
        if(code.Length is 0 or >96)return false;
        var segments=code.Split('.');
        if(segments.Length==0)return false;
        foreach(var segment in segments)
        {
            if(segment.Length==0||segment[0] is not (>= 'a' and <= 'z'))return false;
            if(segment[^1] is not (>= 'a' and <= 'z' or >= '0' and <= '9'))return false;
            if(segment.Any(character=>character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))return false;
        }
        return true;
    }

    public static string NormalizeOrFallback(string? value,string fallback)=>
        TryNormalize(value,out var code)?code:fallback;

    public static string ResolveForDurableIntent(ExecutionIntent intent,string nonReductionFallback)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if(TryNormalize(intent.ReasonCode,out var explicitCode))return explicitCode;
        if(!intent.ReduceOnly)return nonReductionFallback;
        if(TryNormalize(intent.Reason,out var legacyCode))return legacyCode;
        return "action."+intent.Action.ToString().ToLowerInvariant();
    }
}

public sealed record ExecutionIntent(string Symbol, PositionSide Side, decimal Quantity, bool ReduceOnly, decimal StopLoss, decimal TakeProfit, string ClientOrderId, string Reason, DecisionAction Action = DecisionAction.Hold, ExecutionOrderType OrderType = ExecutionOrderType.Market, decimal LimitPrice = 0, decimal ExpectedPrice = 0, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ReasonCode = null);
public sealed record PersistedIntent(string CycleId, ExecutionIntent Intent, string Status, string? ExchangeOrderId);
public sealed record PersistedBacktestRun(string Id,string StrategyId,string StrategyVersion,string Symbol,string Status,DateTime CompletedAtUtc,int CoverageDays,int Trades,double OutOfSampleReturn,double MaxDrawdown,double Sharpe);
public sealed record RecoveryResult(bool SafeToIncreaseRisk, IReadOnlyList<string> Messages);

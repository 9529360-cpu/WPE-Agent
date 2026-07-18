using System;
using System.Collections.Generic;
using 币安量化机器人.Core.Abstractions;

namespace 币安量化机器人.Core.Models;

public record StrategyParameters(IReadOnlyDictionary<string, double> Values)
{
    public double Get(string key, double defaultValue = 0d)
        => Values != null && Values.TryGetValue(key, out var value) ? value : defaultValue;
}

public record MarketObservation(
    string Symbol,
    TimeSpan Timeframe,
    DateTime Timestamp,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    IReadOnlyDictionary<string, double> Indicators,
    IReadOnlyDictionary<string, double>? Features = null);

public record TimeframeSeries(TimeSpan Timeframe, IReadOnlyList<MarketObservation> Observations);

public record CompositeSignal(
    string Symbol,
    double TrendScore,
    double MomentumScore,
    double MeanReversionScore,
    IReadOnlyDictionary<TimeSpan, double> ConfidenceByTimeframe);

public enum TradeActionType
{
    Hold,
    EnterLong,
    EnterShort,
    Exit
}

public record TradeAction(TradeActionType ActionType, double Quantity, string Reason);

public record StrategyDecision(TradeAction Action, double Confidence, CompositeSignal Signal, MachineLearningSignal MlSignal);

public record TradeSignal(string Symbol, TradeAction Action, double Confidence, MachineLearningSignal MlSignal);

public record MachineLearningSignal(string Symbol, double ProbabilityUp, double ProbabilityDown, string ModelVersion);

public record StrategyPerformanceSnapshot(
    string Strategy,
    DateTime Timestamp,
    double Equity,
    double Drawdown,
    double Sharpe,
    double Sortino,
    double WinRate);

public record OptimizationRequest(
    string Symbol,
    DateTime TrainingStart,
    DateTime TrainingEnd,
    IReadOnlyDictionary<string, IReadOnlyList<double>> ParameterSpace,
    IBacktestEngine BacktestEngine);

public record OptimizationResult(
    StrategyParameters Parameters,
    BacktestResult Metrics,
    IReadOnlyList<OptimizationCandidate> Candidates);

public record OptimizationCandidate(StrategyParameters Parameters, BacktestResult Result);

public record OptimizationProgress(StrategyParameters Parameters, double Score, int Completed, int Total);

public record BacktestRequest(
    string Symbol,
    DateTime Start,
    DateTime End,
    ITradingStrategy Strategy,
    IReadOnlyDictionary<string, object>? Metadata = null);

public record BacktestResult(
    string Strategy,
    double NetProfit,
    double Sharpe,
    double Sortino,
    double MaxDrawdown,
    double Calmar,
    double WinRate,
    double ProfitFactor,
    IReadOnlyList<TradeSignal> Signals);

public record RawDataFrame(
    string Source,
    string Symbol,
    DateTime Timestamp,
    IReadOnlyDictionary<string, object> Payload);

public record DataQuery(string Symbol, DateTime Start, DateTime End, TimeSpan? Timeframe = null);

public record DataQualityResult(string Rule, bool Passed, string? Message = null);

public record ModelFeatureVector(string Symbol, DateTime Timestamp, IReadOnlyList<double> Values, int Label);

public record PositionSnapshot(
    string Symbol,
    double Quantity,
    double EntryPrice,
    double CurrentPrice,
    double UnrealizedPnl,
    double RealizedPnl,
    double Equity,
    double MaxDrawdown,
    double DailyPnl,
    int ConsecutiveLosingTrades,
    IReadOnlyDictionary<string, double>? Indicators = null);

public record TradeFill(string Symbol, double Quantity, double Price, DateTime Timestamp, TradeActionType ActionType);

public record RiskConfiguration(
    double MaxPositionSize,
    double MaxDrawdown,
    double DailyLossLimit,
    double StopLossMultiplier,
    int BlacklistThreshold,
    TimeSpan RiskEvaluationInterval);

public record RiskProfile(
    double CurrentExposure,
    double Drawdown,
    double DailyLoss,
    IReadOnlyCollection<string> BlacklistedSymbols,
    double KellyFraction,
    double ValueAtRisk);

public record RiskEvent(string Symbol, string Rule, string Message, DateTime Timestamp);

public record PositionRiskBreakdown(string Symbol, decimal Notional, decimal UnrealizedPnl, double Quantity);

public record PortfolioRiskSnapshot(
    DateTime Timestamp,
    decimal WalletBalance,
    decimal AvailableBalance,
    decimal UnrealizedPnl,
    decimal MaintenanceMargin,
    decimal NotionalExposure,
    decimal GrossLeverage,
    decimal MarginUsage,
    decimal FreeMargin,
    decimal LargestPositionShare,
    IReadOnlyList<PositionRiskBreakdown> TopPositions);

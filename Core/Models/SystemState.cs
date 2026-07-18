using System;

namespace 币安量化机器人.Core.Models;

/// <summary>
/// 系统运行模式。
/// </summary>
public enum TradingMode
{
    Paper,
    Testnet,
    Live
}

/// <summary>
/// 智能体运行状态。
/// </summary>
public enum AgentStatus
{
    Idle,
    Running,
    Paused,
    Degraded,
    Stopped
}

/// <summary>
/// 全局系统状态快照，用于 UI 展示与逻辑决策。
/// </summary>
public class SystemState
{
    public TradingMode Mode { get; set; } = TradingMode.Testnet;
    public AgentStatus Status { get; set; } = AgentStatus.Idle;
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1m";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public double DailyPnl { get; set; }
    public double MaxDrawdown { get; set; }
    public string SkillStage { get; set; } = "未启动";
    public string LastDecision { get; set; } = "HOLD";
    public string LastReason { get; set; } = string.Empty;
    public decimal WalletBalance { get; set; }
    public decimal AvailableBalance { get; set; }
    public decimal PositionQuantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal Support { get; set; }
    public decimal Resistance { get; set; }
    public string LastError { get; set; } = string.Empty;
    public string LastMessage { get; set; } = "就绪";
    public int EvidenceCompleteness { get; set; }
    public string BrainName { get; set; } = "未配置";
    public DateTime? NextCycleAtUtc { get; set; }
    public string PositionsSummary { get; set; } = "当前无持仓";
    public string OrdersSummary { get; set; } = "当前无挂单";
    public string MarketSummary { get; set; } = "等待市场证据";
    public string NewsSummary { get; set; } = "等待新闻证据";
    public string RiskSummary { get; set; } = "等待风险检查";
    public string DecisionDiagnostics { get; set; } = "等待信号聚合与 Reviewer 检查";
    public double BrainConfidence { get; set; }
    public double DecisionScore { get; set; }
    public double RiskLoad { get; set; }
    public double ConflictRate { get; set; }
    public string MarketRegime { get; set; } = "UNKNOWN";
    public string WorkflowNode { get; set; } = "IDLE";
    public string ReflectionStatus { get; set; } = "MEMORY READY";
    public int ThinkingProgress { get; set; }
    public decimal BtcPrice { get; set; }
    public decimal EthPrice { get; set; }
    public double BtcTrend { get; set; }
    public double EthTrend { get; set; }
    public double BtcRsi { get; set; }
    public double EthRsi { get; set; }
    public IReadOnlyDictionary<string,double> SignalContributions { get; set; } = new Dictionary<string,double>();
}



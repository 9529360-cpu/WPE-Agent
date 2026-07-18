using System;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Core.Risk;

namespace 币安量化机器人.Services;

/// <summary>
/// 轻量级风险守护：基于 PortfolioRiskSnapshot 和 AppSettings，更新 SystemState（Degraded/Stopped）。
/// </summary>
public static class RiskGuardian
{
    public static void Evaluate(PortfolioRiskSnapshot snapshot)
    {
        var cfg = AppSettingsService.Current;
        var state = ServiceLocator.SystemState;

        if (snapshot.WalletBalance <= 0)
        {
            state.Status = AgentStatus.Stopped;
            state.LastMessage = "钱包余额为 0，自动停止交易";
            state.LastUpdated = DateTime.UtcNow;
            return;
        }

        var equity = (double)snapshot.WalletBalance;
        var unrealized = (double)snapshot.UnrealizedPnl;
        var lossRatio = equity <= 0 ? 0 : Math.Max(0, -unrealized / equity);

        var dailyLimit = cfg.DailyLossLimitRatio <= 0 ? 0.05 : cfg.DailyLossLimitRatio;
        var maxDd = cfg.MaxDrawdownRatio <= 0 ? 0.15 : cfg.MaxDrawdownRatio;

        // 简化：把 UnrealizedPnl 视为当前回撤估计
        if (lossRatio >= maxDd)
        {
            state.Status = AgentStatus.Stopped;
            state.LastMessage = $"触发最大回撤限制 ({lossRatio:P1} ≥ {maxDd:P1})，系统已停止";
        }
        else if (lossRatio >= dailyLimit && state.Status != AgentStatus.Stopped)
        {
            state.Status = AgentStatus.Degraded;
            state.LastMessage = $"触发当日亏损限制 ({lossRatio:P1} ≥ {dailyLimit:P1})，仅允许平仓";
        }

        state.MaxDrawdown = Math.Max(state.MaxDrawdown, lossRatio);
        state.DailyPnl = -lossRatio * equity;
        state.LastUpdated = DateTime.UtcNow;
    }
}



using System;
using System.IO;
using System.Text.Json;

namespace 币安量化机器人.Services;

public class AppSettings
{
    public bool AutoReconnect { get; set; } = true;
    public bool EnableNotifications { get; set; } = true;
    /// <summary>
    /// 运行环境描述：Production / Staging / Dev，仅用于日志。
    /// </summary>
    public string Environment { get; set; } = "Production";

    /// <summary>
    /// 交易模式：Paper / Testnet / Live
    /// </summary>
    public string TradingMode { get; set; } = "Testnet";

    /// <summary>
    /// 默认交易对，例如 BTCUSDT
    /// </summary>
    public string DefaultSymbol { get; set; } = "BTCUSDT";

    /// <summary>
    /// 默认时间框架，例如 1m / 5m / 15m
    /// </summary>
    public string DefaultTimeframe { get; set; } = "15m";

    /// <summary>
    /// 策略基础下单数量（合约张数或币数量，具体由策略解释）
    /// </summary>
    public decimal BaseOrderQuantity { get; set; } = 0.001m;

    /// <summary>
    /// 单日最大亏损占权益比例，到达后自动降级或停止（例如 0.05 = 5%）
    /// </summary>
    public double DailyLossLimitRatio { get; set; } = 0.05;

    /// <summary>
    /// 最大回撤占权益比例（0.15 = 15%）
    /// </summary>
    public double MaxDrawdownRatio { get; set; } = 0.15;

    /// <summary>
    /// 允许的最大连续亏损笔数，超过后触发黑名单/风控
    /// </summary>
    public int MaxConsecutiveLosingTrades { get; set; } = 3;

    /// <summary>
    /// 是否默认连接到合约测试网，建议正式使用前保持 true。
    /// </summary>
    public bool UseFuturesTestnet { get; set; } = true;
    public int RefreshIntervalSeconds { get; set; } = 60;
    public string LogLevel { get; set; } = "Info";
    public string? TelegramBotToken { get; set; }
    public string? TelegramChatId { get; set; }
    public string? DingTalkWebhook { get; set; }
    public decimal MaxOrderQuantity { get; set; } = 50m;
}

public static class AppSettingsService
{
    private static readonly string SettingsPath = AppDataPaths.File("appsettings.json");
    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (settings is not null)
                    Current = settings;
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}

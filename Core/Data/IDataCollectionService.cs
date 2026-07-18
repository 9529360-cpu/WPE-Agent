namespace 币安量化机器人.Core.Data;

/// <summary>
/// 数据采集服务接口
/// 负责从 Binance API 采集实时市场数据
/// </summary>
public interface IDataCollectionService
{
    /// <summary>
    /// 启动数据采集
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止数据采集
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 获取最新的市场数据快照
    /// </summary>
    /// <param name="symbol">交易对，如 "BTCUSDT"</param>
    /// <returns>市场数据，如果不存在则返回 null</returns>
    Task<MarketSnapshot?> GetLatestMarketSnapshotAsync(string symbol);

    /// <summary>
    /// 获取给定时间范围内的历史蜡烛数据
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="interval">时间周期，如 "1h", "4h", "1d"</param>
    /// <param name="startTime">开始时间</param>
    /// <param name="endTime">结束时间</param>
    /// <returns>蜡烛数据列表</returns>
    Task<List<KlineData>> GetHistoricalKlinesAsync(
        string symbol,
        string interval,
        DateTime startTime,
        DateTime endTime);

    /// <summary>
    /// 订阅实时行情更新
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="onDataReceived">数据接收的回调方法</param>
    /// <returns>订阅 ID，用于取消订阅</returns>
    string SubscribeToRealTimeData(string symbol, Func<MarketSnapshot, Task> onDataReceived);

    /// <summary>
    /// 取消订阅实时行情
    /// </summary>
    /// <param name="subscriptionId">订阅 ID</param>
    void UnsubscribeFromRealTimeData(string subscriptionId);

    /// <summary>
    /// 获取订单簿深度数据
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="depth">深度级别，如 5, 10, 20</param>
    Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20);

    /// <summary>
    /// 获取最近的交易记录
    /// </summary>
    /// <param name="symbol">交易对</param>
    /// <param name="limit">限制条数</param>
    Task<List<RecentTrade>> GetRecentTradesAsync(string symbol, int limit = 100);
}

/// <summary>
/// 市场数据快照
/// </summary>
public record MarketSnapshot
{
    public string Symbol { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public decimal PriceChangePercent { get; init; }
    public decimal HighPrice { get; init; }
    public decimal LowPrice { get; init; }
    public decimal Volume { get; init; }
    public decimal QuoteAssetVolume { get; init; }
    public long Timestamp { get; init; }
    public DateTime UpdateTime => new DateTime(1970, 1, 1, 0, 0, 0).AddMilliseconds(Timestamp);
}

/// <summary>
/// K线数据
/// </summary>
public record KlineData
{
    public long OpenTime { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Volume { get; init; }
    public long CloseTime { get; init; }
    public decimal QuoteAssetVolume { get; init; }
    public int NumberOfTrades { get; init; }
    public decimal TakerBuyBaseAssetVolume { get; init; }
    public decimal TakerBuyQuoteAssetVolume { get; init; }
}

/// <summary>
/// 订单簿
/// </summary>
public record OrderBook
{
    public string Symbol { get; init; } = string.Empty;
    public List<OrderBookLevel> Bids { get; init; } = [];
    public List<OrderBookLevel> Asks { get; init; } = [];
    public long Timestamp { get; init; }
}

/// <summary>
/// 订单簿级别
/// </summary>
public record OrderBookLevel
{
    public decimal Price { get; init; }
    public decimal Quantity { get; init; }
}

/// <summary>
/// 最近交易
/// </summary>
public record RecentTrade
{
    public long Id { get; init; }
    public decimal Price { get; init; }
    public decimal Quantity { get; init; }
    public long Time { get; init; }
    public bool IsBuyerMaker { get; init; }
}

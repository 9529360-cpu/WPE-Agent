namespace 币安量化机器人.Infrastructure.Data;

using Serilog;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Services;
using 币安量化机器人.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Binance 数据采集服务实现
/// 负责从 Binance API 采集市场数据
/// </summary>
public class BinanceDataCollectionService : IDataCollectionService
{
    private readonly BinanceApiClient _apiClient;
    private readonly ILogger _logger;
    private bool _isRunning;
    private readonly Dictionary<string, Func<MarketSnapshot, Task>> _subscriptions = new();

    public BinanceDataCollectionService(BinanceApiClient apiClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = Log.ForContext<BinanceDataCollectionService>();
        _isRunning = false;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            _logger.Warning("数据采集服务已在运行中");
            return;
        }

        try
        {
            _isRunning = true;
            _logger.Information("? 数据采集服务已启动");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "? 启动数据采集服务失败");
            _isRunning = false;
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            _logger.Warning("数据采集服务未运行");
            return;
        }

        try
        {
            _subscriptions.Clear();
            _isRunning = false;
            _logger.Information("? 数据采集服务已停止");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "? 停止数据采集服务失败");
            throw;
        }
    }

    public async Task<MarketSnapshot?> GetLatestMarketSnapshotAsync(string symbol)
    {
        if (!_isRunning)
        {
            _logger.Warning("数据采集服务未运行，无法获取市场数据");
            return null;
        }

        try
        {
            var tickers = await _apiClient.GetMiniTickersAsync(new[] { symbol });
            var ticker = tickers.FirstOrDefault(t => t.Symbol == symbol.ToUpperInvariant());

            if (ticker == null)
            {
                _logger.Warning("无法获取 {Symbol} 的行情数据", symbol);
                return null;
            }

            var snapshot = new MarketSnapshot
            {
                Symbol = symbol,
                Price = (decimal)ticker.LastPrice,
                PriceChangePercent = (decimal)ticker.ChangePercent,
                HighPrice = (decimal)ticker.HighPrice,
                LowPrice = (decimal)ticker.LowPrice,
                Volume = (decimal)ticker.Volume,
                QuoteAssetVolume = 0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "获取市场数据失败: {Symbol}", symbol);
            return null;
        }
    }

    public async Task<List<KlineData>> GetHistoricalKlinesAsync(
        string symbol,
        string interval,
        DateTime startTime,
        DateTime endTime)
    {
        if (!_isRunning)
        {
            _logger.Warning("数据采集服务未运行");
            return new List<KlineData>();
        }

        try
        {
            var closes = await _apiClient.GetKlineClosesAsync(symbol, interval, limit: 1000);

            if (closes == null || closes.Count == 0)
            {
                _logger.Warning("无法获取 K 线数据: {Symbol}", symbol);
                return new List<KlineData>();
            }

            var result = closes.Select((close, index) => new KlineData
            {
                OpenTime = new DateTimeOffset(startTime).AddDays(index).ToUnixTimeMilliseconds(),
                Open = close,
                High = close,
                Low = close,
                Close = close,
                Volume = 0,
                CloseTime = new DateTimeOffset(startTime).AddDays(index + 1).ToUnixTimeMilliseconds(),
                QuoteAssetVolume = 0,
                NumberOfTrades = 0,
                TakerBuyBaseAssetVolume = 0,
                TakerBuyQuoteAssetVolume = 0
            }).ToList();

            _logger.Information("? 获取了 {Count} 条 K 线数据: {Symbol}", result.Count, symbol);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "获取 K 线数据异常: {Symbol}", symbol);
            return new List<KlineData>();
        }
    }

    public string SubscribeToRealTimeData(string symbol, Func<MarketSnapshot, Task> onDataReceived)
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("数据采集服务未运行");
        }

        var subscriptionId = Guid.NewGuid().ToString();
        _subscriptions[subscriptionId] = onDataReceived;

        _logger.Information("? 订阅 {Symbol} 的实时数据，ID: {SubscriptionId}", symbol, subscriptionId);
        return subscriptionId;
    }

    public void UnsubscribeFromRealTimeData(string subscriptionId)
    {
        if (_subscriptions.Remove(subscriptionId))
        {
            _logger.Information("? 已取消订阅: {SubscriptionId}", subscriptionId);
        }
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20)
    {
        await Task.CompletedTask;
        if (!_isRunning)
        {
            throw new InvalidOperationException("数据采集服务未运行");
        }

        try
        {
            var orderBook = new OrderBook
            {
                Symbol = symbol,
                Bids = new List<OrderBookLevel>(),
                Asks = new List<OrderBookLevel>(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _logger.Information("订单簿查询（当前实现不支持）: {Symbol}", symbol);
            return orderBook;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "获取订单簿失败: {Symbol}", symbol);
            return new OrderBook();
        }
    }

    public async Task<List<RecentTrade>> GetRecentTradesAsync(string symbol, int limit = 100)
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("数据采集服务未运行");
        }

        try
        {
            var trades = await _apiClient.GetRecentTradesAsync(symbol, limit: limit);

            if (trades == null || trades.Count == 0)
            {
                _logger.Warning("无法获取最近交易: {Symbol}", symbol);
                return new List<RecentTrade>();
            }

            return trades
                .Select(t => new RecentTrade
                {
                    Id = 0,
                    Price = t.Price,
                    Quantity = t.Quantity,
                    Time = new DateTimeOffset(t.Time).ToUnixTimeMilliseconds(),
                    IsBuyerMaker = t.Side == OrderSide.Buy
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "获取最近交易失败: {Symbol}", symbol);
            return new List<RecentTrade>();
        }
    }
}

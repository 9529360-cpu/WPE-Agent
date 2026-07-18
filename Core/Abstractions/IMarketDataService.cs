using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Abstractions;

public interface IMarketDataService
{
    IAsyncEnumerable<MarketObservation> StreamAsync(string symbol, IEnumerable<TimeSpan> timeframes, CancellationToken cancellationToken = default);

    ValueTask<TimeframeSeries> GetSeriesAsync(string symbol, TimeSpan timeframe, DateTime start, DateTime end, CancellationToken cancellationToken = default);
}

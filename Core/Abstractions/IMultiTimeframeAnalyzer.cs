using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Abstractions;

public interface IMultiTimeframeAnalyzer
{
    ValueTask<CompositeSignal> AnalyzeAsync(string symbol, IReadOnlyDictionary<TimeSpan, TimeframeSeries> series, CancellationToken cancellationToken = default);
}

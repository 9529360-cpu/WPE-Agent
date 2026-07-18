using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Abstractions;

public interface IBacktestEngine
{
    ValueTask<BacktestResult> RunAsync(BacktestRequest request, CancellationToken cancellationToken = default);
}

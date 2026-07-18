using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Core.Risk;

public interface IRiskManager
{
    event EventHandler<RiskEvent>? RiskTriggered;

    RiskProfile CurrentProfile { get; }

    void Configure(RiskConfiguration configuration);

    ValueTask UpdateAsync(PositionSnapshot position, CancellationToken cancellationToken = default);

    bool Approve(TradeAction action);

    ValueTask RecordFillAsync(TradeFill fill, CancellationToken cancellationToken = default);
}

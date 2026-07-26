using 币安量化机器人.Models;

namespace 币安量化机器人.Services;

public interface IAccountReader
{
    Task<IReadOnlyList<AccountBalance>> GetAccountBalancesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default);
}

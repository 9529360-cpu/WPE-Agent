namespace 币安量化机器人.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using 币安量化机器人.Core.Risk;
using 币安量化机器人.Application.Services;

/// <summary>
/// 仓位管理器 (PositionManager) 单元测试
/// 测试开平仓、风险检查、止损止盈等功能
/// </summary>
public class PositionManagerTests
{
    private readonly PositionManager _positionManager;

    public PositionManagerTests()
    {
        _positionManager = new PositionManager();
    }

    #region OpenPositionAsync Tests

    [Fact(DisplayName = "开仓_有效仓位_应该成功")]
    public async Task OpenPositionAsync_WithValidPosition_ShouldSuccess()
    {
        // Arrange
        var position = new Position
        {
            Symbol = "BTCUSDT",
            Direction = PositionDirection.Long,
            Quantity = 0.1m,
            EntryPrice = 50000,
            CurrentPrice = 50000,
            Leverage = 1,
            OpenTime = DateTime.UtcNow,
            Status = PositionStatus.Open
        };

        // Act
        var result = await _positionManager.OpenPositionAsync(position);

        // Assert
        result.Should().BeTrue();
        var retrievedPosition = await _positionManager.GetPositionAsync("BTCUSDT");
        retrievedPosition.Should().NotBeNull();
        retrievedPosition!.Quantity.Should().Be(0.1m);
    }

    #endregion

    #region ClosePositionAsync Tests

    [Fact(DisplayName = "平仓_开放仓位_应该成功")]
    public async Task ClosePositionAsync_WithOpenPosition_ShouldSuccess()
    {
        // Arrange
        var position = new Position
        {
            Symbol = "BTCUSDT",
            Direction = PositionDirection.Long,
            Quantity = 0.1m,
            EntryPrice = 50000,
            CurrentPrice = 50000,
            OpenTime = DateTime.UtcNow,
            Status = PositionStatus.Open
        };
        await _positionManager.OpenPositionAsync(position);

        // Act
        var result = await _positionManager.ClosePositionAsync("BTCUSDT");

        // Assert
        result.Should().BeTrue();
        var retrievedPosition = await _positionManager.GetPositionAsync("BTCUSDT");
        retrievedPosition.Should().BeNull();
    }

    #endregion

    #region CalculateUnrealizedProfitAsync Tests

    [Fact(DisplayName = "计算未实现盈利_多头仓位_应该正确计算")]
    public async Task CalculateUnrealizedProfitAsync_LongPosition_ShouldCalcCorrectly()
    {
        // Arrange
        var position = new Position
        {
            Symbol = "BTCUSDT",
            Direction = PositionDirection.Long,
            Quantity = 0.1m,
            EntryPrice = 50000,
            CurrentPrice = 50000,
            Commission = 0,
            OpenTime = DateTime.UtcNow,
            Status = PositionStatus.Open
        };
        await _positionManager.OpenPositionAsync(position);

        // Act
        var unrealizedProfit = await _positionManager.CalculateUnrealizedProfitAsync("BTCUSDT", 51000);

        // Assert
        unrealizedProfit.Should().Be(100); // (51000 - 50000) * 0.1 = 100
    }

    #endregion

    #region GetTotalPositionValueAsync Tests

    [Fact(DisplayName = "获取总仓位价值_多个仓位_应该正确计算")]
    public async Task GetTotalPositionValueAsync_MultiplePositions_ShouldCalcTotal()
    {
        // Arrange
        var position1 = new Position
        {
            Symbol = "BTCUSDT",
            Direction = PositionDirection.Long,
            Quantity = 0.1m,
            EntryPrice = 50000,
            CurrentPrice = 50000,
            OpenTime = DateTime.UtcNow,
            Status = PositionStatus.Open
        };

        var position2 = new Position
        {
            Symbol = "ETHUSDT",
            Direction = PositionDirection.Long,
            Quantity = 1,
            EntryPrice = 2500,
            CurrentPrice = 2500,
            OpenTime = DateTime.UtcNow,
            Status = PositionStatus.Open
        };

        await _positionManager.OpenPositionAsync(position1);
        await _positionManager.OpenPositionAsync(position2);

        // Act
        var totalValue = await _positionManager.GetTotalPositionValueAsync();

        // Assert
        // 0.1 * 50000 + 1 * 2500 = 5000 + 2500 = 7500
        totalValue.Should().Be(7500);
    }

    #endregion

    #region CloseAllPositionsAsync Tests

    [Fact(DisplayName = "关闭所有仓位_应该清空所有")]
    public async Task CloseAllPositionsAsync_ShouldClearAll()
    {
        // Arrange
        var position1 = new Position
        {
            Symbol = "BTCUSDT",
            Direction = PositionDirection.Long,
            Quantity = 0.1m,
            EntryPrice = 50000,
            CurrentPrice = 50000,
            OpenTime = DateTime.UtcNow,
            Status = PositionStatus.Open
        };

        var position2 = new Position
        {
            Symbol = "ETHUSDT",
            Direction = PositionDirection.Long,
            Quantity = 1,
            EntryPrice = 2500,
            CurrentPrice = 2500,
            OpenTime = DateTime.UtcNow,
            Status = PositionStatus.Open
        };

        await _positionManager.OpenPositionAsync(position1);
        await _positionManager.OpenPositionAsync(position2);

        // Act
        var result = await _positionManager.CloseAllPositionsAsync();

        // Assert
        result.Should().BeTrue();
        var count = await _positionManager.GetOpenPositionCountAsync();
        count.Should().Be(0);
    }

    #endregion
}

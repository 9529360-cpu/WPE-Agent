using System;

namespace 币安量化机器人.Services.Execution;

public record ExecutionResult(
    string Symbol,
    long OrderId,
    string ClientOrderId,
    decimal FilledQuantity,
    decimal AveragePrice,
    string Status,
    DateTime Timestamp);

namespace 币安量化机器人.Core.Execution;

public enum OrderState
{
    New = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Canceled = 4,
    PendingCancel = 5,
    Rejected = 6,
    Expired = 7
}

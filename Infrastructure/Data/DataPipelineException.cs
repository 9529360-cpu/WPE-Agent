using System;

namespace 币安量化机器人.Infrastructure.Data;

public class DataPipelineException : Exception
{
    public DataPipelineException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace 币安量化机器人.Services.Agent;

internal static class TradingTelemetry
{
    internal const string InstrumentationName="WPE.Agent.Trading";
    private static readonly ActivitySource ActivitySource=new(InstrumentationName,"1.0.0");
    private static readonly Meter Meter=new(InstrumentationName,"1.0.0");
    private static readonly Counter<long> ExecutionCounter=Meter.CreateCounter<long>("wpe.execution.count");
    private static readonly Counter<long> PreflightCounter=Meter.CreateCounter<long>("wpe.execution.preflight.count");
    private static readonly Counter<long> OverfillCounter=Meter.CreateCounter<long>("wpe.execution.overfill.count");
    private static readonly Histogram<double> PreflightDurationMs=Meter.CreateHistogram<double>("wpe.execution.preflight.duration","ms");
    private static readonly Histogram<double> SpreadBps=Meter.CreateHistogram<double>("wpe.execution.preflight.spread","bps");
    private static readonly Histogram<double> SlippageBps=Meter.CreateHistogram<double>("wpe.execution.preflight.slippage","bps");

    internal static Activity? StartExecution(string cycle,ExecutionIntent intent)
    {
        var activity=ActivitySource.StartActivity("wpe.execution",ActivityKind.Internal);
        activity?.SetTag("wpe.cycle_id",cycle);
        activity?.SetTag("wpe.client_order_id",intent.ClientOrderId);
        activity?.SetTag("wpe.symbol",intent.Symbol);
        activity?.SetTag("wpe.side",intent.Side.ToString());
        activity?.SetTag("wpe.action",intent.Action.ToString());
        activity?.SetTag("wpe.reduce_only",intent.ReduceOnly);
        return activity;
    }

    internal static Activity? StartPreflight(ExecutionIntent intent)
    {
        var activity=ActivitySource.StartActivity("wpe.execution.preflight",ActivityKind.Internal);
        activity?.SetTag("wpe.symbol",intent.Symbol);
        activity?.SetTag("wpe.side",intent.Side.ToString());
        activity?.SetTag("wpe.expected_price",(double)intent.ExpectedPrice);
        return activity;
    }

    internal static void RecordPreflight(ExecutionIntent intent,bool allowed,double spreadBps,double slippageBps,double durationMs,int failureCount)
    {
        var tags=new TagList
        {
            {"wpe.symbol",intent.Symbol},
            {"wpe.side",intent.Side.ToString()},
            {"wpe.result",allowed?"allow":"block"}
        };
        PreflightCounter.Add(1,tags);
        PreflightDurationMs.Record(durationMs,tags);
        SpreadBps.Record(spreadBps,tags);
        SlippageBps.Record(slippageBps,tags);
    }

    internal static void RecordExecution(ExecutionIntent intent,string result)
    {
        ExecutionCounter.Add(1,new TagList
        {
            {"wpe.symbol",intent.Symbol},
            {"wpe.side",intent.Side.ToString()},
            {"wpe.result",result}
        });
    }

    internal static void RecordOverfill(ExecutionIntent intent)
    {
        OverfillCounter.Add(1,new TagList
        {
            {"wpe.symbol",intent.Symbol},
            {"wpe.side",intent.Side.ToString()}
        });
    }

    internal static void MarkError(Activity? activity,Exception error)
    {
        activity?.SetStatus(ActivityStatusCode.Error,error.GetType().Name);
        activity?.SetTag("error.type",error.GetType().FullName);
    }
}

namespace 币安量化机器人.Services.Agent;

public sealed class AutomaticExecutionWorker
{
    private readonly AutomaticExecutionProcessor _processor;
    public AutomaticExecutionWorker(AutomaticExecutionProcessor processor)=>_processor=processor??throw new ArgumentNullException(nameof(processor));
    public async Task<AutomaticExecutionProcessorResult> RunOnceAsync(string workerId,CancellationToken ct)
    {
        var reconciliation=await _processor.ReconcileNextAsync(workerId,ct);return reconciliation.Handled?reconciliation:await _processor.ProcessNextAsync(workerId,ct);
    }
    public async Task RunAsync(string workerId,TimeSpan interval,CancellationToken ct)
    {
        if(interval<TimeSpan.FromMilliseconds(500)||interval>TimeSpan.FromSeconds(5))throw new ArgumentOutOfRangeException(nameof(interval));
        while(true){ct.ThrowIfCancellationRequested();await RunOnceAsync(workerId,ct);await Task.Delay(interval,ct);}
    }
}

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
        var roles=WpeAgent.RuntimeServices.AgentRoleRuntimeRegistry.Shared;
        roles.Publish("execution","waiting","Execution queue is ready; no approved order is pending.");
        roles.Publish("recovery","monitoring","Unknown and interrupted order states are being monitored.");
        try{await _processor.BackfillObservationsAsync(ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{/* Observation repair failure must not disable execution/recovery processing. */}
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result=await RunOnceAsync(workerId,ct);
                var recoveryHandled=result.Handled&&result.Code.StartsWith("automatic.reconcile",StringComparison.Ordinal);
                if(recoveryHandled)
                {
                    roles.Publish("recovery","running","An unresolved execution outcome was reconciled or quarantined.");
                    roles.Publish("execution","waiting","Execution queue remains ready while recovery completes.");
                }
                else if(result.Handled)
                {
                    roles.Publish("execution","running","An approved execution or terminal transition was processed.");
                    roles.Publish("recovery","monitoring","Execution outcome was checked for reconciliation needs.");
                }
                else
                {
                    roles.Publish("execution","waiting","Execution queue is ready; no approved order is pending.");
                    roles.Publish("recovery","monitoring","No unresolved order currently requires reconciliation.");
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception)
            {
                roles.Publish("execution","degraded","Execution worker failed a queue cycle; retry remains scheduled.");
                roles.Publish("recovery","degraded","Recovery worker failed a reconciliation cycle; retry remains scheduled.");
            }
            await Task.Delay(interval,ct);
        }
    }
}

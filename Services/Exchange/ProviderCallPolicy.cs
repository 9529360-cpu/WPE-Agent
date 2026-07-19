using System.Net.Http;

namespace 币安量化机器人.Services.Exchange;

public sealed class ProviderCallPolicy(int timeoutSeconds=20,int retries=2)
{
    private readonly TimeSpan _timeout=TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds,1,120));
    private readonly int _retries=Math.Clamp(retries,0,5);
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken,Task<T>> operation,CancellationToken ct)
    {
        Exception? last=null;
        for(var attempt=0;attempt<=_retries;attempt++)
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);linked.CancelAfter(_timeout);
            try{return await operation(linked.Token);}
            catch(Exception ex)when(IsTransient(ex,ct)&&attempt<_retries){last=ex;await Task.Delay(TimeSpan.FromMilliseconds(100*Math.Pow(2,attempt)),ct);}
        }
        throw last??new InvalidOperationException("Provider operation failed.");
    }
    private static bool IsTransient(Exception ex,CancellationToken caller)=>ex is HttpRequestException||ex is TimeoutException||ex is TaskCanceledException&&!caller.IsCancellationRequested;
}

using 币安量化机器人.Services;

namespace WpeAgent.Notifications;

public sealed class NotificationDispatcher
{
    private readonly NotificationOutboxStore _store;
    private readonly INotificationConfigurationProvider _configuration;
    private readonly IReadOnlyDictionary<NotificationChannel,INotificationTransport> _transports;
    private readonly NotificationFormatter _formatter;
    private readonly INotificationClock _clock;
    private readonly Dictionary<NotificationChannel,Queue<DateTime>> _rateWindows=new();
    private readonly int _maxAttempts;

    public NotificationDispatcher(
        NotificationOutboxStore store,
        INotificationConfigurationProvider configuration,
        IEnumerable<INotificationTransport> transports,
        NotificationFormatter? formatter=null,
        INotificationClock? clock=null,
        int maxAttempts=5)
    {
        _store=store;
        _configuration=configuration;
        _transports=transports.ToDictionary(x=>x.Channel);
        _formatter=formatter??new NotificationFormatter();
        _clock=clock??SystemNotificationClock.Instance;
        _maxAttempts=Math.Clamp(maxAttempts,1,20);
    }

    public async Task<int> DispatchDueAsync(CancellationToken ct=default)
    {
        var configurations=_configuration.GetEnabled()
            .Where(x=>x.Enabled)
            .GroupBy(x=>x.Channel)
            .ToDictionary(x=>x.Key,x=>x.First());
        var items=await _store.ClaimDueAsync(50,TimeSpan.FromMinutes(2),ct);
        var sent=0;
        foreach(var item in items)
        {
            if(!configurations.TryGetValue(item.Channel,out var channel)||
               !_transports.TryGetValue(item.Channel,out var transport))
            {
                await FailAsync(item,"configuration unavailable",ct);
                continue;
            }
            if(!TryAcquire(channel))
            {
                await _store.MarkFailedAsync(item.Id,"WPE-NOTIFY-RATE-LIMIT",
                    _clock.UtcNow.AddMinutes(1),false,ct);
                continue;
            }

            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(channel.TimeoutSeconds,1,60)));
                await transport.SendAsync(_formatter.Format(item.Event),channel,timeout.Token);
                await _store.MarkSentAsync(item.Id,ct);
                sent++;
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                await FailAsync(item,ex.ToString(),ct);
            }
        }
        return sent;
    }

    public async Task RunAsync(TimeSpan interval,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{await DispatchDueAsync(ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception){/* Notification infrastructure is isolated from the trading runtime. */}
            await Task.Delay(interval,ct);
        }
    }

    private async Task FailAsync(NotificationOutboxItem item,string diagnostic,CancellationToken ct)
    {
        var code=UiDiagnostic.FromText(diagnostic,"Notification delivery failed").Code;
        var dead=item.Attempts>=_maxAttempts;
        var exponent=Math.Min(10,Math.Max(0,item.Attempts-1));
        var delay=TimeSpan.FromSeconds(Math.Min(3600,Math.Pow(2,exponent)*5));
        await _store.MarkFailedAsync(item.Id,code,_clock.UtcNow+delay,dead,ct);
    }

    private bool TryAcquire(NotificationChannelConfiguration configuration)
    {
        var now=_clock.UtcNow;
        if(!_rateWindows.TryGetValue(configuration.Channel,out var window))
            _rateWindows[configuration.Channel]=window=new Queue<DateTime>();
        while(window.Count>0&&now-window.Peek()>=TimeSpan.FromMinutes(1))window.Dequeue();
        if(window.Count>=Math.Clamp(configuration.MaxRequestsPerMinute,1,600))return false;
        window.Enqueue(now);
        return true;
    }
}

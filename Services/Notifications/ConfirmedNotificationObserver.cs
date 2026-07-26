using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using System.Security.Cryptography;
using System.Text;

namespace WpeAgent.Notifications;

public interface IConfirmedNotificationObserver
{
    Task ObserveAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct);
}

public sealed record NotificationTestRequest(
    bool UserConfirmed,string Confirmation,string Provider,string Environment,
    NotificationChannel Channel,string RequestId);
public interface INotificationTestEventService
{
    Task<bool> QueueAsync(NotificationTestRequest request,CancellationToken ct);
}
public sealed class NotificationTestEventService(
    NotificationOutboxStore store,INotificationConfigurationProvider configuration):INotificationTestEventService
{
    public async Task<bool> QueueAsync(NotificationTestRequest request,CancellationToken ct)
    {
        if(!request.UserConfirmed||!string.Equals(request.Confirmation,"SEND TEST NOTIFICATION",StringComparison.Ordinal))
            throw new InvalidOperationException("Notification test requires explicit user confirmation.");
        if(string.IsNullOrWhiteSpace(request.RequestId)||request.RequestId.Length>80)throw new InvalidOperationException("Notification test request ID is invalid.");
        var selected=configuration.GetEnabled().SingleOrDefault(x=>x.Channel==request.Channel&&x.Enabled)
            ??throw new InvalidOperationException("The selected notification channel is not ready.");
        var now=DateTime.UtcNow;
        var value=ConfirmedNotificationTruth.System(
            ConfirmedNotificationTruth.EventKey("user-test",request.RequestId,NotificationEventKind.Test),
            NotificationEventKind.Test,request.Provider,request.Environment,now,
            UiDiagnostic.FromText("user-confirmed notification test","Notification test").Code);
        // Test events deliberately bypass business event allowlists and quiet hours after explicit confirmation.
        return await store.EnqueueAsync(new(value,selected.Channel),ct);
    }
}

public sealed class ConfirmedNotificationObserver(
    INotificationEventPublisher publisher):IConfirmedNotificationObserver
{
    public async Task ObserveAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct)
    {
        try{await publisher.PublishAsync(notificationEvent,ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)
        {
            // Notifications are observers. Persistence or delivery failure must not affect execution.
            _=SensitiveDataRedactor.ForLog(ex.Message,180);
        }
    }
}

public sealed class NullConfirmedNotificationObserver:IConfirmedNotificationObserver
{
    public static NullConfirmedNotificationObserver Instance{get;}=new();
    public Task ObserveAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct)=>Task.CompletedTask;
}

public static class ConfirmedNotificationTruth
{
    public static string EventKey(string correlation,string source,NotificationEventKind kind)
    {
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{correlation}|{source}|{kind}")));
        return $"notify:{kind}:{hash[..24]}";
    }
    public static ConfirmedNotificationEvent? FromExecution(
        string eventKey,string provider,string environment,
        ExecutionIntent intent,ExchangeOrder observation,DateTime occurredAtUtc,string diagnosticCode)
    {
        if(observation.ExecutedQuantity<=0||
           observation.Status is not("FILLED" or "PARTIALLY_FILLED"))return null;
        var kind=intent.ReduceOnly?NotificationEventKind.PositionClosed:NotificationEventKind.PositionOpened;
        return new(
            eventKey,kind,environment,provider,intent.Symbol,intent.Side.ToString(),
            observation.AvgPrice>0?observation.AvgPrice:null,
            observation.ExecutedQuantity,intent.StopLoss>0?intent.StopLoss:null,
            intent.TakeProfit>0?intent.TakeProfit:null,
            occurredAtUtc.ToUniversalTime(),diagnosticCode);
    }

    public static ConfirmedNotificationEvent Protection(
        string eventKey,NotificationEventKind kind,string provider,string environment,
        string symbol,PositionSide side,decimal? stopLoss,decimal? takeProfit,
        DateTime occurredAtUtc,string diagnosticCode)
    {
        if(kind is not(NotificationEventKind.ProtectionPlaced or NotificationEventKind.ProtectionUpdated or NotificationEventKind.ProtectionFailed))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return new(eventKey,kind,environment,provider,symbol,side.ToString(),null,null,
            stopLoss,takeProfit,occurredAtUtc.ToUniversalTime(),diagnosticCode);
    }

    public static ConfirmedNotificationEvent System(
        string eventKey,NotificationEventKind kind,string provider,string environment,
        DateTime occurredAtUtc,string diagnosticCode,string? symbol=null,string? side=null,string? content=null)
    {
        if(kind is not(NotificationEventKind.RiskBlocked or NotificationEventKind.AgentDegraded or NotificationEventKind.MarketBrief or NotificationEventKind.Test))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return new(eventKey,kind,environment,provider,symbol,side,null,null,null,null,
            occurredAtUtc.ToUniversalTime(),diagnosticCode,content);
    }
}

public sealed record NotificationRuntime(
    IConfirmedNotificationObserver Observer,
    Func<CancellationToken,Task> RunDispatcher)
{
    public static NotificationRuntime Disabled{get;}=new(
        NullConfirmedNotificationObserver.Instance,_=>Task.CompletedTask);
}

public static class NotificationRuntimeFactory
{
    public static IConfirmedNotificationObserver CurrentObserver{get;private set;}=NullConfirmedNotificationObserver.Instance;
    public static NotificationRuntime Create()
    {
        try
        {
            var configuration=new AgentNotificationConfigurationProvider();
            var store=new NotificationOutboxStore();
            var observer=new ConfirmedNotificationObserver(new NotificationOutboxPublisher(store,configuration));
            var dispatcher=new NotificationDispatcher(
                store,configuration,
                [new TelegramNotificationTransport(),new WhatsAppCloudNotificationTransport()]);
            CurrentObserver=observer;
            return new(observer,async ct=>
            {
                try{await dispatcher.RunAsync(TimeSpan.FromSeconds(5),ct);}
                catch(OperationCanceledException)when(ct.IsCancellationRequested){}
            });
        }
        catch{CurrentObserver=NullConfirmedNotificationObserver.Instance;return NotificationRuntime.Disabled;}
    }
}

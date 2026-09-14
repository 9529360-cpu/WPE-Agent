using System.Text.Json;
using WpeAgent.Notifications;
using WpeAgent.RuntimeContracts;

namespace WPE.Tests;

public sealed class TelegramRuntimeHealthTests
{
    [Theory]
    [InlineData(1,5)]
    [InlineData(2,10)]
    [InlineData(3,20)]
    [InlineData(4,40)]
    [InlineData(5,80)]
    [InlineData(6,160)]
    [InlineData(7,300)]
    [InlineData(20,300)]
    public void RetryDelay_IsBoundedExponential(int failures,int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),TelegramSubscriberRuntime.RetryDelay(failures));
    }

    [Fact]
    public void HealthStore_KeepsPollerAndDispatcherIndependent()
    {
        var store=new TelegramRuntimeHealthStore();
        var now=DateTimeOffset.Parse("2026-09-14T04:00:00Z");
        var poller=new TelegramWorkerHealthSnapshot("BackingOff",3,now.AddMinutes(-2),now,now.AddSeconds(20),"WPE-TG-WORKER-ABCDEF123456",true,now);
        var dispatcher=new TelegramWorkerHealthSnapshot("Healthy",0,now,null,now.AddSeconds(5),null,false,now);

        store.PublishPoller(poller);
        store.PublishDispatcher(dispatcher);
        var snapshot=store.Read();

        Assert.Equal(poller,snapshot.Poller);
        Assert.Equal(dispatcher,snapshot.Dispatcher);
    }

    [Fact]
    public void RuntimeNotificationStatus_SerializesStructuredTelegramHealth()
    {
        var now=DateTimeOffset.Parse("2026-09-14T04:00:00Z");
        var poller=new RuntimeNotificationWorkerHealthV1("Standby",0,null,null,now.AddSeconds(15),"WPE-TG-POLL-SINGLE-OWNER",false,now);
        var dispatcher=new RuntimeNotificationWorkerHealthV1("Healthy",0,now,null,now.AddSeconds(5),null,false,now);
        var status=new RuntimeNotificationStatusV1(true,true,true,false,false,false,null,[],false,"00:00","00:00","UTC",1,2,3,4,poller,dispatcher);

        using var document=JsonDocument.Parse(JsonSerializer.Serialize(status,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        Assert.Equal("Standby",document.RootElement.GetProperty("telegramPoller").GetProperty("state").GetString());
        Assert.False(document.RootElement.GetProperty("telegramPoller").GetProperty("ownsSingleInstanceLease").GetBoolean());
        Assert.Equal("Healthy",document.RootElement.GetProperty("telegramDispatcher").GetProperty("state").GetString());
    }

    [Fact]
    public void ProductionFactory_UsesOneTelegramSubscriberSupervisor()
    {
        var root=Root();
        var source=File.ReadAllText(Path.Combine(root,"Services","Notifications","ConfirmedNotificationObserver.cs"));
        var start=source.IndexOf("public static NotificationRuntime Create()",StringComparison.Ordinal);
        Assert.True(start>=0);
        var method=source[start..];

        Assert.Contains("new TelegramSubscriberRuntime(subscriberPoller,subscriberDispatcher)",method,StringComparison.Ordinal);
        Assert.Contains("subscriberRuntime.RunAsync(ct)",method,StringComparison.Ordinal);
        Assert.DoesNotContain("subscriberPoller.RunAsync(ct)",method,StringComparison.Ordinal);
        Assert.DoesNotContain("subscriberDispatcher.RunAsync(ct)",method,StringComparison.Ordinal);
    }

    [Fact]
    public void PollingLease_IsLocalSingleOwnerAndCredentialFree()
    {
        var source=RuntimeSource();
        Assert.Contains("telegram-getupdates.lock",source,StringComparison.Ordinal);
        Assert.Contains("FileShare.None",source,StringComparison.Ordinal);
        Assert.DoesNotContain("AccessToken",source,StringComparison.Ordinal);
        Assert.DoesNotContain("botToken",source,StringComparison.Ordinal);
    }

    [Fact]
    public void Supervisor_ShutdownPublishesStoppedStateAndReleasesLeaseTruth()
    {
        var source=RuntimeSource();
        Assert.Contains("finally",source,StringComparison.Ordinal);
        Assert.Contains("PublishPoller(ToStopped(current.Poller, now))",source,StringComparison.Ordinal);
        Assert.Contains("PublishDispatcher(ToStopped(current.Dispatcher, now))",source,StringComparison.Ordinal);
        Assert.Contains("new(\"Stopped\", 0, current.LastSuccessAtUtc, current.LastFailureAtUtc, null, null, false, now)",source,StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerDiagnosticCode_DoesNotHashExceptionMessages()
    {
        var source=RuntimeSource();
        Assert.Contains("exception.GetType()",source,StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message",source,StringComparison.Ordinal);
        Assert.DoesNotContain("exception.ToString()",source,StringComparison.Ordinal);
    }

    private static string RuntimeSource()=>File.ReadAllText(Path.Combine(Root(),"Services","Notifications","TelegramRuntimeHealth.cs"));
    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}

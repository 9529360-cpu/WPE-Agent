using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using WpeAgent.Notifications;

namespace WPE.Tests;

public sealed class TelegramSubscriberDistributionTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-tg-subscribers-"+Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock=new(new DateTime(2026,7,28,1,0,0,DateTimeKind.Utc));
    public TelegramSubscriberDistributionTests()=>Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Start_is_pending_and_cannot_receive_until_explicit_approval()
    {
        var store=Store();await store.ApplyUpdateAsync(new(1,1001,"private","/start"),default);
        var pending=Assert.Single(await store.ListAsync(default));Assert.Equal(TelegramSubscriberState.Pending,pending.State);
        Assert.Equal(0,await store.EnqueueApprovedAsync(Event("before",NotificationEventKind.RiskBlocked),default));

        await store.ApproveAsync(1001,new HashSet<NotificationEventKind>{NotificationEventKind.RiskBlocked},"local-owner",default);
        Assert.Equal(1,await store.EnqueueApprovedAsync(Event("allowed",NotificationEventKind.RiskBlocked),default));
        Assert.Equal(0,await store.EnqueueApprovedAsync(Event("blocked",NotificationEventKind.PositionOpened),default));
        Assert.Single(await store.ClaimDueAsync(10,default));
    }

    [Fact]
    public async Task Duplicate_update_is_idempotent_and_stop_revokes_pending_delivery()
    {
        var store=Store();var start=new TelegramBotUpdate(4,2002,"private","/start");
        await store.ApplyUpdateAsync(start,default);await store.ApplyUpdateAsync(start,default);
        Assert.Single(await store.ListAsync(default));Assert.Equal(5,await store.ReadNextUpdateOffsetAsync(default));
        await store.ApproveAsync(2002,new HashSet<NotificationEventKind>{NotificationEventKind.AgentDegraded},"owner",default);
        await store.EnqueueApprovedAsync(Event("queued",NotificationEventKind.AgentDegraded),default);
        await store.ApplyUpdateAsync(new(5,2002,"private","/stop"),default);
        Assert.Equal(TelegramSubscriberState.Disabled,Assert.Single(await store.ListAsync(default)).State);
        Assert.Empty(await store.ClaimDueAsync(10,default));
    }

    [Fact]
    public async Task Approved_subscribers_receive_independent_batch_deliveries()
    {
        var store=Store();
        foreach(var chat in new[]{3001L,3002L}){await store.ApplyUpdateAsync(new(chat,chat,"private","/start"),default);await store.ApproveAsync(chat,new HashSet<NotificationEventKind>{NotificationEventKind.RiskBlocked},"owner",default);}
        var transport=new RecordingTransport();var dispatcher=new TelegramSubscriberDispatcher(store,new StaticConfiguration(),transport);
        Assert.Equal(2,await store.EnqueueApprovedAsync(Event("batch",NotificationEventKind.RiskBlocked),default));
        Assert.Equal(2,await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(new[]{"3001","3002"},transport.Destinations.Order().ToArray());
    }

    [Fact]
    public async Task Poller_persists_cursor_and_only_registers_supported_commands()
    {
        var store=Store();var source=new StaticUpdates([
            new(8,4001,"private","/start"),new(9,4002,"private","hello"),new(10,4001,"private","/stop")]);
        var poller=new TelegramSubscriptionPoller(store,source,new StaticConfiguration());
        Assert.Equal(3,await poller.PollOnceAsync(default));
        Assert.Equal(11,await store.ReadNextUpdateOffsetAsync(default));
        Assert.Equal(TelegramSubscriberState.Disabled,Assert.Single(await store.ListAsync(default)).State);
        Assert.Equal(0,await poller.PollOnceAsync(default));
    }

    [Fact]
    public async Task Http_source_returns_ordinary_and_non_text_messages_so_cursor_advances()
    {
        var handler=new TelegramUpdatesHandler("""
            {"ok":true,"result":[
              {"update_id":21,"message":{"chat":{"id":4101,"type":"private"},"text":"ordinary message"}},
              {"update_id":22,"message":{"chat":{"id":4101,"type":"private"},"photo":[{"file_id":"photo"}]}}
            ]}
            """);
        var source=new HttpTelegramBotUpdateSource(new HttpClient(handler));var store=Store();
        var poller=new TelegramSubscriptionPoller(store,source,new StaticConfiguration());

        Assert.Equal(2,await poller.PollOnceAsync(default));
        Assert.Equal(23,await store.ReadNextUpdateOffsetAsync(default));
        Assert.Empty(await store.ListAsync(default));
        Assert.Equal(0,await poller.PollOnceAsync(default));
        Assert.Equal(new[]{0L,23L},handler.Offsets);
    }

    [Fact]
    public async Task Http_source_mixed_commands_and_ordinary_updates_do_not_replay()
    {
        var handler=new TelegramUpdatesHandler("""
            {"ok":true,"result":[
              {"update_id":31,"message":{"chat":{"id":4201,"type":"private"},"text":"hello"}},
              {"update_id":32,"message":{"chat":{"id":4201,"type":"private"},"text":"/start payload"}},
              {"update_id":33,"message":{"chat":{"id":4201,"type":"private"},"sticker":{"file_id":"sticker"}}}
            ]}
            """);
        var store=Store();var poller=new TelegramSubscriptionPoller(store,new HttpTelegramBotUpdateSource(new HttpClient(handler)),new StaticConfiguration());

        Assert.Equal(3,await poller.PollOnceAsync(default));
        Assert.Equal(TelegramSubscriberState.Pending,Assert.Single(await store.ListAsync(default)).State);
        Assert.Equal(34,await store.ReadNextUpdateOffsetAsync(default));
        Assert.Equal(0,await poller.PollOnceAsync(default));
        Assert.Equal(new[]{0L,34L},handler.Offsets);
    }

    [Fact]
    public async Task Full_chat_id_is_encrypted_and_absent_from_subscriber_audit_and_outbox_plaintext()
    {
        const long chatId=-1009876543210;var path=Path.Combine(_directory,"encrypted.db");
        var store=new TelegramSubscriberStore(path,_clock,new ReversibleTestProtector());
        await store.ApplyUpdateAsync(new(41,chatId,"supergroup","/start"),default);
        var subscriber=Assert.Single(await store.ListAsync(default));
        await store.ApproveAsync(subscriber.SubscriberKey,new HashSet<NotificationEventKind>{NotificationEventKind.RiskBlocked},"owner",default);
        await store.EnqueueApprovedAsync(Event("encrypted-target",NotificationEventKind.RiskBlocked),default);

        await using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly}.ToString());
        await connection.OpenAsync();
        foreach(var table in new[]{"telegram_subscribers","telegram_subscriber_audit","telegram_subscriber_outbox"})
        {
            await using var columns=connection.CreateCommand();columns.CommandText=$"PRAGMA table_info({table})";
            await using var reader=await columns.ExecuteReaderAsync();var names=new List<string>();while(await reader.ReadAsync())names.Add(reader.GetString(1));
            Assert.DoesNotContain("chat_id",names);
        }
        await using(var values=connection.CreateCommand())
        {
            values.CommandText="SELECT encrypted_chat_id||'|'||subscriber_key FROM telegram_subscribers UNION ALL SELECT actor||'|'||subscriber_key FROM telegram_subscriber_audit UNION ALL SELECT payload||'|'||subscriber_key FROM telegram_subscriber_outbox";
            await using var reader=await values.ExecuteReaderAsync();while(await reader.ReadAsync())Assert.DoesNotContain(chatId.ToString(),reader.GetString(0),StringComparison.Ordinal);
        }
        var claimed=Assert.Single(await store.ClaimDueAsync(10,default));Assert.Equal(chatId,claimed.ChatId);
    }

    private TelegramSubscriberStore Store()=>new(Path.Combine(_directory,"notifications.db"),_clock,new ReversibleTestProtector());
    private static ConfirmedNotificationEvent Event(string key,NotificationEventKind kind)=>new(key,kind,"Testnet","binance-futures","BTCUSDT",null,null,null,null,null,new DateTime(2026,7,28,1,0,0,DateTimeKind.Utc),"WPE-TEST");
    public void Dispose(){try{Directory.Delete(_directory,true);}catch{}}

    private sealed class TestClock(DateTime now):INotificationClock{public DateTime UtcNow{get;set;}=now;}
    private sealed class StaticConfiguration:INotificationConfigurationProvider
    {
        public IReadOnlyList<NotificationChannelConfiguration> GetEnabled()=>[new(NotificationChannel.Telegram,true,"123456:token","owner")];
    }
    private sealed class RecordingTransport:INotificationTransport
    {
        public NotificationChannel Channel=>NotificationChannel.Telegram;public List<string> Destinations{get;}=[];
        public Task SendAsync(NotificationOutboundMessage message,NotificationChannelConfiguration configuration,CancellationToken ct){Destinations.Add(configuration.Destination);return Task.CompletedTask;}
    }
    private sealed class StaticUpdates(IReadOnlyList<TelegramBotUpdate> updates):ITelegramBotUpdateSource
    {
        public Task<IReadOnlyList<TelegramBotUpdate>> GetUpdatesAsync(string botToken,long offset,int timeoutSeconds,CancellationToken ct)=>Task.FromResult<IReadOnlyList<TelegramBotUpdate>>(updates.Where(x=>x.UpdateId>=offset).ToArray());
    }
    private sealed class ReversibleTestProtector:ITelegramSubscriberSecretProtector
    {
        public string Protect(string value)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(value.Reverse().ToArray())));
        public string Unprotect(string value)=>new string(Encoding.UTF8.GetString(Convert.FromBase64String(value)).Reverse().ToArray());
    }
    private sealed class TelegramUpdatesHandler(string firstResponse):HttpMessageHandler
    {
        public List<long> Offsets{get;}=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var query=System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);var offset=long.Parse(query["offset"]!);Offsets.Add(offset);
            var body=Offsets.Count==1?firstResponse:"{\"ok\":true,\"result\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
}

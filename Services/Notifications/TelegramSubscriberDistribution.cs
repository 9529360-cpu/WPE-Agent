using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WpeAgent.Notifications;

public enum TelegramSubscriberState { Pending, Approved, Disabled }

public sealed record TelegramSubscriber(
    string SubscriberKey,
    string ChatType,
    TelegramSubscriberState State,
    IReadOnlySet<NotificationEventKind> AllowedEventKinds,
    DateTime FirstSeenAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TelegramBotUpdate(long UpdateId,long ChatId,string ChatType,string? Command);

public interface ITelegramSubscriberSecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public sealed class DpapiTelegramSubscriberSecretProtector:ITelegramSubscriberSecretProtector
{
    public string Protect(string value)=>Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),null,DataProtectionScope.CurrentUser));
    public string Unprotect(string value)=>Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser));
}

public interface ITelegramBotUpdateSource
{
    Task<IReadOnlyList<TelegramBotUpdate>> GetUpdatesAsync(
        string botToken,long offset,int timeoutSeconds,CancellationToken ct);
}

public sealed class HttpTelegramBotUpdateSource(HttpClient? http=null):ITelegramBotUpdateSource
{
    private static readonly Uri Origin=new("https://api.telegram.org/");
    private static readonly Regex TokenPattern=new("^[A-Za-z0-9_-]+:[A-Za-z0-9_-]+$",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking);
    private readonly HttpClient _http=http??new HttpClient();

    public async Task<IReadOnlyList<TelegramBotUpdate>> GetUpdatesAsync(string botToken,long offset,int timeoutSeconds,CancellationToken ct)
    {
        if(botToken.Length>256||!TokenPattern.IsMatch(botToken))throw new ArgumentException("The Telegram access token format is invalid.",nameof(botToken));
        timeoutSeconds=Math.Clamp(timeoutSeconds,1,30);
        var endpoint=new Uri($"{Origin.AbsoluteUri}bot{botToken}/getUpdates?offset={offset}&timeout={timeoutSeconds}&allowed_updates=%5B%22message%22%5D");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds+5));
        using var response=await _http.GetAsync(endpoint,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is > 1_048_576)throw new InvalidDataException("Telegram updates response exceeds the size limit.");
        await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer=new MemoryStream();var chunk=new byte[16_384];int read,total=0;
        while((read=await stream.ReadAsync(chunk,timeout.Token))>0)
        {
            total+=read;if(total>1_048_576)throw new InvalidDataException("Telegram updates response exceeds the size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0,read),timeout.Token);
        }
        buffer.Position=0;
        using var document=await JsonDocument.ParseAsync(buffer,new JsonDocumentOptions{MaxDepth=16},timeout.Token);
        if(!document.RootElement.TryGetProperty("ok",out var ok)||!ok.GetBoolean()||
           !document.RootElement.TryGetProperty("result",out var result)||result.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("Telegram updates response is invalid.");
        var updates=new List<TelegramBotUpdate>();
        foreach(var item in result.EnumerateArray())
        {
            if(!item.TryGetProperty("update_id",out var updateId)||!updateId.TryGetInt64(out var id)||id<0||
               !item.TryGetProperty("message",out var message)||
               !message.TryGetProperty("chat",out var chat)||
               !chat.TryGetProperty("id",out var chatId)||!chatId.TryGetInt64(out var destination)||destination==0||
               !chat.TryGetProperty("type",out var type)||type.ValueKind!=JsonValueKind.String)continue;
            string? command=null;
            if(message.TryGetProperty("text",out var messageText)&&messageText.ValueKind==JsonValueKind.String)
            {
                var candidate=messageText.GetString()?.Trim().Split(' ',2)[0].Split('@',2)[0].ToLowerInvariant();
                if(candidate is "/start" or "/stop")command=candidate;
            }
            updates.Add(new(id,destination,type.GetString()??"unknown",command));
        }
        return updates.OrderBy(x=>x.UpdateId).ToArray();
    }
}

public sealed class TelegramSubscriberStore
{
    private readonly string _connectionString;
    private readonly INotificationClock _clock;
    private readonly ITelegramSubscriberSecretProtector _protector;
    public TelegramSubscriberStore(string? databasePath=null,INotificationClock? clock=null,ITelegramSubscriberSecretProtector? protector=null)
    {
        var path=databasePath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WPE Agent","notification-outbox.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,Cache=SqliteCacheMode.Shared}.ToString();
        _clock=clock??SystemNotificationClock.Instance;
        _protector=protector??new DpapiTelegramSubscriberSecretProtector();
        Initialize();
    }

    public async Task ApplyUpdateAsync(TelegramBotUpdate update,CancellationToken ct)
    {
        if(update.ChatId==0||update.UpdateId<0||update.ChatType.Length is <1 or >32)throw new ArgumentException("Telegram update is invalid.");
        var now=_clock.UtcNow;
        await using var connection=await OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var command=connection.CreateCommand())
        {
            command.Transaction=(SqliteTransaction)transaction;
            command.CommandText="INSERT OR IGNORE INTO telegram_updates(update_id,processed_at) VALUES($id,$now)";
            command.Parameters.AddWithValue("$id",update.UpdateId);command.Parameters.AddWithValue("$now",Iso(now));
            if(await command.ExecuteNonQueryAsync(ct)==0){await transaction.RollbackAsync(ct);return;}
        }
        if(update.Command is not("/start" or "/stop")){await transaction.CommitAsync(ct);return;}
        var subscriberKey=LookupKey(update.ChatId);var encryptedChatId=_protector.Protect(FormatChatId(update.ChatId));
        string? before;
        await using(var read=connection.CreateCommand())
        {
            read.Transaction=(SqliteTransaction)transaction;read.CommandText="SELECT state FROM telegram_subscribers WHERE subscriber_key=$key";
            read.Parameters.AddWithValue("$key",subscriberKey);before=await read.ExecuteScalarAsync(ct) as string;
        }
        var state=update.Command=="/stop"?TelegramSubscriberState.Disabled:TelegramSubscriberState.Pending;
        await using(var command=connection.CreateCommand())
        {
            command.Transaction=(SqliteTransaction)transaction;
            command.CommandText="""
                INSERT INTO telegram_subscribers(subscriber_key,encrypted_chat_id,chat_type,state,event_kinds,first_seen_at,updated_at)
                VALUES($key,$encrypted,$type,$state,'[]',$now,$now)
                ON CONFLICT(subscriber_key) DO UPDATE SET encrypted_chat_id=excluded.encrypted_chat_id,chat_type=excluded.chat_type,
                  state=CASE WHEN excluded.state='Disabled' THEN 'Disabled' WHEN telegram_subscribers.state='Disabled' THEN 'Pending' ELSE telegram_subscribers.state END,
                  updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key",subscriberKey);command.Parameters.AddWithValue("$encrypted",encryptedChatId);command.Parameters.AddWithValue("$type",update.ChatType);
            command.Parameters.AddWithValue("$state",state.ToString());command.Parameters.AddWithValue("$now",Iso(now));
            await command.ExecuteNonQueryAsync(ct);
        }
        string after;
        await using(var read=connection.CreateCommand())
        {
            read.Transaction=(SqliteTransaction)transaction;read.CommandText="SELECT state FROM telegram_subscribers WHERE subscriber_key=$key";
            read.Parameters.AddWithValue("$key",subscriberKey);after=(string)(await read.ExecuteScalarAsync(ct)??throw new InvalidOperationException("Subscriber state was not persisted."));
        }
        await AuditAsync(connection,(SqliteTransaction)transaction,subscriberKey,"BotCommand",before,after,update.Command,now,ct);
        await transaction.CommitAsync(ct);
    }

    public Task ApproveAsync(long chatId,IReadOnlySet<NotificationEventKind> kinds,string actor,CancellationToken ct)=>
        ChangeStateAsync(LookupKey(chatId),TelegramSubscriberState.Approved,kinds,actor,"Approve",ct);
    public Task DisableAsync(long chatId,string actor,CancellationToken ct)=>
        ChangeStateAsync(LookupKey(chatId),TelegramSubscriberState.Disabled,new HashSet<NotificationEventKind>(),actor,"Disable",ct);
    public Task ApproveAsync(string subscriberKey,IReadOnlySet<NotificationEventKind> kinds,string actor,CancellationToken ct)=>
        ChangeStateAsync(ValidateKey(subscriberKey),TelegramSubscriberState.Approved,kinds,actor,"Approve",ct);
    public Task DisableAsync(string subscriberKey,string actor,CancellationToken ct)=>
        ChangeStateAsync(ValidateKey(subscriberKey),TelegramSubscriberState.Disabled,new HashSet<NotificationEventKind>(),actor,"Disable",ct);

    public async Task<IReadOnlyList<TelegramSubscriber>> ListAsync(CancellationToken ct)
    {
        await using var connection=await OpenAsync(ct);await using var command=connection.CreateCommand();
        command.CommandText="SELECT subscriber_key,chat_type,state,event_kinds,first_seen_at,updated_at FROM telegram_subscribers ORDER BY first_seen_at,subscriber_key";
        await using var reader=await command.ExecuteReaderAsync(ct);var values=new List<TelegramSubscriber>();
        while(await reader.ReadAsync(ct))values.Add(new(reader.GetString(0),reader.GetString(1),Enum.Parse<TelegramSubscriberState>(reader.GetString(2)),ParseKinds(reader.GetString(3)),Parse(reader.GetString(4)),Parse(reader.GetString(5))));
        return values;
    }

    public async Task<int> EnqueueApprovedAsync(ConfirmedNotificationEvent value,CancellationToken ct)
    {
        var now=_clock.UtcNow;var payload=JsonSerializer.Serialize(value);var count=0;
        await using var connection=await OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        await using var query=connection.CreateCommand();query.Transaction=(SqliteTransaction)transaction;
        query.CommandText="SELECT subscriber_key,event_kinds FROM telegram_subscribers WHERE state='Approved'";
        await using var reader=await query.ExecuteReaderAsync(ct);var targets=new List<string>();
        while(await reader.ReadAsync(ct))if(ParseKinds(reader.GetString(1)).Contains(value.Kind))targets.Add(reader.GetString(0));
        await reader.DisposeAsync();
        foreach(var subscriberKey in targets)
        {
            await using var insert=connection.CreateCommand();insert.Transaction=(SqliteTransaction)transaction;
            insert.CommandText="""
                INSERT OR IGNORE INTO telegram_subscriber_outbox(event_key,subscriber_key,payload,state,attempts,next_attempt_at,created_at,updated_at)
                VALUES($key,$subscriber,$payload,'Queued',0,$now,$now,$now)
                """;
            insert.Parameters.AddWithValue("$key",value.EventKey);insert.Parameters.AddWithValue("$subscriber",subscriberKey);
            insert.Parameters.AddWithValue("$payload",payload);insert.Parameters.AddWithValue("$now",Iso(now));count+=await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);return count;
    }

    public async Task<IReadOnlyList<(long Id,long ChatId,ConfirmedNotificationEvent Event,int Attempts)>> ClaimDueAsync(int limit,CancellationToken ct)
    {
        var now=_clock.UtcNow;await using var connection=await OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        await using var command=connection.CreateCommand();command.Transaction=(SqliteTransaction)transaction;
        command.CommandText="""
            UPDATE telegram_subscriber_outbox SET state='Sending',attempts=attempts+1,updated_at=$now
            WHERE id IN (SELECT o.id FROM telegram_subscriber_outbox o JOIN telegram_subscribers s ON s.subscriber_key=o.subscriber_key
              WHERE o.state IN ('Queued','Failed') AND o.next_attempt_at<=$now AND s.state='Approved' ORDER BY o.id LIMIT $limit)
            RETURNING id,subscriber_key,payload,attempts;
            """;
        command.Parameters.AddWithValue("$now",Iso(now));command.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));
        await using var reader=await command.ExecuteReaderAsync(ct);var claimed=new List<(long Id,string SubscriberKey,ConfirmedNotificationEvent Event,int Attempts)>();
        while(await reader.ReadAsync(ct))
        {
            claimed.Add((reader.GetInt64(0),reader.GetString(1),JsonSerializer.Deserialize<ConfirmedNotificationEvent>(reader.GetString(2))!,reader.GetInt32(3)));
        }
        await reader.DisposeAsync();var result=new List<(long,long,ConfirmedNotificationEvent,int)>(claimed.Count);
        foreach(var item in claimed)result.Add((item.Id,await ReadChatIdAsync(connection,(SqliteTransaction)transaction,item.SubscriberKey,ct),item.Event,item.Attempts));
        await transaction.CommitAsync(ct);return result;
    }

    public Task MarkSentAsync(long id,CancellationToken ct)=>ExecuteAsync("UPDATE telegram_subscriber_outbox SET state='Sent',updated_at=$now WHERE id=$id",ct,("$id",id),("$now",Iso(_clock.UtcNow)));
    public Task MarkFailedAsync(long id,string diagnostic,int attempts,CancellationToken ct)
    {
        var dead=attempts>=5;var retry=_clock.UtcNow.AddSeconds(Math.Min(3600,5*Math.Pow(2,Math.Max(0,attempts-1))));
        var code="WPE-TG-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(diagnostic)))[..12];
        return ExecuteAsync("UPDATE telegram_subscriber_outbox SET state=$state,next_attempt_at=$next,diagnostic_code=$code,updated_at=$now WHERE id=$id",ct,("$id",id),("$state",dead?"DeadLetter":"Failed"),("$next",Iso(retry)),("$code",code),("$now",Iso(_clock.UtcNow)));
    }

    public async Task<long> ReadNextUpdateOffsetAsync(CancellationToken ct)
    {
        await using var connection=await OpenAsync(ct);await using var command=connection.CreateCommand();command.CommandText="SELECT COALESCE(MAX(update_id)+1,0) FROM telegram_updates";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
    }

    private async Task ChangeStateAsync(string subscriberKey,TelegramSubscriberState state,IReadOnlySet<NotificationEventKind> kinds,string actor,string action,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(actor)||actor.Length>80)throw new ArgumentException("Actor is required.",nameof(actor));
        if(state==TelegramSubscriberState.Approved&&kinds.Count==0)throw new ArgumentException("At least one event kind is required.",nameof(kinds));
        var now=_clock.UtcNow;var json=JsonSerializer.Serialize(kinds.OrderBy(x=>x.ToString()).Select(x=>x.ToString()));
        await using var connection=await OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        string? before=null;await using(var read=connection.CreateCommand()){read.Transaction=(SqliteTransaction)transaction;read.CommandText="SELECT state FROM telegram_subscribers WHERE subscriber_key=$key";read.Parameters.AddWithValue("$key",subscriberKey);before=await read.ExecuteScalarAsync(ct) as string;}
        if(before is null)throw new KeyNotFoundException("Telegram subscriber was not found.");
        await using(var update=connection.CreateCommand()){update.Transaction=(SqliteTransaction)transaction;update.CommandText="UPDATE telegram_subscribers SET state=$state,event_kinds=$kinds,updated_at=$now WHERE subscriber_key=$key";update.Parameters.AddWithValue("$state",state.ToString());update.Parameters.AddWithValue("$kinds",json);update.Parameters.AddWithValue("$now",Iso(now));update.Parameters.AddWithValue("$key",subscriberKey);await update.ExecuteNonQueryAsync(ct);}
        await AuditAsync(connection,(SqliteTransaction)transaction,subscriberKey,actor,before,state.ToString(),action,now,ct);await transaction.CommitAsync(ct);
    }

    private void Initialize()
    {
        using var connection=new SqliteConnection(_connectionString);connection.Open();
        using(var secureDelete=connection.CreateCommand()){secureDelete.CommandText="PRAGMA secure_delete=ON";secureDelete.ExecuteNonQuery();}
        if(HasLegacyPlaintextSchema(connection))
        {
            using var remove=connection.CreateCommand();remove.CommandText="""
                DROP TRIGGER IF EXISTS telegram_subscriber_audit_no_update;
                DROP TRIGGER IF EXISTS telegram_subscriber_audit_no_delete;
                DROP TABLE IF EXISTS telegram_subscriber_outbox;
                DROP TABLE IF EXISTS telegram_subscriber_audit;
                DROP TABLE IF EXISTS telegram_subscribers;
                """;remove.ExecuteNonQuery();
        }
        using var command=connection.CreateCommand();command.CommandText="""
            CREATE TABLE IF NOT EXISTS telegram_subscribers(subscriber_key TEXT PRIMARY KEY,encrypted_chat_id TEXT NOT NULL,chat_type TEXT NOT NULL,state TEXT NOT NULL,event_kinds TEXT NOT NULL,first_seen_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS telegram_updates(update_id INTEGER PRIMARY KEY,processed_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS telegram_subscriber_audit(id INTEGER PRIMARY KEY AUTOINCREMENT,subscriber_key TEXT NOT NULL,actor TEXT NOT NULL,before_state TEXT,after_state TEXT NOT NULL,action TEXT NOT NULL,occurred_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS telegram_subscriber_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT,event_key TEXT NOT NULL,subscriber_key TEXT NOT NULL,payload TEXT NOT NULL,state TEXT NOT NULL,attempts INTEGER NOT NULL,next_attempt_at TEXT NOT NULL,diagnostic_code TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(event_key,subscriber_key));
            CREATE INDEX IF NOT EXISTS ix_telegram_subscriber_outbox_due ON telegram_subscriber_outbox(state,next_attempt_at);
            CREATE TRIGGER IF NOT EXISTS telegram_subscriber_audit_no_update BEFORE UPDATE ON telegram_subscriber_audit BEGIN SELECT RAISE(ABORT,'telegram subscriber audit is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS telegram_subscriber_audit_no_delete BEFORE DELETE ON telegram_subscriber_audit BEGIN SELECT RAISE(ABORT,'telegram subscriber audit is append-only'); END;
            """;command.ExecuteNonQuery();
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct){var value=new SqliteConnection(_connectionString);await value.OpenAsync(ct);return value;}
    private async Task<long> ReadChatIdAsync(SqliteConnection connection,SqliteTransaction transaction,string subscriberKey,CancellationToken ct)
    {
        await using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT encrypted_chat_id FROM telegram_subscribers WHERE subscriber_key=$key";command.Parameters.AddWithValue("$key",subscriberKey);
        var encrypted=await command.ExecuteScalarAsync(ct) as string??throw new InvalidOperationException("Subscriber destination is unavailable.");
        var plaintext=_protector.Unprotect(encrypted);
        return long.TryParse(plaintext,NumberStyles.Integer,CultureInfo.InvariantCulture,out var chatId)&&chatId!=0?chatId:throw new CryptographicException("Subscriber destination is invalid.");
    }
    private static bool HasLegacyPlaintextSchema(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();command.CommandText="SELECT 1 FROM pragma_table_info('telegram_subscribers') WHERE name='chat_id' LIMIT 1";
        return command.ExecuteScalar() is not null;
    }
    private static Task AuditAsync(SqliteConnection c,SqliteTransaction tx,string subscriberKey,string actor,string? before,string after,string action,DateTime now,CancellationToken ct){var command=c.CreateCommand();command.Transaction=tx;command.CommandText="INSERT INTO telegram_subscriber_audit(subscriber_key,actor,before_state,after_state,action,occurred_at) VALUES($key,$actor,$before,$after,$action,$now)";command.Parameters.AddWithValue("$key",subscriberKey);command.Parameters.AddWithValue("$actor",actor);command.Parameters.AddWithValue("$before",(object?)before??DBNull.Value);command.Parameters.AddWithValue("$after",after);command.Parameters.AddWithValue("$action",action);command.Parameters.AddWithValue("$now",Iso(now));return command.ExecuteNonQueryAsync(ct);}
    private async Task ExecuteAsync(string sql,CancellationToken ct,params (string,object)[] values){await using var connection=await OpenAsync(ct);await using var command=connection.CreateCommand();command.CommandText=sql;foreach(var (name,value) in values)command.Parameters.AddWithValue(name,value);await command.ExecuteNonQueryAsync(ct);}
    private static IReadOnlySet<NotificationEventKind> ParseKinds(string json)=>(JsonSerializer.Deserialize<string[]>(json)??[]).Select(x=>Enum.TryParse<NotificationEventKind>(x,true,out var value)?value:(NotificationEventKind?)null).OfType<NotificationEventKind>().ToHashSet();
    private static string LookupKey(long chatId)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormatChatId(chatId))));
    private static string FormatChatId(long chatId)=>chatId.ToString(CultureInfo.InvariantCulture);
    private static string ValidateKey(string value)=>value.Length==64&&value.All(Uri.IsHexDigit)?value.ToUpperInvariant():throw new ArgumentException("Subscriber key is invalid.",nameof(value));
    private static string Iso(DateTime value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTime Parse(string value)=>DateTime.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();
}

public sealed class TelegramSubscriptionPoller(TelegramSubscriberStore store,ITelegramBotUpdateSource source,INotificationConfigurationProvider configuration)
{
    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        var telegram=configuration.GetEnabled().SingleOrDefault(x=>x.Channel==NotificationChannel.Telegram&&x.Enabled);
        if(telegram is null)return 0;
        var updates=await source.GetUpdatesAsync(telegram.AccessToken,await store.ReadNextUpdateOffsetAsync(ct),10,ct);
        foreach(var update in updates)await store.ApplyUpdateAsync(update,ct);
        return updates.Count;
    }
    public async Task RunAsync(CancellationToken ct){while(!ct.IsCancellationRequested){try{await PollOnceAsync(ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{/* isolated */}await Task.Delay(TimeSpan.FromSeconds(5),ct);}}
}

public sealed class TelegramSubscriberNotificationPublisher(TelegramSubscriberStore store):INotificationEventPublisher
{
    public async Task PublishAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct)=>_ = await store.EnqueueApprovedAsync(notificationEvent,ct);
}

public sealed class CompositeNotificationPublisher(params INotificationEventPublisher[] publishers):INotificationEventPublisher
{
    public async Task PublishAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct)
    {
        foreach(var publisher in publishers)
        {
            try{await publisher.PublishAsync(notificationEvent,ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch{/* One notification destination must not suppress the others. */}
        }
    }
}

public sealed class TelegramSubscriberDispatcher(TelegramSubscriberStore store,INotificationConfigurationProvider configuration,INotificationTransport transport,NotificationFormatter? formatter=null)
{
    private readonly NotificationFormatter _formatter=formatter??new NotificationFormatter();
    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var telegram=configuration.GetEnabled().SingleOrDefault(x=>x.Channel==NotificationChannel.Telegram&&x.Enabled);if(telegram is null)return 0;
        var sent=0;foreach(var item in await store.ClaimDueAsync(50,ct))try{await transport.SendAsync(_formatter.Format(item.Event),telegram with{Destination=item.ChatId.ToString(CultureInfo.InvariantCulture)},ct);await store.MarkSentAsync(item.Id,ct);sent++;}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception ex){await store.MarkFailedAsync(item.Id,ex.ToString(),item.Attempts,ct);}return sent;
    }
    public async Task RunAsync(CancellationToken ct){while(!ct.IsCancellationRequested){try{await DispatchOnceAsync(ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{/* isolated */}await Task.Delay(TimeSpan.FromSeconds(5),ct);}}
}

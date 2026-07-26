using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services;

namespace WpeAgent.Notifications;

public sealed class NotificationOutboxStore
{
    private readonly string _connectionString;
    private readonly INotificationClock _clock;

    public NotificationOutboxStore(string? databasePath=null,INotificationClock? clock=null)
    {
        var path=databasePath??AppDataPaths.File("notification-outbox.db");
        var directory=Path.GetDirectoryName(path);
        if(!string.IsNullOrWhiteSpace(directory))Directory.CreateDirectory(directory);
        _connectionString=new SqliteConnectionStringBuilder
        {
            DataSource=path,
            Mode=SqliteOpenMode.ReadWriteCreate,
            Cache=SqliteCacheMode.Shared
        }.ToString();
        _clock=clock??SystemNotificationClock.Instance;
        Initialize();
    }

    public async Task<bool> EnqueueAsync(NotificationEnvelope envelope,CancellationToken ct)
    {
        var now=_clock.UtcNow;
        await using var connection=await OpenAsync(ct);
        await using var command=connection.CreateCommand();
        command.CommandText="""
            INSERT OR IGNORE INTO notification_outbox(
                event_key,channel,payload_json,state,attempts,next_attempt_at,last_diagnostic_code,
                lease_until,created_at,updated_at)
            VALUES($key,$channel,$payload,'Queued',0,$next,NULL,NULL,$created,$updated);
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$key",envelope.Event.EventKey);
        command.Parameters.AddWithValue("$channel",envelope.Channel.ToString());
        command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(envelope.Event));
        command.Parameters.AddWithValue("$next",Iso(now));
        command.Parameters.AddWithValue("$created",Iso(now));
        command.Parameters.AddWithValue("$updated",Iso(now));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==1;
    }

    public async Task<IReadOnlyList<NotificationOutboxItem>> ClaimDueAsync(
        int limit,TimeSpan lease,CancellationToken ct)
    {
        var now=_clock.UtcNow;
        await using var connection=await OpenAsync(ct);
        await using var transaction=connection.BeginTransaction(IsolationLevel.Serializable);
        await using(var recover=connection.CreateCommand())
        {
            recover.Transaction=transaction;
            recover.CommandText="""
                UPDATE notification_outbox
                SET state='Failed',lease_until=NULL,next_attempt_at=$now,updated_at=$now,
                    last_diagnostic_code=COALESCE(last_diagnostic_code,'WPE-NOTIFY-LEASE-EXPIRED')
                WHERE state='Sending' AND lease_until IS NOT NULL AND lease_until<=$now;
                """;
            recover.Parameters.AddWithValue("$now",Iso(now));
            await recover.ExecuteNonQueryAsync(ct);
        }

        var ids=new List<long>();
        await using(var select=connection.CreateCommand())
        {
            select.Transaction=transaction;
            select.CommandText="""
                SELECT id FROM notification_outbox
                WHERE state IN ('Queued','Failed') AND next_attempt_at<=$now
                ORDER BY next_attempt_at,id LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$now",Iso(now));
            select.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));
            await using var reader=await select.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))ids.Add(reader.GetInt64(0));
        }

        var claimed=new List<NotificationOutboxItem>();
        foreach(var id in ids)
        {
            await using(var update=connection.CreateCommand())
            {
                update.Transaction=transaction;
                update.CommandText="""
                    UPDATE notification_outbox SET state='Sending',attempts=attempts+1,
                        lease_until=$lease,updated_at=$now WHERE id=$id;
                    """;
                update.Parameters.AddWithValue("$lease",Iso(now+lease));
                update.Parameters.AddWithValue("$now",Iso(now));
                update.Parameters.AddWithValue("$id",id);
                await update.ExecuteNonQueryAsync(ct);
            }
            claimed.Add(await ReadByIdAsync(connection,transaction,id,ct));
        }
        await transaction.CommitAsync(ct);
        return claimed;
    }

    public async Task MarkSentAsync(long id,CancellationToken ct)
    {
        var now=_clock.UtcNow;
        await ExecuteAsync("""
            UPDATE notification_outbox SET state='Sent',lease_until=NULL,
                last_diagnostic_code=NULL,updated_at=$now WHERE id=$id AND state='Sending';
            """,ct,("$id",id),("$now",Iso(now)));
    }

    public async Task MarkFailedAsync(
        long id,string diagnosticCode,DateTime nextAttemptAtUtc,bool deadLetter,CancellationToken ct)
    {
        var now=_clock.UtcNow;
        await ExecuteAsync("""
            UPDATE notification_outbox SET state=$state,lease_until=NULL,
                next_attempt_at=$next,last_diagnostic_code=$code,updated_at=$now
            WHERE id=$id AND state='Sending';
            """,ct,
            ("$state",deadLetter?"DeadLetter":"Failed"),
            ("$next",Iso(nextAttemptAtUtc)),
            ("$code",SensitiveDataRedactor.ForLog(diagnosticCode,80)),
            ("$now",Iso(now)),
            ("$id",id));
    }

    public async Task<IReadOnlyList<NotificationOutboxItem>> ListAsync(CancellationToken ct)
    {
        await using var connection=await OpenAsync(ct);
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT id FROM notification_outbox ORDER BY id";
        var result=new List<NotificationOutboxItem>();
        await using var reader=await command.ExecuteReaderAsync(ct);
        var ids=new List<long>();while(await reader.ReadAsync(ct))ids.Add(reader.GetInt64(0));
        await reader.DisposeAsync();
        foreach(var id in ids)result.Add(await ReadByIdAsync(connection,null,id,ct));
        return result;
    }

    public async Task<NotificationOutboxProjection> ReadProjectionAsync(int recentLimit=20,int maxAttempts=5,CancellationToken ct=default)
    {
        await using var connection=await OpenAsync(ct);
        await using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT SUM(CASE WHEN state IN ('Queued','Sending') THEN 1 ELSE 0 END),
              SUM(CASE WHEN state='Failed' THEN 1 ELSE 0 END),SUM(CASE WHEN state='Sent' THEN 1 ELSE 0 END),
              SUM(CASE WHEN state='DeadLetter' THEN 1 ELSE 0 END) FROM notification_outbox;
            SELECT id,channel,payload_json,state,attempts,next_attempt_at,last_diagnostic_code,updated_at
              FROM notification_outbox ORDER BY updated_at DESC,id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit",Math.Clamp(recentLimit,1,100));
        await using var reader=await command.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);
        var summary=new NotificationOutboxSummary(reader.IsDBNull(0)?0:reader.GetInt32(0),reader.IsDBNull(1)?0:reader.GetInt32(1),reader.IsDBNull(2)?0:reader.GetInt32(2),reader.IsDBNull(3)?0:reader.GetInt32(3));
        var recent=new List<NotificationOutboxSafeRow>();await reader.NextResultAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var notification=JsonSerializer.Deserialize<ConfirmedNotificationEvent>(reader.GetString(2))??throw new InvalidOperationException("Notification outbox payload is invalid.");
            var state=Enum.Parse<NotificationOutboxState>(reader.GetString(3),true);
            recent.Add(new(reader.GetInt64(0),Enum.Parse<NotificationChannel>(reader.GetString(1),true),notification.Kind,
                state switch{NotificationOutboxState.Queued or NotificationOutboxState.Sending=>NotificationUiState.Pending,NotificationOutboxState.Failed=>NotificationUiState.Retrying,NotificationOutboxState.Sent=>NotificationUiState.Sent,_=>NotificationUiState.DeadLetter},
                state==NotificationOutboxState.Sending,reader.GetInt32(4),Math.Clamp(maxAttempts,1,20),notification.OccurredAtUtc,Parse(reader.GetString(5)),Parse(reader.GetString(7)),reader.IsDBNull(6)?null:reader.GetString(6)));
        }
        return new(summary,recent);
    }

    public async Task<int> PurgeTerminalAsync(TimeSpan sentRetention,TimeSpan deadLetterRetention,int limit=500,CancellationToken ct=default)
    {
        if(sentRetention<TimeSpan.FromDays(1)||sentRetention>TimeSpan.FromDays(365)||deadLetterRetention<TimeSpan.FromDays(7)||deadLetterRetention>TimeSpan.FromDays(730))throw new ArgumentOutOfRangeException(nameof(sentRetention),"Notification retention is outside the supported range.");
        var now=_clock.UtcNow;await using var connection=await OpenAsync(ct);await using var transaction=connection.BeginTransaction(IsolationLevel.Serializable);await using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            DELETE FROM notification_outbox WHERE id IN (SELECT id FROM notification_outbox
              WHERE (state='Sent' AND updated_at<$sentBefore) OR (state='DeadLetter' AND updated_at<$deadBefore)
              ORDER BY updated_at LIMIT $limit); SELECT changes();
            """;
        command.Parameters.AddWithValue("$sentBefore",Iso(now-sentRetention));command.Parameters.AddWithValue("$deadBefore",Iso(now-deadLetterRetention));command.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,5000));
        var deleted=Convert.ToInt32(await command.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);await transaction.CommitAsync(ct);return deleted;
    }

    private void Initialize()
    {
        using var connection=new SqliteConnection(_connectionString);
        connection.Open();
        using var command=connection.CreateCommand();
        command.CommandText="""
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS notification_outbox(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_key TEXT NOT NULL,
                channel TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                state TEXT NOT NULL,
                attempts INTEGER NOT NULL,
                next_attempt_at TEXT NOT NULL,
                last_diagnostic_code TEXT,
                lease_until TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(event_key,channel));
            CREATE INDEX IF NOT EXISTS ix_notification_outbox_due
                ON notification_outbox(state,next_attempt_at);
            """;
        command.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection=new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var pragma=connection.CreateCommand();
        pragma.CommandText="PRAGMA busy_timeout=5000";
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private static async Task<NotificationOutboxItem> ReadByIdAsync(
        SqliteConnection connection,SqliteTransaction? transaction,long id,CancellationToken ct)
    {
        await using var command=connection.CreateCommand();
        command.Transaction=transaction;
        command.CommandText="""
            SELECT event_key,channel,payload_json,state,attempts,next_attempt_at,
                   last_diagnostic_code,created_at,updated_at
            FROM notification_outbox WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id",id);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw new InvalidOperationException("Notification outbox item was not found.");
        var value=JsonSerializer.Deserialize<ConfirmedNotificationEvent>(reader.GetString(2))
            ??throw new InvalidOperationException("Notification outbox payload is invalid.");
        return new(
            id,reader.GetString(0),Enum.Parse<NotificationChannel>(reader.GetString(1),true),value,
            Enum.Parse<NotificationOutboxState>(reader.GetString(3),true),reader.GetInt32(4),
            Parse(reader.GetString(5)),reader.IsDBNull(6)?null:reader.GetString(6),
            Parse(reader.GetString(7)),Parse(reader.GetString(8)));
    }

    private async Task ExecuteAsync(string sql,CancellationToken ct,params (string Name,object? Value)[] values)
    {
        await using var connection=await OpenAsync(ct);
        await using var command=connection.CreateCommand();
        command.CommandText=sql;
        foreach(var value in values)command.Parameters.AddWithValue(value.Name,value.Value??DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Iso(DateTime value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTime Parse(string value)=>DateTime.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();
}

public sealed class NotificationOutboxPublisher(
    NotificationOutboxStore store,
    INotificationConfigurationProvider configuration,
    INotificationClock? clock=null):INotificationEventPublisher
{
    private readonly INotificationClock _clock=clock??SystemNotificationClock.Instance;
    public async Task PublishAsync(ConfirmedNotificationEvent notificationEvent,CancellationToken ct)
    {
        foreach(var channel in configuration.GetEnabled().Where(x=>x.Enabled))
        {
            if(channel.AllowedEventKinds is not{Count:>0}||!channel.AllowedEventKinds.Contains(notificationEvent.Kind))continue;
            if(IsQuietHours(channel,_clock.UtcNow))continue;
            await store.EnqueueAsync(new(notificationEvent,channel.Channel),ct);
        }
    }

    private static bool IsQuietHours(NotificationChannelConfiguration value,DateTime utcNow)
    {
        if(!value.QuietHoursEnabled||value.QuietHoursStart is null||value.QuietHoursEnd is null)return false;
        TimeZoneInfo zone;try{zone=TimeZoneInfo.FindSystemTimeZoneById(value.QuietHoursTimeZone);}catch{return true;}
        var local=TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow,zone));
        var start=value.QuietHoursStart.Value;var end=value.QuietHoursEnd.Value;
        return start<=end?local>=start&&local<end:local>=start||local<end;
    }
}

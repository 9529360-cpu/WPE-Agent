namespace 币安量化机器人.Services.Agent;
using WpeAgent.Notifications;

public sealed class TeacherCryptoEvidenceSchedulerV2:IDisposable
{
    private readonly AgentSqliteStore _store;private readonly TeacherPublicEvidenceGatewayV2 _gateway;private readonly TeacherCryptoEvidenceCollectorV2 _collector;private readonly Func<DateTimeOffset> _utcNow;private readonly TimeSpan _interval;private readonly TeacherLessonNotificationPublisherV2? _publisher;private readonly string _provider;private readonly string _environment;
    public TeacherCryptoEvidenceSchedulerV2(AgentSqliteStore store,IConfirmedNotificationObserver? observer=null,Func<NotificationEventKind,bool>? isAllowed=null,string provider="binance-futures-public",string environment="PublicReadOnly",TimeSpan? interval=null,Func<DateTimeOffset>? utcNow=null)
    {
        _store=store;_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);_interval=interval??TimeSpan.FromMinutes(5);_gateway=new([TeacherBinancePublicEvidenceAdapterV2.Source],_utcNow);_collector=new(new(_gateway),store,_utcNow);_publisher=observer is not null&&isAllowed is not null?new(store,observer,isAllowed):null;_provider=provider;_environment=environment;
    }
    internal TeacherCryptoEvidenceSchedulerV2(AgentSqliteStore store,TeacherPublicEvidenceGatewayV2 gateway,TimeSpan interval,Func<DateTimeOffset> utcNow){_store=store;_gateway=gateway;_interval=interval;_utcNow=utcNow;_collector=new(new(gateway),store,utcNow);_provider="binance-futures-public";_environment="PublicReadOnly";}
    public Task StartAsync(IReadOnlyList<string> symbols,CancellationToken ct)=>Task.Run(()=>RunAsync(symbols,ct),CancellationToken.None);
    private async Task RunAsync(IReadOnlyList<string> symbols,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested){try{await RefreshOnceAsync(symbols,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception ex){await _store.RecordErrorAsync("TEACHER_CRYPTO_PUBLIC",ex,CancellationToken.None);}await Task.Delay(_interval,ct);}
    }
    internal async Task<IReadOnlyList<TeacherCryptoRefreshResultV2>> RefreshOnceAsync(IReadOnlyList<string> symbols,CancellationToken ct)
    {
        var results=new List<TeacherCryptoRefreshResultV2>();foreach(var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase).Take(4)){var now=_utcNow().ToUniversalTime();var previous=await _store.GetLatestTeacherCryptoEvidenceAsync(symbol,now,TimeSpan.FromMinutes(30),ct);var budget=new TeacherNetworkBudgetStateV2(new(3,768_000,0,TimeSpan.FromSeconds(20)),now);var result=await _collector.RefreshAsync(symbol,budget,ct);results.Add(result);if(previous is null||result.EvidenceHash is null)continue;var current=await _store.GetLatestTeacherCryptoEvidenceAsync(symbol,_utcNow(),TimeSpan.FromMinutes(5),ct);if(current is null||current.CanonicalSha256==previous.CanonicalSha256)continue;var marketEvent=TeacherCryptoEventDetectorV2.Detect(previous,current);if(marketEvent is null)continue;var cycle=await _store.GetLatestTeacherEligibleCycleIdAsync(ct);if(cycle is null)continue;var agents=await _store.GetTeacherEvidenceForCycleAsync(cycle,_utcNow(),ct);var lesson=TeacherEventLessonComposerV2.Compose(marketEvent,agents,_utcNow());if(await _store.TryClaimTeacherEventAsync(marketEvent,TimeSpan.FromHours(1),ct)){var saved=await _store.SaveTeacherLessonAsync(lesson,ct);if(saved.Succeeded&&_publisher is not null)await _publisher.PublishIfAllowedAsync(lesson,_provider,_environment,ct);}}
        return results;
    }
    public void Dispose()=>_gateway.Dispose();
}

using System.IO;
using System.Text.Json;
using WpeAgent.RuntimeServices;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services;

public sealed record HeadlessRuntimeObservationV1(
    bool Available,
    bool Ready,
    string State,
    string Code,
    DateTimeOffset? ObservedAtUtc,
    bool AgentRunning,
    string AgentStatus,
    string RunId,
    DateTimeOffset? RuntimeHeartbeatAtUtc,
    string RecoveryStatus,
    long EventSequence,
    bool AccessFresh,
    bool HeartbeatFresh,
    bool LeaseLost,
    string? Diagnostic)
{
    public static HeadlessRuntimeObservationV1 Unavailable(string diagnostic)=>
        new(false,false,"unavailable","headless.unavailable",null,false,"Stopped",string.Empty,null,"UNKNOWN",0,false,false,false,diagnostic);
}

public sealed class HeadlessRuntimeHealthReader
{
    public static readonly TimeSpan MaximumHealthAge=TimeSpan.FromSeconds(15);
    private const long MaximumHealthBytes=64*1024;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _utcNow;

    public HeadlessRuntimeHealthReader(string? path=null,Func<DateTimeOffset>? utcNow=null)
    {
        _path=Path.GetFullPath(path??AppDataPaths.RuntimeFile("headless-health-v1.json"));
        _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }

    public HeadlessRuntimeObservationV1 Read()
    {
        try
        {
            var info=new FileInfo(_path);
            if(!info.Exists||info.Length<=0||info.Length>MaximumHealthBytes)
                return HeadlessRuntimeObservationV1.Unavailable("Headless health evidence is missing.");

            using var document=JsonDocument.Parse(File.ReadAllText(_path));
            var root=document.RootElement;
            if(!root.TryGetProperty("schema",out var schema)||
               !string.Equals(schema.GetString(),"wpe.headless-process-health/1.0",StringComparison.Ordinal))
                return HeadlessRuntimeObservationV1.Unavailable("Headless health schema is unsupported.");
            if(!TryInstant(root,"observedAtUtc",out var observedAt))
                return HeadlessRuntimeObservationV1.Unavailable("Headless observation time is invalid.");

            var now=_utcNow().ToUniversalTime();
            if(observedAt>now||now-observedAt>MaximumHealthAge)
                return new(true,false,Text(root,"state","stale"),Text(root,"code","headless.stale"),observedAt,false,"Stopped",string.Empty,null,"STALE",0,false,false,false,"Headless health evidence is stale.");
            if(!root.TryGetProperty("runtime",out var runtime)||runtime.ValueKind!=JsonValueKind.Object)
                return HeadlessRuntimeObservationV1.Unavailable("Headless runtime health is missing.");

            _=TryInstant(runtime,"runtimeHeartbeatAtUtc",out var heartbeat);
            var agentRunning=Bool(runtime,"agentRunning");
            var accessFresh=Bool(runtime,"accessFresh");
            var heartbeatFresh=Bool(runtime,"heartbeatFresh");
            var leaseLost=Bool(runtime,"leaseLost");
            var runtimeReady=Bool(runtime,"ready");
            var state=Text(root,"state","unknown");
            var ready=string.Equals(state,"ready",StringComparison.OrdinalIgnoreCase)&&runtimeReady&&agentRunning&&accessFresh&&heartbeatFresh&&!leaseLost;

            return new(
                true,
                ready,
                state,
                Text(root,"code","headless.unknown"),
                observedAt,
                agentRunning,
                Text(runtime,"agentStatus",agentRunning?"Running":"Stopped"),
                Text(runtime,"runId",string.Empty),
                heartbeat==default?null:heartbeat,
                Text(runtime,"recoveryStatus","UNKNOWN"),
                Long(runtime,"eventSequence"),
                accessFresh,
                heartbeatFresh,
                leaseLost,
                ready?null:"Headless runtime is present but not ready.");
        }
        catch(JsonException)
        {
            return HeadlessRuntimeObservationV1.Unavailable("Headless health evidence is malformed.");
        }
        catch(IOException)
        {
            return HeadlessRuntimeObservationV1.Unavailable("Headless health evidence could not be read.");
        }
        catch(UnauthorizedAccessException)
        {
            return HeadlessRuntimeObservationV1.Unavailable("Headless health evidence is inaccessible.");
        }
    }

    private static string Text(JsonElement element,string name,string fallback)=>
        element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String
            ?value.GetString()??fallback
            :fallback;
    private static bool Bool(JsonElement element,string name)=>
        element.TryGetProperty(name,out var value)&&(value.ValueKind is JsonValueKind.True or JsonValueKind.False)&&value.GetBoolean();
    private static long Long(JsonElement element,string name)=>
        element.TryGetProperty(name,out var value)&&value.TryGetInt64(out var parsed)?parsed:0;
    private static bool TryInstant(JsonElement element,string name,out DateTimeOffset value)
    {
        value=default;
        return element.TryGetProperty(name,out var raw)&&
               raw.ValueKind==JsonValueKind.String&&
               DateTimeOffset.TryParse(raw.GetString(),out value);
    }
}

public sealed class HeadlessDesktopObserver:IAsyncDisposable
{
    private static readonly TimeSpan PollInterval=TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CapabilityRefreshInterval=TimeSpan.FromMinutes(2);
    private readonly AgentSettingsStore _settingsStore;
    private readonly HeadlessRuntimeHealthReader _health;
    private readonly CancellationTokenSource _shutdown=new();
    private IProviderReadOnlyAccessClient? _client;
    private IReadOnlyList<string> _symbols=Array.Empty<string>();
    private DateTimeOffset _nextCapabilityRefreshAtUtc=DateTimeOffset.MinValue;
    private Task? _loop;
    private int _active;

    public HeadlessDesktopObserver(
        AgentSettingsStore? settingsStore=null,
        HeadlessRuntimeHealthReader? health=null)
    {
        _settingsStore=settingsStore??new AgentSettingsStore();
        _health=health??new HeadlessRuntimeHealthReader();
    }

    public bool Active=>Volatile.Read(ref _active)!=0;
    public HeadlessRuntimeObservationV1 ReadHeadless()=>_health.Read();

    public async Task<bool> TryStartAsync(CancellationToken ct)
    {
        if(Active)return true;
        var observed=_health.Read();
        if(!observed.Ready)return false;

        var settings=_settingsStore.Load();
        var profile=_settingsStore.GetActiveExchange(settings);
        if(settings.Environment!=ExchangeEnvironment.Testnet||!profile.IsTestnet)
            throw new InvalidOperationException("Desktop headless observation is Testnet-only.");

        var credentials=_settingsStore.GetExchangeCredentials(profile);
        var provider=new ExchangeProviderCatalog().Create(profile,credentials);
        _client=new ProviderReadOnlyAccessClient(provider);
        _symbols=settings.Symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Interlocked.Exchange(ref _active,1);
        ApplyHealth(observed);
        await RefreshCapabilitiesAsync(ct).ConfigureAwait(false);
        await RefreshTradingAsync(ct).ConfigureAwait(false);
        _loop=Task.Run(()=>RunAsync(_shutdown.Token),CancellationToken.None);
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(PollInterval);
        while(await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var observed=_health.Read();
            ApplyHealth(observed);
            try
            {
                await RefreshCapabilitiesAsync(ct).ConfigureAwait(false);
                await RefreshTradingAsync(ct).ConfigureAwait(false);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch
            {
                ServiceLocator.RuntimeTrading.PublishError("Headless observer could not refresh provider positions/orders.");
                ServiceLocator.SystemState.ExchangeConnected=false;
            }
        }
    }

    private async Task RefreshCapabilitiesAsync(CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;
        if(now<_nextCapabilityRefreshAtUtc)return;
        var client=_client??throw new InvalidOperationException("Headless observer is not initialized.");
        if(_symbols.Count==0)
        {
            ServiceLocator.RuntimeMarkets.Publish(new Dictionary<string,WpeAgent.RuntimeContracts.ExchangeCapability>(), "No configured Testnet symbols are available.");
            _nextCapabilityRefreshAtUtc=now.AddMinutes(1);
            return;
        }
        try
        {
            var capabilities=await client.ProbeCapabilitiesAsync(_symbols,ct).ConfigureAwait(false);
            ServiceLocator.RuntimeMarkets.Publish(capabilities);
            _nextCapabilityRefreshAtUtc=now.AddMinutes(1);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch
        {
            ServiceLocator.RuntimeMarkets.PublishError("Headless observer could not refresh provider capabilities.");
            _nextCapabilityRefreshAtUtc=now.AddSeconds(30);
        }
    }

    private async Task RefreshCapabilitiesAsync(CancellationToken ct)
    {
        var client=_client??throw new InvalidOperationException("Headless observer is not initialized.");
        try
        {
            var capabilities=await client.ProbeCapabilitiesAsync(_symbols,ct).ConfigureAwait(false);
            ServiceLocator.RuntimeMarkets.Publish(capabilities);
            _nextCapabilityRefreshUtc=DateTimeOffset.UtcNow+CapabilityRefreshInterval;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch
        {
            ServiceLocator.RuntimeMarkets.PublishError("Headless observer capability refresh failed.");
            _nextCapabilityRefreshUtc=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(30);
        }
    }

    private async Task RefreshTradingAsync(CancellationToken ct)
    {
        var client=_client??throw new InvalidOperationException("Headless observer is not initialized.");
        var account=await client.GetAccountAsync(ct).ConfigureAwait(false);
        var positions=await client.GetPositionsAsync(ct).ConfigureAwait(false);
        var orders=await client.GetOpenOrdersAsync(ct).ConfigureAwait(false);
        ServiceLocator.RuntimeTrading.Publish(positions,orders);

        var state=ServiceLocator.SystemState;
        state.WalletBalance=account.WalletBalance;
        state.AvailableBalance=account.AvailableBalance;
        state.PositionQuantity=positions.Sum(x=>x.Side==PositionSide.Long?x.Quantity:-x.Quantity);
        state.EntryPrice=positions.FirstOrDefault()?.EntryPrice??0;
        state.PositionsSummary=positions.Count==0
            ?"No open positions"
            :string.Join("\n",positions.Select(x=>$"{x.Symbol} {x.Side} · {x.Quantity} · {x.EntryPrice:F2} · {x.UnrealizedPnl:F2}"));
        state.OrdersSummary=orders.Count==0
            ?"No open orders"
            :string.Join("\n",orders.Take(8).Select(x=>$"{x.Symbol} {x.Type} · {x.Status} · {x.PositionSide}"));
        state.ExchangeConnected=true;
        state.LastUpdated=DateTime.UtcNow;
    }

    private static void ApplyHealth(HeadlessRuntimeObservationV1 observed)
    {
        var state=ServiceLocator.SystemState;
        state.RuntimeRunId=observed.RunId;
        state.RuntimeHeartbeatAtUtc=observed.RuntimeHeartbeatAtUtc?.UtcDateTime;
        state.RuntimeRecoveryStatus=observed.RecoveryStatus;
        state.RuntimeEventSequence=observed.EventSequence;
        state.Status=observed.Ready?AgentStatus.Running:observed.Available?AgentStatus.Degraded:AgentStatus.Stopped;
        state.AgentControlAllowed=false;
        state.LastMessage=observed.Ready
            ?"Desktop observer attached to the active Headless trading runtime."
            :observed.Diagnostic??"Headless runtime is unavailable.";
        state.LastUpdated=observed.ObservedAtUtc?.UtcDateTime??DateTime.UtcNow;
    }

    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _active,0)==0)return;
        _shutdown.Cancel();
        if(_loop is not null)
        {
            try{await _loop.ConfigureAwait(false);}
            catch(OperationCanceledException) when(_shutdown.IsCancellationRequested){}
        }
        if(_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client=null;
        }
        _shutdown.Dispose();
    }
}

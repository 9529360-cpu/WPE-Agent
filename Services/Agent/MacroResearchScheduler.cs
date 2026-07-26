using System.Text.Json;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.AgentServices;

public sealed class MacroResearchScheduler
{
    private static readonly string[] SeriesIds=["CUUR0000SA0","LNS14000000"];
    private readonly BlsMacroDataClient _client;
    private readonly BlsReleaseCalendarClient? _calendar;
    private readonly AgentSqliteStore _database;
    private readonly Func<DateTimeOffset> _utcNow;

    public MacroResearchScheduler(BlsMacroDataClient client,AgentSqliteStore database,Func<DateTimeOffset>? utcNow=null,BlsReleaseCalendarClient? calendar=null)
    { _client=client;_database=database;_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);_calendar=calendar; }

    public Task StartAsync(CancellationToken ct)=>Task.Run(()=>RunAsync(ct),CancellationToken.None);

    internal async Task<int> CollectOnceAsync(CancellationToken ct)
    {
        var now=_utcNow().ToUniversalTime();var persisted=0;var formallyCorrelated=0;BlsReleaseCalendarSnapshotV1? calendar=null;
        if(_calendar is not null){var calendarResult=await _calendar.FetchAsync(now,ct);calendar=calendarResult.Snapshot;}
        foreach(var seriesId in SeriesIds)
        {
            var result=await _client.FetchLatestAsync(seriesId,now.Year-1,now.Year,now,calendar,ct);
            if(result.Status!=BlsMacroFetchStatus.Available||result.Observation is null)continue;
            var saved=await _database.SaveMacroObservationAsync(result.Observation,ct);
            if(saved.Succeeded){persisted++;if(result.Observation.Facts.GetProperty("releaseTimeBasis").GetString()=="official-release-calendar")formallyCorrelated++;}
        }
        await _database.SetStateAsync("macro-research:health",JsonSerializer.Serialize(new{status=persisted==SeriesIds.Length&&formallyCorrelated==SeriesIds.Length?"READY":"DEGRADED",observedAtUtc=now,persisted,expected=SeriesIds.Length,formallyCorrelated,releaseCalendar=calendar is null?"UNAVAILABLE":"AVAILABLE"}),ct);
        return persisted;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{await CollectOnceAsync(ct);await Task.Delay(TimeSpan.FromHours(6),ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){try{await _database.RecordErrorAsync("MACRO_RESEARCH_SCHEDULER",ex,CancellationToken.None);await _database.SetStateAsync("macro-research:health",JsonSerializer.Serialize(new{status="DEGRADED",observedAtUtc=_utcNow().ToUniversalTime()}),CancellationToken.None);}catch{}try{await Task.Delay(TimeSpan.FromMinutes(15),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}}
        }
    }
}

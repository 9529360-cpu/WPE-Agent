using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace WpeAgent.AgentServices;

public enum BlsReleaseCalendarStatusV1 { Available,Error }
public sealed record BlsReleaseEventV1(string EventId,string ReleaseKind,DateTimeOffset ScheduledAtUtc);
public sealed record BlsReleaseCalendarSnapshotV1(DateTimeOffset FetchedAtUtc,string ArtifactHash,IReadOnlyList<BlsReleaseEventV1> Events);
public sealed record BlsReleaseCalendarResultV1(BlsReleaseCalendarStatusV1 Status,BlsReleaseCalendarSnapshotV1? Snapshot,string ReasonCode);

public interface IBlsReleaseCalendarTransport
{
    Task<string> GetAsync(Uri endpoint,CancellationToken ct);
}

public sealed class HttpBlsReleaseCalendarTransport(HttpClient httpClient):IBlsReleaseCalendarTransport
{
    public const int MaximumResponseBytes=1_048_576;
    public async Task<string> GetAsync(Uri endpoint,CancellationToken ct)
    {
        if(endpoint!=BlsReleaseCalendarClient.OfficialEndpoint)throw new InvalidOperationException("Only the official BLS release calendar endpoint is allowed.");
        using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/calendar"));
        using var response=await httpClient.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is >MaximumResponseBytes)throw new InvalidDataException("The BLS calendar exceeds the allowed size.");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var buffer=new MemoryStream();var chunk=new byte[16_384];
        while(true){var read=await stream.ReadAsync(chunk,ct);if(read==0)break;if(buffer.Length+read>MaximumResponseBytes)throw new InvalidDataException("The BLS calendar exceeds the allowed size.");await buffer.WriteAsync(chunk.AsMemory(0,read),ct);}
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public sealed class BlsReleaseCalendarClient(IBlsReleaseCalendarTransport transport)
{
    public static readonly Uri OfficialEndpoint=new("https://www.bls.gov/schedule/news_release/bls.ics");
    public async Task<BlsReleaseCalendarResultV1> FetchAsync(DateTimeOffset fetchedAtUtc,CancellationToken ct=default)
    {
        if(fetchedAtUtc.Offset!=TimeSpan.Zero)return Fail("macro.bls-calendar.request-invalid");string raw;
        try{raw=await transport.GetAsync(OfficialEndpoint,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{return Fail("macro.bls-calendar.transport-error");}
        try
        {
            if(string.IsNullOrWhiteSpace(raw)||raw.Length>HttpBlsReleaseCalendarTransport.MaximumResponseBytes)return Fail("macro.bls-calendar.response-invalid");
            var lines=Unfold(raw);if(!lines.Contains("BEGIN:VCALENDAR",StringComparer.Ordinal)||!lines.Contains("END:VCALENDAR",StringComparer.Ordinal))return Fail("macro.bls-calendar.response-invalid");
            var events=new List<BlsReleaseEventV1>();
            for(var index=0;index<lines.Count;index++)if(lines[index]=="BEGIN:VEVENT")
            {
                var block=new List<string>();while(++index<lines.Count&&lines[index]!="END:VEVENT")block.Add(lines[index]);if(index>=lines.Count)return Fail("macro.bls-calendar.response-invalid");
                var summary=Value(block,"SUMMARY");var kind=ReleaseKind(summary);if(kind is null)continue;
                var id=Value(block,"UID");var start=block.SingleOrDefault(x=>x.StartsWith("DTSTART",StringComparison.Ordinal));
                if(!SafeId(id)||start is null||!TryStart(start,out var scheduled)||scheduled>fetchedAtUtc.AddYears(2)||scheduled<fetchedAtUtc.AddYears(-2))return Fail("macro.bls-calendar.event-invalid");
                events.Add(new(id!,kind,scheduled));
            }
            if(events.Count==0||events.GroupBy(x=>(x.EventId,x.ReleaseKind,x.ScheduledAtUtc)).Any(x=>x.Count()>1)||events.Select(x=>x.EventId).Distinct(StringComparer.Ordinal).Count()!=events.Count)return Fail("macro.bls-calendar.event-invalid");
            var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
            return new(BlsReleaseCalendarStatusV1.Available,new(fetchedAtUtc,hash,events.OrderBy(x=>x.ScheduledAtUtc).ThenBy(x=>x.EventId,StringComparer.Ordinal).ToArray()),"macro.bls-calendar.available");
        }
        catch(InvalidOperationException){return Fail("macro.bls-calendar.response-invalid");}
    }

    public static BlsReleaseEventV1? Resolve(BlsReleaseCalendarSnapshotV1? snapshot,string seriesId,DateTimeOffset observationAtUtc,DateTimeOffset firstObservedAtUtc)
    {
        if(snapshot is null||observationAtUtc.Offset!=TimeSpan.Zero||firstObservedAtUtc.Offset!=TimeSpan.Zero)return null;
        var kind=seriesId switch{"CUUR0000SA0"=>"consumer-price-index","LNS14000000"=>"employment-situation",_=>null};if(kind is null)return null;
        var windowStart=observationAtUtc.AddMonths(1);var windowEnd=observationAtUtc.AddMonths(2);
        var matches=snapshot.Events.Where(x=>x.ReleaseKind==kind&&x.ScheduledAtUtc>=windowStart&&x.ScheduledAtUtc<windowEnd&&x.ScheduledAtUtc<=firstObservedAtUtc).ToArray();
        return matches.Length==1?matches[0]:null;
    }

    private static List<string> Unfold(string raw)
    {
        var result=new List<string>();foreach(var line in raw.Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n').Split('\n'))
        {if((line.StartsWith(' ')||line.StartsWith('\t'))&&result.Count>0)result[^1]+=line[1..];else result.Add(line.TrimEnd());}return result;
    }
    private static string? Value(IEnumerable<string> block,string name)=>block.SingleOrDefault(x=>x.StartsWith(name+":",StringComparison.Ordinal))?[(name.Length+1)..];
    private static string? ReleaseKind(string? summary)
    {
        if(string.IsNullOrWhiteSpace(summary))return null;
        if(summary.Contains("Consumer Price Index",StringComparison.OrdinalIgnoreCase))return "consumer-price-index";
        if(summary.Contains("Employment Situation",StringComparison.OrdinalIgnoreCase))return "employment-situation";
        return null;
    }
    private static bool SafeId(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=160&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '.' or '_' or '-' or '@');
    private static bool TryStart(string line,out DateTimeOffset value)
    {
        value=default;var separator=line.IndexOf(':');if(separator<0)return false;var metadata=line[..separator];var text=line[(separator+1)..];
        if(text.EndsWith('Z')&&DateTimeOffset.TryParseExact(text,"yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out value))return true;
        if(!metadata.Contains("TZID=America/New_York",StringComparison.Ordinal)||!DateTime.TryParseExact(text,"yyyyMMdd'T'HHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var local))return false;
        var zone=TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");if(zone.IsInvalidTime(local)||zone.IsAmbiguousTime(local))return false;value=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local,DateTimeKind.Unspecified),zone),TimeSpan.Zero);return true;
    }
    private static BlsReleaseCalendarResultV1 Fail(string reason)=>new(BlsReleaseCalendarStatusV1.Error,null,reason);
}

using System.Net;
using WpeAgent.AgentServices;

namespace WPE.Tests;

public sealed class BlsReleaseCalendarClientTests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,12,0,0,TimeSpan.Zero);
    [Fact]
    public async Task OfficialCalendarParsesUtcAndEasternEventsAndResolvesObservationMonth()
    {
        var transport=new FakeTransport(Calendar());var result=await new BlsReleaseCalendarClient(transport).FetchAsync(Now);
        Assert.Equal(BlsReleaseCalendarStatusV1.Available,result.Status);Assert.Equal(BlsReleaseCalendarClient.OfficialEndpoint,transport.Endpoint);
        var snapshot=Assert.IsType<BlsReleaseCalendarSnapshotV1>(result.Snapshot);Assert.Equal(64,snapshot.ArtifactHash.Length);Assert.Equal(2,snapshot.Events.Count);
        var cpi=BlsReleaseCalendarClient.Resolve(snapshot,"CUUR0000SA0",new(2026,6,1,0,0,0,TimeSpan.Zero),Now);
        Assert.NotNull(cpi);Assert.Equal(new DateTimeOffset(2026,7,14,12,30,0,TimeSpan.Zero),cpi.ScheduledAtUtc);
        Assert.NotNull(BlsReleaseCalendarClient.Resolve(snapshot,"LNS14000000",new(2026,6,1,0,0,0,TimeSpan.Zero),Now));
        Assert.Null(BlsReleaseCalendarClient.Resolve(snapshot,"UNKNOWN",new(2026,6,1,0,0,0,TimeSpan.Zero),Now));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate-id")]
    [InlineData("date-only")]
    [InlineData("unsafe-id")]
    public async Task MalformedOrAmbiguousCalendarFailsClosed(string defect)
    {
        var text=defect switch
        {
            "malformed"=>"not a calendar",
            "duplicate-id"=>Calendar().Replace("employment-202607@bls.gov","cpi-202607@bls.gov",StringComparison.Ordinal),
            "date-only"=>Calendar().Replace("20260714T083000","20260714",StringComparison.Ordinal),
            "unsafe-id"=>Calendar().Replace("cpi-202607@bls.gov","unsafe id",StringComparison.Ordinal),
            _=>throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var result=await new BlsReleaseCalendarClient(new FakeTransport(text)).FetchAsync(Now);Assert.Equal(BlsReleaseCalendarStatusV1.Error,result.Status);Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task HttpTransportRejectsNonOfficialEndpointBeforeNetwork()
    {
        var handler=new RecordingHandler();var transport=new HttpBlsReleaseCalendarTransport(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>transport.GetAsync(new("https://example.invalid/calendar.ics"),default));Assert.Equal(0,handler.Calls);
    }

    internal static string Calendar()=>"""
BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
UID:cpi-202607@bls.gov
DTSTART;TZID=America/New_York:20260714T083000
SUMMARY:Consumer Price Index
END:VEVENT
BEGIN:VEVENT
UID:employment-202607@bls.gov
DTSTART:20260702T123000Z
SUMMARY:The Employment Situation
END:VEVENT
END:VCALENDAR
""";
    private sealed class FakeTransport(string response):IBlsReleaseCalendarTransport
    {public Uri? Endpoint{get;private set;}public Task<string> GetAsync(Uri endpoint,CancellationToken ct){Endpoint=endpoint;return Task.FromResult(response);}}
    private sealed class RecordingHandler:HttpMessageHandler
    {public int Calls{get;private set;}protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Calendar())});}}
}

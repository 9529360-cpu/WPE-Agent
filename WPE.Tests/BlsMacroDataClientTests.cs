using System.Text.Json;
using System.Net;
using WpeAgent.AgentServices;
using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class BlsMacroDataClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OfficialResponseProducesEligibleDeterministicMacroInput()
    {
        var transport = new FakeTransport(Response(("2026", "M05", "335.123"), ("2026", "M06", "333.952")));
        var result = await new BlsMacroDataClient(transport).FetchLatestAsync("CUUR0000SA0", 2025, 2026, Now);

        Assert.Equal(BlsMacroFetchStatus.Available, result.Status);
        Assert.Equal(BlsMacroDataClient.OfficialEndpoint, transport.Endpoint);
        Assert.Contains("CUUR0000SA0", transport.Body, StringComparison.Ordinal);
        var observation = Assert.IsType<BlsMacroObservationV1>(result.Observation);
        Assert.Equal(333.952m, observation.Facts.GetProperty("value").GetDecimal());
        Assert.Equal("official-endpoint-first-observed", observation.Facts.GetProperty("releaseTimeBasis").GetString());
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), observation.Facts.GetProperty("observationAtUtc").GetDateTimeOffset());
        Assert.Equal(ModelOffSourceKindV1.Macro, observation.Source.Kind);
        Assert.Equal(64, observation.Source.ArtifactHash!.Length);
    }

    [Fact]
    public async Task OfficialCalendarUpgradesReleaseTimeProvenance()
    {
        var calendar=(await new BlsReleaseCalendarClient(new CalendarTransport()).FetchAsync(Now)).Snapshot;
        var result=await new BlsMacroDataClient(new FakeTransport(Response(("2026","M06","333.952")))).FetchLatestAsync("CUUR0000SA0",2025,2026,Now,calendar);
        var facts=Assert.IsType<BlsMacroObservationV1>(result.Observation).Facts;
        Assert.Equal("official-release-calendar",facts.GetProperty("releaseTimeBasis").GetString());
        Assert.Equal(new DateTimeOffset(2026,7,14,12,30,0,TimeSpan.Zero),facts.GetProperty("releasedAtUtc").GetDateTimeOffset());
        Assert.Equal(calendar!.ArtifactHash,facts.GetProperty("releaseCalendarArtifactHash").GetString());
        Assert.Equal("cpi-202607@bls.gov",facts.GetProperty("releaseCalendarEventId").GetString());
    }

    [Theory]
    [InlineData("UNKNOWN", 2025, 2026, "macro.bls.series-unsupported")]
    [InlineData("CUUR0000SA0", 2026, 2025, "macro.bls.request-invalid")]
    [InlineData("CUUR0000SA0", 2010, 2026, "macro.bls.request-invalid")]
    public async Task InvalidRequestsFailBeforeTransport(string series, int start, int end, string reason)
    {
        var transport = new FakeTransport(Response(("2026", "M06", "333.952")));
        var result = await new BlsMacroDataClient(transport).FetchLatestAsync(series, start, end, Now);
        Assert.Equal(BlsMacroFetchStatus.Unsupported, result.Status);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("wrong-series")]
    [InlineData("malformed")]
    [InlineData("unavailable")]
    [InlineData("future-observation")]
    [InlineData("outside-request")]
    [InlineData("duplicate-observation")]
    public async Task InvalidOrUnavailableResponsesNeverProduceInput(string fixture)
    {
        var response = fixture switch
        {
            "failed" => "{\"status\":\"REQUEST_FAILED\",\"Results\":{\"series\":[]}}",
            "wrong-series" => Response(("2026", "M06", "333.952")).Replace("CUUR0000SA0", "LNS14000000", StringComparison.Ordinal),
            "malformed" => "{",
            "unavailable" => Response(("2026", "M06", "-")),
            "future-observation" => Response(("2026", "M12", "340.1")),
            "outside-request" => Response(("2024", "M12", "320.1")),
            "duplicate-observation" => Response(("2026", "M06", "333.952"),("2026", "M06", "334.100")),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };
        var result = await new BlsMacroDataClient(new FakeTransport(response)).FetchLatestAsync("CUUR0000SA0", 2025, 2026, Now);
        Assert.NotEqual(BlsMacroFetchStatus.Available, result.Status);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task TransportFailureIsSafeAndCancellationPropagates()
    {
        var failed = await new BlsMacroDataClient(new FakeTransport(new HttpRequestException("secret response"))).FetchLatestAsync("CUUR0000SA0", 2025, 2026, Now);
        Assert.Equal(BlsMacroFetchStatus.Error, failed.Status);
        Assert.Equal("macro.bls.transport-error", failed.ReasonCode);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new BlsMacroDataClient(new FakeTransport(new OperationCanceledException(cts.Token))).FetchLatestAsync("CUUR0000SA0", 2025, 2026, Now, cts.Token));
    }

    [Fact]
    public async Task HttpTransportRejectsEveryNonOfficialEndpointBeforeNetwork()
    {
        var handler = new RecordingHandler();
        var transport = new HttpBlsMacroDataTransport(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.PostAsync(new Uri("https://example.invalid/data"), "{}", default));
        Assert.Equal(0, handler.Calls);
    }

    private static string Response(params (string Year, string Period, string Value)[] rows) => JsonSerializer.Serialize(new
    {
        status = "REQUEST_SUCCEEDED",
        Results = new { series = new[] { new { seriesID = "CUUR0000SA0", data = rows.Select(x => new { year = x.Year, period = x.Period, value = x.Value }) } } }
    });

    private sealed class FakeTransport : IBlsMacroDataTransport
    {
        private readonly string? _response;
        private readonly Exception? _exception;
        public int Calls { get; private set; }
        public Uri? Endpoint { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public FakeTransport(string response) => _response = response;
        public FakeTransport(Exception exception) => _exception = exception;
        public Task<string> PostAsync(Uri endpoint, string jsonBody, CancellationToken ct)
        {
            Calls++; Endpoint = endpoint; Body = jsonBody;
            return _exception is null ? Task.FromResult(_response!) : Task.FromException<string>(_exception);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
    private sealed class CalendarTransport:IBlsReleaseCalendarTransport
    {public Task<string> GetAsync(Uri endpoint,CancellationToken ct)=>Task.FromResult(BlsReleaseCalendarClientTests.Calendar());}
}

using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpeAgent.ModelOff;

namespace WpeAgent.AgentServices;

public enum BlsMacroFetchStatus { Available, Unsupported, Error }

public sealed record BlsMacroFetchResult(
    BlsMacroFetchStatus Status,
    BlsMacroObservationV1? Observation,
    string ReasonCode);

public sealed record BlsMacroObservationV1(JsonElement Facts, ModelOffSourceV1 Source);

public interface IBlsMacroDataTransport
{
    Task<string> PostAsync(Uri endpoint, string jsonBody, CancellationToken ct);
}

public sealed class HttpBlsMacroDataTransport(HttpClient httpClient) : IBlsMacroDataTransport
{
    public const int MaximumResponseBytes = 1_048_576;

    public async Task<string> PostAsync(Uri endpoint, string jsonBody, CancellationToken ct)
    {
        if (endpoint != BlsMacroDataClient.OfficialEndpoint)
            throw new InvalidOperationException("Only the official BLS public API endpoint is allowed.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The BLS response exceeds the allowed size.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The BLS response exceeds the allowed size.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public sealed class BlsMacroDataClient(IBlsMacroDataTransport transport)
{
    public static readonly Uri OfficialEndpoint = new("https://api.bls.gov/publicAPI/v2/timeseries/data/");
    private static readonly IReadOnlyDictionary<string, SeriesDefinition> Series =
        new Dictionary<string, SeriesDefinition>(StringComparer.Ordinal)
        {
            ["CUUR0000SA0"] = new("US", "monthly", "index"),
            ["LNS14000000"] = new("US", "monthly", "percent")
        };

    public async Task<BlsMacroFetchResult> FetchLatestAsync(
        string seriesId,
        int startYear,
        int endYear,
        DateTimeOffset fetchedAtUtc,
        CancellationToken ct = default)=>await FetchLatestAsync(seriesId,startYear,endYear,fetchedAtUtc,null,ct);

    public async Task<BlsMacroFetchResult> FetchLatestAsync(
        string seriesId,int startYear,int endYear,DateTimeOffset fetchedAtUtc,
        BlsReleaseCalendarSnapshotV1? releaseCalendar,CancellationToken ct = default)
    {
        if (!Series.TryGetValue(seriesId ?? string.Empty, out var definition))
            return Fail(BlsMacroFetchStatus.Unsupported, "macro.bls.series-unsupported");
        if (fetchedAtUtc.Offset != TimeSpan.Zero || startYear < 1900 || endYear < startYear || endYear - startYear > 9)
            return Fail(BlsMacroFetchStatus.Unsupported, "macro.bls.request-invalid");
        string response;
        try
        {
            var body = JsonSerializer.Serialize(new { seriesid = new[] { seriesId }, startyear = startYear.ToString(CultureInfo.InvariantCulture), endyear = endYear.ToString(CultureInfo.InvariantCulture) });
            response = await transport.PostAsync(OfficialEndpoint, body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Fail(BlsMacroFetchStatus.Error, "macro.bls.transport-error"); }

        try
        {
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "REQUEST_SUCCEEDED")
                return Fail(BlsMacroFetchStatus.Error, "macro.bls.request-failed");
            if (!root.TryGetProperty("Results", out var results) || !results.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Array)
                return Fail(BlsMacroFetchStatus.Error, "macro.bls.response-invalid");
            var matching = series.EnumerateArray().Where(item => item.TryGetProperty("seriesID", out var id) && id.GetString() == seriesId).ToArray();
            if (matching.Length != 1 || !matching[0].TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Fail(BlsMacroFetchStatus.Error, "macro.bls.response-series-invalid");

            var monthlyRows=data.EnumerateArray().Where(IsMonthlyRow).ToArray();
            var parsed=monthlyRows.Select(ParseObservation).ToArray();
            if(parsed.Any(item=>item is null))return Fail(BlsMacroFetchStatus.Error,"macro.bls.response-invalid");
            var observations=parsed.Select(item=>item!.Value).ToArray();
            if(observations.Any(item=>item.Year<startYear||item.Year>endYear||new DateTimeOffset(item.Year,item.Month,1,0,0,0,TimeSpan.Zero)>fetchedAtUtc)||
               observations.GroupBy(item=>(item.Year,item.Month)).Any(group=>group.Count()>1))
                return Fail(BlsMacroFetchStatus.Error,"macro.bls.response-observation-invalid");
            observations=observations.OrderByDescending(item=>item.Year).ThenByDescending(item=>item.Month).ToArray();
            if (observations.Length == 0)
                return Fail(BlsMacroFetchStatus.Unsupported, "macro.bls.observation-unavailable");
            var latest = observations[0];
            var observationAt=new DateTimeOffset(latest.Year,latest.Month,1,0,0,0,TimeSpan.Zero);
            var release=BlsReleaseCalendarClient.Resolve(releaseCalendar,seriesId!,observationAt,fetchedAtUtc);
            var facts = JsonSerializer.SerializeToElement(new
            {
                schema = "wpe.macro-facts/1.0",
                indicatorId = seriesId,
                geography = definition.Geography,
                frequency = definition.Frequency,
                unit = definition.Unit,
                observationAtUtc = observationAt,
                releasedAtUtc = release?.ScheduledAtUtc??fetchedAtUtc,
                releaseTimeBasis = release is null?"official-endpoint-first-observed":"official-release-calendar",
                releaseCalendarArtifactHash=release is null?null:releaseCalendar!.ArtifactHash,
                releaseCalendarEventId=release?.EventId,
                value = latest.Value
            });
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response))).ToLowerInvariant();
            var source = new ModelOffSourceV1("bls-public-api-v2", ModelOffSourceKindV1.Macro, fetchedAtUtc, fetchedAtUtc, ModelOffSourceStatusV1.Available, hash);
            return new(BlsMacroFetchStatus.Available, new(facts, source), "macro.bls.available");
        }
        catch (JsonException) { return Fail(BlsMacroFetchStatus.Error, "macro.bls.response-invalid"); }
        catch (InvalidOperationException) { return Fail(BlsMacroFetchStatus.Error, "macro.bls.response-invalid"); }
    }

    private static Observation? ParseObservation(JsonElement item)
    {
        if (!item.TryGetProperty("year", out var yearValue) || !int.TryParse(yearValue.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return null;
        if (!item.TryGetProperty("period", out var periodValue)) return null;
        var period = periodValue.GetString();
        if (period is null || period.Length != 3 || period[0] != 'M' || !int.TryParse(period.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var month) || month is < 1 or > 12) return null;
        if (!item.TryGetProperty("value", out var valueElement) || !decimal.TryParse(valueElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return null;
        return new(year, month, value);
    }

    private static bool IsMonthlyRow(JsonElement item)=>item.TryGetProperty("period",out var periodValue)&&periodValue.GetString() is {Length:3} period&&period[0]=='M'&&period[1..] is not "13";

    private static BlsMacroFetchResult Fail(BlsMacroFetchStatus status, string reasonCode) => new(status, null, reasonCode);
    private readonly record struct SeriesDefinition(string Geography, string Frequency, string Unit);
    private readonly record struct Observation(int Year, int Month, decimal Value);
}

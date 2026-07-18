using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Core.Data;
using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Infrastructure.Data;

public class ApiDataSource : IDataSource
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;

    public ApiDataSource(HttpClient httpClient, string endpoint)
    {
        _httpClient = httpClient;
        _endpoint = endpoint.TrimEnd('/');
    }

    public string Name => "Api";

    public async IAsyncEnumerable<RawDataFrame> ReadAsync(DataQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = $"{_endpoint}/marketdata?symbol={query.Symbol}&start={query.Start:o}&end={query.End:o}";
        var frames = await _httpClient.GetFromJsonAsync<List<ApiCandle>>(url, cancellationToken) ?? new List<ApiCandle>();
        foreach (var candle in frames)
        {
            yield return new RawDataFrame(Name, query.Symbol, candle.Timestamp, new Dictionary<string, object>
            {
                ["open"] = candle.Open,
                ["high"] = candle.High,
                ["low"] = candle.Low,
                ["close"] = candle.Close,
                ["volume"] = candle.Volume
            });
        }
    }

    private sealed record ApiCandle(DateTime Timestamp, double Open, double High, double Low, double Close, double Volume);
}

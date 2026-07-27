using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TeacherCryptoPublicEvidenceV2Tests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,27,6,0,0,TimeSpan.Zero);private readonly string _dir=Path.Combine(Path.GetTempPath(),"teacher-crypto-"+Guid.NewGuid().ToString("N"));private string Db=>Path.Combine(_dir,"agent.db");public TeacherCryptoPublicEvidenceV2Tests()=>Directory.CreateDirectory(_dir);public void Dispose(){SqliteConnection.ClearAllPools();try{Directory.Delete(_dir,true);}catch(IOException){}}
    [Fact] public async Task OfficialThreeEndpointEvidenceIsCanonicalAndCredentialFree()
    {
        var handler=new Handler(request=>request.RequestUri!.AbsolutePath switch{"/fapi/v1/premiumIndex"=>Json($$"""{"symbol":"BTCUSDT","markPrice":"100.5","indexPrice":"100.0","lastFundingRate":"0.0001","nextFundingTime":{{Now.AddHours(2).ToUnixTimeMilliseconds()}},"time":{{Now.AddSeconds(-2).ToUnixTimeMilliseconds()}}}"""),"/fapi/v1/openInterest"=>Json($$"""{"symbol":"BTCUSDT","openInterest":"1234.5","time":{{Now.AddSeconds(-3).ToUnixTimeMilliseconds()}}}"""),_=>Json($$"""{"symbol":"BTCUSDT","priceChangePercent":"2.5","quoteVolume":"999999.9","closeTime":{{Now.AddSeconds(-1).ToUnixTimeMilliseconds()}}}""")});using var client=new HttpClient(handler);using var gateway=new TeacherPublicEvidenceGatewayV2([TeacherBinancePublicEvidenceAdapterV2.Source],client,false,()=>Now);var fact=await new TeacherBinancePublicEvidenceAdapterV2(gateway).FetchAsync("btcusdt",new(new(3,1_000_000,0,TimeSpan.FromSeconds(10)),Now),default);Assert.NotNull(fact);Assert.True(TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(fact));Assert.Equal(100.5m,fact.MarkPrice);Assert.Equal(3,handler.Requests.Count);Assert.All(handler.Requests,x=>Assert.Null(x.Headers.Authorization));
    }
    [Fact] public async Task CrossSymbolMalformedAndStaleResponsesFailClosed()
    {
        async Task<TeacherCryptoMarketFactV2?> Fetch(Func<HttpRequestMessage,HttpResponseMessage> response){using var client=new HttpClient(new Handler(response));using var gateway=new TeacherPublicEvidenceGatewayV2([TeacherBinancePublicEvidenceAdapterV2.Source],client,false,()=>Now);return await new TeacherBinancePublicEvidenceAdapterV2(gateway).FetchAsync("BTCUSDT",new(new(3,1_000_000,0,TimeSpan.FromSeconds(10)),Now),default);}
        Assert.Null(await Fetch(_=>Json("{}")));Assert.Null(await Fetch(request=>request.RequestUri!.AbsolutePath.Contains("premium")?Json($$"""{"symbol":"ETHUSDT","markPrice":"1","indexPrice":"1","lastFundingRate":"0","nextFundingTime":{{Now.ToUnixTimeMilliseconds()}},"time":{{Now.ToUnixTimeMilliseconds()}}}"""):request.RequestUri.AbsolutePath.Contains("openInterest")?Json($$"""{"symbol":"BTCUSDT","openInterest":"1","time":{{Now.ToUnixTimeMilliseconds()}}}"""):Json($$"""{"symbol":"BTCUSDT","priceChangePercent":"1","quoteVolume":"1","closeTime":{{Now.ToUnixTimeMilliseconds()}}}""")));
    }
    [Fact] public async Task CacheIsAppendOnlyIdempotentRestartSafeAndAgeBounded()
    {
        var fact=TeacherCryptoMarketFactCanonicalizerV2.Create("binance-futures-public","BTCUSDT",Now.AddSeconds(-2),Now,100.5m,100m,.0001m,Now.AddHours(2),1234m,2.5m,999m,[Hash("a"),Hash("b"),Hash("c")]);var store=new AgentSqliteStore(Db);Assert.True((await store.SaveTeacherCryptoEvidenceAsync(fact,default)).Succeeded);Assert.True((await new AgentSqliteStore(Db).SaveTeacherCryptoEvidenceAsync(fact,default)).Idempotent);Assert.NotNull(await new AgentSqliteStore(Db).GetLatestTeacherCryptoEvidenceAsync("btcusdt",Now.AddMinutes(1),TimeSpan.FromMinutes(5),default));Assert.Null(await store.GetLatestTeacherCryptoEvidenceAsync("BTCUSDT",Now.AddMinutes(10),TimeSpan.FromMinutes(5),default));await using var c=new SqliteConnection($"Data Source={Db}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="DELETE FROM teacher_public_evidence";await Assert.ThrowsAsync<SqliteException>(()=>q.ExecuteNonQueryAsync());
    }
    [Fact] public async Task CollectorPersistsUnavailableHealthWithoutInventingEvidence()
    {
        using var client=new HttpClient(new Handler(_=>new(HttpStatusCode.ServiceUnavailable)));using var gateway=new TeacherPublicEvidenceGatewayV2([TeacherBinancePublicEvidenceAdapterV2.Source],client,false,()=>Now);var store=new AgentSqliteStore(Db);var result=await new TeacherCryptoEvidenceCollectorV2(new(gateway),store,()=>Now).RefreshAsync("BTCUSDT",new(new(3,1000,0,TimeSpan.FromSeconds(2)),Now),default);Assert.Equal("unavailable",result.Status);Assert.Null(result.EvidenceHash);Assert.Null(await store.GetLatestTeacherCryptoEvidenceAsync("BTCUSDT",Now,TimeSpan.FromMinutes(5),default));await using var c=new SqliteConnection($"Data Source={Db}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="SELECT status||':'||diagnostic_code FROM teacher_source_health_events";Assert.Equal("unavailable:teacher.crypto.fetch-unavailable",await q.ExecuteScalarAsync());
    }
    private static HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value,Encoding.UTF8,"application/json")};private static string Hash(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler{public List<HttpRequestMessage> Requests{get;}=[];protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){Requests.Add(request);return Task.FromResult(response(request));}}
}

using System.Net;
using System.Text;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TeacherPublicEvidenceGatewayV2Tests
{
    private static readonly DateTimeOffset Now=new(2026,7,27,6,0,0,TimeSpan.Zero);
    private static TeacherPublicSourceV2 Source(int bytes=1024)=>new("official-test","official","data.example.gov",["/api/"],["application/json"],bytes,TimeSpan.FromSeconds(2));
    [Fact] public async Task AllowsOnlyExactHttpsReadOnlySourcePath()
    {
        var handler=new RecordingHandler(_=>Json("{\"value\":1}"));using var client=new HttpClient(handler);using var gateway=new TeacherPublicEvidenceGatewayV2([Source()],client,false,()=>Now);var budget=new TeacherNetworkBudgetStateV2(TeacherNetworkBudgetV2.Default,Now);
        var ok=await gateway.GetAsync("official-test",new("https://data.example.gov/api/value"),budget,default);Assert.Equal("available",ok.Status);Assert.Equal(64,ok.Sha256.Length);Assert.Equal(HttpMethod.Get,handler.Method);
        Assert.Equal("teacher.network.uri-denied",(await gateway.GetAsync("official-test",new("http://data.example.gov/api/value"),budget,default)).DiagnosticCode);Assert.Equal("teacher.network.uri-denied",(await gateway.GetAsync("official-test",new("https://evil.example/api/value"),budget,default)).DiagnosticCode);Assert.Equal("teacher.network.uri-denied",(await gateway.GetAsync("official-test",new("https://data.example.gov/private/value"),budget,default)).DiagnosticCode);
    }
    [Fact] public async Task RejectsRedirectContentTypeAndOversize()
    {
        async Task<string> Run(HttpResponseMessage response,TeacherPublicSourceV2? source=null){using var client=new HttpClient(new RecordingHandler(_=>response));using var gateway=new TeacherPublicEvidenceGatewayV2([source??Source()],client,false,()=>Now);return(await gateway.GetAsync("official-test",new("https://data.example.gov/api/value"),new(TeacherNetworkBudgetV2.Default,Now),default)).DiagnosticCode;}
        Assert.Equal("teacher.network.redirect-denied",await Run(new(HttpStatusCode.Redirect){Headers={Location=new Uri("https://data.example.gov/api/other")}}));Assert.Equal("teacher.network.content-type-denied",await Run(new(HttpStatusCode.OK){Content=new StringContent("html",Encoding.UTF8,"text/html")}));Assert.Equal("teacher.network.response-too-large",await Run(Json("{\"long\":12345}"),Source(4)));
    }
    [Fact] public async Task RequestAndByteBudgetsFailClosed()
    {
        using var client=new HttpClient(new RecordingHandler(_=>Json("{\"value\":1}")));using var gateway=new TeacherPublicEvidenceGatewayV2([Source()],client,false,()=>Now);var requests=new TeacherNetworkBudgetStateV2(new(1,100,0,TimeSpan.FromMinutes(1)),Now);Assert.Equal("available",(await gateway.GetAsync("official-test",new("https://data.example.gov/api/a"),requests,default)).Status);Assert.Equal("teacher.network.request-budget-exhausted",(await gateway.GetAsync("official-test",new("https://data.example.gov/api/b"),requests,default)).DiagnosticCode);
        var bytes=new TeacherNetworkBudgetStateV2(new(2,2,0,TimeSpan.FromMinutes(1)),Now);Assert.Equal("teacher.network.byte-budget-exhausted",(await gateway.GetAsync("official-test",new("https://data.example.gov/api/a"),bytes,default)).DiagnosticCode);
    }
    private static HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private sealed class RecordingHandler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler{public HttpMethod? Method{get;private set;}protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){Method=request.Method;return Task.FromResult(response(request));}}
}

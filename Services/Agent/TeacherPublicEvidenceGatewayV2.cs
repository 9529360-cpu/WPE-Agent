using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.IO;

namespace 币安量化机器人.Services.Agent;

public sealed record TeacherPublicSourceV2(string SourceId,string Tier,string Host,IReadOnlyList<string> PathPrefixes,IReadOnlyList<string> ContentTypes,int MaximumResponseBytes,TimeSpan Timeout,bool Enabled=true,string AuthenticationClass="none",string Attribution="source-required",string Redistribution="unknown",string CommercialUse="unknown",string? PolicyUrl=null,DateOnly? PolicyReviewedOn=null);
public sealed record TeacherNetworkBudgetV2(int MaximumRequests,long MaximumBytes,int MaximumRetries,TimeSpan MaximumElapsed)
{
    public static TeacherNetworkBudgetV2 Default { get; }=new(6,2_000_000,1,TimeSpan.FromSeconds(20));
}
public sealed record TeacherPublicEvidenceResponseV2(string SourceId,Uri Uri,DateTimeOffset RetrievedAtUtc,string ContentType,byte[] Bytes,string Sha256,string Status,string DiagnosticCode);

public sealed class TeacherNetworkBudgetStateV2
{
    private readonly TeacherNetworkBudgetV2 _budget;private readonly DateTimeOffset _startedAtUtc;private int _requests;private long _bytes;
    public TeacherNetworkBudgetStateV2(TeacherNetworkBudgetV2 budget,DateTimeOffset startedAtUtc){_budget=budget;_startedAtUtc=startedAtUtc.ToUniversalTime();}
    public bool TryBegin(DateTimeOffset nowUtc)=>_requests<_budget.MaximumRequests&&nowUtc.ToUniversalTime()-_startedAtUtc<=_budget.MaximumElapsed&&++_requests>0;
    public bool TryAddBytes(long bytes){if(bytes<0||_bytes+bytes>_budget.MaximumBytes)return false;_bytes+=bytes;return true;}
    public int Requests=>_requests;public long Bytes=>_bytes;
}

public sealed class TeacherPublicEvidenceGatewayV2:IDisposable
{
    private readonly IReadOnlyDictionary<string,TeacherPublicSourceV2> _sources;private readonly HttpClient _client;private readonly bool _ownsClient;private readonly Func<DateTimeOffset> _utcNow;
    public TeacherPublicEvidenceGatewayV2(IEnumerable<TeacherPublicSourceV2> sources,Func<DateTimeOffset>? utcNow=null):this(sources,CreateClient(),true,utcNow){}
    internal TeacherPublicEvidenceGatewayV2(IEnumerable<TeacherPublicSourceV2> sources,HttpClient client,bool ownsClient=false,Func<DateTimeOffset>? utcNow=null)
    {
        var configured=sources.ToArray();if(configured.Any(x=>string.IsNullOrWhiteSpace(x.SourceId)||string.IsNullOrWhiteSpace(x.Host)||x.PathPrefixes.Count==0||x.ContentTypes.Count==0||x.MaximumResponseBytes is <1 or >5_000_000||x.Timeout<=TimeSpan.Zero||x.Timeout>TimeSpan.FromSeconds(30)||x.AuthenticationClass!="none"))throw new ArgumentException("Teacher public source policy is invalid.",nameof(sources));_sources=configured.ToDictionary(x=>x.SourceId,StringComparer.Ordinal);_client=client;_ownsClient=ownsClient;_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }
    public async Task<TeacherPublicEvidenceResponseV2> GetAsync(string sourceId,Uri uri,TeacherNetworkBudgetStateV2 budget,CancellationToken ct)
    {
        if(!_sources.TryGetValue(sourceId,out var source)||!source.Enabled)return Failed(sourceId,uri,"unsupported","teacher.network.source-disabled");
        if(!IsAllowed(source,uri))return Failed(sourceId,uri,"invalid","teacher.network.uri-denied");var started=_utcNow().ToUniversalTime();if(!budget.TryBegin(started))return Failed(sourceId,uri,"error","teacher.network.request-budget-exhausted");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(source.Timeout);using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.TryAddWithoutValidation("Accept",string.Join(", ",source.ContentTypes));request.Headers.TryAddWithoutValidation("User-Agent","WPE-Teacher/2.0 public-read-only");
        try
        {
            using var response=await _client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);if((int)response.StatusCode is >=300 and <400)return Failed(sourceId,uri,"error","teacher.network.redirect-denied");if(!response.IsSuccessStatusCode)return Failed(sourceId,uri,"error","teacher.network.http-"+(int)response.StatusCode);
            var contentType=response.Content.Headers.ContentType?.MediaType??string.Empty;if(!source.ContentTypes.Contains(contentType,StringComparer.OrdinalIgnoreCase))return Failed(sourceId,uri,"invalid","teacher.network.content-type-denied");var declared=response.Content.Headers.ContentLength;if(declared>source.MaximumResponseBytes)return Failed(sourceId,uri,"invalid","teacher.network.response-too-large");
            await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var memory=new MemoryStream();var buffer=new byte[8192];while(true){var read=await stream.ReadAsync(buffer,timeout.Token);if(read==0)break;if(memory.Length+read>source.MaximumResponseBytes||!budget.TryAddBytes(read))return Failed(sourceId,uri,"error","teacher.network.byte-budget-exhausted");memory.Write(buffer,0,read);}var bytes=memory.ToArray();return new(sourceId,uri,_utcNow().ToUniversalTime(),contentType,bytes,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),"available","teacher.network.available");
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return Failed(sourceId,uri,"error","teacher.network.timeout");}
        catch(HttpRequestException){return Failed(sourceId,uri,"error","teacher.network.transport-error");}
    }
    private static bool IsAllowed(TeacherPublicSourceV2 source,Uri uri)=>uri.IsAbsoluteUri&&uri.Scheme==Uri.UriSchemeHttps&&string.IsNullOrEmpty(uri.UserInfo)&&uri.Port==443&&string.Equals(uri.IdnHost,source.Host,StringComparison.OrdinalIgnoreCase)&&source.PathPrefixes.Any(prefix=>uri.AbsolutePath.StartsWith(prefix,StringComparison.Ordinal));
    private TeacherPublicEvidenceResponseV2 Failed(string sourceId,Uri uri,string status,string code)=>new(sourceId,uri,_utcNow().ToUniversalTime(),string.Empty,[],string.Empty,status,code);
    private static HttpClient CreateClient(){var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate};return new(handler,true);}
    public void Dispose(){if(_ownsClient)_client.Dispose();}
}

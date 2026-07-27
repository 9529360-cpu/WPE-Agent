using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace 币安量化机器人.Services.Agent;

public sealed record TeacherSourceAcceptanceSampleV2(DateTimeOffset ObservedAtUtc,DateTimeOffset RetrievedAtUtc,string Symbol,string EvidenceHash);
public sealed record TeacherSourceAcceptanceArtifactV2(string Schema,string CandidateVersion,string CandidateSha256,string SourceId,string Environment,string Symbol,DateTimeOffset StartedAtUtc,DateTimeOffset CompletedAtUtc,IReadOnlyList<TeacherSourceAcceptanceSampleV2> Samples,bool CredentialUsed,bool MutationAttempted,string CanonicalSha256);
public sealed record TeacherV2AcceptanceRunResult(bool Success,string ArtifactDirectory,IReadOnlyList<string> Errors);

public static class TeacherSourceAcceptanceCanonicalizerV2
{
    public static TeacherSourceAcceptanceArtifactV2 Create(string version,string candidateHash,string symbol,IReadOnlyList<TeacherCryptoMarketFactV2> facts)
    {
        if(facts.Count<4||facts.Any(x=>!TeacherCryptoMarketFactCanonicalizerV2.IsCanonical(x)||x.Symbol!=symbol)||facts.Select(x=>x.CanonicalSha256).Distinct().Count()!=facts.Count)throw new InvalidOperationException("Teacher source acceptance samples are incomplete.");var ordered=facts.OrderBy(x=>x.RetrievedAtUtc).ToArray();if(ordered[^1].RetrievedAtUtc-ordered[0].RetrievedAtUtc<TimeSpan.FromMinutes(15))throw new InvalidOperationException("Teacher source acceptance observation window is too short.");var samples=ordered.Select(x=>new TeacherSourceAcceptanceSampleV2(x.ObservedAtUtc,x.RetrievedAtUtc,x.Symbol,x.CanonicalSha256)).ToArray();var draft=new TeacherSourceAcceptanceArtifactV2("wpe.teacher-source-acceptance/2.0",version,candidateHash,TeacherBinancePublicEvidenceAdapterV2.Source.SourceId,"PublicReadOnly",symbol,ordered[0].RetrievedAtUtc,ordered[^1].RetrievedAtUtc,samples,false,false,string.Empty);return draft with{CanonicalSha256=MarketTeacherComposerV2.Hash(JsonSerializer.Serialize(draft))};
    }
}

public static class TeacherV2AcceptanceRunner
{
    public static async Task<TeacherV2AcceptanceRunResult> RunAsync(string[] args,CancellationToken ct=default)
    {
        var samples=Math.Max(4,IntArg(args,"--teacher-samples",4));var interval=TimeSpan.FromMinutes(Math.Max(5,IntArg(args,"--teacher-interval-minutes",5)));var symbol=StringArg(args,"--teacher-symbol")?.ToUpperInvariant()??"BTCUSDT";var directory=Path.Combine(AppDataPaths.TestArtifactsDirectory,"teacher-v2-acceptance",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var errors=new List<string>();TeacherSourceAcceptanceArtifactV2? artifact=null;
        try
        {
            using var gateway=new TeacherPublicEvidenceGatewayV2([TeacherBinancePublicEvidenceAdapterV2.Source]);var adapter=new TeacherBinancePublicEvidenceAdapterV2(gateway);var facts=new List<TeacherCryptoMarketFactV2>();for(var i=0;i<samples;i++){var now=DateTimeOffset.UtcNow;var fact=await adapter.FetchAsync(symbol,new(new(3,768_000,0,TimeSpan.FromSeconds(20)),now),ct);if(fact is null)throw new InvalidOperationException($"teacher source sample {i+1} unavailable");facts.Add(fact);if(i+1<samples)await Task.Delay(interval,ct);}var assembly=Assembly.GetExecutingAssembly();var path=assembly.Location;var version=assembly.GetName().Version?.ToString()??"unknown";var hash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path,ct))).ToLowerInvariant();artifact=TeacherSourceAcceptanceCanonicalizerV2.Create(version,hash,symbol,facts);await AtomicWrite(Path.Combine(directory,"source-sustained.json"),JsonSerializer.SerializeToUtf8Bytes(artifact),ct);await AtomicWrite(Path.Combine(directory,"source-sustained.sha256"),Encoding.ASCII.GetBytes(artifact.CanonicalSha256),ct);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception ex){errors.Add("teacher-v2-acceptance.failed:"+ex.GetType().Name+":"+SensitiveDataRedactor.ForLog(ex.Message,160));}
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.teacher-v2-acceptance-run/2.0",success=errors.Count==0,completedAtUtc=DateTimeOffset.UtcNow,sourceArtifact=artifact?.CanonicalSha256,credentialUsed=false,mutationAttempted=false,mainnetTradingEnabled=false,errors});await AtomicWrite(Path.Combine(directory,"manifest.json"),manifest,CancellationToken.None);await AtomicWrite(Path.Combine(directory,"manifest.sha256"),Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant()),CancellationToken.None);return new(errors.Count==0,directory,errors);
    }
    private static int IntArg(string[] args,string name,int fallback){var i=Array.FindIndex(args,x=>x.Equals(name,StringComparison.OrdinalIgnoreCase));return i>=0&&i+1<args.Length&&int.TryParse(args[i+1],out var value)?value:fallback;}
    private static string? StringArg(string[] args,string name){var i=Array.FindIndex(args,x=>x.Equals(name,StringComparison.OrdinalIgnoreCase));return i>=0&&i+1<args.Length?args[i+1]:null;}
    private static async Task AtomicWrite(string path,byte[] bytes,CancellationToken ct){var temp=path+".tmp-"+Guid.NewGuid().ToString("N");await File.WriteAllBytesAsync(temp,bytes,ct);File.Move(temp,path,true);}
}

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record AuditLocalAcceptanceRunResult(bool Success,string ArtifactDirectory,IReadOnlyList<string> Errors);

public static class AuditLocalAcceptanceRunnerV1
{
    public static async Task<AuditLocalAcceptanceRunResult> RunAsync(string[] args,CancellationToken ct=default)
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WPE Agent","Runtime","TestArtifacts");var directory=Path.Combine(root,"audit-local-acceptance",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var errors=new List<string>();var files=new List<object>();var version="unknown";var candidateHash="";var testHash="";
        try{var options=Parse(args);var append=Required(options,"append-only-trx");var correlation=Required(options,"seven-role-correlation-trx");var testAssembly=Required(options,"test-assembly");var assembly=Assembly.GetExecutingAssembly();var candidateBytes=await File.ReadAllBytesAsync(assembly.Location,ct);var testBytes=await File.ReadAllBytesAsync(testAssembly,ct);version=assembly.GetName().Version?.ToString()??"unknown";candidateHash=Hash(candidateBytes);testHash=Hash(testBytes);foreach(var input in new[]{("audit.append-only",append),("audit.seven-role-correlation",correlation)}){var artifact=LocalTestAcceptanceCanonicalizerV1.Create(input.Item1,version,candidateBytes,testBytes,await File.ReadAllBytesAsync(input.Item2,ct));if(!LocalTestAcceptanceCanonicalizerV1.IsCanonical(artifact,DateTimeOffset.UtcNow,candidateHash))throw new InvalidOperationException("Generated Audit local evidence is ineligible: "+input.Item1);await AtomicWrite(Path.Combine(directory,input.Item1+".json"),artifact.CanonicalBytes,ct);files.Add(new{name=input.Item1+".json",artifact.RequirementId,artifact.CanonicalSha256,artifact.ObservedAtUtc,tests=artifact.Tests.Count});}}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception ex){errors.Add("audit-local-acceptance.failed:"+ex.GetType().Name);}
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.audit-local-acceptance-run/1.0",candidateVersion=version,candidateSha256=candidateHash,testAssemblySha256=testHash,completedAtUtc=DateTimeOffset.UtcNow,success=errors.Count==0,files,errors});await AtomicWrite(Path.Combine(directory,"manifest.json"),manifest,ct);await AtomicWrite(Path.Combine(directory,"manifest.sha256"),System.Text.Encoding.ASCII.GetBytes(Hash(manifest)),ct);return new(errors.Count==0,directory,errors);
    }
    private static Dictionary<string,string> Parse(string[] args){var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<args.Length;i++)if(args[i].StartsWith("--",StringComparison.Ordinal)&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal))result[args[i][2..]]=args[++i];return result;}private static string Required(IReadOnlyDictionary<string,string> options,string key)=>options.TryGetValue(key,out var value)&&File.Exists(value)?Path.GetFullPath(value):throw new InvalidOperationException("Missing or invalid --"+key+".");private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();private static async Task AtomicWrite(string path,byte[] bytes,CancellationToken ct){var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllBytesAsync(temp,bytes,ct);File.Move(temp,path,false);}
}

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record RecoveryLocalAcceptanceRunResult(bool Success,string ArtifactDirectory,IReadOnlyList<string> Errors);

public static class RecoveryLocalAcceptanceRunnerV1
{
    public static async Task<RecoveryLocalAcceptanceRunResult> RunAsync(string[] args,CancellationToken ct=default)
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WPE Agent","Runtime","TestArtifacts");var directory=Path.Combine(root,"recovery-local-acceptance",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var errors=new List<string>();var files=new List<object>();var candidateVersion="unknown";var candidateHash="";var testHash="";
        try
        {
            var options=Parse(args);var restartPath=Required(options,"restart-reconciliation-trx");var quarantinePath=Required(options,"unknown-quarantine-trx");var testAssemblyPath=Required(options,"test-assembly");var assembly=Assembly.GetExecutingAssembly();var candidateBytes=await File.ReadAllBytesAsync(assembly.Location,ct);var testBytes=await File.ReadAllBytesAsync(testAssemblyPath,ct);candidateVersion=assembly.GetName().Version?.ToString()??"unknown";candidateHash=Hash(candidateBytes);testHash=Hash(testBytes);
            foreach(var input in new[]{("recovery.restart-reconciliation",restartPath),("recovery.unknown-quarantine",quarantinePath)}){var trx=await File.ReadAllBytesAsync(input.Item2,ct);var artifact=LocalTestAcceptanceCanonicalizerV1.Create(input.Item1,candidateVersion,candidateBytes,testBytes,trx);if(!LocalTestAcceptanceCanonicalizerV1.IsCanonical(artifact,DateTimeOffset.UtcNow,candidateHash))throw new InvalidOperationException($"Generated local evidence is ineligible: {input.Item1}");var name=input.Item1+".json";await AtomicWrite(Path.Combine(directory,name),artifact.CanonicalBytes,ct);files.Add(new{name,artifact.RequirementId,artifact.CanonicalSha256,artifact.ObservedAtUtc,tests=artifact.Tests.Count});}
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){errors.Add("recovery-local-acceptance.failed:"+ex.GetType().Name);}
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.recovery-local-acceptance-run/1.0",candidateVersion,candidateSha256=candidateHash,testAssemblySha256=testHash,completedAtUtc=DateTimeOffset.UtcNow,success=errors.Count==0,files,errors});await AtomicWrite(Path.Combine(directory,"manifest.json"),manifest,ct);await AtomicWrite(Path.Combine(directory,"manifest.sha256"),System.Text.Encoding.ASCII.GetBytes(Hash(manifest)),ct);return new(errors.Count==0,directory,errors);
    }
    private static Dictionary<string,string> Parse(string[] args){var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<args.Length;i++)if(args[i].StartsWith("--",StringComparison.Ordinal)&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal))result[args[i][2..]]=args[++i];return result;}
    private static string Required(IReadOnlyDictionary<string,string> options,string key)=>options.TryGetValue(key,out var value)&&File.Exists(value)?Path.GetFullPath(value):throw new InvalidOperationException($"Missing or invalid --{key}.");
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task AtomicWrite(string path,byte[] bytes,CancellationToken ct){var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllBytesAsync(temp,bytes,ct);File.Move(temp,path,false);}
}

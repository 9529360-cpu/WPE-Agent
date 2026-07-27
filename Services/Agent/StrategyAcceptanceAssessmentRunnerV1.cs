using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record StrategyAcceptanceAssessmentRunResult(bool Accepted,string ReportPath,IReadOnlyList<string> Errors);

public static class StrategyAcceptanceAssessmentRunnerV1
{
    public static async Task<StrategyAcceptanceAssessmentRunResult> RunAsync(string[] args,CancellationToken ct=default)
    {
        var artifactsRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WPE Agent","Runtime","TestArtifacts");var reportDirectory=Path.Combine(artifactsRoot,"strategy-aggregate-assessment");Directory.CreateDirectory(reportDirectory);var report=Path.Combine(reportDirectory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json");var errors=new List<string>();AggregateAcceptanceAssessmentV1? assessment=null;
        try
        {
            var options=Parse(args);var local=Required(options,"local-directory");var testnet=Required(options,"testnet-directory");var assembly=Assembly.GetExecutingAssembly();var candidateHash=Hash(await File.ReadAllBytesAsync(assembly.Location,ct));var now=DateTimeOffset.UtcNow;var evidence=new List<AggregateAcceptanceEvidenceV1>();
            foreach(var id in new[]{"strategy.lifecycle-contract","strategy.anti-overfit","strategy.no-risk-bypass"}){var bytes=await File.ReadAllBytesAsync(Path.Combine(local,id+".json"),ct);if(!LocalTestAcceptanceCanonicalizerV1.TryParseCanonical(bytes,out var artifact)||artifact!.CandidateSha256!=candidateHash)throw new InvalidOperationException($"Strategy local artifact is malformed or belongs to another candidate: {id}");evidence.Add(LocalTestAcceptanceCanonicalizerV1.ToAcceptanceEvidence(artifact,now,candidateHash));}
            var liveBytes=await File.ReadAllBytesAsync(Path.Combine(testnet,StrategyAggregateEvidenceCanonicalizerV1.ShadowRequirement+".json"),ct);if(!StrategyAggregateEvidenceCanonicalizerV1.TryParseCanonical(liveBytes,out var live)||live!.CandidateSha256!=candidateHash||!StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(live,now))throw new InvalidOperationException("Strategy Testnet artifact is malformed, stale, or belongs to another candidate.");evidence.Add(StrategyAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(live,now));assessment=ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Strategy,evidence,now);errors.AddRange(assessment.Errors);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){errors.Add("strategy-assessment.failed:"+ex.GetType().Name);}
        var output=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.strategy-aggregate-assessment-run/1.0",completedAtUtc=DateTimeOffset.UtcNow,accepted=assessment?.Accepted==true&&errors.Count==0,evidenceSetSha256=assessment?.EvidenceSetSha256??string.Empty,satisfiedRequirements=assessment?.SatisfiedRequirements??[],errors});var temporary=report+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllBytesAsync(temporary,output,ct);File.Move(temporary,report,false);return new(assessment?.Accepted==true&&errors.Count==0,report,errors);
    }
    private static Dictionary<string,string> Parse(string[] args){var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<args.Length;i++)if(args[i].StartsWith("--",StringComparison.Ordinal)&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal))result[args[i][2..]]=args[++i];return result;}
    private static string Required(IReadOnlyDictionary<string,string> options,string key)=>options.TryGetValue(key,out var value)&&Directory.Exists(value)?Path.GetFullPath(value):throw new InvalidOperationException($"Missing or invalid --{key}.");
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

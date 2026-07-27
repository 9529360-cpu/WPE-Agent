using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace WpeAgent.ModelOff;

public sealed record RiskAcceptanceAssessmentRunResult(bool Accepted,string ReportPath,IReadOnlyList<string> Errors);

public static class RiskAcceptanceAssessmentRunnerV1
{
    public static async Task<RiskAcceptanceAssessmentRunResult> RunAsync(string[] args,CancellationToken ct=default)
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WPE Agent","Runtime","TestArtifacts");var reportDirectory=Path.Combine(root,"risk-aggregate-assessment");Directory.CreateDirectory(reportDirectory);var report=Path.Combine(reportDirectory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json");var errors=new List<string>();AggregateAcceptanceAssessmentV1? assessment=null;
        try
        {
            var options=Parse(args);var local=Required(options,"local-directory");var testnet=Required(options,"testnet-directory");var candidateHash=Hash(await File.ReadAllBytesAsync(Assembly.GetExecutingAssembly().Location,ct));var now=DateTimeOffset.UtcNow;var evidence=new List<AggregateAcceptanceEvidenceV1>();foreach(var id in new[]{"risk.deterministic-gate","risk.adversarial-bypass"}){var bytes=await File.ReadAllBytesAsync(Path.Combine(local,id+".json"),ct);if(!LocalTestAcceptanceCanonicalizerV1.TryParseCanonical(bytes,out var artifact)||artifact!.CandidateSha256!=candidateHash)throw new InvalidOperationException($"Risk local artifact is malformed or belongs to another candidate: {id}");evidence.Add(LocalTestAcceptanceCanonicalizerV1.ToAcceptanceEvidence(artifact,now,candidateHash));}foreach(var id in new[]{RiskAggregateEvidenceCanonicalizerV1.PreauthorizationRequirement,RiskAggregateEvidenceCanonicalizerV1.ToctouRequirement}){var bytes=await File.ReadAllBytesAsync(Path.Combine(testnet,id+".json"),ct);if(!RiskAggregateEvidenceCanonicalizerV1.TryParseCanonical(bytes,out var artifact)||artifact!.CandidateSha256!=candidateHash||!RiskAggregateEvidenceCanonicalizerV1.IsCanonical(artifact,now))throw new InvalidOperationException($"Risk Testnet artifact is malformed, stale, or belongs to another candidate: {id}");evidence.Add(RiskAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(artifact,now));}assessment=ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Risk,evidence,now);errors.AddRange(assessment.Errors);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){errors.Add("risk-assessment.failed:"+ex.GetType().Name);}
        var output=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.risk-aggregate-assessment-run/1.0",completedAtUtc=DateTimeOffset.UtcNow,accepted=assessment?.Accepted==true&&errors.Count==0,evidenceSetSha256=assessment?.EvidenceSetSha256??string.Empty,satisfiedRequirements=assessment?.SatisfiedRequirements??[],errors});var temp=report+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllBytesAsync(temp,output,ct);File.Move(temp,report,false);return new(assessment?.Accepted==true&&errors.Count==0,report,errors);
    }
    private static Dictionary<string,string> Parse(string[] args){var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<args.Length;i++)if(args[i].StartsWith("--",StringComparison.Ordinal)&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal))result[args[i][2..]]=args[++i];return result;}
    private static string Required(IReadOnlyDictionary<string,string> options,string key)=>options.TryGetValue(key,out var value)&&Directory.Exists(value)?Path.GetFullPath(value):throw new InvalidOperationException($"Missing or invalid --{key}.");
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

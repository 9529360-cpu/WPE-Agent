using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using 币安量化机器人.Services;

namespace WpeAgent.ModelOff;

public sealed record ResearchAcceptanceAssessmentRunResult(bool Accepted,string ReportPath,IReadOnlyList<string> Errors);

public static class ResearchAcceptanceAssessmentRunnerV1
{
    public static async Task<ResearchAcceptanceAssessmentRunResult> RunAsync(string[] args,CancellationToken ct=default)
    {
        var report=AppDataPaths.TestArtifactFile(Path.Combine("research-aggregate-assessment",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json"));var errors=new List<string>();AggregateAcceptanceAssessmentV1? assessment=null;
        try
        {
            var options=Parse(args);var local=Required(options,"local-directory");var target=Required(options,"target-directory");var assembly=Assembly.GetExecutingAssembly();var candidateHash=Hash(await File.ReadAllBytesAsync(assembly.Location,ct));var now=DateTimeOffset.UtcNow;var evidence=new List<AggregateAcceptanceEvidenceV1>();
            foreach(var id in new[]{"research.canonical-contract","research.point-in-time-replay"}){var bytes=await File.ReadAllBytesAsync(Path.Combine(local,id+".json"),ct);if(!LocalTestAcceptanceCanonicalizerV1.TryParseCanonical(bytes,out var artifact)||artifact!.CandidateSha256!=candidateHash)throw new InvalidOperationException($"Research local artifact is malformed or belongs to another candidate: {id}");evidence.Add(LocalTestAcceptanceCanonicalizerV1.ToAcceptanceEvidence(artifact,now,candidateHash));}
            foreach(var id in new[]{ResearchAggregateEvidenceCanonicalizerV1.LiveSourceRequirement,ResearchAggregateEvidenceCanonicalizerV1.ModelOffCycleRequirement}){var bytes=await File.ReadAllBytesAsync(Path.Combine(target,id+".json"),ct);if(!ResearchAggregateEvidenceCanonicalizerV1.TryParseCanonical(bytes,out var artifact)||artifact!.CandidateSha256!=candidateHash||!ResearchAggregateEvidenceCanonicalizerV1.IsCanonical(artifact,now))throw new InvalidOperationException($"Research Target artifact is malformed, stale, or belongs to another candidate: {id}");evidence.Add(ResearchAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(artifact,now));}
            assessment=ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Research,evidence,now);errors.AddRange(assessment.Errors);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){errors.Add("research-assessment.failed:"+SensitiveDataRedactor.ForLog(ex.Message,180));}
        var output=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.research-aggregate-assessment-run/1.0",completedAtUtc=DateTimeOffset.UtcNow,accepted=assessment?.Accepted==true&&errors.Count==0,evidenceSetSha256=assessment?.EvidenceSetSha256??string.Empty,satisfiedRequirements=assessment?.SatisfiedRequirements??[],errors});var temporary=report+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllBytesAsync(temporary,output,ct);File.Move(temporary,report,false);return new(assessment?.Accepted==true&&errors.Count==0,report,errors);
    }
    private static Dictionary<string,string> Parse(string[] args){var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<args.Length;i++)if(args[i].StartsWith("--",StringComparison.Ordinal)&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal))result[args[i][2..]]=args[++i];return result;}
    private static string Required(IReadOnlyDictionary<string,string> options,string key)=>options.TryGetValue(key,out var value)&&Directory.Exists(value)?Path.GetFullPath(value):throw new InvalidOperationException($"Missing or invalid --{key}.");private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

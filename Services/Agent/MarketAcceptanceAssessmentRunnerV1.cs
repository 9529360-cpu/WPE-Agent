using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using 币安量化机器人.Services;

namespace WpeAgent.ModelOff;

public sealed record MarketAcceptanceAssessmentRunResult(bool Accepted,string ReportPath,IReadOnlyList<string> Errors);

public static class MarketAcceptanceAssessmentRunnerV1
{
    public static async Task<MarketAcceptanceAssessmentRunResult> RunAsync(string[] args,CancellationToken ct=default)
    {
        var report=AppDataPaths.TestArtifactFile(Path.Combine("market-aggregate-assessment",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json"));var errors=new List<string>();AggregateAcceptanceAssessmentV1? assessment=null;
        try
        {
            var options=Parse(args);var local=Required(options,"local-directory");var live=Required(options,"live-directory");var assembly=Assembly.GetExecutingAssembly();var candidateHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly.Location,ct))).ToLowerInvariant();var now=DateTimeOffset.UtcNow;var evidence=new List<AggregateAcceptanceEvidenceV1>();
            foreach(var id in new[]{"market.canonical-contract","market.adversarial-fail-closed"}){var bytes=await File.ReadAllBytesAsync(Path.Combine(local,id+".json"),ct);if(!LocalTestAcceptanceCanonicalizerV1.TryParseCanonical(bytes,out var artifact)||artifact!.CandidateSha256!=candidateHash)throw new InvalidOperationException($"Local artifact is malformed or belongs to another candidate: {id}");evidence.Add(LocalTestAcceptanceCanonicalizerV1.ToAcceptanceEvidence(artifact,now,candidateHash));}
            foreach(var id in new[]{MarketAggregateEvidenceCanonicalizerV1.LiveReadRequirement,MarketAggregateEvidenceCanonicalizerV1.SustainedFreshnessRequirement}){var bytes=await File.ReadAllBytesAsync(Path.Combine(live,id+".json"),ct);if(!MarketAggregateEvidenceCanonicalizerV1.TryParseCanonical(bytes,out var artifact)||artifact!.CandidateSha256!=candidateHash||!MarketAggregateEvidenceCanonicalizerV1.IsCanonical(artifact,now))throw new InvalidOperationException($"Live artifact is malformed, stale, or belongs to another candidate: {id}");evidence.Add(MarketAggregateEvidenceCanonicalizerV1.ToAcceptanceEvidence(artifact,now));}
            assessment=ModelOffAggregateAcceptanceGateV1.Evaluate(ModelOffAggregateAgentV1.Market,evidence,now);errors.AddRange(assessment.Errors);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){errors.Add("market-assessment.failed:"+SensitiveDataRedactor.ForLog(ex.Message,180));}
        var bytesOut=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.market-aggregate-assessment-run/1.0",completedAtUtc=DateTimeOffset.UtcNow,accepted=assessment?.Accepted==true&&errors.Count==0,evidenceSetSha256=assessment?.EvidenceSetSha256??string.Empty,satisfiedRequirements=assessment?.SatisfiedRequirements??[],errors});var temp=report+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllBytesAsync(temp,bytesOut,ct);File.Move(temp,report,false);return new(assessment?.Accepted==true&&errors.Count==0,report,errors);
    }
    private static Dictionary<string,string> Parse(string[] args){var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<args.Length;i++)if(args[i].StartsWith("--",StringComparison.Ordinal)&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal))result[args[i][2..]]=args[++i];return result;}
    private static string Required(IReadOnlyDictionary<string,string> options,string key)=>options.TryGetValue(key,out var value)&&Directory.Exists(value)?Path.GetFullPath(value):throw new InvalidOperationException($"Missing or invalid --{key}.");
}

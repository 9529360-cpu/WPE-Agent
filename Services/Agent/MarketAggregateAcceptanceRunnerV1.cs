using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.Reflection;
using 币安量化机器人.Services;

namespace WpeAgent.ModelOff;

public sealed record MarketAggregateAcceptanceRunResult(bool Success,string ArtifactDirectory,IReadOnlyList<string> Errors);

public static class MarketAggregateAcceptanceRunnerV1
{
    public static async Task<MarketAggregateAcceptanceRunResult> RunAsync(string symbol="BTCUSDT",CancellationToken ct=default)
    {
        var directory=Path.Combine(AppDataPaths.TestArtifactsDirectory,"market-aggregate-acceptance",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var errors=new List<string>();var files=new List<object>();var assembly=Assembly.GetExecutingAssembly();var candidatePath=assembly.Location;var candidate=new MarketAcceptanceCandidateV1(assembly.GetName().Version?.ToString()??"unknown",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidatePath))).ToLowerInvariant());
        try
        {
            await using var source=new BinanceTestnetMarketAcceptanceSourceV1();
            var live=await MarketAggregateAcceptanceCollectorV1.CollectAsync(source,MarketAggregateEvidenceCanonicalizerV1.LiveReadRequirement,symbol,candidate,1,TimeSpan.Zero,ct:ct);
            var sustained=await MarketAggregateAcceptanceCollectorV1.CollectAsync(source,MarketAggregateEvidenceCanonicalizerV1.SustainedFreshnessRequirement,symbol,candidate,4,TimeSpan.FromMinutes(5),ct:ct);
            foreach(var artifact in new[]{live,sustained})
            {
                if(!MarketAggregateEvidenceCanonicalizerV1.IsCanonical(artifact,DateTimeOffset.UtcNow)){errors.Add($"market-acceptance.invalid.{artifact.RequirementId}");continue;}
                var name=artifact.RequirementId+".json";var path=Path.Combine(directory,name);await AtomicWriteAsync(path,artifact.CanonicalBytes,ct);var persisted=await File.ReadAllBytesAsync(path,ct);if(!MarketAggregateEvidenceCanonicalizerV1.TryParseCanonical(persisted,out var reread)||!MarketAggregateEvidenceCanonicalizerV1.IsCanonical(reread,DateTimeOffset.UtcNow)){errors.Add($"market-acceptance.persisted-invalid.{artifact.RequirementId}");continue;}files.Add(new{name,artifact.RequirementId,artifact.CanonicalSha256,artifact.StartedAtUtc,artifact.CompletedAtUtc,samples=artifact.Samples.Count});
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){errors.Add("market-acceptance.collection-failed:"+SensitiveDataRedactor.ForLog(ex.Message,160));}
        var manifestBytes=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.market-aggregate-acceptance-run/1.0",candidateVersion=candidate.Version,candidateSha256=candidate.Sha256,provider="binance-futures",environment="Testnet",symbol,mainnetEnabled=false,mutationEnabled=false,completedAtUtc=DateTimeOffset.UtcNow,success=errors.Count==0,files,errors});await AtomicWriteAsync(Path.Combine(directory,"manifest.json"),manifestBytes,ct);await AtomicWriteAsync(Path.Combine(directory,"manifest.sha256"),System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant()),ct);
        return new(errors.Count==0,directory,errors);
    }

    private static async Task AtomicWriteAsync(string path,byte[] bytes,CancellationToken ct)
    {
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllBytesAsync(temporary,bytes,ct);File.Move(temporary,path,false);
    }
}

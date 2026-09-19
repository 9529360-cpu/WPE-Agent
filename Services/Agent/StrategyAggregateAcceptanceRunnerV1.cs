using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WpeAgent.ModelOff;

public sealed record StrategyAggregateAcceptanceRunResult(bool Success,string ArtifactDirectory,IReadOnlyList<string> Errors);

public static class StrategyAggregateAcceptanceRunnerV1
{
    public static async Task<StrategyAggregateAcceptanceRunResult> RunAsync(CancellationToken ct=default)
    {
        var artifactsRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WPE Agent","Runtime","TestArtifacts");var directory=Path.Combine(artifactsRoot,"strategy-aggregate-acceptance",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var errors=new List<string>();var files=new List<object>();var assembly=Assembly.GetExecutingAssembly();var candidateBytes=await File.ReadAllBytesAsync(assembly.Location,ct);var candidate=new MarketAcceptanceCandidateV1(assembly.GetName().Version?.ToString()??"unknown",Hash(candidateBytes));
        try
        {
            var now=DateTimeOffset.UtcNow;await using var adapter=new BinanceFuturesAdapter(new ExchangeConnectionProfile{Id="strategy-acceptance",ProviderId="binance-futures",DisplayName="Binance Futures Testnet strategy read-only",IsTestnet=true,ExecutionEnabled=false,Endpoint="https://testnet.binancefuture.com",TimeoutSeconds=20},string.Empty,string.Empty);var fetched=await adapter.GetCandlesAsync("BTCUSDT","1m",240,ct);var closed=ConfirmedMarketCandlesV1.Select(fetched,"1m",now.UtcDateTime).OrderBy(x=>x.OpenTime).ToArray();if(closed.Length<StrategyAggregateEvidenceCanonicalizerV1.RequiredObservations+64)throw new InvalidOperationException("Insufficient confirmed Testnet history for Strategy shadow observation.");var selected=closed.TakeLast(StrategyAggregateEvidenceCanonicalizerV1.RequiredObservations).ToArray();if(now-new DateTimeOffset(selected[^1].OpenTime.ToUniversalTime()).AddMinutes(1)>TimeSpan.FromMinutes(20))throw new InvalidOperationException("Latest confirmed Testnet candle is stale.");
            var sourceHash=Hash(Encoding.UTF8.GetBytes(string.Join('\n',closed.Select(CandleIdentity))));var temp=Path.Combine(Path.GetTempPath(),"wpe-strategy-acceptance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
            try
            {
                var store=new AgentSqliteStore(Path.Combine(temp,"agent.db"));var registry=new DeterministicStrategyRegistry();var parameters=LocalStrategyParameters.For(StrategyFamily.TrendBreakout,0);var profile=new StrategyProfile{Id="acceptance-shadow",Version=registry.BindProfileVersion(StrategyFamily.TrendBreakout,"trendbreakout-1"),Symbol="BTCUSDT",Family=StrategyFamily.TrendBreakout,Parameters=parameters,ParametersHash=LocalStrategyParameters.Hash(parameters),LineageHash=StrategyLineage.Hash("BTCUSDT",StrategyFamily.TrendBreakout,null,null,0,LocalStrategyParameters.Hash(parameters)),Lifecycle=StrategyLifecycle.Shadow,QualityScore=.80,Expectancy=.01,MaxDrawdown=.10,ValidationTrades=StrategyGovernor.MinimumValidationTrades,LastReason="candidate-bound Testnet shadow acceptance"};await store.UpsertStrategyAsync(profile,ct);var agent=new StrategyResearchAgent(store);agent.SetSchedulerHealth(true);var samples=new List<StrategyShadowSampleV1>();
                foreach(var candle in selected)
                {
                    var index=Array.IndexOf(closed,candle);var prefix=closed.Take(index+1).ToArray();var closeAt=new DateTimeOffset(candle.OpenTime.ToUniversalTime()).AddMinutes(1);var market=new MarketEvidence("BTCUSDT",candle.Close,prefix.TakeLast(48).Min(x=>x.Low),prefix.TakeLast(48).Max(x=>x.High),50,0,0,0,new(0,1,1,1,1,1,0),closeAt.UtcDateTime){Candles=prefix,Quality=new(){QualityScore=90}};market=market with{Provenance=MarketEvidenceProvenanceCanonicalizerV1.Create(market,"binance-futures","Testnet")};var signal=agent.GetSignal(profile,market,[]);await agent.ObserveAsync(new EvidencePack{CollectedAt=closeAt.UtcDateTime,Completeness=100,Markets=new Dictionary<string,MarketEvidence>(StringComparer.OrdinalIgnoreCase){{"BTCUSDT",market}}},ct);samples.Add(new(new DateTimeOffset(candle.OpenTime.ToUniversalTime()),closeAt,candle.Close,signal.Direction,signal.Confidence,market.Provenance!.CanonicalSha256));
                }
                var final=(await store.GetStrategiesAsync(ct)).Single(x=>x.Id==profile.Id);var artifact=StrategyAggregateEvidenceCanonicalizerV1.Create("BTCUSDT",candidate,profile.Id,profile.Version,DateTimeOffset.UtcNow,sourceHash,samples,final.ShadowObservations,final.Lifecycle.ToString(),false);if(!StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(artifact,DateTimeOffset.UtcNow))throw new InvalidOperationException("Generated Strategy Testnet evidence is ineligible.");var name=StrategyAggregateEvidenceCanonicalizerV1.ShadowRequirement+".json";var path=Path.Combine(directory,name);await AtomicWriteAsync(path,artifact.CanonicalBytes,ct);var persisted=await File.ReadAllBytesAsync(path,ct);if(!StrategyAggregateEvidenceCanonicalizerV1.TryParseCanonical(persisted,out var reread)||!StrategyAggregateEvidenceCanonicalizerV1.IsCanonical(reread,DateTimeOffset.UtcNow))throw new InvalidOperationException("Persisted Strategy Testnet evidence is invalid.");files.Add(new{name,artifact.RequirementId,artifact.CanonicalSha256,artifact.ObservedAtUtc,observations=artifact.Samples.Count,artifact.FinalLifecycle});
            }
            finally{SqliteConnection.ClearAllPools();try{Directory.Delete(temp,true);}catch(IOException){}}
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){errors.Add("strategy-aggregate-acceptance.failed:"+ex.GetType().Name+":"+Safe(ex.Message));}
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.strategy-aggregate-acceptance-run/1.0",candidateVersion=candidate.Version,candidateSha256=candidate.Sha256,provider="binance-futures",environment="Testnet",symbol="BTCUSDT",mainnetEnabled=false,mutationEnabled=false,completedAtUtc=DateTimeOffset.UtcNow,success=errors.Count==0,files,errors});await AtomicWriteAsync(Path.Combine(directory,"manifest.json"),manifest,ct);await AtomicWriteAsync(Path.Combine(directory,"manifest.sha256"),Encoding.ASCII.GetBytes(Hash(manifest)),ct);return new(errors.Count==0,directory,errors);
    }

    private static string CandleIdentity(CandleEvidence x)=>string.Join('|',new DateTimeOffset(x.OpenTime.ToUniversalTime()).ToString("O"),x.Open.ToString(CultureInfo.InvariantCulture),x.High.ToString(CultureInfo.InvariantCulture),x.Low.ToString(CultureInfo.InvariantCulture),x.Close.ToString(CultureInfo.InvariantCulture),x.Volume.ToString(CultureInfo.InvariantCulture));
    private static string Safe(string value)=>new(value.Where(x=>char.IsAsciiLetterOrDigit(x)||x is ' ' or '-' or '_' or '.' or ':').Take(180).ToArray());
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task AtomicWriteAsync(string path,byte[] bytes,CancellationToken ct){var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllBytesAsync(temporary,bytes,ct);File.Move(temporary,path,false);}
}

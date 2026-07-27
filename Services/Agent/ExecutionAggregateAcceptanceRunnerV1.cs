using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Core.Models;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WpeAgent.ModelOff;

public sealed record ExecutionAggregateAcceptanceRunResult(bool Success,string ArtifactDirectory,IReadOnlyList<string> Errors);

public static class ExecutionAggregateAcceptanceRunnerV1
{
    private const string Symbol="BTCUSDT";
    public static async Task<ExecutionAggregateAcceptanceRunResult> RunAsync(CancellationToken ct=default)
    {
        var directory=Path.Combine(AppDataPaths.TestArtifactsDirectory,"execution-aggregate-acceptance",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var errors=new List<string>();var files=new List<object>();var candidate=await Candidate(ct);var opened=false;IExchangeProvider? provider=null;TradingExecutionGateway? gateway=null;TestnetSmokeAuthorization? authorization=null;var originalStatus=ServiceLocator.SystemState.Status;
        try
        {
            var settingsStore=new AgentSettingsStore();var settings=settingsStore.Load();if(settingsStore.LastLoadDiagnostic is not null||settings.Environment!=ExchangeEnvironment.Testnet)throw new InvalidOperationException("Execution acceptance requires valid Testnet settings.");
            var configured=settingsStore.GetActiveExchange(settings);if(!configured.IsTestnet||!configured.Enabled||!configured.ExecutionEnabled||!configured.ProviderId.Equals("binance-futures",StringComparison.OrdinalIgnoreCase)||!ProviderEndpointPolicy.IsOfficialHttpsOrigin(configured.Endpoint,"testnet.binancefuture.com"))throw new InvalidOperationException("Execution acceptance requires the enabled official Binance Futures Testnet execution profile.");
            var credentials=settingsStore.GetExchangeCredentials(configured);if(!credentials.TryGetValue("apiKey",out var key)||!credentials.TryGetValue("secret",out var secret)||string.IsNullOrWhiteSpace(key)||string.IsNullOrWhiteSpace(secret))throw new InvalidOperationException("Execution acceptance Testnet credentials are unavailable.");
            var profile=new ExchangeConnectionProfile{Id="execution-acceptance",ProviderId="binance-futures",DisplayName="Binance Futures Testnet execution acceptance",Enabled=true,ExecutionEnabled=true,IsTestnet=true,Endpoint=configured.Endpoint,UseProxy=configured.UseProxy,ProxyUrl=configured.ProxyUrl,ReceiveWindow=configured.ReceiveWindow,TimeoutSeconds=configured.TimeoutSeconds};
            provider=new BinanceFuturesAdapter(profile,key,secret);key=string.Empty;secret=string.Empty;
            var permissions=await provider.CheckPermissionsAsync(ct);var account=await provider.GetAccountAsync(ct);var initialPositions=await provider.GetPositionsAsync(ct);var initialOrders=await provider.GetOpenOrdersAsync(Symbol,ct);
            if(!permissions.CanRead||!permissions.CanTrade||permissions.CanWithdraw||account.Equity<=0)throw new InvalidOperationException("Execution acceptance permissions or account authority are ineligible.");
            if(initialPositions.Any(x=>x.Symbol==Symbol&&x.Quantity>0)||initialOrders.Any(x=>x.Symbol==Symbol&&x.IsProtection))throw new InvalidOperationException("Execution acceptance requires an isolated flat BTCUSDT starting state.");
            var capabilities=await new ProviderCapabilityProbe().ProbeAsync(provider,[Symbol],true,ct);if(!capabilities.TryGetValue(Symbol,out var capability)||capability.Status!=CapabilityStatus.Available||!capability.CanRead||!capability.CanTrade||!capability.TestnetAvailable)throw new InvalidOperationException("Execution acceptance provider capability is unavailable.");
            var rule=await provider.GetRulesAsync(Symbol,ct);var market=await provider.GetMarketAsync(Symbol,ct);if(rule.StepSize<=0||rule.MinQuantity<=0||rule.MinNotional<=0||market.Price<=0)throw new InvalidOperationException("Execution acceptance rule or market evidence is invalid.");
            var temp=Path.Combine(Path.GetTempPath(),"wpe-execution-acceptance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);var store=new AgentSqliteStore(Path.Combine(temp,"agent.db"));var executor=new ReliableOrderExecutor(provider,store,settings.Risk,SystemOrderPollScheduler.Instance,capabilities,true);gateway=new TradingExecutionGateway(executor,store);
            var correlation="execution-acceptance-"+Guid.NewGuid().ToString("N");var issued=DateTimeOffset.UtcNow;authorization=new("execution-auth-"+Guid.NewGuid().ToString("N"),correlation,"execution-acceptance",DeviceLicenseService.GetCurrentDeviceCode(),"execution-session-"+Guid.NewGuid().ToString("N"),issued,issued.AddMinutes(10));
            var quantity=Ceiling(Math.Max(rule.MinQuantity,rule.MinNotional*1.10m/market.Price),rule.StepSize);var openingId=ClientId("OPEN");var opening=new ExecutionIntent(Symbol,PositionSide.Long,quantity,false,rule.RoundPrice(market.Price*.98m),rule.RoundPrice(market.Price*1.02m),openingId,"formal execution acceptance",DecisionAction.OpenLong,ExpectedPrice:market.Price);
            ServiceLocator.SystemState.Status=AgentStatus.Running;var openResult=await gateway.ExecuteTestnetSmokeAsync(new(authorization,opening,Math.Min(10,rule.MaxLeverage),true,rule,market),ct);if(!openResult.Executed)throw new InvalidOperationException("Execution opening was rejected: "+openResult.Code);opened=true;
            var openOrder=await provider.FindOrderAsync(Symbol,openingId,ct);if(!Exact(openOrder,opening))throw new InvalidOperationException("Execution opening is not exactly correlated with a filled exchange order.");
            var position=(await provider.GetPositionsAsync(ct)).SingleOrDefault(x=>x.Symbol==Symbol&&x.Side==PositionSide.Long&&x.Quantity>0)??throw new InvalidOperationException("Execution opening did not create the expected Testnet position.");
            var protections=(await provider.GetOpenOrdersAsync(Symbol,ct)).Where(x=>x.IsProtection&&x.PositionSide==PositionSide.Long).ToArray();if(protections.Length<2)throw new InvalidOperationException("Execution opening did not create both protection orders.");
            var closingId=ClientId("CLOSE");var closing=new ExecutionIntent(Symbol,PositionSide.Long,position.Quantity,true,0,0,closingId,"formal execution acceptance cleanup",DecisionAction.CloseLong,ExpectedPrice:position.MarkPrice);var closeResult=await gateway.ExecuteTestnetSmokeAsync(new(authorization,closing,Math.Max(1,(int)position.Leverage),position.Isolated,ObservedPosition:position),ct);if(!closeResult.Executed)throw new InvalidOperationException("Execution cleanup was rejected: "+closeResult.Code);
            var closeOrder=await provider.FindOrderAsync(Symbol,closingId,ct);if(!Exact(closeOrder,closing))throw new InvalidOperationException("Execution cleanup is not exactly correlated with a filled exchange order.");opened=false;
            var finalPositions=await provider.GetPositionsAsync(ct);var finalOrders=await provider.GetOpenOrdersAsync(Symbol,ct);var flat=!finalPositions.Any(x=>x.Symbol==Symbol&&x.Side==PositionSide.Long&&x.Quantity>0);var noProtection=!finalOrders.Any(x=>x.Symbol==Symbol&&x.IsProtection&&x.PositionSide==PositionSide.Long);if(!flat||!noProtection)throw new InvalidOperationException("Execution acceptance cleanup left position or protection residue.");
            var observed=DateTimeOffset.UtcNow;var correlationHash=Hash(correlation);var openingIntentHash=IntentHash(opening);var openingOrderHash=OrderHash(openOrder!);var closingIntentHash=IntentHash(closing);var closingOrderHash=OrderHash(closeOrder!);
            foreach(var requirement in new[]{ExecutionAggregateEvidenceCanonicalizerV1.LifecycleRequirement,ExecutionAggregateEvidenceCanonicalizerV1.CorrelationRequirement})
            {
                var evidence=ExecutionAggregateEvidenceCanonicalizerV1.Create(requirement,candidate,observed,correlationHash,openingIntentHash,openingOrderHash,closingIntentHash,closingOrderHash,"execution.testnet-open-protect-close-confirmed",true,flat,noProtection);if(!ExecutionAggregateEvidenceCanonicalizerV1.IsCanonical(evidence,DateTimeOffset.UtcNow))throw new InvalidOperationException("Generated Execution evidence is ineligible: "+requirement);var name=requirement+".json";await AtomicWrite(Path.Combine(directory,name),evidence.CanonicalBytes,ct);if(!ExecutionAggregateEvidenceCanonicalizerV1.TryParseCanonical(await File.ReadAllBytesAsync(Path.Combine(directory,name),ct),out var reread)||!ExecutionAggregateEvidenceCanonicalizerV1.IsCanonical(reread,DateTimeOffset.UtcNow))throw new InvalidOperationException("Persisted Execution evidence is invalid: "+requirement);files.Add(new{name,evidence.RequirementId,evidence.CanonicalSha256,evidence.ObservedAtUtc});
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){errors.Add("execution-aggregate-acceptance.failed:"+ex.GetType().Name+":"+SensitiveDataRedactor.ForLog(ex.Message,180));}
        finally
        {
            if(opened&&provider is not null&&gateway is not null&&authorization is not null)try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(60));var position=(await provider.GetPositionsAsync(cleanup.Token)).FirstOrDefault(x=>x.Symbol==Symbol&&x.Side==PositionSide.Long&&x.Quantity>0);if(position is not null){var close=new ExecutionIntent(Symbol,PositionSide.Long,position.Quantity,true,0,0,ClientId("FAILCLOSE"),"formal execution acceptance failure cleanup",DecisionAction.CloseLong,ExpectedPrice:position.MarkPrice);var result=await gateway.ExecuteTestnetSmokeAsync(new(authorization,close,Math.Max(1,(int)position.Leverage),position.Isolated,ObservedPosition:position),cleanup.Token);if(!result.Executed)errors.Add("execution-aggregate-acceptance.cleanup-rejected");}var residual=(await provider.GetPositionsAsync(cleanup.Token)).Any(x=>x.Symbol==Symbol&&x.Side==PositionSide.Long&&x.Quantity>0);if(residual)errors.Add("execution-aggregate-acceptance.cleanup-residual");}catch(Exception){errors.Add("execution-aggregate-acceptance.cleanup-failed");}
            ServiceLocator.SystemState.Status=originalStatus;if(provider is not null)await provider.DisposeAsync();
        }
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{schema="wpe.execution-aggregate-acceptance-run/1.0",candidateVersion=candidate.Version,candidateSha256=candidate.Sha256,completedAtUtc=DateTimeOffset.UtcNow,success=errors.Count==0,files,errors});await AtomicWrite(Path.Combine(directory,"manifest.json"),manifest,CancellationToken.None);await AtomicWrite(Path.Combine(directory,"manifest.sha256"),Encoding.ASCII.GetBytes(Hash(manifest)),CancellationToken.None);return new(errors.Count==0,directory,errors);
    }

    private static bool Exact(ExchangeOrder? order,ExecutionIntent intent)=>order is not null&&order.Symbol==intent.Symbol&&order.ClientOrderId==intent.ClientOrderId&&order.Status=="FILLED"&&order.ExecutedQuantity==intent.Quantity&&order.AvgPrice>0;
    private static decimal Ceiling(decimal value,decimal step)=>step<=0?value:Math.Ceiling(value/step)*step;
    private static string ClientId(string action){var raw=$"WPE-EXACC-{action}-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid():N}";return raw[..Math.Min(36,raw.Length)];}
    private static string IntentHash(ExecutionIntent x)=>Hash(string.Join('|',x.Symbol,x.Side,x.Quantity.ToString(CultureInfo.InvariantCulture),x.ReduceOnly,x.StopLoss.ToString(CultureInfo.InvariantCulture),x.TakeProfit.ToString(CultureInfo.InvariantCulture),x.ClientOrderId,x.Action,x.OrderType,x.ExpectedPrice.ToString(CultureInfo.InvariantCulture)));
    private static string OrderHash(ExchangeOrder x)=>Hash(string.Join('|',x.Symbol,x.OrderId,x.ClientOrderId,x.Status,x.ExecutedQuantity.ToString(CultureInfo.InvariantCulture),x.AvgPrice.ToString(CultureInfo.InvariantCulture),x.Type,x.PositionSide,x.IsProtection,x.UpdatedAt.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)));
    private static string Hash(string text)=>Hash(Encoding.UTF8.GetBytes(text));private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task<MarketAcceptanceCandidateV1> Candidate(CancellationToken ct){var a=Assembly.GetExecutingAssembly();return new(a.GetName().Version?.ToString()??"unknown",Hash(await File.ReadAllBytesAsync(a.Location,ct)));}
    private static async Task AtomicWrite(string path,byte[] bytes,CancellationToken ct){var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllBytesAsync(temp,bytes,ct);File.Move(temp,path,false);}
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Agent;

public sealed record SmokeRunResult(bool Success,string ReportPath);
public sealed record SmokeStep(string Name,string Status,long DurationMs,string Detail);
internal sealed record ModelOffSmokeSafetyVerdict(bool Allowed, string Code)
{
    internal static ModelOffSmokeSafetyVerdict Evaluate(bool criticalReadiness, bool canonicalInputsValid) =>
        !criticalReadiness ? new(false, "smoke.model-off.critical-readiness-failed") :
        !canonicalInputsValid ? new(false, "smoke.model-off.canonical-input-invalid") :
        new(true, "smoke.model-off.ready");
}
internal enum SmokeReadinessState { Available,Missing,Stale,Unknown,Unsupported }
internal sealed record SmokeReadinessObservation(string Name,SmokeReadinessState State,string SourceId,DateTimeOffset ObservedAtUtc);
internal sealed record SmokeMutationReadiness(
    bool IsTestnet,string SourceId,IReadOnlyList<SmokeReadinessObservation> Observations,
    IReadOnlyList<string> MissingSources);
internal sealed record SmokeMutationGateResult(bool Allowed,string Code);
public sealed class SmokeReport
{
    public DateTime StartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; set; }
    public string Environment { get; set; } = "Testnet";
    public bool Success { get; set; }
    public string Failure { get; set; } = string.Empty;
    public List<SmokeStep> Steps { get; } = new();
}

public static class SmokeTestRunner
{
    private static readonly TimeSpan MaximumReadinessAge=TimeSpan.FromMinutes(2);
    public static async Task<SmokeRunResult> RunAsync(CancellationToken ct=default)
    {
        var report=new SmokeReport{StartedAtUtc=DateTime.UtcNow};var reportDir=Path.Combine(AppDataPaths.TestArtifactsDirectory,"smoke-tests");Directory.CreateDirectory(reportDir);var reportPath=Path.Combine(reportDir,$"smoke-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");IExchangeProvider? exchange=null;IRealtimeMarketFeed? realtime=null;TradingExecutionGateway? gateway=null;TestnetSmokeAuthorization? authorization=null;System.Collections.Concurrent.ConcurrentDictionary<string,ExchangeCapability>? capabilities=null;string? symbol=null;var openedBySmoke=false;
        try
        {
            var store=new AgentSettingsStore();var settings=store.Load();var requestedConnection=Environment.GetEnvironmentVariable("WPE_SMOKE_EXCHANGE_CONNECTION_ID");var allowOverride=string.Equals(Environment.GetEnvironmentVariable("WPE_SMOKE_ALLOW_EXCHANGE_OVERRIDE"),"1",StringComparison.Ordinal);var profile=ResolveSmokeExchangeProfile(store,settings,requestedConnection,allowOverride);if(!profile.IsTestnet)throw new InvalidOperationException("冒烟入口只允许 Testnet，当前配置不是测试网");var credentials=store.GetExchangeCredentials(profile);var catalog=new ExchangeProviderCatalog();
            exchange=catalog.Create(profile,credentials);report.Environment=$"{profile.DisplayName} / Testnet";var db=new AgentSqliteStore();realtime=exchange.CreateRealtimeFeed(settings.Symbols,db);if(realtime is not null){await realtime.StartAsync(ct);var streamDeadline=DateTime.UtcNow.AddSeconds(30);while(DateTime.UtcNow<streamDeadline&&!realtime.Healthy)await Task.Delay(500,ct);if(!realtime.Healthy)throw new InvalidOperationException("smoke.realtime-unhealthy:"+realtime.Status);}capabilities=new(StringComparer.OrdinalIgnoreCase);await RefreshCapabilitiesAsync(exchange,settings.Symbols,capabilities,ct);var executor=new ReliableOrderExecutor(exchange,db,settings.Risk,SystemOrderPollScheduler.Instance,capabilities,true);gateway=new TradingExecutionGateway(executor,db);AccountSnapshot? account=null;ExchangePermissionSnapshot? permission=null;ExchangeHealthSnapshot? health=null;DateTimeOffset permissionObservedAt=default;await Step(report,"测试网账户与持仓读取",async()=>{health=await exchange.HealthCheckAsync(ct);permission=await exchange.CheckPermissionsAsync(ct);permissionObservedAt=DateTimeOffset.UtcNow;account=await exchange.GetAccountAsync(ct);var p=await exchange.GetPositionsAsync(ct);var o=await exchange.GetOpenOrdersAsync(null,ct);if(account.Equity<=0)throw new InvalidOperationException("测试网账户权益为0，需要 Faucet Token");return $"provider={profile.ProviderId}, equity={account.Equity:F2}, available={account.AvailableBalance:F2}, positions={p.Count}, openOrders={o.Count}";});
            var positions=await exchange.GetPositionsAsync(ct);symbol=settings.Symbols.FirstOrDefault(s=>positions.All(p=>!p.Symbol.Equals(s,StringComparison.OrdinalIgnoreCase)));if(symbol is null)throw new InvalidOperationException(settings.Symbols.Count==0?"未配置交易品种，请先在 Setup 中选择至少一个品种":"所有已配置品种均已有仓位，无法选择不干扰现有仓位的冒烟品种");
            TradingRule? rule=null;MarketEvidence? market=null;DateTimeOffset ruleObservedAt=default;await Step(report,"交易规则与市场证据",async()=>{rule=await exchange.GetRulesAsync(symbol,ct);ruleObservedAt=DateTimeOffset.UtcNow;market=await exchange.GetMarketAsync(symbol,ct);market=realtime?.Enrich(market)??market;if(rule.StepSize<=0||rule.MinQuantity<=0||market.Price<=0)throw new InvalidOperationException("交易规则或市场价格无效");return $"{symbol} price={market.Price:F2}, step={rule.StepSize}, minQty={rule.MinQuantity}, minNotional={rule.MinNotional}, basis={market.Derivatives.Basis:P4}";});
            EvidencePack? evidence=null;await Step(report,"完整证据包与新闻",async()=>{evidence=await new EvidenceCollector(exchange,settings.Symbols,realtime).CollectAsync(ct);if(evidence.Markets.Count==0)throw new InvalidOperationException("没有可用市场证据");return $"completeness={evidence.Completeness}/100, markets={evidence.Markets.Count}, news={evidence.News.Count}, missing={string.Join(',',evidence.MissingSources)}";});
            DecisionPlan? directPlan=null;await Step(report,"Direct candle-structure decision",()=>{directPlan=DirectMarketStructureDecisionSkill.Decide(evidence!,false);if(string.IsNullOrWhiteSpace(directPlan.Instrument)&&evidence!.Markets.Count>0)throw new InvalidOperationException("direct decision did not identify a market");return Task.FromResult($"{directPlan.Action} {directPlan.Instrument} · {directPlan.Reason}");});
            await Step(report,"Model-off deterministic readiness",()=>
            {
                var plan=directPlan??throw new InvalidOperationException("direct decision unavailable");
                var review=new DecisionGovernanceSkill().Review(plan,evidence!,settings.Decision);
                var verdict=ModelOffSmokeSafetyVerdict.Evaluate(review.Accepted&&!string.IsNullOrWhiteSpace(review.Explanation),CanonicalSmokeInputsValid(evidence!,plan));
                if(!verdict.Allowed)throw new InvalidOperationException(verdict.Code);
                return Task.FromResult($"{verdict.Code}; action={review.Decision.Action}; instrument={review.Decision.Instrument}; local_direct=true");
            });
            await Step(report,"最小仓位开仓、成交与保护单",async()=>
            {
                await RefreshCapabilitiesAsync(exchange,settings.Symbols,capabilities,ct);
                var readiness=CreateMutationReadiness(profile,$"{exchange.ProviderId}:{exchange.ConnectionId}",permission!,permissionObservedAt,health!,account!,rule!,ruleObservedAt,market!,evidence!,capabilities[symbol]);
                authorization=CreateAuthorization(settings);var quantity=CeilingToStep(Math.Max(rule!.MinQuantity,rule.MinNotional*1.10m/market!.Price),rule.StepSize);var stop=rule.RoundPrice(market.Price*.98m);var take=rule.RoundPrice(market.Price*1.02m);var id=ClientId("OPEN");var intent=new ExecutionIntent(symbol,PositionSide.Long,quantity,false,stop,take,id,"Testnet smoke open",DecisionAction.OpenLong,ExpectedPrice:market.Price);
                var execution=await ExecuteRiskIncreaseAsync(readiness,async()=>{openedBySmoke=true;return await gateway.ExecuteTestnetSmokeAsync(new(authorization,intent,Math.Min(10,rule.MaxLeverage),true,rule,market),ct);},ct);if(!execution.Executed)throw new InvalidOperationException(execution.Code);return $"{symbol} long quantity={quantity}, stop={stop}, take={take}, result={execution.ExecutionResult}";
            });
            await Step(report,"平仓、保护单清理与最终核对",async()=>{await RefreshCapabilitiesAsync(exchange,settings.Symbols,capabilities,ct);await CloseSmokePosition(exchange,gateway,authorization!,symbol,ct);var remaining=await exchange.GetPositionsAsync(ct);if(remaining.Any(p=>p.Symbol==symbol&&p.Side==PositionSide.Long&&p.Quantity>0))throw new InvalidOperationException("冒烟多仓仍有残留");var protections=(await exchange.GetOpenOrdersAsync(symbol,ct)).Count(x=>x.IsProtection&&x.PositionSide==PositionSide.Long);if(protections>0)throw new InvalidOperationException($"仍有 {protections} 个多仓保护单残留");openedBySmoke=false;return"仓位与保护单均已清理";});
            report.Success=true;
        }
        catch(Exception ex){var safe=SensitiveDataRedactor.ForLog(ex.Message,500);report.Failure=safe;report.Steps.Add(new("冒烟测试终止","FAILED",0,safe));}
        finally
        {
            if(openedBySmoke&&exchange is not null&&gateway is not null&&authorization is not null&&capabilities is not null&&symbol is not null)try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(60));await RefreshCapabilitiesAsync(exchange,[symbol],capabilities,cleanup.Token);await CloseSmokePosition(exchange,gateway,authorization,symbol,cleanup.Token);report.Steps.Add(new("失败后强制清理","PASSED",0,"已尝试平掉冒烟仓位并清理保护单"));}catch(Exception ex){report.Steps.Add(new("失败后强制清理","FAILED",0,SensitiveDataRedactor.ForLog(ex.Message,240)));report.Success=false;}
            if(realtime is not null)await realtime.DisposeAsync();if(exchange is not null)await exchange.DisposeAsync();report.CompletedAtUtc=DateTime.UtcNow;await global::币安量化机器人.Services.SensitiveDataRedactor.WriteRedactedJsonAsync(reportPath,report,CancellationToken.None);
        }
        return new(report.Success,reportPath);
    }

    internal static ExchangeConnectionProfile ResolveSmokeExchangeProfile(AgentSettingsStore store,AgentSettings settings,string? requestedConnectionId,bool allowOverride)
    {
        ArgumentNullException.ThrowIfNull(store);ArgumentNullException.ThrowIfNull(settings);
        if(string.IsNullOrWhiteSpace(requestedConnectionId))return store.GetActiveExchange(settings);
        if(!allowOverride)throw new InvalidOperationException("smoke.exchange-override-not-authorized");
        var profile=settings.Exchanges.FirstOrDefault(x=>x.Enabled&&x.Id.Equals(requestedConnectionId.Trim(),StringComparison.OrdinalIgnoreCase))
            ??throw new InvalidOperationException("smoke.exchange-override-not-found");
        if(!profile.IsTestnet)throw new InvalidOperationException("smoke.exchange-override-mainnet-denied");
        return new ExchangeConnectionProfile
        {
            Id=profile.Id,ProviderId=profile.ProviderId,DisplayName=profile.DisplayName,Enabled=true,ExecutionEnabled=true,IsTestnet=true,
            Endpoint=profile.Endpoint,UseProxy=profile.UseProxy,ProxyUrl=profile.ProxyUrl,ReceiveWindow=profile.ReceiveWindow,TimeoutSeconds=profile.TimeoutSeconds,
            SubAccount=profile.SubAccount,UpstreamConnectionId=profile.UpstreamConnectionId,EncryptedCredentials=new(profile.EncryptedCredentials,StringComparer.OrdinalIgnoreCase),
            SymbolMappings=new(profile.SymbolMappings,StringComparer.OrdinalIgnoreCase),LastVerifiedAtUtc=profile.LastVerifiedAtUtc,ReadPermission=profile.ReadPermission,
            TradePermission=profile.TradePermission,WithdrawPermission=profile.WithdrawPermission,AccountId=profile.AccountId
        };
    }
    private static async Task Step(SmokeReport report,string name,Func<Task<string>> action){var sw=Stopwatch.StartNew();try{var detail=await action();report.Steps.Add(new(name,"PASSED",sw.ElapsedMilliseconds,detail));}catch(Exception ex){report.Steps.Add(new(name,"FAILED",sw.ElapsedMilliseconds,SensitiveDataRedactor.ForLog(ex.Message,500)));throw;}}
    internal static bool CanonicalSmokeInputsValid(EvidencePack evidence,DecisionPlan decision)
    {
        if(evidence is null||decision is null||string.IsNullOrWhiteSpace(decision.Instrument))return false;
        if(!evidence.Markets.TryGetValue(decision.Instrument,out var market)||market is null)return false;
        if(!MarketEvidenceProvenanceCanonicalizerV1.IsCanonical(market)||market.Price<=0||market.CollectedAt.Kind!=DateTimeKind.Utc)return false;
        if(!DeterministicPlanSkill.IsRiskIncreasing(decision.Action))return decision.Action==DecisionAction.Hold;
        return DirectMarketStructureDecisionSkill.IsDirect(decision)&&DirectMarketStructureDecisionSkill.ContextMatches(decision,market);
    }
    internal static SmokeMutationGateResult AssessMutationReadiness(SmokeMutationReadiness readiness,DateTimeOffset now)
    {
        if(!readiness.IsTestnet)return new(false,"smoke.readiness.mainnet-denied");
        if(string.IsNullOrWhiteSpace(readiness.SourceId))return new(false,"smoke.readiness.source-missing");
        if(readiness.MissingSources.Any(IsBlockingMissingSource))return new(false,"smoke.readiness.evidence-source-missing");
        foreach(var observation in readiness.Observations)
        {
            if(!string.Equals(observation.SourceId,readiness.SourceId,StringComparison.Ordinal))return new(false,$"smoke.readiness.{observation.Name}-source-mismatch");
            if(observation.State!=SmokeReadinessState.Available)return new(false,$"smoke.readiness.{observation.Name}-{observation.State.ToString().ToLowerInvariant()}");
            var age=now-observation.ObservedAtUtc;
            if(age<TimeSpan.Zero||age>MaximumReadinessAge)return new(false,$"smoke.readiness.{observation.Name}-stale");
        }
        return new(true,"smoke.readiness.allowed");
    }
    internal static bool IsBlockingMissingSource(string source)
    {
        if(string.IsNullOrWhiteSpace(source))return true;
        if(source.EndsWith(":liquidation_feed_missing",StringComparison.OrdinalIgnoreCase))return false;
        return source is not ("SEC" or "CFTC" or "Federal Reserve" or "ECB" or "CoinDesk" or "Cointelegraph" or "Google News");
    }
    internal static async Task<TradingExecutionGatewayResult> ExecuteRiskIncreaseAsync(
        SmokeMutationReadiness readiness,Func<Task<TradingExecutionGatewayResult>> mutate,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var gate=AssessMutationReadiness(readiness,DateTimeOffset.UtcNow);
        return gate.Allowed?await mutate():new(false,gate.Code);
    }
    internal static SmokeMutationReadiness CreateMutationReadiness(
        ExchangeConnectionProfile profile,string source,ExchangePermissionSnapshot permission,DateTimeOffset permissionObservedAt,
        ExchangeHealthSnapshot health,AccountSnapshot account,TradingRule rule,DateTimeOffset ruleObservedAt,
        MarketEvidence market,EvidencePack evidence,ExchangeCapability capability)
    {
        var now=DateTimeOffset.UtcNow;
        SmokeReadinessObservation Observation(string name,bool available,DateTimeOffset observedAt)=>
            new(name,available?SmokeReadinessState.Available:SmokeReadinessState.Unsupported,source,observedAt);
        var endpointOk=Uri.TryCreate(profile.Endpoint,UriKind.Absolute,out var endpoint)&&endpoint.Scheme==Uri.UriSchemeHttps;
        return new(profile.IsTestnet,source,
        [
            Observation("clock",health.Healthy&&AccessReadinessService.ClockWithinTradingTolerance(health.ClockSkewMs),health.CheckedAtUtc),
            Observation("capability",capability.Status==CapabilityStatus.Available&&capability.CanRead&&capability.CanTrade&&capability.TestnetAvailable,capability.CheckedAt),
            Observation("permission",permission.CanRead&&permission.CanTrade&&!string.IsNullOrWhiteSpace(permission.AccountId),permissionObservedAt),
            Observation("endpoint",endpointOk,now),
            Observation("account",account.Equity>0&&account.Timestamp!=default,account.Timestamp),
            Observation("rule",rule.StepSize>0&&rule.MinQuantity>0&&rule.MinNotional>0,ruleObservedAt),
            Observation("market",market.Price>0,market.CollectedAt),
            Observation("evidence",evidence.Markets.ContainsKey(market.Symbol),evidence.CollectedAt)
        ],evidence.MissingSources);
    }
    private static async Task RefreshCapabilitiesAsync(IExchangeProvider exchange,IReadOnlyList<string> symbols,System.Collections.Concurrent.ConcurrentDictionary<string,ExchangeCapability> target,CancellationToken ct)
    {
        var refreshed=await new ProviderCapabilityProbe().ProbeAsync(exchange,symbols,true,ct);
        if(refreshed.Count!=symbols.Count||refreshed.Values.Any(x=>x.Status!=CapabilityStatus.Available||!x.CanRead||!x.CanTrade||!x.TestnetAvailable))throw new InvalidOperationException("Smoke test capability probe failed closed.");
        target.Clear();foreach(var item in refreshed)target[item.Key]=item.Value;
    }
    private static async Task CloseSmokePosition(IExchangeAdapter exchange,TradingExecutionGateway gateway,TestnetSmokeAuthorization authorization,string symbol,CancellationToken ct){var position=(await exchange.GetPositionsAsync(ct)).FirstOrDefault(p=>p.Symbol==symbol&&p.Side==PositionSide.Long);if(position is null||position.Quantity<=0)return;var intent=new ExecutionIntent(symbol,PositionSide.Long,position.Quantity,true,0,0,ClientId("CLOSE"),"Testnet smoke cleanup",DecisionAction.CloseLong,ExpectedPrice:position.MarkPrice);var execution=await gateway.ExecuteTestnetSmokeAsync(new(authorization,intent,Math.Max(1,(int)position.Leverage),position.Isolated,ObservedPosition:position),ct);if(!execution.Executed)throw new InvalidOperationException(execution.Code);}
    private static TestnetSmokeAuthorization CreateAuthorization(AgentSettings settings){if(string.IsNullOrWhiteSpace(settings.ActiveUser))throw new InvalidOperationException("smoke.authorization-user-required");var now=DateTimeOffset.UtcNow;var id=Guid.NewGuid().ToString("N");return new(id,"SMOKE-"+id,settings.ActiveUser,DeviceLicenseService.GetCurrentDeviceCode(),"smoke-session-"+id,now,now.AddMinutes(10));}
    internal static IAssistantProvider CreateBrain(AgentSettings settings,Func<IAssistantProvider>? createLocal=null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return(createLocal??AssistantProviderFactory.CreateLocal)();
    }
    private static decimal CeilingToStep(decimal value,decimal step)=>step<=0?value:Math.Ceiling(value/step)*step;
    private static string ClientId(string action){var raw=$"WPE-SMOKE-{action}-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid():N}";return raw[..Math.Min(36,raw.Length)];}
}
